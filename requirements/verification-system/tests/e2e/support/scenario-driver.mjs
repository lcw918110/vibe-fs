/**
 * scenario-driver.mjs — one driver, one static TOML scenario per e2e case.
 *
 *   node scenario-driver.mjs process-stress        (or `runCanary('process-stress')`)
 *
 * The scenario file says what the model replies and what order things must happen in; this
 * file turns the flow verbs into real Host calls. A request the scenario does not declare
 * fail-stops the run immediately (StrictMock `onFatal`).
 *
 * ── what K9 removed here ────────────────────────────────────────────────────
 *
 * `loadScripts` — swapping in more edges mid-run. §8 retires it: a scenario is one static
 * file and a restart does not change what the model would say. Measured while converting:
 * one of the two recovery files it loaded was byte-identical to edges the main file already
 * declared, and the other was `scripts: []` — an empty swap indistinguishable at runtime
 * from a meaningful one.
 *
 * `readScript` + `loadScripts` reading the same file twice. The scenario is compiled once
 * and the harness keys ride on the compiled object, so there is no second reader to
 * disagree with the first.
 */

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import {
  runStaticGate,
  setupScenario,
  teardownScenario,
  getSessionId,
} from './index.js';
import { WAIT_FACT_WINDOW_MS } from './time-budget.js';
import { bindLaneSession } from './lane.mjs';
import { compileScenario } from './scenario-schema.js';
import { attachEventCeilings, normalizeEventCeilings } from './event-ceiling.js';
import { ScenarioRuntime } from './scenario-runtime.js';
import { readJournal, watchJournal, getOrCreateSharedObserver } from './journal-observer.js';
import { isIdleEvent } from './session-quiescence.js';
import { createNodeDelayPort } from './delay-port.mjs';
import { kindOf } from './runtime-key.js';
import { parse as parseToml } from 'smol-toml';

const SCENARIO_DIR = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'scenarios');

/** Short wall-clock guard when journal/SSE wake is unavailable; not a success-path sleep. */
const FACT_WAKE_GUARD_MS = 50;

const delayPort = createNodeDelayPort();

function wakeOnJournal(workDir, ms) {
  return new Promise((resolve) => {
    let settled = false;
    let stop = () => {};
    let delay = null;
    const done = () => {
      if (settled) return;
      settled = true;
      try { stop(); } catch {}
      try { delay?.cancel?.(); } catch {}
      resolve();
    };
    const obs = getOrCreateSharedObserver(workDir);
    stop = obs.subscribe(done);
    delay = delayPort.delay(ms);
    delay.then(done);
  });
}

function wakeOnSignal(ms, subscribe) {
  return new Promise((resolve) => {
    let settled = false;
    let unsubscribe = () => {};
    let delay = null;
    const done = () => {
      if (settled) return;
      settled = true;
      try { unsubscribe(); } catch {}
      try { delay?.cancel?.(); } catch {}
      resolve();
    };
    try {
      unsubscribe = subscribe(done) || (() => {});
    } catch {
      unsubscribe = () => {};
    }
    delay = delayPort.delay(ms);
    delay.then(done);
  });
}

async function fetchSessionsByAgent(scenario, agent) {
  const response = await scenario.client.request('GET', '/session', { query: { scope: 'project' } });
  if (!response.ok) {
    throw new Error(`bindChild session snapshot failed: ${JSON.stringify(response.data)}`);
  }
  const payload = response.data?.data?.data ?? response.data?.data ?? response.data;
  const sessions = Array.isArray(payload) ? payload : [];
  return sessions.filter((session) => session?.agent === agent && typeof session?.id === 'string');
}

async function awaitSessionsByAgent(scenario, agent, timeoutMs) {
  const deadline = timeoutMs === undefined ? Number.POSITIVE_INFINITY : Date.now() + timeoutMs;
  let observed = await fetchSessionsByAgent(scenario, agent);
  if (observed.length > 0) return observed;

  while (Date.now() < deadline) {
    const remaining = Math.max(1, deadline - Date.now());
    // Identity comes only from the Host snapshot (GET /session); a session.created
    // event is diagnostic transport noise, never a session fact. Wake is the short
    // wall guard only, time-independent on the success path.
    await delayPort.delay(Math.min(remaining, FACT_WAKE_GUARD_MS));
    observed = await fetchSessionsByAgent(scenario, agent);
    if (observed.length > 0) return observed;
  }

  throw new Error(`bindChild timed out waiting for Host agent ${agent}; observed=${JSON.stringify(observed)}`);
}

/**
 * Compile the named scenario, or fail with every problem at once.
 *
 * A scenario that half-loads is a scenario whose author believes something is covered and
 * it is not — so there is no partial mode, and the problems are thrown rather than logged.
 */
function loadScenario(name) {
  const file = path.isAbsolute(name)
    ? name
    : path.join(SCENARIO_DIR, name.endsWith('.toml') ? name : `${name}.toml`);

  const result = compileScenario(fs.readFileSync(file, 'utf8'), { name: path.basename(file) });
  if (!result.ok) throw new Error(`scenario did not compile:\n  ${result.problems.join('\n  ')}`);
  return result.scenario;
}

function countFact(workDir, factName) {
  return readJournal(workDir, factName).named;
}

async function sendPrompt(scenario, sessionId, prompt) {
  // Omit model when prompt.model is null/undefined so Host + plugin continue
  // from durable LastAuthority / provider target (session does not own a model).
  // Explicit prompt.model always wins.
  const body = {
    agent: prompt.agent,
    parts: [{ type: 'text', text: prompt.text }],
  };
  if (prompt.model !== undefined && prompt.model !== null) {
    body.model = prompt.model;
  } else if (prompt.model === undefined && prompt.omitModel !== true) {
    // Back-compat: most scripts still expect a default model on the wire.
    body.model = { providerID: 'test', modelID: 'test-model' };
  }
  const afterSeq = scenario.events.lastSeq;
  const response = await scenario.client.request('POST', `/session/${sessionId}/prompt_async`, { body });
  assert.ok(response.ok, `prompt failed: ${JSON.stringify(response.data)}`);

  const releaseHeldChildAfterAccepted =
    prompt.resumeAfterIdleText !== undefined || prompt.releaseHeldChildAfterAccepted === true;

  if (releaseHeldChildAfterAccepted) {
    await scenario.events.awaitEvent((event) => {
      const eventSession = event.sessionID ?? event.properties?.sessionID;
      const role = event.properties?.info?.role ?? event.properties?.role;
      return event.seq > afterSeq && event.type === 'message.updated' && eventSession === sessionId && role === 'user';
    }, prompt.acceptTimeoutMs ?? 10000);
    assert.equal(typeof scenario.releaseHeldChild, 'function', 'active prompt requires a held-child release');
    scenario.releaseHeldChild();
    scenario.releaseHeldChild = null;
  }

  if (prompt.resumeAfterIdleText !== undefined) {
    const beforeIdle = scenario.events.lastSeq;
    await scenario.events.awaitEvent((event) => {
      const eventSession = event.sessionID ?? event.properties?.sessionID;
      return event.seq > beforeIdle && eventSession === sessionId && isIdleEvent(event);
    }, prompt.idleTimeoutMs ?? 60000);
    const resumed = await scenario.client.request('POST', `/session/${sessionId}/prompt_async`, {
      body: { ...body, parts: [{ type: 'text', text: prompt.resumeAfterIdleText }] },
    });
    assert.ok(resumed.ok, `prompt resume failed: ${JSON.stringify(resumed.data)}`);
    return resumed;
  }

  return response;
}

/**
 * Await a durable journal fact, renewing the silence budget only on an observation.
 *
 * ── the defect this replaced ────────────────────────────────────────────────
 *
 * The previous loop called `advance({ blocking: true })` at the top of every iteration and then
 * waited on the event probe with a predicate that accepted EVERY host event, for 500ms. So the
 * silence budget was renewed every slice whether or not the fact moved, and the thing being
 * awaited was 「有字节在动」 rather than a step of the causal chain. verification-system-006 names this exact
 * shape: 「一个反复重连的 SSE 读者能永久续期一个错误的 watchdog」. Measured with a fake event source
 * that answers every slice and a fact that never appears, the loop survived past two silence
 * windows and would have run to the full 120000ms fallback.
 *
 * ── the causal signal, and why it is not just the awaited count ─────────────
 *
 * The awaited count alone is too strict to be the only signal. This barrier spans a host-side
 * publish / fast-forward / join chain that legitimately takes longer than one silence window,
 * and it emits many journal facts on the way to the one being awaited — so a watchdog fed only
 * by the final count would kill a run that is visibly progressing.
 *
 * So renewal follows either observation, and both are semantic:
 *
 *   the awaited count increased      the barrier's own subject moved
 *   any journal fact was appended    production code committed a durable domain fact
 *
 * The second is not transport motion. A journal line is written by the production reducer
 * reaching a decision (PERSIST-002 admits only committed appends), which is why a reconnecting
 * SSE reader cannot manufacture one — the failure mode the clause forbids. A slice that observes
 * neither renews nothing, and two such slices in a row end the run.
 *
 * ── why the overall window stays, and why it is 兜底 rather than the criterion ─
 *
 * WAIT_FACT_WINDOW_MS remains as the loop bound. It is reachable only by a run that keeps
 * appending journal facts for two minutes without ever producing the awaited one — a genuinely
 * progressing chain that is nonetheless not converging, which no silence criterion can detect
 * because there is no silence. The clause permits precisely this (「wall-clock 上限可以作为兜底
 * 存在，但不得是唯一或首要的判据」): the primary criterion is now the silence budget, which fires
 * first in every case where nothing is happening, and this bound only catches livelock.
 */
export async function awaitFactBarrier(scenario, step, ctx = null) {
  const name = step.waitFact.name;
  const renewOn = step.waitFact.renewOn ?? [];
  const targetSession =
    step.waitFact.session === 'child'
      ? ctx?.childId
      : step.waitFact.session === 'self'
        ? ctx?.sessionId
        : step.waitFact.session
          ? (ctx?.sessions?.[step.waitFact.session] ?? step.waitFact.session)
          : null;
  const need = step.waitFact.eq !== undefined ? step.waitFact.eq : step.waitFact.gte !== undefined ? step.waitFact.gte : 1;
  const cmp = step.waitFact.eq !== undefined
    ? (n) => n === need
    : (n) => n >= need;
  const lane = step.lane || `fact:${name}`;
  const deadline = Date.now() + (step.timeoutMs || WAIT_FACT_WINDOW_MS);

  const readCurrentFactCount = () => {
    if (targetSession) {
      const obs = getOrCreateSharedObserver(scenario.host.workDir);
      obs.refresh?.();
      const matched = obs.select({ caseName: name, sessionId: targetSession });
      return { named: matched.length, total: obs.counts().uniqueEvents, renew: 0 };
    }
    return readJournal(scenario.host.workDir, name, renewOn);
  };

  // verification-system-006: a waitFact with an explicit timeout is a bounded wait step —
  // widen the silence window to that bound so a slow-but-progressing promote
  // is not killed by the default 5s silence watchdog mid-poll.
  scenario.watchdog?.setWindow(step.timeoutMs ?? null);
  try {
  let observed = readCurrentFactCount();
  if (step.waitFact.eq !== undefined && observed.named > need) {
    assert.fail(
      `waitFact ${name} overshot eq ${need} (got ${observed.named}); use gte when the producer can race past the exact count`,
    );
  }
  while (!cmp(observed.named) && Date.now() < deadline) {
    const remaining = Math.max(1, deadline - Date.now());
    // Journal watch wakes on local .git/wanxiang/events writer-file changes; ≤FACT_WAKE_GUARD_MS wall guard is fallback only.
    await wakeOnJournal(scenario.host.workDir, Math.min(remaining, FACT_WAKE_GUARD_MS));

    const next = readCurrentFactCount();
    // Livelock guard: journal growth without the awaited fact still trips the
    // scenario's declared exact event ceiling (primary over silence alone).
    scenario.eventCeilings?.checkJournal?.();
    if (step.waitFact.eq !== undefined && next.named > need) {
      assert.fail(
        `waitFact ${name} overshot eq ${need} (got ${next.named}); use gte when the producer can race past the exact count`,
      );
    }
    // verification-system-006 / waitFact causal renewal: only the awaited count OR an explicitly
    // declared renewOn fact is causal blocking progress. Any other journal growth is
    // background traffic — recorded for diagnosis, never renewing the silence window.
    if (next.named > observed.named) {
      scenario.watchdog?.advance({ reason: `fact-count:${name}=${next.named}`, lane });
    } else if (renewOn.length > 0 && next.renew > observed.renew) {
      scenario.watchdog?.advance({
        reason: `renewOn:${name}=${next.named},intermediate=${next.renew}`,
        lane,
      });
    } else if (next.total > observed.total) {
      scenario.watchdog?.advance({
        reason: `journal-append-while-awaiting:${name}`,
        lane,
        blocking: false,
      });
    }
    observed = next;
  }

  assert.ok(
    cmp(observed.named),
    `waitFact ${name} not satisfied (need ${step.waitFact.eq !== undefined ? 'eq' : 'gte'} ${need}, got ${observed.named})`,
  );
  scenario.watchdog?.advance({ reason: `fact-ready:${name}`, lane });
  } finally {
    scenario.watchdog?.setWindow(null);
  }
}

const idleBarrierKey = (target, attempts) => `${target}@${attempts}`;

function armIdleBarrier(scenario, ctx, target, attempts = 1) {
  ctx.idleBarriers ??= new Map();
  const key = idleBarrierKey(target, attempts);
  const existing = ctx.idleBarriers.get(key);
  if (existing) return existing;

  let capture;
  const baseline = new Promise((resolve) => {
    capture = resolve;
  });
  const barrier = { key, baseline };
  scenario.provider.afterExpectation(
    target,
    (observation) => capture({ seq: scenario.events.lastSeq, sessionId: observation?.sessionId ?? null }),
    attempts,
  );
  ctx.idleBarriers.set(key, barrier);
  return barrier;
}

async function awaitIdleBarrier(scenario, ctx, step) {
  const target = step.awaitIdle.after;
  const attempts = step.awaitIdle.attempts ?? 1;
  const anchor = step.awaitIdle.arm ?? target;
  const anchorAttempts = step.awaitIdle.armAttempts ?? attempts;
  const explicitSessionId = step.awaitIdle.session === 'child' ? ctx.childId
    : step.awaitIdle.session === 'guard' ? (ctx.guardId || ctx.sessions?.guard)
    : step.awaitIdle.session === 'nudge' ? (ctx.nudgeId || ctx.sessions?.nudge)
    : step.awaitIdle.session ? ctx.sessions?.[step.awaitIdle.session] : null;

  const barrier = armIdleBarrier(scenario, ctx, anchor, anchorAttempts);
  scenario.watchdog?.setWindow(step.timeoutMs ?? null);
  try {
    await scenario.provider.waitForExpectationAttempt(target, attempts, step.timeoutMs);
    if (anchor !== target || anchorAttempts !== attempts) {
      await scenario.provider.waitForExpectationAttempt(anchor, anchorAttempts, step.timeoutMs);
    }
    const observed = await barrier.baseline;
    const sessionId = explicitSessionId || observed.sessionId;
    assert.ok(sessionId, `awaitIdle ${target} has no physical session identity`);
    if (process.env.MOCK_TRACE) {
      console.error(`[IDLE-BARRIER] target=${target} session=${sessionId} afterSeq=${observed.seq} currentSeq=${scenario.events.lastSeq}`);
    }
    await scenario.events.awaitEvent((event) => {
      const eventSession = event.sessionID ?? event.properties?.sessionID;
      return event.seq > observed.seq && eventSession === sessionId && isIdleEvent(event);
    }, step.timeoutMs ?? null);
    ctx.idleBarriers?.delete(barrier.key);
    scenario.watchdog?.advance({
      reason: `provider-terminal-idle:${target}`,
      lane: `session:${sessionId}`,
      blocking: true,
    });
  } finally {
    scenario.watchdog?.setWindow(null);
  }
}

/**
 * A flow step's own name, for the verbose step trace. Steps are one-key objects in almost every
 * case, so the key IS the verb; a step with several keys reports all of them rather than whichever
 * one `Object.keys` happened to return first.
 */
const stepLabel = (step) =>
  Object.entries(step)
    .filter(([, value]) => value !== false && value !== undefined)
    .map(([key, value]) => (typeof value === 'string' ? `${key}=${value}` : key))
    .join(',');

async function runFlow(scenario, doc, ctx) {
  const flow = doc.flow || [];
  const traceSteps = process.env.CANARY_VERBOSE === '1';
  const flowStartedAt = Date.now();
  /** Every step's wall time, so the distribution can be reported rather than reconstructed. */
  const stepTimings = [];

  /** One flow step. Nested so the verbs keep reading `scenario` / `doc` / `ctx` directly. */
  const runStep = async (step) => {
    if (step.wait) {
      // verification-system-006: an explicitly-bounded wait is a declared slow step — the
      // watchdog silence window widens to its timeoutMs so a legitimate
      // recovery chain is not mistaken for a hang. Background traffic never
      // renews the window; only a blocking advance does.
      scenario.watchdog?.setWindow(step.timeoutMs ?? null);
      try {
        if ((step.attempts ?? 1) === 1) {
          await scenario.provider.waitForExpectation(step.wait, step.timeoutMs);
        } else {
          await scenario.provider.waitForExpectationAttempt(step.wait, step.attempts, step.timeoutMs);
        }
      } finally {
        scenario.watchdog?.setWindow(null);
      }
      if (step.watchdog !== false) {
        scenario.watchdog?.advance({
          reason: step.wait,
          lane: step.lane || step.wait,
          blocking: step.blocking !== false,
        });
      }
      return;
    }
    if (step.waitAny) {
      scenario.watchdog?.setWindow(step.timeoutMs ?? null);
      let matched;
      try {
        matched = await scenario.provider.waitForAnyExpectation(step.waitAny, step.timeoutMs);
      } finally {
        scenario.watchdog?.setWindow(null);
      }
      if (step.watchdog !== false) {
        scenario.watchdog?.advance({
          reason: matched,
          lane: step.lane || matched,
          blocking: step.blocking !== false,
        });
      }
      return;
    }
    if (step.waitFact) {
      await awaitFactBarrier(scenario, step, ctx);
      return;
    }
    if (step.armIdle) {
      armIdleBarrier(scenario, ctx, step.armIdle.id, step.armIdle.attempts ?? 1);
      return;
    }
    if (step.awaitIdle) {
      await awaitIdleBarrier(scenario, ctx, step);
      return;
    }
    if (step.prompt) {
      const sid = step.session === 'child' ? ctx.childId
        : step.session === 'guard' ? (ctx.guardId || ctx.sessions?.guard)
        : step.session === 'nudge' ? (ctx.nudgeId || ctx.sessions?.nudge)
        : (step.session && ctx.sessions?.[step.session]) || ctx.sessionId;
      assert.ok(sid, 'prompt requires session');
      if (step.startTurn !== false) {
        scenario.turn.start(sid, step.afterSeq ? { afterSeq: step.afterSeq } : undefined);
      }
      scenario.watchdog?.setWindow(step.timeoutMs ?? null);
      try {
        await sendPrompt(scenario, sid, step.prompt);
      } finally {
        scenario.watchdog?.setWindow(null);
      }
      return;
    }
    if (step.restart) {
      await scenario.restart();
      return;
    }
    if (step.awaitTerminal) {
      const sid = step.session === 'child' ? ctx.childId
        : step.session === 'guard' ? (ctx.guardId || ctx.sessions?.guard)
        : step.session === 'nudge' ? (ctx.nudgeId || ctx.sessions?.nudge)
        : (step.session && ctx.sessions?.[step.session]) || ctx.sessionId;
      const turn = scenario.turn.current(sid) || scenario.turn.start(sid);
      scenario.watchdog?.setWindow(step.timeoutMs ?? null);
      try {
        await turn.awaitTerminal({
          timeoutMs: step.timeoutMs ?? null,
          requireActivity: step.requireActivity !== false,
          requireAssistantTerminal: step.requireAssistantTerminal === true,
          requireIdleAfterActivity: step.requireIdleAfterActivity !== false,
        });
      } finally {
        scenario.watchdog?.setWindow(null);
      }
      return;
    }
    if (step.expectSatisfied) {
      scenario.provider.expectSatisfied();
      return;
    }
    if (step.abort) {
      const sid = step.session === 'child' ? ctx.childId
        : step.session === 'guard' ? (ctx.guardId || ctx.sessions?.guard)
        : step.session === 'nudge' ? (ctx.nudgeId || ctx.sessions?.nudge)
        : (step.session && ctx.sessions?.[step.session]) || ctx.sessionId;
      await scenario.client.request('POST', `/session/${sid}/abort`, { body: {} }).catch(() => {});
      return;
    }
    if (step.bindChild) {
      // Host physical parents are flattened to the family root. Read the project-wide
      // session snapshot and bind every exact-agent session; `session.created` remains
      // a signal/diagnostic and never becomes identity data.
      const agent = step.bindChild.agent;
      const sessions = await awaitSessionsByAgent(scenario, agent, step.timeoutMs);
      // Preflow HumanRoot / Strength canaries also create manager sessions — and Strength
      // may nest a further manager child that is NOT in scenario.sessionIds. Prefer the
      // Manager whose parent is the Orchestrator session; otherwise exclude anyone whose
      // parent is already a known preflow session.
      const known = new Set(scenario.sessionIds ?? []);
      const parentOf = (session) =>
        session?.parentID
        ?? session?.parentId
        ?? session?.parent_id
        ?? session?.parent
        ?? null;
      const fresh = sessions.filter((session) => !known.has(session.id));
      const underOrch = fresh.filter((session) => parentOf(session) === ctx.sessionId);
      const notUnderKnown = fresh.filter((session) => {
        const parent = parentOf(session);
        return !parent || !known.has(parent);
      });
      // Prefer Orchestrator child; else the newest non-preflow session. Strength nests a
      // manager under the canary owner that is absent from scenario.sessionIds — taking
      // fresh[0] binds that nested session and the real orch Manager never gets the lane.
      const chosen =
        underOrch[0]
        ?? notUnderKnown[notUnderKnown.length - 1]
        ?? fresh[fresh.length - 1]
        ?? sessions[sessions.length - 1]
        ?? sessions[0];
      assert.ok(chosen?.id, `bindChild found no Host agent ${agent} session`);
      ctx.childId = chosen.id;
      const bound = step.bindChild.bind || [agent];
      if (!scenario.sessionIds.includes(ctx.childId)) scenario.sessionIds.push(ctx.childId);
      bindLaneSession(scenario.provider, ctx.childId, ...bound);
      scenario.watchdog?.advance({
        reason: 'child-created',
        lane: `session:${ctx.childId}`,
        blocking: true,
      });
      return;
    }
    if (step.createSession) {
      const created = await scenario.client.createSession({ agent: step.createSession.agent });
      const sid = getSessionId(created);
      assert.ok(sid, `createSession failed: ${JSON.stringify(created)}`);
      scenario.sessionIds.push(sid);
      const aliases = step.createSession.bind || step.createSession.aliases || [step.createSession.agent];
      bindLaneSession(scenario.provider, sid, ...aliases);
      const key = step.createSession.as || step.createSession.agent || 'session';
      ctx.sessions = ctx.sessions || {};
      ctx.sessions[key] = sid;
      if (key === 'guard') ctx.guardId = sid;
      if (key === 'nudge') ctx.nudgeId = sid;
      scenario.watchdog?.advance({ reason: `create-session-${key}`, lane: `session:${sid}`, blocking: true });
      return;
    }
    if (step.createChild) {
      const parentId = ctx.sessionId;
      assert.ok(parentId, 'createChild requires parent session');
      const child = await scenario.client.request('POST', '/api/session', {
        body: {
          parentID: parentId,
          title: step.createChild.title || 'scenario child',
          agent: step.createChild.agent,
          model: step.createChild.model || { providerID: 'test', id: 'test-model' },
        },
      });
      ctx.childId = getSessionId(child);
      assert.ok(ctx.childId, `createChild failed: ${JSON.stringify(child)}`);
      scenario.sessionIds.push(ctx.childId);
      const aliases = step.createChild.bind || step.createChild.aliases || [step.createChild.agent];
      bindLaneSession(scenario.provider, ctx.childId, ...aliases);
      scenario.watchdog?.advance({
        reason: 'create-child',
        lane: `session:${ctx.childId}`,
        blocking: true,
      });
      return;
    }
    if (step.afterExpectation) {
      let resolveRestart;
      let rejectRestart;
      if (step.afterExpectation.restart) {
        ctx.restartPromise = new Promise((resolve, reject) => {
          resolveRestart = resolve;
          rejectRestart = reject;
        });
      }

      scenario.provider.afterExpectation(step.afterExpectation.id, () => {
        try {
          if (step.afterExpectation.gitConflictProof) {
            const workDir = scenario.host.workDir;
            const proof = step.afterExpectation.file || 'publish_proof.txt';
            fs.writeFileSync(path.join(workDir, proof), 'Conflicting target edit\n');
            execFileSync('git', ['-C', workDir, 'add', proof], { encoding: 'utf8' });
            execFileSync('git', ['-C', workDir, 'commit', '-m', 'target: conflicting edit to proof file'], {
              encoding: 'utf8',
            });
          }
          if (step.afterExpectation.restart) {
            scenario.restart().then(resolveRestart, rejectRestart);
          }
        } catch (error) {
          rejectRestart?.(error);
          throw error;
        }
      }, step.afterExpectation.attempts ?? 1);
      return;
    }
    if (step.awaitRestart) {
      assert.ok(ctx.restartPromise, 'awaitRestart without afterExpectation restart');
      // A Host restart is declared slow work: the silence window widens to the
      // restart ceiling while the boot races turn advances (orch.2-style
      // recovered-run answers match mid-restart and re-arm the watchdog). The
      // restart promise itself is the criterion; background traffic still
      // never renews it, and a boot past the ceiling still fires.
      scenario.watchdog?.setWindow(WAIT_FACT_WINDOW_MS);
      try {
        await ctx.restartPromise;
      } finally {
        scenario.watchdog?.setWindow(null);
      }
      return;
    }
    if (step.assertFacts) {
      const n = countFact(scenario.host.workDir, step.assertFacts.name);
      if (step.assertFacts.eq !== undefined) {
        assert.equal(n, step.assertFacts.eq, `${step.assertFacts.name} count`);
      }
      if (step.assertFacts.gte !== undefined) {
        assert.ok(n >= step.assertFacts.gte, `${step.assertFacts.name} expected >= ${step.assertFacts.gte}, got ${n}`);
      }
      return;
    }
    if (step.assertDeliveries) {
      const { id, eq, gte, lte } = step.assertDeliveries;
      const n = scenario.provider.matchCount(id);
      assert.ok(
        eq !== undefined || gte !== undefined || lte !== undefined,
        `assertDeliveries ${id} requires eq, gte, or lte`,
      );
      if (eq !== undefined) assert.equal(n, eq, `${id} delivery count`);
      if (gte !== undefined) assert.ok(n >= gte, `${id} expected >= ${gte} deliveries, got ${n}`);
      if (lte !== undefined) assert.ok(n <= lte, `${id} expected <= ${lte} deliveries, got ${n}`);
      return;
    }
    if (step.assertActiveRequests) {
      const n = scenario.provider.activeRequestCount;
      if (step.assertActiveRequests.gte !== undefined) {
        assert.ok(n >= step.assertActiveRequests.gte, `activeRequestCount ${n}`);
      }
      return;
    }
    if (step.awaitEvent) {
      const afterSeq = scenario.events.lastSeq;
      await scenario.events.awaitEvent((e) => {
        if (e.seq <= afterSeq) return false;
        if (step.awaitEvent.type && e.type !== step.awaitEvent.type) return false;
        if (step.awaitEvent.session === 'self' && e.sessionID !== ctx.sessionId) return false;
        if (step.awaitEvent.statusType) {
          return e.properties?.status?.type === step.awaitEvent.statusType;
        }
        return true;
      }, step.timeoutMs ?? null);
      scenario.watchdog?.advance({
        reason: step.awaitEvent.reason || step.awaitEvent.type || 'event',
        lane: `session:${ctx.sessionId}`,
        blocking: true,
      });
      return;
    }
    if (step.assertFile) {
      const full = path.join(scenario.host.workDir, step.assertFile.path);
      assert.ok(fs.existsSync(full), `missing file ${step.assertFile.path}`);
      const body = fs.readFileSync(full, 'utf8');
      if (step.assertFile.equals !== undefined) {
        assert.equal(body, step.assertFile.equals, `file content ${step.assertFile.path}`);
      }
      if (step.assertFile.includes !== undefined) {
        assert.ok(body.includes(step.assertFile.includes), `file ${step.assertFile.path} missing ${step.assertFile.includes}`);
      }
      return;
    }
    if (step.assertGitLogContains) {
      const log = execFileSync('git', ['log', '--format=%s', 'HEAD'], {
        cwd: scenario.host.workDir,
        encoding: 'utf8',
      });
      assert.ok(log.includes(step.assertGitLogContains), `git log missing ${step.assertGitLogContains}: ${log}`);
      return;
    }
    if (step.assertWorktreeClean) {
      const status = execFileSync('git', ['status', '--porcelain'], {
        cwd: scenario.host.workDir,
        encoding: 'utf8',
      }).trim();
      assert.equal(status, '', `worktree not clean: ${status}`);
      const worktrees = execFileSync('git', ['worktree', 'list', '--porcelain'], {
        cwd: scenario.host.workDir,
        encoding: 'utf8',
      });
      const extra = worktrees.split('\n').filter((line) =>
        line.startsWith('worktree ') && !line.includes(scenario.host.workDir));
      assert.equal(extra.length, 0, `extra worktrees remain: ${worktrees}`);
      return;
    }
    if (step.assertModelTrajectory) {
      // Provider-visible physical target trajectory is an assertion rather than a
      // matching input. PROMPT-008 makes `AttemptExecutionProfile` the only source of the
      // effective model, so the model on the wire is a CONCLUSION of the run — a scenario
      // that matched on it would silently agree with whatever the scheduler did.
      //
      // The lane is resolved through the session binding, so this counts only requests that
      // belong to the Logical Run under test. The old scenario filtered by two hard-coded
      // prompt substrings instead, which also swept in any other session that happened to
      // send the same text.
      //
      // Poll until exact equality. `wait = "continue"` is permanent-after-first consume in
      // StrictMockSignals, so it cannot barrier multi-delivery continues; this poll is the
      // barrier for the trailing success delivery. Renew watchdog only on observed progress
      // (length growth or matching-prefix growth). Overshoot or prefix divergence fails
      // immediately — no slice/normalize (verification-system-004). Stuck silence → existing watchdog.
      const claim = step.assertModelTrajectory;
      const sessionId = scenario.provider.sessionFor(claim.lane);
      assert.ok(sessionId, `assertModelTrajectory lane '${claim.lane}' is not bound`);
      const expected = claim.models;

      const collectModels = () => (scenario.provider.requests || [])
        .filter((request) => (request?.sessionID ?? request?.sessionId) === sessionId)
        .filter((request) => kindOf(request) === 'chat')
        .map((request) => {
          const model = request?.model;
          return typeof model === 'string' ? model : (model?.modelID ?? model?.id ?? null);
        });

      let lastProgress = -1;
      for (;;) {
        const models = collectModels();

        if (models.length > expected.length) {
          assert.fail(
            `model trajectory for lane ${claim.lane}: overshot expected length ` +
            `${expected.length}, got ${models.length}: ${JSON.stringify(models)} ` +
            `(expected ${JSON.stringify(expected)})`,
          );
        }

        for (let i = 0; i < models.length; i++) {
          if (models[i] !== expected[i]) {
            assert.fail(
              `model trajectory for lane ${claim.lane}: diverged at index ${i}: ` +
              `got ${JSON.stringify(models)} (expected prefix of ${JSON.stringify(expected)})`,
            );
          }
        }

        if (models.length === expected.length) {
      // Exact, and deliberately so. The old scenario carried a
      // `rawModels.length === 5 → slice(1)` normalization to tolerate a duplicated first
      // attempt — assertion weakening of the kind verification-system-004 forbids. If the Host ever does
          // deliver that duplicate, this must fail and be explained.
          assert.deepEqual(models, expected, `model trajectory for lane ${claim.lane}`);
          ctx.modelTrajectory = models;
          scenario.watchdog?.advance({
            reason: `model-trajectory-ready:${claim.lane}`,
            lane: claim.lane,
          });
          break;
        }

        // Progress = longer matching prefix (length growth under the prefix check above).
        const progress = models.length;
        if (progress > lastProgress) {
          lastProgress = progress;
          scenario.watchdog?.advance({
            reason: `model-trajectory:${claim.lane}:len=${progress}`,
            lane: claim.lane,
          });
        }

        await wakeOnSignal(FACT_WAKE_GUARD_MS, (done) => {
          const unsubs = [];
          if (scenario.provider && 'onExpectationConsumed' in scenario.provider) {
            const prev = scenario.provider.onExpectationConsumed;
            scenario.provider.onExpectationConsumed = (arg) => {
              try { prev?.(arg); } finally { done(); }
            };
            unsubs.push(() => {
              scenario.provider.onExpectationConsumed = prev;
            });
          }
          if (typeof scenario.events?.onEvent === 'function') {
            unsubs.push(scenario.events.onEvent((e) => {
              if (e.type === 'message.updated') done();
            }));
          }
          return () => {
            for (const unsub of unsubs) {
              try { unsub(); } catch {}
            }
          };
        });
      }
      return;
    }
    if (step.assertPtyEcho) {
      const results = [];
      for (const request of scenario.provider.requests || []) {
        for (const message of request.messages || []) {
          if (message.role === 'tool' || message.role === 'toolResult') {
            const content = typeof message.content === 'string' ? message.content : JSON.stringify(message.content || '');
            try { results.push(parseToml(content)); } catch { results.push({ raw: content }); }
          }
        }
      }
      const readResult = results.find((r) => typeof r.output === 'string' && r.output.includes('ECHO_TEST'));
      assert.ok(readResult, `read must return ECHO_TEST: ${JSON.stringify(results)}`);
      assert.ok(readResult.output.includes('CWD='), `read must surface cwd: ${readResult.output}`);
      const joinResult = results.find(
        (r) => r?.status === 'completed' && Array.isArray(r?.result) && r.result.some((item) => item?.kind === 'pty' && item.closed === true),
      );
      assert.ok(joinResult, `join must deliver closed: ${JSON.stringify(results)}`);
      const listResult = results.find((r) => Array.isArray(r?.item) || (r && typeof r === 'object' && Object.keys(r).length === 0));
      assert.ok(listResult, `list must return item table: ${JSON.stringify(results)}`);
      assert.ok(!listResult.item || !listResult.item.some((e) => e && e.kind === 'pty'), `leaked pty: ${JSON.stringify(listResult)}`);
      return;
    }
    if (step.custom) {
      const fn = ctx.customs?.[step.custom];
      assert.ok(typeof fn === 'function', `unknown custom step ${step.custom}`);
      await fn(scenario, ctx, step);
      return;
    }
    throw new Error(`unknown flow step: ${JSON.stringify(step)}`);
  };

  for (const step of flow) {
    const stepStartedAt = Date.now();
    try {
      await runStep(step);
    } finally {
      const now = Date.now();
      stepTimings.push({ label: stepLabel(step), ms: now - stepStartedAt });
      // Per-step lines are verbose; the distribution below is not, and prints always.
      if (traceSteps) {
        console.log(
          `[flow +${((now - flowStartedAt) / 1000).toFixed(1)}s] ${stepLabel(step)} took ${
            now - stepStartedAt
          }ms`,
        );
      }
    }
  }

  reportFlowDistribution(doc.name ?? '<scenario>', stepTimings, Date.now() - flowStartedAt);
}

/**
 * One always-printed line per scenario: how long the flow took and which steps spent it.
 *
 * Default output rather than a flag, because a number nobody sees is a number nobody defends. The
 * previous state — no timing at all — is how a 50s canary stayed a 50s canary: every diagnosis
 * had to start by rebuilding the measurement, so most never started.
 */
function reportFlowDistribution(name, timings, totalMs) {
  if (timings.length === 0) return;
  const ranked = [...timings].sort((left, right) => right.ms - left.ms);
  const top = ranked
    .slice(0, 3)
    .filter((entry) => entry.ms > 0)
    .map((entry) => `${entry.label} ${(entry.ms / 1000).toFixed(1)}s`)
    .join(', ');
  console.log(
    `[flow-profile] ${name}: ${timings.length} steps in ${(totalMs / 1000).toFixed(1)}s` +
      (top === '' ? '' : `; slowest: ${top}`),
  );
}

export async function runCanary(scriptName, { customs, preFlow, amendDoc } = {}) {
  const doc = loadScenario(scriptName);
  if (typeof amendDoc === 'function') amendDoc(doc);
  let scenario;
  const ctx = { customs: customs || {} };
  try {
    scenario = await setupScenario({
      project: doc.setup?.project || { files: {} },
      strict: doc.setup?.strict !== false,
      extraEnv: doc.setup?.env || {},
      routingSource: doc.routingSource,
      mcpFixture: doc.setup?.mcpFixture,
      acceptanceGate: doc.setup?.acceptanceGate,
      watchdogLabel: doc.setup?.watchdogLabel || doc.name,
    });
    const eventCeilings = normalizeEventCeilings(doc.setup ?? {});
    attachEventCeilings(scenario, eventCeilings);

    scenario.provider.attachScenario(new ScenarioRuntime(doc));

    if (Array.isArray(doc.setup?.preFlowTurns) && doc.setup.preFlowTurns.length > 0 && !preFlow) {
      throw new Error('scenario declares preFlowTurns but no preFlow handler was supplied');
    }
    if (preFlow) await preFlow(scenario, ctx);

    const agent = doc.session?.agent || doc.prompt?.agent;
    if (agent || doc.session) {
      const created = await scenario.client.createSession({ agent: doc.session?.agent });
      ctx.sessionId = getSessionId(created);
      assert.ok(ctx.sessionId, `session creation failed: ${JSON.stringify(created)}`);
      scenario.sessionIds.push(ctx.sessionId);
      const bind = doc.session?.bind || [agent];
      if (bind?.length) bindLaneSession(scenario.provider, ctx.sessionId, ...bind);
    }

    if (doc.prompt) {
      scenario.turn.start(ctx.sessionId);
      await sendPrompt(scenario, ctx.sessionId, doc.prompt);
    }

    await runFlow(scenario, doc, ctx);

    const ceilingSnap = scenario.eventCeilings?.snapshot?.();
    if (ceilingSnap && (ceilingSnap.maxJournalEvents != null || ceilingSnap.maxSseEvents != null)) {
      console.log(
        `[event-ceiling] ${doc.name}: journal=${ceilingSnap.journalEvents}/${ceilingSnap.maxJournalEvents ?? '—'} ` +
          `sse=${ceilingSnap.sseEvents}/${ceilingSnap.maxSseEvents ?? '—'} (heartbeats excluded)`,
      );
    }

    console.log(doc.pass ?? `${doc.name} scenario passed.`);

    await teardownScenario(scenario);
    return 0;
  } catch (error) {
    console.error(`${doc.name} scenario failed: ${error.stack || error}`);
    if (scenario?.provider?.unexpectedRequests?.length) {
      console.error(JSON.stringify(scenario.provider.unexpectedRequests.slice(0, 3)));
    }
    if (scenario?.host?.workDir) {
      try {
        const { execFileSync } = await import('node:child_process');
        const gitLog = execFileSync('git', ['-C', scenario.host.workDir, 'log', '--oneline', '--all', '--graph', '-10'], { encoding: 'utf8' }).trim();
        const branches = execFileSync('git', ['-C', scenario.host.workDir, 'branch', '-a'], { encoding: 'utf8' }).trim();
        const worktrees = execFileSync('git', ['-C', scenario.host.workDir, 'worktree', 'list'], { encoding: 'utf8' }).trim();
        console.error(`[GIT-DIAG] workdir=${scenario.host.workDir}\nlog:\n${gitLog}\nbranches:\n${branches}\nworktrees:\n${worktrees}`);
      } catch (e) {
        console.error(`[GIT-DIAG-ERR] ${e?.message}`);
      }
    }
    if (process.env.SCENARIO_DEBUG_FACTS === '1' && scenario?.host?.workDir) {
      const { journalFactTail, storeTip } = await import('./journal-observer.js');
      console.error(`[scenario-facts] tip=${storeTip(scenario.host.workDir)}`);
      console.error(journalFactTail(scenario.host.workDir, 40).join('\n'));
    }
    if (scenario) {
      try { await teardownScenario(scenario, { keepOnFailure: true }); } catch {}
    }
    return 1;
  }
}

// CLI: node scenario-driver.mjs process-stress
const isMain = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  const script = process.argv[2];
  if (!script) {
    console.error('usage: node scenario-driver.mjs <scenario>');
    process.exit(2);
  }
  process.exit(await runCanary(script));
}
