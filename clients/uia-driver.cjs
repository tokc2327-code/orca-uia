// orca-uia scripted driver: reads a JSON job and runs its steps in order.
//   job: { "title": "<window title substring>", "steps": [
//            { "tool": "app_snapshot", "args": {}, "expect": "…", "reject": "…" }, … ] }
// Per step:
//   expect        string or array of strings — the reply must contain all of them
//   reject        string or array of strings — the reply must contain none of them
//   expect_error  true — the step is supposed to fail; a successful call counts as a failure
//   stop_on_error default true
// Exit code is 0 only when every step passed.
// usage: node clients/uia-driver.cjs <job.json>
// Set ORCA_UIA_EXE to the built server, or drop the exe next to this file as orca-uia.exe.
const { spawn } = require('node:child_process');
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

  if (job.title) {
    const at = await call('app_attach', { title_contains: job.title, preload: true });
    console.log('ATTACH ' + (at.isError ? 'ERR ' + at.text.slice(0, 300) : 'OK'));
    if (at.isError) { child.kill(); process.exit(1); }
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
  process.exit(fail ? 1 : 0);
})();
