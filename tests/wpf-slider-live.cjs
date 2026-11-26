'use strict';
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const readline = require('node:readline');
const path = require('node:path');
const { McpClient, sleep } = require('./events-retrofit-common.cjs');

const repo = path.resolve(__dirname, '..');
const exe = path.join(repo, 'src', 'bin', 'Release', 'net8.0-windows', 'orca-uia.exe');
const fixturePath = path.join(__dirname, 'fixtures', 'wpf-slider-fixture.ps1');

async function main() {
  const fixture = spawn('pwsh', ['-NoProfile', '-STA', '-File', fixturePath], {
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: false, // UIA must be able to enumerate the WPF fixture window.
  });
  const lines = [];
  let fixtureErr = '';
  fixture.stderr.on('data', d => { fixtureErr += d.toString(); });

  let readySettled = false;
  let readyResolve, readyReject;
  const ready = new Promise((resolve, reject) => {
    readyResolve = v => { if (!readySettled) { readySettled = true; resolve(v); } };
    readyReject = e => { if (!readySettled) { readySettled = true; reject(e); } };
  });

  const rl = readline.createInterface({ input: fixture.stdout });
  rl.on('line', line => {
    const trimmed = line.trim();
    lines.push(trimmed);
    if (trimmed === 'READY:wpf' || trimmed.startsWith('READY:wpf')) {
      readyResolve(trimmed);
    }
  });

  fixture.on('error', err => {
    const errText = fixtureErr.trim();
    readyReject(new Error(
      `fixture spawn failed: ${err.message}${errText ? ` | stderr: ${errText}` : ' (stderr empty)'}`
    ));
  });
  fixture.on('close', (code, signal) => {
    const errText = fixtureErr.trim();
    readyReject(new Error(
      `fixture exited before READY:wpf (code=${code}, signal=${signal})${errText ? ` | stderr: ${errText}` : ' (stderr empty)'}`
    ));
  });

  let client = null;
  const results = [];
  const result = (name, ok, detail = '') => {
    const line = `${ok ? 'PASS' : 'FAIL'}: ${name}${detail ? ` — ${detail}` : ''}`;
    results.push(line);
    console.log(line);
  };
  const waitState = async re => {
    const end = Date.now() + 4000;
    while (Date.now() < end) {
      const found = lines.slice().reverse().find(x => re.test(x));
      if (found) return found;
      await sleep(50);
    }
    throw new Error(`state timeout; recent=${lines.slice(-5).join(' | ')}`);
  };
  const waitStableGround = async () => {
    // Fixture DispatcherTimer ticks every 300ms and prints STATE:wpf=<int> on change;
    // wait across consecutive ticks until the latest STATE:wpf= reading stabilizes.
    await sleep(400);
    let prev = await waitState(/^STATE:wpf=-?\d+$/);
    const end = Date.now() + 4000;
    while (Date.now() < end) {
      await sleep(350);
      const cur = await waitState(/^STATE:wpf=-?\d+$/);
      if (cur === prev) {
        return { line: cur, ground: Number(cur.slice('STATE:wpf='.length)) };
      }
      prev = cur;
    }
    return { line: prev, ground: Number(prev.slice('STATE:wpf='.length)) };
  };

  try {
    await Promise.race([
      ready,
      sleep(10000).then(() => {
        const errText = fixtureErr.trim();
        throw new Error(
          errText
            ? `fixture READY:wpf timeout after 10000ms (stderr has output): ${errText}`
            : 'fixture READY:wpf timeout after 10000ms (stderr empty)'
        );
      }),
    ]);
    await waitState(/^STATE:wpf=-?\d+$/);

    client = new McpClient(exe);
    await client.initialize();
    const attached = await client.callTool('app_attach', { title_contains: 'OrcaUiaWpfFixture' });
    assert.equal(attached.isError, false, attached.text);

    const target = { type: 'Slider', aid: 'wpfSlider' };

    // 1. unit:"native", text:"40" -> ok:true, ground === 40
    const r1 = await client.callTool('app_act', { action: 'set_value', target, unit: 'native', text: '40' });
    const s1 = await waitStableGround();
    const ok1 = r1.isError === false && r1.json?.ok === true && s1.ground === 40;
    result('WPF Slider unit=native text=40', ok1, `response=${r1.text}; ground=${s1.line}`);

    // 2. default unit (omitted), text:"40" -> ok:true, ground === 40
    const r2 = await client.callTool('app_act', { action: 'set_value', target, text: '40' });
    const s2 = await waitStableGround();
    const ok2 = r2.isError === false && r2.json?.ok === true && s2.ground === 40;
    result('WPF Slider default unit text=40', ok2, `response=${r2.text}; ground=${s2.line}`);

    // 3. unit:"percent", text:"80" -> ok:true, ground === 80
    // Kept off 100 on purpose: case 4 asserts the slider did not move, and a preceding write to
    // 100 would make that assertion pass for the wrong reason (or fail even when the write was
    // correctly refused).
    const r3 = await client.callTool('app_act', { action: 'set_value', target, unit: 'percent', text: '80' });
    const s3 = await waitStableGround();
    const ok3 = r3.isError === false && r3.json?.ok === true && s3.ground === 80;
    result('WPF Slider unit=percent text=80', ok3, `response=${r3.text}; ground=${s3.line}`);

    // 4. unit:"native", text:"150" -> isError:true (VALUE_OUT_OF_RANGE | VALUE_READBACK_MISMATCH)
    //    and the slider must still hold 80 from case 3: a refused write leaves the control alone.
    const allowedErrors = ['VALUE_OUT_OF_RANGE', 'VALUE_READBACK_MISMATCH'];
    const r4 = await client.callTool('app_act', { action: 'set_value', target, unit: 'native', text: '150' });
    const s4 = await waitStableGround();
    const ok4 = r4.isError === true && allowedErrors.includes(r4.json?.error) && s4.ground === 80;
    result('WPF Slider unit=native text=150 rejected without clamp', ok4, `response=${r4.text}; ground=${s4.line}`);
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
