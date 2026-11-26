'use strict';

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const {
  parseArgs,
  median,
  linearSlope,
  sleep,
  startSampler,
  startFixture,
  McpClient,
} = require('./events-retrofit-common.cjs');

const PRIV_LIMIT_BYTES = 8 * 1024 * 1024; // 8 MiB = 8,388,608 bytes
const THR_LIMIT = 2;
const HANDLE_BAND = 5;

function sha256File(filePath) {
  if (!filePath || !fs.existsSync(filePath)) return null;
  const h = crypto.createHash('sha256');
  h.update(fs.readFileSync(filePath));
  return h.digest('hex').toUpperCase();
}

function stripTiming(obj) {
  if (Array.isArray(obj)) return obj.map(stripTiming);
  if (obj && typeof obj === 'object') {
    const out = {};
    for (const [k, v] of Object.entries(obj)) {
      if (k === 'elapsed_ms' || k === 'preload_ms') continue;
      out[k] = stripTiming(v);
    }
    return out;
  }
  return obj;
}

function stripSnapshotHeaderTiming(text) {
  return text.replace(/elapsed_ms=\d+/g, 'elapsed_ms=<TIMING>');
}

function checkIdentity(sample, expectedIdentity) {
  if (!expectedIdentity) return true;
  return (
    sample.pid === expectedIdentity.pid &&
    sample.processStartTime === expectedIdentity.processStartTime
  );
}

async function collectCooldownSamples(sampler, pid, expectedIdentity, sampleCount, sampleMs, t0) {
  const samples = [];
  let dropped = false;
  let identityOk = true;
  for (let s = 1; s <= sampleCount; s++) {
    const target = t0 + s * sampleMs;
    const wait = target - Date.now();
    if (wait > 0) await sleep(wait);
    try {
      const sample = await sampler.sample(pid);
      if (!checkIdentity(sample, expectedIdentity)) identityOk = false;
      samples.push(sample);
    } catch {
      dropped = true;
    }
  }
  if (samples.length !== sampleCount) dropped = true;
  return { samples, dropped, identityOk };
}

function summarizeTail(samples, tailCount) {
  const tail = samples.slice(-tailCount);
  const handles = tail.map((s) => s.handles);
  const minH = Math.min(...handles);
  const maxH = Math.max(...handles);
  return {
    tailCount: tail.length,
    medianHandles: median(handles),
    spanHandles: maxH - minH,
    minHandles: minH,
    maxHandles: maxH,
    medianPrivateBytes: median(tail.map((s) => s.privateBytes)),
    medianThreads: median(tail.map((s) => s.threads)),
    medianRss: median(tail.map((s) => s.rss)),
  };
}

async function runSingleBinaryRegression(label, exePath, opts) {
  const { cycles, cooldownMs, sampleMs, warmupMs } = opts;
  const sampleCount = Math.max(5, Math.floor(cooldownMs / sampleMs));
  const sampler = startSampler();
  const fixture = startFixture(`OrcaUia_${label}`);
  const [, info] = await Promise.all([sampler.ready(), fixture.ready()]);
  const mcp = new McpClient(exePath);
  await mcp.initialize();

  await mcp.callTool('app_attach', { pid: info.pid });
  await mcp.callTool('app_snapshot', {});

  const initSample = await sampler.sample(mcp.pid);
  const identity = {
    pid: initSample.pid,
    processStartTime: initSample.processStartTime,
  };

  let allIdentityOk = true;
  let noDroppedSamples = true;

  // Cold idle period (no subscriptions)
  if (warmupMs > 0) await sleep(warmupMs);

  // 5 consecutive 1s cold baseline samples (B0)
  const coldSamples = [];
  const coldT0 = Date.now();
  for (let i = 0; i < 5; i++) {
    if (i > 0) await sleep(coldT0 + i * sampleMs - Date.now());
    try {
      const s = await sampler.sample(mcp.pid);
      if (!checkIdentity(s, identity)) allIdentityOk = false;
      coldSamples.push(s);
    } catch {
      noDroppedSamples = false;
    }
  }
  if (coldSamples.length !== 5) noDroppedSamples = false;
  const coldSummary = summarizeTail(coldSamples, 5);
  const B0 = coldSummary.medianHandles;
  const b0Span = coldSummary.spanHandles;
  const b0SpanOk = b0Span <= HANDLE_BAND;

  // Helper to run one real subscribe -> UIA event -> poll -> unsubscribe -> cooldown cycle
  async function runOneSubCycle(tag) {
    const preSub = await sampler.sample(mcp.pid);
    if (!checkIdentity(preSub, identity)) allIdentityOk = false;

    const sub = await mcp.callTool('app_events', {
      action: 'subscribe',
      window: { pid: info.pid },
      debounce_ms: 40,
      lease_s: 120,
    });
    if (sub.isError) throw new Error(`[${label}] ${tag} subscribe failed: ${sub.text}`);
    const regHandlers = sub.json.registered_handlers;
    const subId = sub.json.subscription_id;
    let cursor = sub.json.cursor;

    fixture.send(`rename ${tag}_Btn`);
    fixture.send(`value ${tag}_Val`);
    fixture.send(`add ${tag}_Dyn`);
    await sleep(200);

    const poll1 = await mcp.callTool('app_events', { action: 'poll', since: cursor });
    if (poll1.isError || !poll1.json.events || poll1.json.events.length === 0) {
      throw new Error(`[${label}] ${tag} expected observable events, got: ${poll1.text}`);
    }
    cursor = poll1.json.cursor;

    const unsub = await mcp.callTool('app_events', {
      action: 'unsubscribe',
      subscription_id: subId,
    });
    const unsubReceiptTs = Date.now();
    if (unsub.isError) throw new Error(`[${label}] ${tag} unsubscribe failed: ${unsub.text}`);
    const removedHandlers = unsub.json.removed_handlers;
    const rawEvents = unsub.json.raw_events;

    fixture.send(`rename PostUnsub_${tag}`);
    const cd = await collectCooldownSamples(sampler, mcp.pid, identity, sampleCount, sampleMs, unsubReceiptTs);
    if (cd.dropped) noDroppedSamples = false;
    if (!cd.identityOk) allIdentityOk = false;

    const statusRes = await mcp.callTool('app_events', { action: 'status' });
    const oldCursorRes = await mcp.callTool('app_events', { action: 'poll', since: cursor });
    const statusZero =
      !statusRes.isError &&
      statusRes.json.active_subscriptions === 0 &&
      statusRes.json.registered_handlers === 0;
    const unsubCodeOk = oldCursorRes.isError && oldCursorRes.json?.error === 'EVENTS_UNSUBSCRIBED';
    const removedMatch = removedHandlers === regHandlers && regHandlers === 3;

    return {
      preSub,
      registered_handlers: regHandlers,
      removed_handlers: removedHandlers,
      removed_matches_registered: removedMatch,
      raw_events: rawEvents,
      events_observed: poll1.json.events.length,
      status_zero_after_unsub: statusZero,
      old_cursor_error: oldCursorRes.json?.error ?? null,
      unsubscribed_error_ok: unsubCodeOk,
      dropped: cd.dropped,
      identityOk: cd.identityOk,
      samples: cd.samples,
    };
  }

  // Fixed single warmup round -> establishes W0 once
  const warmupRound = await runOneSubCycle('Warmup_0');
  const w0Summary = summarizeTail(warmupRound.samples, 5);
  const W0 = w0Summary.medianHandles;
  const w0Span = w0Summary.spanHandles;
  const w0SpanOk = w0Span <= HANDLE_BAND;
  const M_W0 = {
    handles: W0,
    privateBytes: w0Summary.medianPrivateBytes,
    threads: w0Summary.medianThreads,
    rss: w0Summary.medianRss,
  };
  const warmupProtocolOk =
    !warmupRound.dropped &&
    warmupRound.identityOk &&
    warmupRound.removed_matches_registered &&
    warmupRound.status_zero_after_unsub &&
    warmupRound.unsubscribed_error_ok;

  // 10 scoring cycles in the SAME process (cycle 1 is subscription #2)
  const cycleResults = [];
  const inW0Band = (h) => Math.abs(h - W0) <= HANDLE_BAND;

  for (let c = 1; c <= cycles; c++) {
    const res = await runOneSubCycle(`Cycle_${c}`);
    const last3 = res.samples.slice(-3);
    const last3Summary = summarizeTail(res.samples, 3);
    const last3InW0Band = last3.length === 3 && last3.every((s) => inW0Band(s.handles));
    const H_i = last3Summary.medianHandles;
    const M_i = {
      handles: H_i,
      privateBytes: last3Summary.medianPrivateBytes,
      threads: last3Summary.medianThreads,
      rss: last3Summary.medianRss,
    };
    const passedCycle =
      !res.dropped &&
      res.identityOk &&
      last3InW0Band &&
      res.removed_matches_registered &&
      res.status_zero_after_unsub &&
      res.unsubscribed_error_ok;

    cycleResults.push({
      cycle: c,
      subscriptionIndex: c + 1,
      ...res,
      last3Handles: last3.map((s) => s.handles),
      last3_in_w0_band: last3InW0Band,
      H_i,
      H_i_minus_W0: H_i - W0,
      M_i,
      passed_cycle: passedCycle,
      peak_handles: Math.max(...res.samples.map((s) => s.handles)),
      min_handles: Math.min(...res.samples.map((s) => s.handles)),
      final_sample: res.samples[res.samples.length - 1],
    });
  }

  await mcp.close();
  await fixture.close();
  sampler.close();

  const tailStart = cycles >= 10 ? 5 : Math.floor(cycles / 2);
  const P = median(cycleResults.slice(tailStart).map((c) => c.H_i));
  const passedCycles = cycleResults.filter((r) => r.passed_cycle).length;
  const validW0 = allIdentityOk && noDroppedSamples && b0SpanOk && w0SpanOk && warmupProtocolOk;
  const passedR1 = validW0 && passedCycles === cycles;

  return {
    label,
    exePath,
    identity,
    allIdentityOk,
    noDroppedSamples,
    B0,
    b0Span,
    b0SpanOk,
    M_B0: {
      handles: B0,
      privateBytes: coldSummary.medianPrivateBytes,
      threads: coldSummary.medianThreads,
      rss: coldSummary.medianRss,
    },
    coldSamples,
    W0,
    w0Span,
    w0SpanOk,
    W0_minus_B0: W0 - B0,
    M_W0,
    warmupProtocolOk,
    warmupRound,
    P,
    P_minus_B0: P - B0,
    cycles,
    passedCycles,
    validW0,
    passedR1,
    cycleResults,
  };
}

function evaluatePair(pairIndex, order, a0, cand) {
  const pairValid = a0.passedR1;
  const dW0 = cand.W0 - a0.W0;
  const dW0Cost = cand.W0_minus_B0 - a0.W0_minus_B0;
  const dP = cand.P - a0.P;
  const dPCost = cand.P_minus_B0 - a0.P_minus_B0;

  const dPriv_W0 = cand.M_W0.privateBytes - a0.M_W0.privateBytes;
  const dThr_W0 = cand.M_W0.threads - a0.M_W0.threads;
  const warmup3DOk = dPriv_W0 <= PRIV_LIMIT_BYTES && dThr_W0 <= THR_LIMIT;
  const warmupHandleOk = dW0 <= HANDLE_BAND && dW0Cost <= HANDLE_BAND;
  const tailPlateauOk = dP <= HANDLE_BAND && dPCost <= HANDLE_BAND;

  let firstExceedingCycle = null;
  const perCycle = [];
  for (let i = 0; i < cand.cycleResults.length; i++) {
    const ca = a0.cycleResults[i];
    const cc = cand.cycleResults[i];
    const dHi = cc.H_i - ca.H_i;
    const dPriv_i = cc.M_i.privateBytes - ca.M_i.privateBytes;
    const dThr_i = cc.M_i.threads - ca.M_i.threads;
    const dPrivGrowth_i =
      (cc.M_i.privateBytes - cand.M_W0.privateBytes) -
      (ca.M_i.privateBytes - a0.M_W0.privateBytes);
    const dThrGrowth_i =
      (cc.M_i.threads - cand.M_W0.threads) -
      (ca.M_i.threads - a0.M_W0.threads);

    const handleOk = dHi <= HANDLE_BAND && cc.passed_cycle;
    const privOk = dPriv_i <= PRIV_LIMIT_BYTES && dPrivGrowth_i <= PRIV_LIMIT_BYTES;
    const thrOk = dThr_i <= THR_LIMIT && dThrGrowth_i <= THR_LIMIT;
    const cycleOk = handleOk && privOk && thrOk;
    if (!cycleOk && firstExceedingCycle === null) {
      firstExceedingCycle = cc.cycle;
    }

    perCycle.push({
      cycle: cc.cycle,
      a0_Hi: ca.H_i,
      cand_Hi: cc.H_i,
      dH_i: dHi,
      a0_Hi_minus_W0: ca.H_i_minus_W0,
      cand_Hi_minus_W0: cc.H_i_minus_W0,
      cand_passed_r1_cycle: cc.passed_cycle,
      a0_priv_i: ca.M_i.privateBytes,
      cand_priv_i: cc.M_i.privateBytes,
      dPriv_i,
      dPrivGrowth_i,
      a0_thr_i: ca.M_i.threads,
      cand_thr_i: cc.M_i.threads,
      dThr_i,
      dThrGrowth_i,
      passed: cycleOk,
    });
  }

  const allCyclesOk = perCycle.every((c) => c.passed);
  const pairPassed =
    pairValid &&
    cand.passedR1 &&
    warmupHandleOk &&
    warmup3DOk &&
    tailPlateauOk &&
    allCyclesOk;

  return {
    pairIndex,
    order,
    pairValid,
    pairPassed,
    a0_B0: a0.B0,
    cand_B0: cand.B0,
    a0_W0: a0.W0,
    cand_W0: cand.W0,
    a0_W0_minus_B0: a0.W0_minus_B0,
    cand_W0_minus_B0: cand.W0_minus_B0,
    dW0,
    dW0Cost,
    dPriv_W0,
    dThr_W0,
    warmupHandleOk,
    warmup3DOk,
    tailPlateauDiff: {
      a0_P: a0.P,
      cand_P: cand.P,
      dP,
      dPCost,
      tailPlateauOk,
    },
    firstExceedingCycle,
    perCycle,
  };
}

async function runEventStormScenario(label, exePath, opts) {
  const { cooldownMs, sampleMs, warmupMs, stormCount } = opts;
  const targetMin = Math.max(500, stormCount);
  const targetMax = 1000;
  const sampleCount = Math.max(5, Math.floor(cooldownMs / sampleMs));

  const sampler = startSampler();
  const fixture = startFixture(`OrcaUiaStorm_${label}`);
  const [, info] = await Promise.all([sampler.ready(), fixture.ready()]);
  const mcp = new McpClient(exePath);
  await mcp.initialize();
  await mcp.callTool('app_attach', { pid: info.pid });
  await mcp.callTool('app_snapshot', {});

  const initSample = await sampler.sample(mcp.pid);
  const identity = { pid: initSample.pid, processStartTime: initSample.processStartTime };
  if (warmupMs > 0) await sleep(warmupMs);

  // Establish W0 cleanly first
  const warmSub = await mcp.callTool('app_events', {
    action: 'subscribe',
    window: { pid: info.pid },
    debounce_ms: 40,
    lease_s: 120,
  });
  fixture.send('rename WarmStorm');
  fixture.send('value WarmStormVal');
  await sleep(200);
  await mcp.callTool('app_events', { action: 'poll', since: warmSub.json.cursor });
  await mcp.callTool('app_events', { action: 'unsubscribe', subscription_id: warmSub.json.subscription_id });
  const warmCd = await collectCooldownSamples(sampler, mcp.pid, identity, sampleCount, sampleMs, Date.now());
  const w0Summary = summarizeTail(warmCd.samples, 5);
  const W0 = w0Summary.medianHandles;

  const preStorm = await sampler.sample(mcp.pid);
  const sub = await mcp.callTool('app_events', {
    action: 'subscribe',
    window: { pid: info.pid },
    debounce_ms: 40,
    lease_s: 180,
  });
  if (sub.isError) throw new Error(`[${label}] storm subscribe failed: ${sub.text}`);
  let cursor = sub.json.cursor;

  // Pump 500..1000 real UIA events (property/text changes) in paced batches
  const pairs = Math.ceil((targetMin + 100) / 2); // e.g. 300 pairs -> ~600-800 UIA events
  for (let i = 1; i <= pairs; i++) {
    fixture.send(`rename StormBtn_${i}`);
    fixture.send(`value StormVal_${i}`);
    if (i % 25 === 0) await sleep(20);
  }
  await sleep(350);

  let observedEvents = 0;
  for (let p = 0; p < 20; p++) {
    const poll = await mcp.callTool('app_events', { action: 'poll', since: cursor });
    if (poll.isError) throw new Error(`[${label}] storm poll failed: ${poll.text}`);
    observedEvents += poll.json.events.length;
    cursor = poll.json.cursor;
    if (!poll.json.has_more) break;
  }

  // Top up only if UIA delivered fewer than targetMin raw events
  let st = await mcp.callTool('app_events', { action: 'status', subscription_id: sub.json.subscription_id });
  let extraIdx = 0;
  while (st.json.raw_events < targetMin && extraIdx < 300) {
    extraIdx++;
    fixture.send(`rename StormTop_${extraIdx}`);
    fixture.send(`value StormTopVal_${extraIdx}`);
    if (extraIdx % 25 === 0) await sleep(25);
    if (extraIdx % 50 === 0) {
      await sleep(150);
      st = await mcp.callTool('app_events', { action: 'status', subscription_id: sub.json.subscription_id });
    }
  }
  await sleep(250);
  for (let p = 0; p < 20; p++) {
    const poll = await mcp.callTool('app_events', { action: 'poll', since: cursor });
    if (poll.isError) break;
    observedEvents += poll.json.events.length;
    cursor = poll.json.cursor;
    if (!poll.json.has_more) break;
  }

  const unsub = await mcp.callTool('app_events', {
    action: 'unsubscribe',
    subscription_id: sub.json.subscription_id,
  });
  const unsubTs = Date.now();
  if (unsub.isError) throw new Error(`[${label}] storm unsub failed: ${unsub.text}`);
  const rawEvents = unsub.json.raw_events;

  const cd = await collectCooldownSamples(sampler, mcp.pid, identity, sampleCount, sampleMs, unsubTs);
  const statusAfter = await mcp.callTool('app_events', { action: 'status' });
  const tail3 = summarizeTail(cd.samples, 3);

  const handleSlopePerSec = linearSlope(cd.samples.map((s) => s.handles));
  const privSlopeBytesPerSec = linearSlope(cd.samples.map((s) => s.privateBytes));
  const thrSlopePerSec = linearSlope(cd.samples.map((s) => s.threads));

  await mcp.close();
  await fixture.close();
  sampler.close();

  const statusZero =
    !statusAfter.isError &&
    statusAfter.json.active_subscriptions === 0 &&
    statusAfter.json.registered_handlers === 0;
  const inW0Band = cd.samples.slice(-3).every((s) => Math.abs(s.handles - W0) <= HANDLE_BAND);
  const rawInRange = rawEvents >= targetMin && rawEvents <= targetMax;
  const privDelta = tail3.medianPrivateBytes - w0Summary.medianPrivateBytes;
  const thrDelta = tail3.medianThreads - w0Summary.medianThreads;
  const cooldownSlopeOk = handleSlopePerSec <= 0.1 && privSlopeBytesPerSec <= 64 * 1024;

  return {
    label,
    identity,
    W0,
    M_W0: {
      handles: W0,
      privateBytes: w0Summary.medianPrivateBytes,
      threads: w0Summary.medianThreads,
      rss: w0Summary.medianRss,
    },
    preStorm,
    raw_events: rawEvents,
    raw_in_500_1000: rawInRange,
    observed_events: observedEvents,
    removed_handlers: unsub.json.removed_handlers,
    status_zero_after_unsub: statusZero,
    identityOk: !warmCd.dropped && warmCd.identityOk && !cd.dropped && cd.identityOk,
    cooldownTail3: tail3,
    delta_handles_vs_W0: tail3.medianHandles - W0,
    delta_priv_vs_W0: privDelta,
    delta_threads_vs_W0: thrDelta,
    last3_in_w0_band: inW0Band,
    handleSlopePerSec,
    privSlopeBytesPerSec,
    thrSlopePerSec,
    cooldownSlopeOk,
    samples: cd.samples,
    passed:
      rawInRange &&
      observedEvents > 0 &&
      unsub.json.removed_handlers === 3 &&
      statusZero &&
      !cd.dropped &&
      cd.identityOk &&
      inW0Band &&
      cooldownSlopeOk &&
      thrDelta <= THR_LIMIT,
  };
}

async function runKillWindowsScenario(label, exePath, opts) {
  const { cooldownMs, sampleMs, stormWindows } = opts;
  const sampleCount = Math.max(5, Math.floor(cooldownMs / sampleMs));

  const sampler = startSampler();
  const baseFix = startFixture(`OrcaUiaKillBase_${label}`);
  const [, baseInfo] = await Promise.all([sampler.ready(), baseFix.ready()]);
  const mcp = new McpClient(exePath);
  await mcp.initialize();
  await mcp.callTool('app_attach', { pid: baseInfo.pid });
  const initSample = await sampler.sample(mcp.pid);
  const identity = { pid: initSample.pid, processStartTime: initSample.processStartTime };

  const preKills = await sampler.sample(mcp.pid);
  const windowRuns = [];

  for (let w = 1; w <= stormWindows; w++) {
    const preSample = await sampler.sample(mcp.pid);
    const tf = startFixture(`OrcaUiaKillWin_${label}_${w}`);
    const ti = await tf.ready();

    const sub = await mcp.callTool('app_events', {
      action: 'subscribe',
      window: { pid: ti.pid },
      debounce_ms: 40,
      lease_s: 120,
    });
    if (sub.isError) throw new Error(`[${label}] kill win ${w} sub failed: ${sub.text}`);
    const regHandlers = sub.json.registered_handlers;
    let cursor = sub.json.cursor;

    tf.send(`rename KillEvt_${w}`);
    tf.send(`value KillVal_${w}`);
    await sleep(120);
    const livePoll = await mcp.callTool('app_events', { action: 'poll', since: cursor });
    if (!livePoll.isError && livePoll.json?.cursor) cursor = livePoll.json.cursor;

    const killStart = Date.now();
    await tf.kill();
    await sleep(150);

    const gonePoll = await mcp.callTool('app_events', { action: 'poll', since: cursor });
    const statusRes = await mcp.callTool('app_events', { action: 'status' });
    const cleanupElapsedMs = Date.now() - killStart;
    const postSample = await sampler.sample(mcp.pid);

    const targetGoneOk = gonePoll.isError && gonePoll.json?.error === 'EVENTS_TARGET_GONE';
    const statusZero =
      !statusRes.isError &&
      statusRes.json.active_subscriptions === 0 &&
      statusRes.json.registered_handlers === 0;

    windowRuns.push({
      windowIndex: w,
      targetPid: ti.pid,
      registered_handlers: regHandlers,
      targetGoneOk,
      goneError: gonePoll.json?.error ?? null,
      statusZero,
      cleanupElapsedMs,
      identityOk: checkIdentity(postSample, identity),
      preSample,
      postSample,
    });
  }

  const cd = await collectCooldownSamples(sampler, mcp.pid, identity, sampleCount, sampleMs, Date.now());
  const tail3 = summarizeTail(cd.samples, 3);

  await mcp.close();
  await baseFix.close();
  sampler.close();

  const postHandles = windowRuns.map((w) => w.postSample.handles);
  const postPriv = windowRuns.map((w) => w.postSample.privateBytes);
  const postThr = windowRuns.map((w) => w.postSample.threads);

  const handleSlopePerWindow = linearSlope(postHandles);
  const privSlopeBytesPerWindow = linearSlope(postPriv);
  const thrSlopePerWindow = linearSlope(postThr);
  const cooldownHandleSlopePerSec = linearSlope(cd.samples.map((s) => s.handles));
  const cooldownPrivSlopeBytesPerSec = linearSlope(cd.samples.map((s) => s.privateBytes));
  const lastPost = windowRuns[windowRuns.length - 1].postSample;

  const allTargetGoneOk = windowRuns.every((w) => w.targetGoneOk && w.statusZero && w.identityOk);
  const cooldownStable = tail3.medianHandles - lastPost.handles <= HANDLE_BAND;

  return {
    label,
    identity,
    stormWindows,
    preKills,
    allTargetGoneOk,
    maxCleanupMs: Math.max(...windowRuns.map((w) => w.cleanupElapsedMs)),
    handleSlopePerWindow,
    privSlopeBytesPerWindow,
    thrSlopePerWindow,
    cooldownHandleSlopePerSec,
    cooldownPrivSlopeBytesPerSec,
    cooldownTail3: tail3,
    cooldownHandleDeltaVsLastKill: tail3.medianHandles - lastPost.handles,
    cooldownPrivDeltaVsLastKill: tail3.medianPrivateBytes - lastPost.privateBytes,
    windowRuns,
    cooldownSamples: cd.samples,
    passed: allTargetGoneOk && !cd.dropped && cd.identityOk && cooldownStable,
  };
}

async function runEdgeAndProtocolChecks(baselineExe, candidateExe) {
  const fixture = startFixture('OrcaUiaEdgeFixture');
  const info = await fixture.ready();
  const mcp = new McpClient(candidateExe);
  await mcp.initialize();
  await mcp.callTool('app_attach', { pid: info.pid });

  // 1. Lease expiration (EVENTS_LEASE_EXPIRED)
  const leaseSub = await mcp.callTool('app_events', {
    action: 'subscribe',
    window: { pid: info.pid },
    lease_s: 1,
  });
  await sleep(1400);
  const leasePoll = await mcp.callTool('app_events', {
    action: 'poll',
    since: leaseSub.json.cursor,
  });
  const leaseStatus = await mcp.callTool('app_events', { action: 'status' });
  const leaseOk =
    leasePoll.isError &&
    leasePoll.json?.error === 'EVENTS_LEASE_EXPIRED' &&
    leaseStatus.json?.active_subscriptions === 0 &&
    leaseStatus.json?.registered_handlers === 0;

  // 2. Target gone (EVENTS_TARGET_GONE)
  const tempFix = startFixture('OrcaUiaTempGone');
  const tempInfo = await tempFix.ready();
  const goneSub = await mcp.callTool('app_events', {
    action: 'subscribe',
    window: { pid: tempInfo.pid },
  });
  await tempFix.close();
  await sleep(300);
  const gonePoll = await mcp.callTool('app_events', {
    action: 'poll',
    since: goneSub.json.cursor,
  });
  const goneStatus = await mcp.callTool('app_events', { action: 'status' });
  const targetGoneOk =
    gonePoll.isError &&
    gonePoll.json?.error === 'EVENTS_TARGET_GONE' &&
    goneStatus.json?.active_subscriptions === 0 &&
    goneStatus.json?.registered_handlers === 0;

  // 3. Two concurrent subscriptions negative test
  const subA = await mcp.callTool('app_events', {
    action: 'subscribe',
    window: { pid: info.pid },
    debounce_ms: 30,
  });
  const subB = await mcp.callTool('app_events', {
    action: 'subscribe',
    window: { pid: info.pid },
    debounce_ms: 30,
  });
  await mcp.callTool('app_events', {
    action: 'unsubscribe',
    subscription_id: subA.json.subscription_id,
  });
  fixture.send('rename TwoSubCheck');
  await sleep(200);
  const pollB = await mcp.callTool('app_events', {
    action: 'poll',
    since: subB.json.cursor,
  });
  const pollA = await mcp.callTool('app_events', {
    action: 'poll',
    since: subA.json.cursor,
  });
  await mcp.callTool('app_events', {
    action: 'unsubscribe',
    subscription_id: subB.json.subscription_id,
  });
  const concurrentIsoOk =
    !pollB.isError &&
    Array.isArray(pollB.json?.events) &&
    pollB.json.events.length > 0 &&
    pollA.isError &&
    pollA.json?.error === 'EVENTS_UNSUBSCRIBED';

  await mcp.close();

  // 4. Partial registration failure rollback (acceptance-only fault injection env var)
  const failMcp = new McpClient(candidateExe, { ORCA_UIA_TEST_INJECT_SUBSCRIBE_FAIL_STEP: '2' });
  await failMcp.initialize();
  const failSub = await failMcp.callTool('app_events', {
    action: 'subscribe',
    window: { pid: info.pid },
  });
  const failStatus = await failMcp.callTool('app_events', { action: 'status' });
  await failMcp.close();
  const partialRollbackOk =
    failSub.isError &&
    failStatus.json?.active_subscriptions === 0 &&
    failStatus.json?.registered_handlers === 0;

  // 5. 10-tool list & old tool compatibility vs baseline
  let compatOk = true;
  let compatDetails = {};
  if (baselineExe) {
    const baseClient = new McpClient(baselineExe);
    const candClient = new McpClient(candidateExe);
    await baseClient.initialize();
    await candClient.initialize();

    const baseTools = await baseClient.request('tools/list', {});
    const candTools = await candClient.request('tools/list', {});
    const baseToolNames = baseTools.result.tools.map((t) => t.name);
    const candToolNames = candTools.result.tools.map((t) => t.name);
    const toolsMatch =
      baseToolNames.length === 10 &&
      JSON.stringify(baseTools.result.tools) === JSON.stringify(candTools.result.tools);

    const bAttach = await baseClient.callTool('app_attach', { pid: info.pid });
    const cAttach = await candClient.callTool('app_attach', { pid: info.pid });
    const attachMatch =
      JSON.stringify(stripTiming(bAttach.json)) === JSON.stringify(stripTiming(cAttach.json));

    const bSnap = await baseClient.callTool('app_snapshot', {});
    const cSnap = await candClient.callTool('app_snapshot', {});
    const snapMatch =
      stripSnapshotHeaderTiming(bSnap.text) === stripSnapshotHeaderTiming(cSnap.text);

    const bWarm = await baseClient.callTool('app_warmup', { min_nodes: 1, timeout_ms: 2000 });
    const cWarm = await candClient.callTool('app_warmup', { min_nodes: 1, timeout_ms: 2000 });
    const warmMatch =
      JSON.stringify(stripTiming(bWarm.json)) === JSON.stringify(stripTiming(cWarm.json));

    const bWin = await baseClient.callTool('app_window', { action: 'list', pid: info.pid });
    const cWin = await candClient.callTool('app_window', { action: 'list', pid: info.pid });
    const winMatch =
      JSON.stringify(stripTiming(bWin.json)) === JSON.stringify(stripTiming(cWin.json));

    await baseClient.close();
    await candClient.close();

    compatOk = toolsMatch && attachMatch && snapMatch && warmMatch && winMatch;
    compatDetails = {
      toolCount: candToolNames.length,
      toolsMatch,
      attachMatch,
      snapMatch,
      warmMatch,
      winMatch,
    };
  }

  await fixture.close();
  return {
    leaseOk,
    leaseError: leasePoll.json?.error,
    targetGoneOk,
    targetGoneError: gonePoll.json?.error,
    concurrentIsoOk,
    partialRollbackOk,
    compatOk,
    compatDetails,
    allPassed: leaseOk && targetGoneOk && concurrentIsoOk && partialRollbackOk && compatOk,
  };
}

async function main() {
  const args = parseArgs(process.argv);
  const baselineExe = args['baseline-exe'] || null;
  const candidateExe = args['candidate-exe'] || null;
  const cycles = Number(args.cycles ?? 10);
  const cooldownMs = Number(args['cooldown-ms'] ?? 30000);
  const sampleMs = Number(args['sample-ms'] ?? 1000);
  const warmupMs = Number(args['warmup-ms'] ?? 30000);
  const pairsCount = Number(args.pairs ?? 4);
  const stormCount = Number(args.storm ?? 500);
  const stormWindows = Number(args['storm-windows'] ?? 10);
  const outDir = args.out || '.';

  fs.mkdirSync(outDir, { recursive: true });
  const outPath = path.join(outDir, 'handles-report.json');

  const opts = { cycles, cooldownMs, sampleMs, warmupMs, pairsCount, stormCount, stormWindows };
  const pairOrders = ['A0->cand', 'cand->A0', 'cand->A0', 'A0->cand'];

  const report = {
    startedAt: new Date().toISOString(),
    opts,
    binaries: {
      baselineExe,
      baselineExeSha256: sha256File(baselineExe),
      baselineDllSha256: baselineExe ? sha256File(path.join(path.dirname(baselineExe), 'orca-uia.dll')) : null,
      candidateExe,
      candidateExeSha256: sha256File(candidateExe),
      candidateDllSha256: candidateExe ? sha256File(path.join(path.dirname(candidateExe), 'orca-uia.dll')) : null,
    },
    pairs: [],
    eventStorm: null,
    killWindows: null,
    edgeAndProtocol: null,
    summary: null,
  };

  if (baselineExe && candidateExe && pairsCount > 0) {
    for (let p = 1; p <= pairsCount; p++) {
      const order = pairOrders[(p - 1) % pairOrders.length];
      console.log(`[A1] === Pair ${p}/${pairsCount} (${order}) ===`);
      let a0, cand;
      if (order === 'A0->cand') {
        a0 = await runSingleBinaryRegression(`p${p}_A0`, baselineExe, opts);
        console.log(`[A1] Pair ${p} A0: B0=${a0.B0}(span=${a0.b0Span}) W0=${a0.W0}(span=${a0.w0Span}) P=${a0.P} passedR1=${a0.passedR1} (${a0.passedCycles}/${cycles})`);
        cand = await runSingleBinaryRegression(`p${p}_cand`, candidateExe, opts);
        console.log(`[A1] Pair ${p} Cand: B0=${cand.B0}(span=${cand.b0Span}) W0=${cand.W0}(span=${cand.w0Span}) P=${cand.P} passedR1=${cand.passedR1} (${cand.passedCycles}/${cycles})`);
      } else {
        cand = await runSingleBinaryRegression(`p${p}_cand`, candidateExe, opts);
        console.log(`[A1] Pair ${p} Cand: B0=${cand.B0}(span=${cand.b0Span}) W0=${cand.W0}(span=${cand.w0Span}) P=${cand.P} passedR1=${cand.passedR1} (${cand.passedCycles}/${cycles})`);
        a0 = await runSingleBinaryRegression(`p${p}_A0`, baselineExe, opts);
        console.log(`[A1] Pair ${p} A0: B0=${a0.B0}(span=${a0.b0Span}) W0=${a0.W0}(span=${a0.w0Span}) P=${a0.P} passedR1=${a0.passedR1} (${a0.passedCycles}/${cycles})`);
      }
      const evaluation = evaluatePair(p, order, a0, cand);
      console.log(`[A1] Pair ${p} eval: valid=${evaluation.pairValid} passed=${evaluation.pairPassed} dW0=${evaluation.dW0} dW0Cost=${evaluation.dW0Cost} dP=${evaluation.tailPlateauDiff.dP} dPCost=${evaluation.tailPlateauDiff.dPCost} firstExceed=${evaluation.firstExceedingCycle}`);
      report.pairs.push({ evaluation, baseline: a0, candidate: cand });
      fs.writeFileSync(outPath, JSON.stringify(report, null, 2), 'utf8');
    }
  } else if (candidateExe) {
    console.log(`[A1] Running standalone candidate: ${candidateExe}`);
    const cand = await runSingleBinaryRegression('cand_only', candidateExe, opts);
    report.pairs.push({ evaluation: { pairValid: true, pairPassed: cand.passedR1 }, baseline: null, candidate: cand });
  }

  if (candidateExe && stormCount > 0) {
    console.log(`[A1] === Scenario 4a: Event Storm (>=${stormCount} events) ===`);
    const baseStorm = baselineExe ? await runEventStormScenario('A0', baselineExe, opts) : null;
    const candStorm = await runEventStormScenario('cand', candidateExe, opts);
    const stormVsBaseOk = !baseStorm || (
      candStorm.delta_handles_vs_W0 - baseStorm.delta_handles_vs_W0 <= HANDLE_BAND &&
      candStorm.delta_priv_vs_W0 - baseStorm.delta_priv_vs_W0 <= PRIV_LIMIT_BYTES &&
      candStorm.cooldownTail3.medianPrivateBytes - baseStorm.cooldownTail3.medianPrivateBytes <= PRIV_LIMIT_BYTES &&
      candStorm.delta_threads_vs_W0 - baseStorm.delta_threads_vs_W0 <= THR_LIMIT
    );
    report.eventStorm = {
      baseline: baseStorm,
      candidate: candStorm,
      stormVsBaseOk,
      passed: candStorm.passed && stormVsBaseOk,
    };
    console.log(`[A1] Event Storm cand: raw=${candStorm.raw_events} obs=${candStorm.observed_events} dH=${candStorm.delta_handles_vs_W0} hSlope=${candStorm.handleSlopePerSec.toFixed(4)}/s privSlope=${(candStorm.privSlopeBytesPerSec / 1024).toFixed(2)}KB/s passed=${report.eventStorm.passed}`);
    fs.writeFileSync(outPath, JSON.stringify(report, null, 2), 'utf8');
  }

  if (candidateExe && stormWindows > 0) {
    console.log(`[A1] === Scenario 4b: Kill Windows (${stormWindows} windows) ===`);
    const baseKill = baselineExe ? await runKillWindowsScenario('A0', baselineExe, opts) : null;
    const candKill = await runKillWindowsScenario('cand', candidateExe, opts);
    const slopeVsBaseOk = !baseKill || (
      candKill.handleSlopePerWindow <= baseKill.handleSlopePerWindow + 0.5 &&
      candKill.privSlopeBytesPerWindow <= baseKill.privSlopeBytesPerWindow + 512 * 1024
    );
    report.killWindows = {
      baseline: baseKill,
      candidate: candKill,
      slopeVsBaseOk,
      passed: candKill.passed && slopeVsBaseOk,
    };
    console.log(`[A1] Kill Windows cand: goneOk=${candKill.allTargetGoneOk} hSlope=${candKill.handleSlopePerWindow.toFixed(3)}/win (base=${baseKill ? baseKill.handleSlopePerWindow.toFixed(3) : 'n/a'}) privSlope=${(candKill.privSlopeBytesPerWindow / 1024).toFixed(1)}KB/win passed=${report.killWindows.passed}`);
    fs.writeFileSync(outPath, JSON.stringify(report, null, 2), 'utf8');
  }

  if (candidateExe) {
    console.log('[A1] === Scenario 4c: Edge & Protocol Compatibility ===');
    report.edgeAndProtocol = await runEdgeAndProtocolChecks(baselineExe, candidateExe);
    console.log('[A1] Edge & protocol:', JSON.stringify(report.edgeAndProtocol));
  }

  const allPairsPassed = report.pairs.length > 0 && report.pairs.every((p) => p.evaluation.pairPassed);
  const stormPassed = !report.eventStorm || report.eventStorm.passed;
  const killPassed = !report.killWindows || report.killWindows.passed;
  const edgePassed = !report.edgeAndProtocol || report.edgeAndProtocol.allPassed;
  const overallPassed = allPairsPassed && stormPassed && killPassed && edgePassed;

  report.summary = {
    overallPassed,
    allPairsPassed,
    stormPassed,
    killPassed,
    edgePassed,
    pairSummaries: report.pairs.map((p) => ({
      pairIndex: p.evaluation.pairIndex,
      order: p.evaluation.order,
      pairValid: p.evaluation.pairValid,
      pairPassed: p.evaluation.pairPassed,
      a0_W0_minus_B0: p.evaluation.a0_W0_minus_B0,
      cand_W0_minus_B0: p.evaluation.cand_W0_minus_B0,
      dW0: p.evaluation.dW0,
      dW0Cost: p.evaluation.dW0Cost,
      dP: p.evaluation.tailPlateauDiff?.dP,
      dPCost: p.evaluation.tailPlateauDiff?.dPCost,
      dPriv_W0: p.evaluation.dPriv_W0,
      dThr_W0: p.evaluation.dThr_W0,
      firstExceedingCycle: p.evaluation.firstExceedingCycle,
    })),
  };

  report.finishedAt = new Date().toISOString();
  fs.writeFileSync(outPath, JSON.stringify(report, null, 2), 'utf8');
  console.log(`[A1] Report written to ${outPath}`);
  console.log(`[A1] OVERALL PASSED: ${overallPassed}`);

  if (!overallPassed) {
    process.exitCode = 1;
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
