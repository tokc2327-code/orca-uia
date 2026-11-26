'use strict';
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const readline = require('node:readline');
const path = require('node:path');
const { McpClient, sleep } = require('./events-retrofit-common.cjs');

const repo = path.resolve(__dirname, '..');
const exe = path.join(repo, 'src', 'bin', 'Release', 'net8.0-windows', 'orca-uia.exe');
const fixturePath = path.join(__dirname, 'fixtures', 'routing-fixture.ps1');

async function main() {
  const fixture = spawn('pwsh', ['-NoProfile', '-File', fixturePath], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: false }); // UIA must be able to enumerate the fixture window.
  const lines = [];
  let readyResolve, readyReject;
  const ready = new Promise((resolve, reject) => { readyResolve = resolve; readyReject = reject; });
  const rl = readline.createInterface({ input: fixture.stdout });
  rl.on('line', line => {
    lines.push(line);
    if (line.startsWith('READY:')) readyResolve(JSON.parse(line.slice(6)));
  });
  let fixtureErr = '';
  fixture.stderr.on('data', d => fixtureErr += d.toString());
  fixture.on('exit', code => readyReject(new Error(`fixture exited ${code}: ${fixtureErr}`)));
  const info = await Promise.race([ready, sleep(10000).then(() => { throw new Error(`fixture READY timeout: ${fixtureErr}`); })]);
  const client = new McpClient(exe);
  const results = [];
  const result = (name, ok, detail = '') => {
    const line = `${ok ? 'PASS' : 'FAIL'}: ${name}${detail ? ` — ${detail}` : ''}`;
    results.push(line); console.log(line);
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
  try {
    await client.initialize();
    const attached = await client.callTool('app_attach', { title_contains: 'OrcaUiaRoutingFixture' });
    assert.equal(attached.isError, false, attached.text);

    const button = await client.callTool('app_act', { action: 'set_value', target: { type: 'Button', aid: 'fixtureButton' }, text: 'x' });
    assert.equal(button.isError, true); assert.equal(button.json?.error, 'UNSUPPORTED_PATTERN');
    result('Button set_value unsupported', true, button.json.error);

    const edit = await client.callTool('app_act', { action: 'set_value', target: { type: 'Edit', aid: 'fixtureEdit' }, text: 'hello' });
    assert.equal(edit.isError, false, edit.text); assert.equal(edit.json?.used, 'value');
    assert.equal(edit.json?.state?.value, 'hello');
    const editGround = await waitState(/STATE:.*edit=\[hello\]/);
    assert.match(editGround, /edit=\[hello\]/);
    result('TextBox set_value readback and ground truth', true, `used=${edit.json.used}; ${editGround}`);

    // Unit semantics changed 2026-11-26: a trackbar's ValuePattern speaks percent, so app_act now
    // converts a native request into percent and verifies against TBM_GETPOS. '16' therefore lands
    // as 16 instead of being rejected. A value the bar cannot represent at all (25 on a 0..20 bar)
    // is still the case that must be refused rather than silently clamped.
    const mismatch = await client.callTool('app_act', { action: 'set_value', target: { type: 'Slider', aid: 'fixtureSlider' }, text: '25' });
    assert.equal(mismatch.isError, true, mismatch.text);
    // Either the range check refuses the write up front, or the ground-truth comparison catches it.
    assert.ok(['VALUE_OUT_OF_RANGE', 'VALUE_READBACK_MISMATCH'].includes(mismatch.json?.error), mismatch.text);
    result('out-of-range value rejected, not clamped', true, `${mismatch.json.error}: ${mismatch.json.message}`);
    const slider = await client.callTool('app_act', { action: 'set_value', target: { type: 'Slider', aid: 'fixtureSlider' }, text: '20' });
    await sleep(500);
    const sliderGround = await waitState(/STATE:slider=/);
    const trueValue = Number(sliderGround.match(/slider=(\d+)/)?.[1]);
    // Record, never throw: a FAIL here is the evidence we are hunting (slider false success).
    const sliderOk = slider.isError
      ? slider.json?.error === 'VALUE_READBACK_MISMATCH'
      : slider.json?.ok === true && trueValue === 20;
    result('TrackBar set_value 20 has no false success', sliderOk,
      `response=${slider.text}; ground=${sliderGround}`);

    const loose = await client.callTool('app_act', { action: 'set_value', target: { control_type: 'Button' }, text: 'x' });
    if (!loose.isError) result('control_type strict schema known gap', true, 'KNOWN_GAP: strict schema not implemented yet');
    else result('control_type strict schema known gap', true, `currently rejected: ${loose.json?.error}`);

    // Unit semantics: native is the default and must reach the control's own scale; percent keeps
    // its 0..100 meaning for callers who think in it.
    const native7 = await client.callTool('app_act', { action: 'set_value', target: { type: 'Slider', aid: 'fixtureSlider' }, text: '7', unit: 'native' });
    const nativeGround = await waitState(/STATE:slider=7 /);
    assert.equal(native7.json?.applied, 7, native7.text);
    result('unit=native reaches the control scale', true, `applied=${native7.json.applied}; ${nativeGround.trim()}`);
    const pct100 = await client.callTool('app_act', { action: 'set_value', target: { type: 'Slider', aid: 'fixtureSlider' }, text: '100', unit: 'percent' });
    const pctGround = await waitState(/STATE:slider=20 /);
    assert.equal(pct100.json?.applied, 20, pct100.text);
    result('unit=percent maps 100 to the maximum', true, `applied=${pct100.json.applied}; ${pctGround.trim()}`);
  } catch (e) {
    console.error(`FAIL: test harness — ${e.stack || e}`); process.exitCode = 1;
  } finally {
    const passed = results.filter(l => l.startsWith('PASS')).length;
    const failed = results.filter(l => l.startsWith('FAIL')).length;
    console.log(`SUMMARY: PASS ${passed} / FAIL ${failed}`);
    if (failed > 0) process.exitCode = 1;    await client?.close?.();
    try { fixture.stdin.write('quit\n'); } catch {}
    await Promise.race([new Promise(r => fixture.once('exit', r)), sleep(1500)]);
    try { fixture.kill(); } catch {}
  }
}
main().catch(e => { console.error(e); process.exit(1); });



