'use strict';

const { spawn } = require('child_process');
const readline = require('readline');

function parseArgs(argv) {
  const out = {};
  for (let i = 2; i < argv.length; i++) {
    const k = argv[i];
    if (!k.startsWith('--')) continue;
    const key = k.slice(2);
    const next = argv[i + 1];
    if (next === undefined || next.startsWith('--')) {
      out[key] = true;
    } else {
      out[key] = next;
      i++;
    }
  }
  return out;
}

function median(nums) {
  if (!nums.length) throw new Error('median of empty array');
  const sorted = [...nums].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 1 ? sorted[mid] : Math.round((sorted[mid - 1] + sorted[mid]) / 2);
}

function linearSlope(ys) {
  const n = ys.length;
  if (n < 2) return 0;
  let sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
  for (let i = 0; i < n; i++) {
    sumX += i;
    sumY += ys[i];
    sumXX += i * i;
    sumXY += i * ys[i];
  }
  const denom = n * sumXX - sumX * sumX;
  return denom === 0 ? 0 : (n * sumXY - sumX * sumY) / denom;
}

function sleep(ms) {
  return new Promise((r) => setTimeout(r, Math.max(0, ms)));
}

// Persistent Win32 process sampler: pid, processStartTime, handles (GetProcessHandleCount),
// privateBytes (PrivateMemorySize64), threads (Threads.Count), rss (WorkingSet64).
function startSampler() {
  const psScript = `
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class ProcSampler {
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessHandleCount(IntPtr hProcess, out uint pdwHandleCount);

    public static string Sample(int pid) {
        using var p = Process.GetProcessById(pid);
        p.Refresh();
        uint hc = 0;
        IntPtr h = OpenProcess(0x0400 /* PROCESS_QUERY_INFORMATION */, false, pid);
        if (h != IntPtr.Zero) {
            try {
                if (!GetProcessHandleCount(h, out hc)) {
                    hc = (uint)p.HandleCount;
                }
            } finally {
                CloseHandle(h);
            }
        } else {
            hc = (uint)p.HandleCount;
        }
        string startIso = p.StartTime.ToUniversalTime().ToString("o");
        return pid + "," + startIso + "," + hc + "," + p.PrivateMemorySize64 + "," + p.Threads.Count + "," + p.WorkingSet64;
    }
}
"@
[Console]::Out.WriteLine("READY")
[Console]::Out.Flush()
while ($null -ne ($line = [Console]::In.ReadLine())) {
    if ($line -eq "QUIT") { break }
    $pidVal = [int]$line
    try {
        [Console]::Out.WriteLine([ProcSampler]::Sample($pidVal))
    } catch {
        [Console]::Out.WriteLine("ERR:" + $_.Exception.Message)
    }
    [Console]::Out.Flush()
}
`;

  const proc = spawn('pwsh', ['-NoProfile', '-Command', psScript], {
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
  });

  const rl = readline.createInterface({ input: proc.stdout });
  const waiters = [];
  let readyResolve;
  const readyPromise = new Promise((res, rej) => {
    readyResolve = { res, rej };
  });

  rl.on('line', (line) => {
    if (line === 'READY') {
      readyResolve.res();
      return;
    }
    const w = waiters.shift();
    if (w) w.res(line);
  });

  proc.on('exit', (code) => {
    while (waiters.length) waiters.shift().rej(new Error(`sampler exited ${code}`));
    readyResolve.rej(new Error(`sampler exited ${code}`));
  });

  return {
    async ready() {
      await readyPromise;
    },
    async sample(pid) {
      const p = new Promise((res, rej) => waiters.push({ res, rej }));
      proc.stdin.write(`${pid}\n`);
      const line = await p;
      if (line.startsWith('ERR:')) throw new Error(line);
      const parts = line.split(',');
      if (parts.length !== 6) throw new Error(`malformed sampler line: ${line}`);
      const samplePid = Number(parts[0]);
      const processStartTime = parts[1];
      const handles = Number(parts[2]);
      const privateBytes = Number(parts[3]);
      const threads = Number(parts[4]);
      const rss = Number(parts[5]);
      if (
        !Number.isFinite(samplePid) ||
        !processStartTime ||
        !Number.isFinite(handles) ||
        !Number.isFinite(privateBytes) ||
        privateBytes <= 0 ||
        !Number.isFinite(threads) ||
        !Number.isFinite(rss)
      ) {
        throw new Error(`invalid sample values: ${line}`);
      }
      return {
        ts: Date.now(),
        pid: samplePid,
        processStartTime,
        handles,
        privateBytes,
        threads,
        rss,
      };
    },
    close() {
      try { proc.stdin.write('QUIT\n'); } catch {}
      try { proc.kill(); } catch {}
    },
  };
}

// Non-activating WinForms fixture (WS_EX_NOACTIVATE + ShowWithoutActivating)
function startFixture(title = 'OrcaUiaRetrofitFixture') {
  const psScript = `
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing,System.ComponentModel.Primitives,System.Drawing.Primitives,System.Console -TypeDefinition @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

public sealed class NoActivateFixtureForm : Form {
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private readonly Button _btn;
    private readonly TextBox _box;
    private readonly FlowLayoutPanel _panel;
    private int _dynCount = 0;

    public NoActivateFixtureForm(string title) {
        Text = title;
        Size = new Size(360, 240);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40);
        ShowInTaskbar = false;

        _btn = new Button { Name = "mainBtn", Text = "InitialButton", Location = new Point(12, 12), Size = new Size(140, 30) };
        _box = new TextBox { Name = "mainBox", Text = "InitialText", Location = new Point(12, 50), Size = new Size(200, 24) };
        _panel = new FlowLayoutPanel { Name = "dynPanel", Location = new Point(12, 85), Size = new Size(320, 100) };

        Controls.Add(_btn);
        Controls.Add(_box);
        Controls.Add(_panel);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        System.Threading.Tasks.Task.Run(() => {
            string line;
            while ((line = Console.In.ReadLine()) != null) {
                var cmd = line;
                try {
                    if (!IsDisposed && IsHandleCreated) {
                        BeginInvoke((Action)(() => ApplyCommand(cmd)));
                    }
                } catch {}
                if (cmd == "quit") break;
            }
        });
    }

    public void ApplyCommand(string cmd) {
        if (IsDisposed) return;
        if (cmd.StartsWith("rename ")) {
            _btn.Text = cmd.Substring(7);
        } else if (cmd.StartsWith("value ")) {
            _box.Text = cmd.Substring(6);
        } else if (cmd == "toggle") {
            _btn.Enabled = !_btn.Enabled;
        } else if (cmd.StartsWith("add ")) {
            var b = new Button { Name = "dynBtn" + (++_dynCount), Text = cmd.Substring(4), Size = new Size(90, 26) };
            _panel.Controls.Add(b);
        } else if (cmd == "remove") {
            if (_panel.Controls.Count > 0) {
                var c = _panel.Controls[_panel.Controls.Count - 1];
                _panel.Controls.Remove(c);
                c.Dispose();
            }
        } else if (cmd == "quit") {
            Close();
        }
    }
}
"@

$form = [NoActivateFixtureForm]::new("${title.replace(/"/g, '')}")
$form.Add_Shown({
    $info = @{
        pid = $PID
        hwnd = $form.Handle.ToInt64()
        title = $form.Text
    } | ConvertTo-Json -Compress
    [Console]::Out.WriteLine("READY:" + $info)
    [Console]::Out.Flush()
})

[System.Windows.Forms.Application]::Run($form)
`;

  const proc = spawn('pwsh', ['-NoProfile', '-Command', psScript], {
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
  });

  const rl = readline.createInterface({ input: proc.stdout });
  let infoResolve;
  const infoPromise = new Promise((res, rej) => { infoResolve = { res, rej }; });

  rl.on('line', (line) => {
    if (line.startsWith('READY:')) {
      infoResolve.res(JSON.parse(line.slice(6)));
    }
  });

  let stderr = '';
  proc.stderr.on('data', (d) => { stderr += d.toString(); });
  proc.on('exit', (code) => {
    infoResolve.rej(new Error(`fixture exited ${code}: ${stderr}`));
  });

  return {
    proc,
    ready: () => infoPromise,
    send(cmd) {
      proc.stdin.write(`${cmd}\n`);
    },
    async kill() {
      try { proc.kill('SIGKILL'); } catch {}
      if (proc.exitCode === null && proc.signalCode === null) {
        await Promise.race([
          new Promise((r) => proc.once('exit', r)),
          sleep(3000),
        ]);
      }
    },
    async close() {
      try { proc.stdin.write('quit\n'); } catch {}
      await Promise.race([
        new Promise((r) => proc.once('exit', r)),
        sleep(2000),
      ]);
      try { proc.kill(); } catch {}
    },
  };
}

class McpClient {
  constructor(exePath, extraEnv = {}) {
    this.exePath = exePath;
    this.proc = spawn(exePath, [], {
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
      env: { ...process.env, ...extraEnv },
    });
    this.pid = this.proc.pid;
    this.nextId = 1;
    this.pending = new Map();
    this.stderr = '';

    this.proc.stderr.on('data', (d) => { this.stderr += d.toString(); });
    const rl = readline.createInterface({ input: this.proc.stdout });
    rl.on('line', (line) => {
      if (!line.trim()) return;
      let msg;
      try { msg = JSON.parse(line); } catch { return; }
      if (msg.id !== undefined && this.pending.has(msg.id)) {
        const p = this.pending.get(msg.id);
        this.pending.delete(msg.id);
        p.res(msg);
      }
    });
    this.proc.on('exit', (code) => {
      for (const p of this.pending.values()) {
        p.rej(new Error(`MCP exited with ${code}: ${this.stderr}`));
      }
      this.pending.clear();
    });
  }

  request(method, params = {}) {
    const id = this.nextId++;
    const payload = JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n';
    return new Promise((res, rej) => {
      this.pending.set(id, { res, rej });
      this.proc.stdin.write(payload);
    });
  }

  notify(method, params = {}) {
    const payload = JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n';
    this.proc.stdin.write(payload);
  }

  async initialize() {
    const r = await this.request('initialize', {
      protocolVersion: '2025-03-26',
      capabilities: {},
      clientInfo: { name: 'events-retrofit-test', version: '1.0' },
    });
    this.notify('notifications/initialized', {});
    return r;
  }

  async callTool(name, args = {}) {
    const resp = await this.request('tools/call', { name, arguments: args });
    if (resp.error) {
      throw new Error(`JSON-RPC error ${resp.error.code}: ${resp.error.message}`);
    }
    const text = resp.result?.content?.[0]?.text ?? '';
    const isError = !!resp.result?.isError;
    let parsed = null;
    try { parsed = JSON.parse(text); } catch {}
    return { isError, text, json: parsed };
  }

  async close() {
    try { this.proc.stdin.end(); } catch {}
    await Promise.race([
      new Promise((r) => this.proc.once('exit', r)),
      sleep(3000),
    ]);
    try { this.proc.kill(); } catch {}
  }
}

module.exports = {
  parseArgs,
  median,
  linearSlope,
  sleep,
  startSampler,
  startFixture,
  McpClient,
};
