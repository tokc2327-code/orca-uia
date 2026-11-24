// orca-uia scripted driver: reads a JSON job and runs its steps in order.
//   job: { "title": "<window title substring>", "steps": [
//            { "tool": "app_snapshot", "args": {}, "expect": "…", "reject": "…" }, … ] }
// Per step:
//   expect        string or array of strings — the reply must contain all of them
//   reject        string or array of strings — the reply must contain none of them
//   expect_error  true — the step is supposed to fail; a successful call counts as a failure
//   stop_on_error default true
// Exit code is 0 only when every step passed.
//
// Running a job takes the screen away from whoever is using the machine, so two job-level
// options put it back afterwards:
//   restore_foreground : "Coopanion"  — bring that window (title substring) to the front again
//                        (ORCA_UIA_FOREGROUND in the environment does the same for every run)
//   kill_on_exit       : "CalculatorApp" | true — close the app the job drove; true means "whatever
//                        the attach reply names as the process"
// usage: node clients/uia-driver.cjs <job.json>
// Set ORCA_UIA_EXE to the built server, or drop the exe next to this file as orca-uia.exe.
const { spawn, spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const exe = process.env.ORCA_UIA_EXE || path.join(__dirname, 'orca-uia.exe');
const jobPath = process.argv[2];
if (!jobPath) { console.error('usage: node clients/uia-driver.cjs <job.json>'); process.exit(2); }
if (!fs.existsSync(exe)) { console.error(`orca-uia.exe not found: ${exe} (set ORCA_UIA_EXE)`); process.exit(2); }
const job = JSON.parse(fs.readFileSync(jobPath, 'utf8').replace(/^\uFEFF/, ''));

const child = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'] });
let buf = '';
const pending = new Map();
let nextId = 1;

function send(method, params) {
  const id = nextId++;
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  return new Promise((resolve) => pending.set(id, resolve));
}

child.stdout.on('data', (d) => {
  buf += d.toString();
  let idx;
  while ((idx = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, idx).trim();
    buf = buf.slice(idx + 1);
    if (!line) continue;
    let msg;
    try { msg = JSON.parse(line); } catch { continue; }
    if (msg.id !== undefined && pending.has(msg.id)) {
      pending.get(msg.id)(msg);
      pending.delete(msg.id);
    }
  }
});
child.stderr.on('data', (d) => console.error('[err] ' + d.toString().trim().slice(0, 400)));

const call = async (name, args) => {
  const r = await send('tools/call', { name, arguments: args || {} });
  const out = r.result || r.error;
  const text = out && out.content && out.content[0] ? out.content[0].text : JSON.stringify(out);
  return { text, isError: Boolean(out && out.isError) };
};

const toList = (v) => (v === undefined ? [] : Array.isArray(v) ? v : [v]);

// One-off PowerShell for the two clean-up jobs; the driver itself needs no native bindings.
const ps = (script) => {
  const r = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script],
    { encoding: 'utf8', windowsHide: true });
  return ((r.stdout || '') + (r.stderr || '')).trim().slice(0, 300);
};

// Give the front of the screen back to whoever had it before the run.
const restoreForeground = (needle) => {
  if (!needle) return;
  const pat = String(needle).replace(/'/g, "''");
  const out = ps(`Add-Type -Namespace W -Name N -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);'; ` +
    `$p = Get-Process | Where-Object { $_.MainWindowTitle -like '*${pat}*' } | Select-Object -First 1; ` +
    `if ($p) { [void][W.N]::SetForegroundWindow($p.MainWindowHandle); 'restored ' + $p.ProcessName } else { 'no window matching ${pat}' }`);
  console.log('FOREGROUND ' + out);
};

const killTarget = (name) => {
  if (!name) return;
  const pat = String(name).replace(/'/g, "''");
  const out = ps(`$p = Get-Process -Name '${pat}' -ErrorAction SilentlyContinue; ` +
    `if ($p) { $p | Stop-Process -Force; 'closed ${pat}' } else { 'not running: ${pat}' }`);
  console.log('KILL ' + out);
};

// Returns '' when the step passed, otherwise the reason it did not.
const judge = (r, step) => {
  if (step.expect_error) return r.isError ? '' : 'expected an error, call succeeded';
  if (r.isError) return 'tool error';
  const missing = toList(step.expect).filter((w) => !r.text.includes(w));
  if (missing.length) return 'missing: ' + missing.join(' | ');
  const forbidden = toList(step.reject).filter((w) => r.text.includes(w));
  if (forbidden.length) return 'unexpected: ' + forbidden.join(' | ');
  return '';
};

(async () => {
  await send('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'uia', version: '1' } });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

  let attachText = '';
  if (job.title) {
    const at = await call('app_attach', { title_contains: job.title, preload: true });
    attachText = at.text;
    console.log('ATTACH ' + (at.isError ? 'ERR ' + at.text.slice(0, 300) : 'OK'));
    if (at.isError) { child.kill(); process.exit(1); }
  }

  // kill_on_exit: true means "the process the attach reply named".
  let killName = job.kill_on_exit === true ? '' : job.kill_on_exit;
  if (job.kill_on_exit === true) {
    const m = /"process"\s*:\s*"([^"]+)"/.exec(attachText) || /"name"\s*:\s*"([^"]+\.exe)"/.exec(attachText);
    killName = m ? m[1] : '';
    if (!killName) console.log('KILL skipped: the attach reply does not name its process, pass a process name instead');
  }

  let pass = 0, fail = 0;
  for (const [i, step] of job.steps.entries()) {
    const r = await call(step.tool, step.args);
    const why = judge(r, step);
    if (why) fail++; else pass++;
    console.log('--- step ' + (i + 1) + '/' + job.steps.length + ' ' + step.tool +
      (why ? ' [FAIL ' + why + ']' : ' [ok]') + (step.note ? '  // ' + step.note : ''));
    console.log(r.text.slice(0, step.limit || 8000));
    const stop = step.stop_on_error === undefined ? true : step.stop_on_error;
    if (why && stop) break;
    if (step.waitMs) await new Promise((res) => setTimeout(res, step.waitMs));
  }

  console.log(`\nSUMMARY steps=${job.steps.length} ok=${pass} failed=${fail}`);
  child.kill();
  killTarget(killName);
  restoreForeground(job.restore_foreground || process.env.ORCA_UIA_FOREGROUND);
  process.exit(fail ? 1 : 0);
})();
