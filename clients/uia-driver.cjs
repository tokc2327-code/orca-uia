
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

(async () => {
  await send('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'uia', version: '1' } });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

  if (job.title) {
    const at = await call('app_attach', { title_contains: job.title, preload: true });
    console.log('ATTACH ' + (at.isError ? 'ERR ' + at.text.slice(0, 300) : 'OK'));
    if (at.isError) { child.kill(); process.exit(1); }
  }

  for (const [i, step] of job.steps.entries()) {
    const r = await call(step.tool, step.args);
    console.log('--- step ' + (i + 1) + ' ' + step.tool + (r.isError ? ' [ERR]' : ''));
    console.log(r.text.slice(0, step.limit || 8000));
    if (r.isError && step.stopOnError !== false) break;
    if (step.waitMs) await new Promise((res) => setTimeout(res, step.waitMs));
  }

  child.kill();
  process.exit(0);
})();
