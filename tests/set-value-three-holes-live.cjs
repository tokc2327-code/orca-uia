'use strict';
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const readline = require('node:readline');
const path = require('node:path');
const { McpClient, sleep } = require('./events-retrofit-common.cjs');

const repo = path.resolve(__dirname, '..');
const exe = path.join(repo, 'src', 'bin', 'Release', 'net8.0-windows', 'orca-uia.exe');

const fixtureScript = `
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing,System.ComponentModel.Primitives,System.Drawing.Primitives,System.Console -TypeDefinition @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed class ThreeHolesFixtureForm : Form {
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public TrackBar Deg0 { get; }
    public TrackBar Deg5 { get; }
    public TrackBar Bar20 { get; }
    public TrackBar Bar1000 { get; }

    public ThreeHolesFixtureForm() {
        Text = "OrcaUiaThreeHolesFixture";
        Size = new Size(460, 320);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(80, 80);
        ShowInTaskbar = false;

        Deg0 = new TrackBar { Name = "sliderDegenerate0", Minimum = 0, Maximum = 0, SmallChange = 1, Value = 0, Location = new Point(20, 15), Size = new Size(380, 45) };
        Deg5 = new TrackBar { Name = "sliderDegenerate5", Minimum = 5, Maximum = 5, SmallChange = 1, Value = 5, Location = new Point(20, 70), Size = new Size(380, 45) };
        Bar20 = new TrackBar { Name = "sliderRange20", Minimum = 0, Maximum = 20, SmallChange = 1, Value = 10, Location = new Point(20, 125), Size = new Size(380, 45) };
        Bar1000 = new TrackBar { Name = "sliderWide1000", Minimum = 0, Maximum = 1000, SmallChange = 1, Value = 250, Location = new Point(20, 180), Size = new Size(380, 45) };

        Controls.AddRange(new Control[] { Deg0, Deg5, Bar20, Bar1000 });
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public string DumpState() {
        return $"STATE:deg0={Deg0.Value} deg5={Deg5.Value} bar20={Bar20.Value} bar1000={Bar1000.Value}";
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Console.Out.WriteLine("READY:" + Handle.ToInt64());
        Console.Out.WriteLine(DumpState());
        Console.Out.Flush();
        System.Threading.Tasks.Task.Run(() => {
            string line;
            while ((line = Console.In.ReadLine()) != null) {
                var cmd = line.Trim();
                if (cmd == "state") {
                    try {
                        BeginInvoke((Action)(() => {
                            Console.Out.WriteLine(DumpState());
                            Console.Out.Flush();
                        }));
                    } catch {}
                } else if (cmd == "quit") {
                    try { BeginInvoke((Action)(() => Close())); } catch {}
                    break;
                }
            }
        });
    }
}
"@

$form = [ThreeHolesFixtureForm]::new()
[System.Windows.Forms.Application]::Run($form)
`;

async function main() {
  const fixture = spawn('pwsh', ['-NoProfile', '-Command', fixtureScript], {
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: false,
  });

  const stateQueue = [];
  let readyResolve, readyReject;
  let readySettled = false;
  const ready = new Promise((res, rej) => {
    readyResolve = v => { if (!readySettled) { readySettled = true; res(v); } };
    readyReject = e => { if (!readySettled) { readySettled = true; rej(e); } };
  });

  let fixtureErr = '';
  fixture.stderr.on('data', d => { fixtureErr += d.toString(); });
  fixture.on('exit', code => {
    readyReject(new Error(`fixture exited ${code}: ${fixtureErr}`));
    while (stateQueue.length) stateQueue.shift().rej(new Error(`fixture exited ${code}`));
  });

  const rl = readline.createInterface({ input: fixture.stdout });
  rl.on('line', line => {
    const trimmed = line.trim();
    if (trimmed.startsWith('READY:')) {
      readyResolve(trimmed);
    } else if (trimmed.startsWith('STATE:')) {
      const waiter = stateQueue.shift();
      if (waiter) waiter.res(trimmed);
    }
  });

  const queryGround = async () => {
    const p = new Promise((res, rej) => stateQueue.push({ res, rej }));
    fixture.stdin.write('state\n');
    const line = await Promise.race([
      p,
      sleep(3000).then(() => { throw new Error('STATE query timeout'); }),
    ]);
    const m = line.match(/^STATE:deg0=(-?\d+) deg5=(-?\d+) bar20=(-?\d+) bar1000=(-?\d+)$/);
    assert.ok(m, `unexpected state line: ${line}`);
    return {
      raw: line,
      deg0: Number(m[1]),
      deg5: Number(m[2]),
      bar20: Number(m[3]),
      bar1000: Number(m[4]),
    };
  };

  let client = null;
  const results = [];
  const result = (name, ok, detail = '') => {
    const line = `${ok ? 'PASS' : 'FAIL'}: ${name}${detail ? ` — ${detail}` : ''}`;
    results.push(line);
    console.log(line);
  };

  try {
    await Promise.race([
      ready,
      sleep(10000).then(() => { throw new Error(`fixture READY timeout: ${fixtureErr}`); }),
    ]);

    client = new McpClient(exe);
    await client.initialize();
    const attached = await client.callTool('app_attach', { title_contains: 'OrcaUiaThreeHolesFixture' });
    assert.equal(attached.isError, false, attached.text);

    // ==================== Hole 1: Max <= Min ====================
    // 1a. Minimum=0, Maximum=0 TrackBar: must NOT return ok:true + applied_verified:true, and must throw NO_GROUND_TRUTH.
    const h1Zero = await client.callTool('app_act', {
      action: 'set_value',
      target: { type: 'Slider', aid: 'sliderDegenerate0' },
      text: '0',
    });
    const h1ZeroFalseSuccess = !h1Zero.isError && h1Zero.json?.ok === true && h1Zero.json?.applied_verified === true;
    const h1ZeroOk = !h1ZeroFalseSuccess && h1Zero.isError === true && h1Zero.json?.error === 'NO_GROUND_TRUTH';
    result(
      '[Hole 1a] Max==Min (0..0) TrackBar rejects set_value with NO_GROUND_TRUTH (never applied_verified:true)',
      h1ZeroOk,
      `response=${h1Zero.text}`
    );

    // 1b. Minimum=5, Maximum=5 TrackBar with text='0': unpatched code reported ok:true, requested:0, applied:5, applied_verified:true.
    const h1Five = await client.callTool('app_act', {
      action: 'set_value',
      target: { type: 'Slider', aid: 'sliderDegenerate5' },
      text: '0',
    });
    const h1FiveFalseSuccess = !h1Five.isError && h1Five.json?.ok === true && h1Five.json?.applied_verified === true;
    const h1FiveOk = !h1FiveFalseSuccess && h1Five.isError === true && h1Five.json?.error === 'NO_GROUND_TRUTH';
    result(
      '[Hole 1b] Max==Min (5..5) TrackBar writing 0 rejects with NO_GROUND_TRUTH instead of stale applied=5',
      h1FiveOk,
      `response=${h1Five.text}`
    );

    // ==================== Hole 2: Out-of-range tolerance ====================
    // 2a. 0..20 slider writing 20.1, 20.08 (pct=100.4%), and -0.1 (pct=-0.5%): all must fail with VALUE_OUT_OF_RANGE, never clamp to ok:true.
    const h2_20_1 = await client.callTool('app_act', {
      action: 'set_value',
      target: { type: 'Slider', aid: 'sliderRange20' },
      text: '20.1',
    });
    const h2_20_08 = await client.callTool('app_act', {
      action: 'set_value',
      target: { type: 'Slider', aid: 'sliderRange20' },
      text: '20.08',
    });
    const h2_neg0_1 = await client.callTool('app_act', {
      action: 'set_value',
      target: { type: 'Slider', aid: 'sliderRange20' },
      text: '-0.1',
    });
    const gAfter2a = await queryGround();
    const h2aOk =
      h2_20_1.isError === true && h2_20_1.json?.error === 'VALUE_OUT_OF_RANGE' &&
      h2_20_08.isError === true && h2_20_08.json?.error === 'VALUE_OUT_OF_RANGE' &&
      h2_neg0_1.isError === true && h2_neg0_1.json?.error === 'VALUE_OUT_OF_RANGE' &&
      gAfter2a.bar20 === 10;
    result(
      '[Hole 2a] 0..20 TrackBar rejects 20.1 / 20.08 / -0.1 with VALUE_OUT_OF_RANGE without clamping',
      h2aOk,
      `20.1=${h2_20_1.text}; 20.08=${h2_20_08.text}; -0.1=${h2_neg0_1.text}; ground=${gAfter2a.bar20}`
    );

    // 2b. 0..1000 slider (initial Value=250) writing -5 (pct=-0.5%): must reject with VALUE_OUT_OF_RANGE before sending "-0.5" to SetValue.
    const h2_neg5 = await client.callTool('app_act', {
      action: 'set_value',
      target: { type: 'Slider', aid: 'sliderWide1000' },
      text: '-5',
    });
    const gAfter2b = await queryGround();
    const h2bOk =
      h2_neg5.isError === true &&
      h2_neg5.json?.error === 'VALUE_OUT_OF_RANGE' &&
      gAfter2b.bar1000 === 250;
    result(
      '[Hole 2b] 0..1000 TrackBar rejects -5 with VALUE_OUT_OF_RANGE without mutating slider to 0',
      h2bOk,
      `response=${h2_neg5.text}; ground=${gAfter2b.bar1000}`
    );

    // ==================== Hole 3: Wide-range (Max - Min > 100) readback tolerance ====================
    // 3a. 0..1000 TrackBar, unit=native text=500 -> ok:true and applied in 495..505.
    const h3_500 = await client.callTool('app_act', {
      action: 'set_value',
      target: { type: 'Slider', aid: 'sliderWide1000' },
      unit: 'native',
      text: '500',
    });
    const gAfter3a = await queryGround();
    const h3aOk =
      h3_500.isError === false &&
      h3_500.json?.ok === true &&
      h3_500.json?.applied_verified === true &&
      typeof h3_500.json?.applied === 'number' &&
      h3_500.json.applied >= 495 &&
      h3_500.json.applied <= 505 &&
      h3_500.json.applied === gAfter3a.bar1000;
    result(
      '[Hole 3a] 0..1000 TrackBar unit=native text=500 succeeds with applied in 495..505',
      h3aOk,
      `response=${h3_500.text}; ground=${gAfter3a.bar1000}`
    );

    // 3b. 0..1000 TrackBar, unit=native text=503 (50.3% snaps to 50%=500 on MSAA 1% scale) -> must NOT false-fail VALUE_READBACK_MISMATCH, and applied must be true control value (500), not requested (503).
    const h3_503 = await client.callTool('app_act', {
      action: 'set_value',
      target: { type: 'Slider', aid: 'sliderWide1000' },
      unit: 'native',
      text: '503',
    });
    const gAfter3b = await queryGround();
    const h3bOk =
      h3_503.isError === false &&
      h3_503.json?.ok === true &&
      h3_503.json?.applied_verified === true &&
      typeof h3_503.json?.applied === 'number' &&
      h3_503.json.applied >= 495 &&
      h3_503.json.applied <= 505 &&
      h3_503.json.applied === gAfter3b.bar1000 &&
      h3_503.json.requested === 503;
    result(
      '[Hole 3b] 0..1000 TrackBar unit=native text=503 succeeds within 1% tolerance and reports true applied value',
      h3bOk,
      `response=${h3_503.text}; ground=${gAfter3b.bar1000}`
    );
  } catch (e) {
    console.error(`FAIL: test harness — ${e.stack || e}`);
    process.exitCode = 1;
  } finally {
    const passed = results.filter(l => l.startsWith('PASS')).length;
    const failed = results.filter(l => l.startsWith('FAIL')).length;
    console.log(`SUMMARY: PASS ${passed} / FAIL ${failed}`);
    if (failed > 0) process.exitCode = 1;
    await client?.close?.();
    try { fixture.stdin.write('quit\n'); } catch {}
    await Promise.race([new Promise(r => fixture.once('exit', r)), sleep(1500)]);
    try { fixture.kill(); } catch {}
  }
}

main().catch(e => { console.error(e); process.exit(1); });
