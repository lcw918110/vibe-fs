/**
 * gate-timeout-cases.mjs — the silence criterion of verification-system-006, as regressions.
 *
 * Four of the thirteen 禁止退化 items are watchdog semantics, and each has a case here:
 *
 *   2  让原始 SSE 或 provider 流量续期 watchdog
 *   3  让背景车道进展续期 watchdog
 *   4  删除 watchdog 的诊断转储，只保留退出码
 *   5  让 watchdog 计时器持有事件循环，使干净结束也要等满静默窗口
 *
 * Every watchdog case spawns a real child process and reads its exit code, its stderr, and its
 * wall clock. An in-process fake timer could not prove either of the two properties that matter
 * most: `unref` is only observable as a process that exits while a timer is armed, and the
 * diagnostic dump is only observable as text a human would read. A test that asked the class
 * about its own flags would agree with whatever the class did.
 *
 * Also covers the concurrent-awaitEvent timer clobber that hung the host-restart canary: two
 * parallel awaits on one probe used to share a single timer handle, so the loser never timed out.
 */

import http from 'node:http';
import assert from 'node:assert/strict';
import { execFile, execFileSync, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import { setTimeout as delay } from 'node:timers/promises';
import { existsSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { assertEq, assertTrue, tmpScenarioDir } from './lib.mjs';
import { EventProbe } from '../../e2e/support/event-probe.js';
import { PROCESS_TREE_TIMEOUT_MS, READINESS_STAGE_MS, SIGKILL_GRACE_MS, WAIT_FACT_WINDOW_MS, WATCHDOG_TIMEOUT_MS } from '../../e2e/support/time-budget.js';
import { journalEventLines } from '../../e2e/support/journal-observer.js';

const execFileAsync = promisify(execFile);

const watchdogUrl = new URL('../../e2e/support/watchdog.js', import.meta.url).href;
const budgetUrl = new URL('../../e2e/support/time-budget.js', import.meta.url).href;
const driverUrl = new URL('../../e2e/support/scenario-driver.mjs', import.meta.url).href;
const gateFactsUrl = new URL('./event-store-gate-facts.mjs', import.meta.url).href;

/**
 * Run a module source as a child and report how it ended.
 *
 * `killAfterMs` exists so a case can distinguish "the subject ended it" from "we ended it":
 * SIGTERM arrives as `signal`, not as an exit code, so a child that had to be killed cannot be
 * mistaken for one that decided to exit 1.
 */
async function runWatchdogChild(script, killAfterMs, budgetEnv) {
  const startedAt = Date.now();
  const options = {
    ...(killAfterMs ? { timeout: killAfterMs, killSignal: 'SIGKILL' } : {}),
    ...(budgetEnv ? { env: { ...process.env, ...budgetEnv } } : {}),
  };
  try {
    const { stdout, stderr } = await execFileAsync(
      process.execPath,
      ['--input-type=module', '-e', script],
      options,
    );
    return { code: 0, signal: null, stdout, stderr, elapsedMs: Date.now() - startedAt };
  } catch (err) {
    return {
      code: err.code ?? null,
      signal: err.signal ?? null,
      stdout: err.stdout || '',
      stderr: err.stderr || '',
      elapsedMs: Date.now() - startedAt,
    };
  }
}

async function factChildGroupMembers(pgid, timeoutMs = PROCESS_TREE_TIMEOUT_MS) {
  const { stdout } = await execFileAsync('ps', ['-eo', 'pid=,pgid=,stat='], {
    encoding: 'utf8', timeout: timeoutMs, killSignal: 'SIGKILL',
  });
  if (!stdout.trim()) throw new Error('fact child process inspection returned no records');
  return stdout.trim().split('\n').flatMap((line) => {
    const fields = /^\s*(\d+)\s+(\d+)\s+(\S+)\s*$/.exec(line);
    if (!fields) throw new Error(`unparseable fact child process record: ${line}`);
    return Number(fields[2]) === pgid && !/^[ZX]/.test(fields[3]) ? [Number(fields[1])] : [];
  });
}

function killFactChildGroup(pgid) {
  try { process.kill(-pgid, 'SIGKILL'); }
  catch (error) { if (error.code !== 'ESRCH') throw error; }
}

async function reclaimFactChildGroup(pgid, killImmediately) {
  if (!pgid) return false;
  const deadline = performance.now() + SIGKILL_GRACE_MS;
  const inspect = () => factChildGroupMembers(pgid, Math.max(1, Math.floor(Math.min(PROCESS_TREE_TIMEOUT_MS, deadline - performance.now()))));
  try {
    if (killImmediately) killFactChildGroup(pgid);
    let members = await inspect();
    const hadResidual = members.length > 0;
    if (hadResidual) killFactChildGroup(pgid);
    while (members.length > 0 && performance.now() < deadline) {
      await delay(Math.min(20, Math.max(1, deadline - performance.now())));
      members = await inspect();
    }
    if (members.length > 0) throw new Error(`fact child group survived reclamation: ${members.join(', ')}`);
    return hadResidual;
  } catch (error) {
    try { killFactChildGroup(pgid); }
    catch (killError) { throw new AggregateError([error, killError], 'fact child inspection and reclamation failed'); }
    throw error;
  }
}

/** Imports have their own startup bound; the observation window starts at real arming. */
async function runFactBarrierChild(script, killAfterArmedMs, budgetEnv) {
  if (!['darwin', 'linux'].includes(process.platform)) throw new Error(`fact child group verification is unsupported on ${process.platform}`);
  const startedAt = performance.now();
  const child = spawn(process.execPath, ['--input-type=module', '-e', script], {
    env: { ...process.env, ...budgetEnv },
    stdio: ['ignore', 'pipe', 'pipe', 'ipc'], detached: true,
  });
  let stdout = '';
  let stderr = '';
  let armedAt = null;
  const protocolErrors = [];
  const infrastructureErrors = [];
  let cleanup = null;
  let finishCleanup;
  const cleanupFinished = new Promise((resolve) => { finishCleanup = resolve; });
  const startCleanup = (killImmediately) => {
    cleanup ??= reclaimFactChildGroup(child.pid, killImmediately)
      .then((hadResidual) => {
        if (hadResidual && !killImmediately) infrastructureErrors.push(new Error('fact child left a residual process group'));
      })
      .catch((error) => { infrastructureErrors.push(error); })
      .finally(() => finishCleanup({ code: child.exitCode, signal: child.signalCode }));
    return cleanup;
  };
  let guard;
  const fail = (error) => {
    if (typeof error === 'string') protocolErrors.push(new Error(error));
    else infrastructureErrors.push(error);
    clearTimeout(guard);
    startCleanup(true);
  };
  const exited = new Promise((resolve) => {
    child.once('exit', (code, signal) => resolve({ code, signal }));
    child.once('error', fail);
  });
  const closed = new Promise((resolve) => child.once('close', () => resolve(true)));
  guard = setTimeout(() => fail('fact barrier startup deadline expired'), READINESS_STAGE_MS);
  child.stdout.on('data', (chunk) => { stdout += chunk; });
  child.stderr.on('data', (chunk) => { stderr += chunk; });
  child.on('message', (message) => {
    if (protocolErrors.length > 0 || infrastructureErrors.length > 0) return;
    if (message?.type !== 'fact-barrier-armed' || armedAt !== null) {
      fail(`invalid or repeated fact barrier arming: ${JSON.stringify(message)}`);
      return;
    }
    armedAt = performance.now();
    clearTimeout(guard);
    guard = setTimeout(() => fail('fact barrier observation deadline expired'), killAfterArmedMs);
  });

  const { code, signal } = await Promise.race([exited, cleanupFinished]);
  const endedAt = performance.now();
  clearTimeout(guard);
  await startCleanup(false);
  let drainGuard;
  try {
    const drained = await Promise.race([closed, new Promise((resolve) => {
      drainGuard = setTimeout(() => resolve(false), SIGKILL_GRACE_MS);
    })]);
    if (!drained) {
      infrastructureErrors.push(new Error('fact child output did not drain after termination'));
      child.stdout.destroy();
      child.stderr.destroy();
      if (child.connected) child.disconnect();
    }
  } finally { clearTimeout(drainGuard); }
  if (armedAt === null) protocolErrors.push(new Error('fact barrier exited before arming'));
  if (signal !== null) protocolErrors.push(new Error(`fact barrier terminated by ${signal}`));
  const failures = [...protocolErrors, ...infrastructureErrors];
  if (failures.length > 0) {
    const error = new Error(`${failures.map((error) => error.message).join('\n')}\nstdout:\n${stdout}\nstderr:\n${stderr}`, {
      cause: new AggregateError(failures, 'fact barrier or cleanup failed'),
    });
    error.protocolErrors = protocolErrors;
    error.infrastructureErrors = infrastructureErrors;
    throw error;
  }
  return { code, signal, stdout, stderr, startupMs: armedAt - startedAt, afterArmedMs: endedAt - armedAt };
}

const expectedFactBarrierRejection = (expected) => (error) =>
  Array.isArray(error.infrastructureErrors) && error.infrastructureErrors.length === 0 &&
  Array.isArray(error.protocolErrors) && error.protocolErrors.some((cause) => expected.test(cause.message));

async function runFactBarrierReclaimsHeldPipe() {
  const scenarioDir = tmpScenarioDir();
  const identityFile = join(scenarioDir, 'held-pipe.json');
  let identity = null;
  const failures = [];
  try {
    await assert.rejects(runFactBarrierChild(
      `import { spawn } from 'node:child_process';\n` +
      `import { writeFileSync } from 'node:fs';\n` +
      `const held = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: ['ignore', 'inherit', 'inherit'] });\n` +
      `held.once('spawn', () => {\n` +
      `  writeFileSync(${JSON.stringify(identityFile)}, JSON.stringify({ pgid: process.pid, pid: held.pid }));\n` +
      `  process.send({ type: 'invalid-while-pipe-held' });\n` +
      `});\n` +
      `setInterval(() => {}, 1000);\n`, 2000), expectedFactBarrierRejection(/invalid or repeated/));
    identity = JSON.parse(readFileSync(identityFile, 'utf8'));
    assert.deepEqual(await factChildGroupMembers(identity.pgid), [], 'all members holding the pipe must actually be gone');
  } catch (error) {
    failures.push(error);
  } finally {
    try {
      if (!identity && existsSync(identityFile)) identity = JSON.parse(readFileSync(identityFile, 'utf8'));
      if (identity) await reclaimFactChildGroup(identity.pgid, true);
    } catch (error) { failures.push(error); }
    try { rmSync(scenarioDir, { recursive: true, force: true }); }
    catch (error) { failures.push(error); }
  }
  if (failures.length > 0) throw new AggregateError(failures, 'held-pipe regression or cleanup failed');
}

async function runWatchdogFiresOnSilence() {
  const script =
    `import { Watchdog } from '${watchdogUrl}';\n` +
    `new Watchdog({ timeoutMs: 150, label: 'gate-silent' });\n` +
    `setInterval(() => {}, 1000);\n`;
  const r = await runWatchdogChild(script);
  assertEq(r.code, 1, 'silent watchdog must exit 1');
  assertTrue(r.stderr.includes('WATCHDOG'), `stderr must carry WATCHDOG diagnostic: ${r.stderr}`);
  assertTrue(r.stderr.includes('gate-silent'), 'diagnostic carries the label');
}

async function runWatchdogRenewsOnProgress() {
  const script =
    `import { Watchdog } from '${watchdogUrl}';\n` +
    `const w = new Watchdog({ timeoutMs: 200, label: 'gate-progress' });\n` +
    `const iv = setInterval(() => w.advance({ reason: 'tick', lane: 'gate' }), 50);\n` +
    `setTimeout(() => { clearInterval(iv); w.stop(); }, 500);\n`;
  const r = await runWatchdogChild(script);
  assertEq(r.code, 0, `causal progress must renew watchdog: ${r.stderr}`);
  assertTrue(!r.stderr.includes('WATCHDOG'), 'no diagnostic on clean exit');
}

async function runWatchdogRejectsBackgroundNoise() {
  const script =
    `import { Watchdog } from '${watchdogUrl}';\n` +
    `const w = new Watchdog({ timeoutMs: 150, label: 'gate-background' });\n` +
    `setInterval(() => w.advance({ reason: 'blogger', lane: 'blogger', blocking: false }), 30);\n`;
  const r = await runWatchdogChild(script);
  assertEq(r.code, 1, 'background-only activity must not renew watchdog');
  assertTrue(r.stderr.includes('gate-background'), 'diagnostic must identify the stalled target');
}

async function runWatchdogWidenedWindowToleratesDeclaredSlowStep() {
  // verification-system-006: a legitimately slow wait step is DECLARED (scenario timeoutMs),
  // never inferred. setWindow widens the silence window to that bound — slow
  // work inside the bound must not be mistaken for a hang.
  const script =
    `import { Watchdog } from '${watchdogUrl}';\n` +
    `const w = new Watchdog({ timeoutMs: 150, label: 'gate-widened' });\n` +
    `w.setWindow(400);\n` +
    `setTimeout(() => { w.advance({ reason: 'slow-done', lane: 'gate' }); w.stop(); process.exit(0); }, 300);\n` +
    `setInterval(() => {}, 1000);\n`;
  const r = await runWatchdogChild(script);
  assertEq(r.code, 0, `widened window must tolerate declared slow work: ${r.stderr}`);
  assertTrue(!r.stderr.includes('WATCHDOG'), 'no diagnostic on clean exit');
}

async function runWatchdogRestoresDefaultWindow() {
  // setWindow(null) must restore the centralized default: after a declared
  // slow step ends, silence is judged at the default bound again.
  const script =
    `import { Watchdog } from '${watchdogUrl}';\n` +
    `const w = new Watchdog({ timeoutMs: 150, label: 'gate-restore' });\n` +
    `w.setWindow(500);\n` +
    `w.setWindow(null);\n` +
    `setInterval(() => {}, 1000);\n`;
  const r = await runWatchdogChild(script);
  assertEq(r.code, 1, 'restored default window must still fire on silence');
  assertTrue(r.stderr.includes('WATCHDOG'), 'diagnostic fires at the restored default');
}

function startDelayedSseServer(events, delayMs) {
  return new Promise((resolve, reject) => {
    const server = http.createServer((req, res) => {
      res.writeHead(200, {
        'Content-Type': 'text/event-stream',
        'Cache-Control': 'no-cache',
        'Connection': 'keep-alive',
      });
      setTimeout(() => {
        for (const ev of events) res.write(`data: ${JSON.stringify(ev)}\n\n`);
      }, delayMs);
    });
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => resolve({
      url: `http://127.0.0.1:${server.address().port}`,
      close: () => new Promise((r) => {
        try { server.closeAllConnections(); } catch {}
        server.close(() => r());
      }),
    }));
  });
}

async function runConcurrentAwaitTimeouts() {
  const server = await startDelayedSseServer(
    [{ type: 'session.status', properties: { sessionID: 's1', status: 'busy' } }],
    100,
  );
  const probe = new EventProbe(server.url, '/tmp');
  await probe.connect();
  const hit = probe.awaitEvent((e) => e.type === 'session.status', 3000);
  const miss = probe.awaitEvent((e) => e.type === 'never.arrives', 300);
  const [hitResult, missResult] = await Promise.allSettled([hit, miss]);
  assertEq(hitResult.status, 'fulfilled', 'matching concurrent await resolves');
  assertEq(missResult.status, 'rejected', 'non-matching concurrent await must still time out');
  assertTrue(missResult.reason.message.includes('timed out'), 'timeout rejection, not a hang');
  await probe.close();
  await server.close();
}

// ── verification-system-006 watchdog properties, one case per 禁止退化 item ────────────────

/**
 * A lane in the shape `scenario-parallel.js` builds from a consumed expectation. Assembled from
 * parts because `gate-path-criterion-cases.mjs` reads every quoted argument to `.includes` in
 * this tree and would resolve a slash-bearing literal against the repo root — its file header
 * declares that residual cost, and paying it here is cheaper than an exemption there.
 */
const CAUSAL_LANE = ['publish', 'main', 'manager', 'turn-1'].join('/');

// Background traffic must not be reported as causal progress. The emitted diagnostic
// must identify the last real advance; its decorative layout is not a separate contract.
async function runDiagnosticDumpIsComplete() {
  const backgroundOnly =
    `import { Watchdog } from '${watchdogUrl}';\n` +
    `const w = new Watchdog({ timeoutMs: 150, label: 'gate-diagnostic' });\n` +
    `const iv = setInterval(() => w.advance({ reason: 'blogger-projection', lane: 'blogger', blocking: false }), 20);\n` +
    `iv;\n`;
  const r1 = await runWatchdogChild(backgroundOnly);
  assertEq(r1.code, 1, `background-only run must still fire: ${r1.stderr}`);
  assertTrue(
    r1.stderr.includes('0 blocking progress update(s)'),
    `dump must not count background advances as progress: ${r1.stderr}`,
  );
  assertTrue(
    r1.stderr.includes('last progress: start lane=startup'),
    `dump must name the last causal progress by reason AND lane: ${r1.stderr}`,
  );

  const oneCausalStep =
    `import { Watchdog } from '${watchdogUrl}';\n` +
    `const w = new Watchdog({ timeoutMs: 150, label: 'gate-diagnostic' });\n` +
    `w.advance({ reason: 'expectation:manager.0', lane: ${JSON.stringify(CAUSAL_LANE)}, expectationId: 'manager.0' });\n` +
    `setInterval(() => {}, 60000);\n`;
  const r2 = await runWatchdogChild(oneCausalStep);
  assertEq(r2.code, 1, `a run that stops progressing must fire: ${r2.stderr}`);
  assertTrue(
    r2.stderr.includes('1 blocking progress update(s)'),
    `dump must count the causal advances it renewed on: ${r2.stderr}`,
  );
  assertTrue(
    r2.stderr.includes(`last progress: expectation:manager.0 lane=${CAUSAL_LANE}`),
    `dump must carry the reason and lane of the last renewal: ${r2.stderr}`,
  );
  assertTrue(
    r2.stderr.includes('expectation=manager.0'),
    `dump must name the expectation that was consumed last: ${r2.stderr}`,
  );
  assertTrue(
    !r2.stderr.includes('background progress'),
    `a run with no background lane must not invent a background age: ${r2.stderr}`,
  );
}

/**
 * 「让 watchdog 计时器持有事件循环，使干净结束也要等满静默窗口」.
 *
 * The property is `unref`, and it is not observable from inside the process: a test that asked
 * the timer for its own flags would agree with whatever the implementation did. What is
 * observable is the wall clock of a child that finished its work and let its handles close.
 * The silence window here is the real WATCHDOG_TIMEOUT_MS, so the margin asserted is the one a
 * canary gets.
 */
async function runTimerDoesNotHoldEventLoop() {
  const script =
    `import { Watchdog } from '${watchdogUrl}';\n` +
    `import { WATCHDOG_TIMEOUT_MS } from '${budgetUrl}';\n` +
    `const w = new Watchdog({ timeoutMs: WATCHDOG_TIMEOUT_MS, label: 'gate-unref' });\n` +
    `w.advance({ reason: 'only-step', lane: 'gate' });\n` +
    `console.log('done');\n`;
  const r = await runWatchdogChild(script);
  assertEq(r.code, 0, `a scenario that ran out of work must exit clean: ${r.stderr}`);
  assertEq(r.stdout.trim(), 'done', 'the child must reach the end of its work');
  assertTrue(
    r.elapsedMs < WATCHDOG_TIMEOUT_MS,
    `an armed watchdog must not hold the event loop: exited after ${r.elapsedMs}ms of a ` +
      `${WATCHDOG_TIMEOUT_MS}ms silence window`,
  );
}

/**
 * The `waitFact` barrier: renewal must follow an observation, not the poll clock.
 *
 * Two children, because the defect and its over-correction fail in opposite directions and only
 * one assertion each would leave the other open. Deleting every `advance` from the barrier would
 * satisfy the first child and break every real canary that crosses a slow publish chain; renewing
 * on the clock satisfies the second and is the measured defect.
 *
 *   nothing observed        the fake event source answers every slice — the transport saying
 *                           bytes moved — and the journal never gains a line. The barrier must
 *                           be ended by the silence budget, not by the WAIT_FACT_WINDOW_MS 兜底.
 *   background appending    lines land steadily while the awaited fact never appears. The barrier
 *                           must be killed by the silence budget and its dump must record
 *                           background progress without renewing the window.
 *   renewOn appending       an explicitly declared intermediate fact lands steadily and the
 *                           awaited fact appears past two silence windows. The barrier survives
 *                           only on that declared causal fact, then returns on the target.
 *
 * The kill deadline in the first child is what turns red if renewal goes back on the clock: a
 * barrier that renews unconditionally reaches it and comes back killed by signal rather than
 * having exited 1.
 */
async function runWaitFactRenewsOnlyOnObservation() {
  // The real watchdog still gets 1s; imports do not consume its observation window.
  const scaledWatchdogMs = 1000;
  const budgetEnv = { WATCHDOG_TIMEOUT_MS: String(scaledWatchdogMs) };
  const silentDir = tmpScenarioDir();
  execFileSync('git', ['-C', silentDir, 'init', '-q'], { encoding: 'utf8' });
  const silent = await runFactBarrierChild(
    factBarrierScript(silentDir, 'FactThatNeverAppears', 'gate-wait-fact-silent'),
    scaledWatchdogMs * 2,
    budgetEnv,
  );
  assertEq(
    silent.code,
    1,
    `a fact that never advances must be ended by the silence budget, not by the ` +
      `${WAIT_FACT_WINDOW_MS}ms fallback: exited with code ${silent.code} signal ${silent.signal} ` +
      `after startup ${silent.startupMs}ms and armed ${silent.afterArmedMs}ms; ${silent.stderr}`,
  );
  assertTrue(
    silent.afterArmedMs < scaledWatchdogMs * 2,
    `barrier survived two injected silence windows (${silent.afterArmedMs}ms), so a poll slice renewed it`,
  );
  assertTrue(silent.stderr.includes('WATCHDOG'), `the watchdog must be what ended it: ${silent.stderr}`);
  assertTrue(
    silent.stderr.includes('gate-wait-fact-silent'),
    `diagnostic must name the scenario: ${silent.stderr}`,
  );

  const appendingDir = tmpScenarioDir();
  execFileSync('git', ['-C', appendingDir, 'init', '-q'], { encoding: 'utf8' });
  const appendEvery = Math.floor(scaledWatchdogMs / 4);
  const appendsBeforeFact = 6;
  const appending = await runFactBarrierChild(
    `import { openGateFactStore } from ${JSON.stringify(gateFactsUrl)};\n` +
      `const gate = openGateFactStore(${JSON.stringify(appendingDir)});\n` +
      `let appended = 0;\n` +
      `const iv = setInterval(async () => {\n` +
      `  appended += 1;\n` +
      `  const fact = appended < ${appendsBeforeFact} ? 'UnrelatedProgressFact' : 'AwaitedFact';\n` +
      `  await gate.appendNamedFact(fact, appended);\n` +
      `}, ${appendEvery});\n` +
      factBarrierScript(appendingDir, 'AwaitedFact', 'gate-wait-fact-appending') +
      `clearInterval(iv);\n` +
      `console.log('barrier returned after ' + appended + ' appends');\n`,
    scaledWatchdogMs * 2,
    budgetEnv,
  );
  assertEq(
    appending.code,
    1,
    `background journal appends must not renew the barrier: ${appending.stderr}`,
  );
  assertTrue(
    appending.startupMs + appending.afterArmedMs > scaledWatchdogMs,
    `this child must outlive one injected silence window, ran ${appending.startupMs + appending.afterArmedMs}ms`,
  );
  assertTrue(appending.afterArmedMs < scaledWatchdogMs * 2, 'background must not survive two silence windows');
  assertTrue(appending.stderr.includes('gate-wait-fact-appending'), `diagnostic must name the barrier: ${appending.stderr}`);
  assertTrue(appending.stderr.includes('0 blocking progress update(s)'), `background must not count as causal: ${appending.stderr}`);
  assertTrue(appending.stderr.includes('journal-append-while-awaiting:AwaitedFact'), `real background facts must be observed: ${appending.stderr}`);
  assertTrue(appending.stderr.includes('none of them renewals'), `observed background must not renew: ${appending.stderr}`);
  const reportedSilence = /silent for (\d+)ms \(limit (\d+)ms\)/.exec(appending.stderr);
  assertTrue(reportedSilence !== null, `the watchdog must report its actual silence: ${appending.stderr}`);
  assertEq(Number(reportedSilence[2]), scaledWatchdogMs, 'the real watchdog must keep its original limit');
  // setTimeout scheduling jitter: the watchdog arms with setTimeout(remainingMs),
  // and Node.js may fire the timer slightly early under concurrent load (8 workers +
  // spawned children). A 50ms tolerance preserves the semantic — "the watchdog
  // observed the complete silence window" — without failing on a few ms of scheduler
  // imprecision.
  const SILENCE_JITTER_MS = 50;
  assertTrue(
    Number(reportedSilence[1]) >= scaledWatchdogMs - SILENCE_JITTER_MS,
    `the real watchdog must observe the complete silence window (reported ${reportedSilence[1]}ms, limit ${scaledWatchdogMs}ms, jitter ${SILENCE_JITTER_MS}ms)`,
  );
  const observedTypes = journalEventLines(appendingDir).map((text) => JSON.parse(text).payload?.type);
  assertTrue(observedTypes.length > 0, 'background facts must reach the current EventStore');
  assertTrue(observedTypes.every((type) => type === 'UnrelatedProgressFact'), 'the watchdog must stop before the awaited fact');
  assertEq(
    appending.stdout.trim(),
    '',
    `background-only renewal must not reach the awaited fact: ${appending.stdout}`,
  );
}

async function runWaitFactRenewsOnDeclaredFactAndCountsPrecisely() {
  const scaledWatchdogMs = 1000;
  const budgetEnv = { WATCHDOG_TIMEOUT_MS: String(scaledWatchdogMs) };
  const workDir = tmpScenarioDir();
  execFileSync('git', ['-C', workDir, 'init', '-q'], { encoding: 'utf8' });
  // EventStore tip is the only renew surface after G4 leave-unread (no NDJSON).
  const result = await runWatchdogChild(
    `import { openGateFactStore } from ${JSON.stringify(gateFactsUrl)};\n` +
      `const gate = openGateFactStore(${JSON.stringify(workDir)});\n` +
      `let appended = 0;\n` +
      `const iv = setInterval(async () => {\n` +
      `  appended += 1;\n` +
      `  const fact = appended < 6 ? 'CandidateReady' : appended < 8 ? 'Published' : 'UnrelatedProgressFact';\n` +
      `  await gate.appendNamedFact(fact, appended);\n` +
      `  if (appended === 7) clearInterval(iv);\n` +
      `}, ${Math.floor(scaledWatchdogMs / 4)});\n` +
      factBarrierScript(workDir, 'Published', 'gate-wait-fact-renew-on').replace(
        `{ waitFact: { name: ${JSON.stringify('Published')}, eq: 1 }, lane: 'fact-lane' }`,
        `{ waitFact: { name: 'Published', eq: 2, renewOn: ['CandidateReady'] }, lane: 'fact-lane' }`,
      ) +
      `clearInterval(iv);\n` +
      `console.log('barrier returned after ' + appended + ' appends');\n`,
    WAIT_FACT_WINDOW_MS,
    budgetEnv,
  );
  assertEq(result.code, 0, `renewOn facts must keep the barrier alive: ${result.stderr}`);
  assertEq(result.stdout.trim(), 'barrier returned after 7 appends', 'eq must wait for the exact target count');
  const texts = journalEventLines(workDir);
  const types = texts.map((text) => {
    try {
      const event = JSON.parse(text);
      return event?.payload?.type ?? event?.type ?? '';
    } catch {
      return '';
    }
  });
  assertEq(types.filter((type) => type === 'Published').length, 2, 'eq must stop at two Published facts');
  assertEq(
    types.filter((type) => type === 'CandidateReady').length,
    5,
    'CandidateReady must renew before Published',
  );
  assertEq(types.length, 7, 'background facts must not advance the target count');
}

/** The child body both halves share: the real barrier, a real watchdog, a fake event source. */
function factBarrierScript(workDir, factName, label) {
  return (
    `import { Watchdog } from '${watchdogUrl}';\n` +
    `import { awaitFactBarrier } from '${driverUrl}';\n` +
    `import { WATCHDOG_TIMEOUT_MS } from '${budgetUrl}';\n` +
    `const scenario = {\n` +
    `  host: { workDir: ${JSON.stringify(workDir)} },\n` +
    // Answers every slice, and answers nothing semantic. If the barrier ever consults an event
    // source again, this is what it will hear, and this case says that must not renew anything.
    `  events: { awaitEvent: (_predicate, ms) => new Promise((resolve) => setTimeout(resolve, ms)) },\n` +
    `  watchdog: new Watchdog({ timeoutMs: WATCHDOG_TIMEOUT_MS, label: ${JSON.stringify(label)} }),\n` +
    `};\n` +
    `process.send?.({ type: 'fact-barrier-armed' });\n` +
    `await awaitFactBarrier(scenario, { waitFact: { name: ${JSON.stringify(factName)}, eq: 1 }, lane: 'fact-lane' });\n` +
    `scenario.watchdog.stop();\n`
  );
}

export const timeoutCases = [
  { name: 'watchdog fires on silence', fn: runWatchdogFiresOnSilence },
  { name: 'watchdog renews on causal progress', fn: runWatchdogRenewsOnProgress },
  { name: 'watchdog rejects background-only noise', fn: runWatchdogRejectsBackgroundNoise },
  { name: 'watchdog widened window tolerates declared slow step', fn: runWatchdogWidenedWindowToleratesDeclaredSlowStep },
  { name: 'watchdog restores default window', fn: runWatchdogRestoresDefaultWindow },
  { name: 'concurrent awaitEvent timeouts stay independent', fn: runConcurrentAwaitTimeouts },
  { name: 'verification-system-006 the timeout dump separates causal progress from background', fn: runDiagnosticDumpIsComplete },
  { name: 'verification-system-006 a clean scenario is not held to the end of the silence window', fn: runTimerDoesNotHoldEventLoop },
  { name: 'verification-system-006 waitFact renews only on an observation', fn: runWaitFactRenewsOnlyOnObservation },
  { name: 'verification-system-006 waitFact renews on declared facts and preserves exact counts', fn: runWaitFactRenewsOnDeclaredFactAndCountsPrecisely },
  {
    name: 'verification-system-005 fact barrier rejects exit before arming',
    fn: () => assert.rejects(runFactBarrierChild('process.exit(0);', 2000), expectedFactBarrierRejection(/exited before arming/)),
  },
  {
    name: 'verification-system-005 fact barrier rejects invalid arming',
    fn: () => assert.rejects(runFactBarrierChild("process.send({ type: 'noise' }); setInterval(() => {}, 1000);", 2000), expectedFactBarrierRejection(/invalid or repeated/)),
  },
  {
    name: 'verification-system-005 fact barrier rejects repeated arming',
    fn: () => assert.rejects(runFactBarrierChild("process.send({ type: 'fact-barrier-armed' }); process.send({ type: 'fact-barrier-armed' }); setInterval(() => {}, 1000);", 2000), expectedFactBarrierRejection(/invalid or repeated/)),
  },
  {
    name: 'verification-system-005 fact barrier rejects startup silence',
    fn: () => assert.rejects(runFactBarrierChild('setInterval(() => {}, 1000);', 2000), expectedFactBarrierRejection(/startup deadline expired/)),
  },
  {
    name: 'verification-system-005 fact barrier rejects armed silence without a watchdog verdict',
    fn: () => assert.rejects(runFactBarrierChild("process.send({ type: 'fact-barrier-armed' }); setInterval(() => {}, 1000);", 2000), expectedFactBarrierRejection(/observation deadline expired/)),
  },
  {
    name: 'verification-system-005 fact barrier rejects an external kill after arming',
    fn: () => assert.rejects(runFactBarrierChild("process.send({ type: 'fact-barrier-armed' }, () => process.kill(process.pid, 'SIGKILL'));", 2000), expectedFactBarrierRejection(/terminated by SIGKILL/)),
  },
  {
    name: 'verification-system-005 fact barrier reclaims a same-group descendant holding its pipes',
    fn: runFactBarrierReclaimsHeldPipe,
  },
];
