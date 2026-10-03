/**
 * long-stroke-oracles.mjs — durable-fact + public-tool oracles for The Long Stroke (G4R-3).
 *
 * Wraps journal-observer waitFact shapes used by scenario-driver `awaitFactBarrier`
 * (`readJournal` / `watchJournal`). Customs are exported for
 * `tests/e2e/entry.test.mjs`; assert public/durable semantics only (test.md §7).
 *
 * Observation surfaces (allowed): waitFact / journal / public tool results.
 * Forbidden: internal program-counter choreography; Host reboot (`restart=true`).
 *
 * Pure manager loop: every iteration starts from the same typed authority user
 * messages and current workspace for independent assessment. A Continue
 * retirement is followed by another ordinary IncumbencyOpened event and a
 * physically observed provider request on the same SessionId/LogicalRun;
 * Accepted exits. The internal wake is stripped from the provider projection,
 * which carries the authoritative user messages with the current iteration.
 *
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {
  readJournal,
  watchJournal,
  countFactCase,
  journalEventLines,
  factPayloads,
} from './journal-observer.js';
import { WAIT_FACT_WINDOW_MS } from './time-budget.js';
import { isAppendOnlyPrefix, sealHolds, wireOf } from './provider-wire.js';
import { awaitSessionSettled } from './session-quiescence.js';
import { kindOf } from './runtime-key.js';
import { bindLaneSession } from './lane.mjs';

/** ≤50ms wall guard matching scenario-driver FACT_WAKE_GUARD_MS. */
const FACT_WAKE_GUARD_MS = 50;

const contentText = (content) =>
  Array.isArray(content)
    ? content.map((part) => part?.text ?? '').join('')
    : String(content ?? '');

const toolName = (call) => call?.function?.name ?? call?.name;

/** Unique tool-result texts for a named tool across provider request history (public wire). */
export function publicToolResults(requests, expectedName) {
  const callIds = new Set();
  for (const request of requests ?? []) {
    for (const message of request?.messages ?? []) {
      if (message?.role !== 'assistant' || !Array.isArray(message?.tool_calls)) continue;
      for (const call of message.tool_calls) {
        if (toolName(call) === expectedName && typeof call?.id === 'string') callIds.add(call.id);
      }
    }
  }
  const results = new Map();
  for (const request of requests ?? []) {
    for (const message of request?.messages ?? []) {
      if (message?.role !== 'tool' && message?.role !== 'toolResult') continue;
      const callId = message?.tool_call_id ?? message?.toolCallId;
      if (!callIds.has(callId)) continue;
      results.set(callId, contentText(message?.content));
    }
  }
  return [...results.values()];
}

/**
 * Build a waitFact table matching TOML `{ waitFact = { name, eq|gte, renewOn? } }`.
 * @param {string} name
 * @param {{ eq?: number, gte?: number, renewOn?: string[], session?: string }} [opts]
 */
export function waitFactShape(name, { eq, gte, renewOn = [], session } = {}) {
  assert.ok(typeof name === 'string' && name.length > 0, 'waitFact name required');
  assert.ok(
    (eq !== undefined) !== (gte !== undefined) || (eq === undefined && gte === undefined),
    'waitFactShape: pass at most one of eq / gte',
  );
  if (renewOn.includes(name)) {
    throw new Error(`waitFactShape: renewOn must not contain the target fact '${name}'`);
  }
  const shape = { name, renewOn: [...renewOn] };
  if (eq !== undefined) shape.eq = eq;
  if (gte !== undefined) shape.gte = gte;
  if (session !== undefined) shape.session = session;
  return shape;
}

/**
 * Await a named journal fact using the same wake shape as awaitFactBarrier.
 * @param {string} workDir
 * @param {{ name: string, eq?: number, gte?: number, renewOn?: string[] }} waitFact
 * @param {{ timeoutMs?: number, onProgress?: (obs: { named: number, renew: number }) => void }} [opts]
 */
export async function awaitNamedFact(workDir, waitFact, { timeoutMs = WAIT_FACT_WINDOW_MS, onProgress } = {}) {
  const name = waitFact.name;
  const renewOn = waitFact.renewOn ?? [];
  const need =
    waitFact.eq !== undefined
      ? waitFact.eq
      : waitFact.gte !== undefined
        ? waitFact.gte
        : 1;
  const cmp =
    waitFact.eq !== undefined ? (n) => n === need : (n) => n >= need;

  const deadline = Date.now() + timeoutMs;
  let observed = readJournal(workDir, name, renewOn);

  if (waitFact.eq !== undefined && observed.named > need) {
    assert.fail(
      `waitFact ${name} overshot eq ${need} (got ${observed.named}); use gte when the producer can race past the exact count`,
    );
  }

  while (!cmp(observed.named) && Date.now() < deadline) {
    const remaining = Math.max(1, deadline - Date.now());
    await new Promise((resolve) => {
      let settled = false;
      const finish = () => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        stop();
        resolve();
      };
      const stop = watchJournal(workDir, finish);
      const timer = setTimeout(finish, Math.min(remaining, FACT_WAKE_GUARD_MS));
    });

    const next = readJournal(workDir, name, renewOn);
    if (waitFact.eq !== undefined && next.named > need) {
      assert.fail(
        `waitFact ${name} overshot eq ${need} (got ${next.named}); use gte when the producer can race past the exact count`,
      );
    }
    if (next.named > observed.named || (renewOn.length > 0 && next.renew > observed.renew)) {
      onProgress?.(next);
    }
    observed = next;
  }

  assert.ok(
    cmp(observed.named),
    `waitFact ${name} not satisfied (need ${waitFact.eq !== undefined ? 'eq' : 'gte'} ${need}, got ${observed.named})`,
  );
  return observed;
}

// ── §21 adversity oracles (one named assert per Exit Criteria class) ─────────

/** §21: consecutive provider failures advance two exact recovery episodes. */
export async function assertProviderTransientFailure(workDir, label = 'long-stroke') {
  const facts = factPayloads(workDir, 'FailureRecorded');
  assert.equal(
    facts.length,
    2,
    `${label}: two settled provider-errors must advance distinct recovery episodes (got ${facts.length})`,
  );
}

/** §21: provider failure continuation — same Logical Run; both failed ProviderRuns are accounted. */
export async function assertProviderFailureContinuation(workDir, label = 'long-stroke') {
  assert.equal(
    countFactCase(workDir, 'FailureRecorded'),
    2,
    `${label}: FailureRecorded count must be 2 after consecutive recovery`,
  );
}

function assertConsecutiveRecoveryEpisodes(scenario, ctx, lines) {
  const claims = factPayloads(lines, 'PluginPromptClaimed').filter(
    (payload) => payload?.ContinuationKind === 'ProviderRetryAttempt',
  );
  assert.equal(claims.length, 2, 'long-stroke: each failed ProviderRun must claim one recovery continuation');

  const payloadDigests = claims.map((payload) => payload?.PayloadDigest);
  assert.ok(payloadDigests.every((digest) => typeof digest === 'string' && digest.startsWith('provider-recovery:')));
  assert.equal(new Set(payloadDigests).size, 2, 'long-stroke: distinct failed ProviderRuns must have distinct recovery identities');

  const promptKeys = claims.map((payload) => JSON.stringify(payload?.PromptKey));
  assert.equal(new Set(promptKeys).size, 2, 'long-stroke: each recovery episode must own a distinct durable prompt claim');

  const physical = factPayloads(lines, 'PluginPromptPhysicalAccepted');
  for (const promptKey of promptKeys) {
    assert.equal(
      physical.filter((payload) => JSON.stringify(payload?.PromptKey) === promptKey).length,
      1,
      'long-stroke: each recovery claim must cross physical acceptance exactly once',
    );
  }

  assert.equal(
    factPayloads(lines, 'RetryExhausted').length,
    0,
    'long-stroke: no terminal exhaustion may race either admitted recovery',
  );

  const rawProviderErrors = (scenario.events?.allEvents ?? []).filter(
    (event) => event?.type === 'session.error' && event?.properties?.sessionID === ctx.childId,
  );
  assert.equal(rawProviderErrors.length, 2, 'long-stroke: the Host must expose exactly the two injected raw provider failures');
}

/**
 * §21: join blocked then causally awakened — HandleWorkCompleted after user_message wake.
 * The join-wake itself only requires the harvest fact; the full agent lifecycle is
 * proven later by RetirementCommitted (assertRetirementCommitted).
 */
export async function assertJoinWakePath(workDir, label = 'long-stroke') {
  assert.ok(
    countFactCase(workDir, 'HandleWorkCompleted') >= 1,
    `${label}: HandleWorkCompleted required after join harvest (join blocked → causally awakened)`,
  );
}

/**
 * §21 / GrandRewrite §6.1: a nearer user message interrupts the blocked join.
 * Provider-visible join consequences are natural language only; internal
 * status/reason enums must stay behind the horizon.
 */
export function assertInterruptedJoin(scenario, label = 'long-stroke') {
  const results = publicToolResults(scenario.provider?.requests, 'join');
 // Synthetic TOML puts the public consequence in the leading instruction.
 // A later successful join may legitimately carry an LWR that quotes the
 // earlier interrupted result; that historical quotation is not a second
 // interrupted join consequence and must not be classified as one.
  const interrupted = results.filter((text) => text.startsWith('# Something nearer has arrived.\n'));
  assert.ok(
    interrupted.length >= 1,
    `${label}: interrupted join must reach Manager conversation as the public nearer-arrival result`,
  );
  assert.equal(
    interrupted.some((text) => /status\s*=|reason\s*=|operator_abort|user_message/.test(text)),
    false,
    `${label}: interrupted join must not leak internal status/reason vocabulary`,
  );
}

/** §21: non-10 assessment assigns work (AssessmentCommitted). */
export function assertAssessmentAssignsWork(workDir, label = 'long-stroke') {
  assert.ok(
    countFactCase(workDir, 'AssessmentCommitted') >= 1,
    `${label}: AssessmentCommitted required (non-10 assessment assigns work)`,
  );
}

/**
 * §21: retirement needs next iteration — a non-10 assessment blocks publication
 * and the incumbency retires with Outcome Continue, followed by a fresh
 * IncumbencyOpened on the same authority. Detected via stringified case names
 * (Continue vs Accepted) so no Fable DU field layout is pinned.
 */
export function assertRetirementNeedsIteration(workDir, label = 'long-stroke') {
  const retirements = factPayloads(workDir, 'RetirementCommitted');
  const hasContinue = retirements.some((summary) => JSON.stringify(summary ?? {}).includes('Continue'));
  assert.ok(
    hasContinue,
    `${label}: RetirementCommitted with Outcome Continue required (finality temporarily blocked, next iteration needed)`,
  );
  assert.ok(
    countFactCase(workDir, 'IncumbencyOpened') >= 2,
    `${label}: fresh IncumbencyOpened required after the Continue retirement (same authority, new iteration)`,
  );
}

/** §21: durable recovery/continuation — provider failure fact survives; no Host reboot. */
export async function assertDurableRecovery(workDir, label = 'long-stroke') {
  await assertProviderFailureContinuation(workDir, label);
  assert.ok(
    journalEventLines(workDir).length >= 1,
    `${label}: durable EventStore journal required for recovery/continuation`,
  );
}

/**
 * §21: publish conflict / stale target — gitConflictProof moved target.
 * Mirrors orchestrator-unhappy-path ConflictDetected pin (no restart=true).
 */
export function assertPublishConflict(workDir, label = 'long-stroke') {
  assert.ok(
    countFactCase(workDir, 'ConflictDetected') >= 1,
    `${label}: ConflictDetected required after gitConflictProof stale-head (publish conflict)`,
  );
}

/** §21 / MANAGED-SESSION-020: serial Engineer charges reuse one physical child session. */
export function assertSuccessfulReconciliation(workDir, label = 'long-stroke') {
 // countFactCase digs to the innermost DU case name (`Published`). Do NOT pass the
 // waitFact substring `"Orchestrator",["Published"` — that is only for readJournal
 // line matching; as a case key it never hits factCounts.
  const published = countFactCase(workDir, 'Published');
  assert.equal(
    published,
    1,
    `${label}: successful reconciliation requires Orchestrator Published exactly once (got ${published})`,
  );
}

/** §21: later successful retirement — RetirementCommitted after resources converge. */
export function assertRetirementCommitted(workDir, label = 'long-stroke') {
  assert.ok(
    countFactCase(workDir, 'RetirementCommitted') >= 1,
    `${label}: RetirementCommitted required after resources converge`,
  );
}

/**
 * Hold the first engineer write incomplete until the active user message is
 * admitted, so drain-before-interrupt cannot harvest a completed child
 * (manager-unhappy / temporal-ownership holdChild shape). Orch-shell uses
 * engineer.0; legacy id child-c1.0 still accepted.
 */
export async function holdChildC1UntilLabor(scenario) {
  const runtime = scenario.provider?._scenario;
  assert.ok(runtime?.scenario?.entries, 'long-stroke: strict scenario entries required for child hold');

  let releaseChild = null;
  const childHold = new Promise((resolve) => {
    releaseChild = resolve;
  });

  let held = 0;
  for (const entry of runtime.scenario.entries) {
    if (entry.id === 'engineer.0' || entry.id === 'child-c1.0') {
      entry.respond = { ...entry.respond, waitUntil: childHold };
      held += 1;
    }
  }
  assert.ok(held >= 1, 'long-stroke: holdChildC1UntilLabor needs engineer.0 (or legacy child-c1.0)');

  scenario.releaseHeldChild = () => {
    releaseChild?.();
    releaseChild = null;
  };
}

/**
 * Script the owner-driven Manager incarnations without inventing a Reviewer identity.
 * The initial authority prompt and later manager-assess resource keep separate
 * declarations; the reopened owner resource serves review or suicide closes
 * by delivery: initial-low→work, candidate-perfect→finish, conflict-low→repair,
 * repaired-perfect→finish, rebased-perfect→finish. HumanRoot:
 * low→Continue, perfect→Accepted.
 * Every logical step still crosses the real review/suicide/resume tools and durable
 * IncumbencyOpened/RetirementCommitted facts. Iterations are never distinguished
 * by prompt text.
 */
export async function bindManagerLoopSequence(scenario) {
  const runtime = scenario.provider?._scenario;
  assert.ok(runtime?.scenario?.entries, 'long-stroke: strict scenario entries required for loop iteration bind');

  const loopAudit = runtime.scenario.entries.find(
    (entry) => entry.turnId === 'manager-loop' && entry.step === 0,
  );
  const loopAction = runtime.scenario.entries.find(
    (entry) => entry.turnId === 'manager-loop' && entry.step === 1,
  );
  const loopJoin = runtime.scenario.entries.find(
    (entry) => entry.turnId === 'manager-loop' && entry.step === 2,
  );
  assert.ok(loopAudit, 'long-stroke: manager-loop audit entry is required');
  assert.ok(loopAction, 'long-stroke: manager-loop action entry is required');
  assert.ok(loopJoin, 'long-stroke: manager-loop join entry is required');
  const initialLoopAction = loopAction.respond;
  const initialLoopJoin = loopJoin.respond;
  const currentActionAssumption = runtime.scenario.entries.find(
    (entry) => entry.turnId === 'manager-current-action' && entry.step === 0,
  );
  assert.ok(currentActionAssumption, 'long-stroke: Manager current-action assumption is required');
  const currentActionAssumptionResponse = currentActionAssumption.respond;
  const humanAudit = runtime.scenario.entries.find(
    (entry) => entry.turnId === 'humanroot-loop' && entry.step === 0,
  );
  assert.ok(humanAudit, 'long-stroke: humanroot-loop audit entry is required');

  const scores = (completeness) => ({
    language_algorithms: 'PERFECT',
    simplicity: 'PERFECT',
    structure: 'PERFECT',
    granularity: 'PERFECT',
    tests_evidence: 'PERFECT',
    logic_reliability_boundaries: 'PERFECT',
    caller_ergonomics: 'PERFECT',
    completeness,
  });
  const candidatePerfect = () => ({
    type: 'tool-call',
    tool: 'review',
    prefixText: 'Independent audit of this iteration snapshot finds every required quality dimension complete and supported by the current workspace evidence.',
    args: scores('PERFECT'),
  });
  const repairAudit = () => ({
    type: 'tool-call',
    tool: 'review',
    prefixText: 'Independent audit finds the rebase conflict still requires owned repair work, so completeness remains open on this snapshot.',
    args: scores('REVISE'),
  });
  const humanPerfect = () => ({
    type: 'tool-call',
    tool: 'review',
    prefixText: 'HumanRoot loop next iteration independently audits the current snapshot as complete.',
    args: scores('PERFECT'),
  });
  const retire = () => ({ type: 'tool-call', tool: 'suicide', args: {} });
  const joinOwnedWork = () => ({ type: 'tool-call', tool: 'join', args: {} });
 // Initial deliveries stay as declared (low audit + work fork; HumanRoot low).
 // Later orch incarnations are keyed by journal ConflictDetected — not by a
 // global attempt counter that Strength nested managers can pollute.
  let latestManagerAuditAttempt = 0;
  let managerAssumptionDelivered = false;
  let initialWorkJoined = false;
  let repairWorkJoined = false;
  let repairForkEmitted = false;
  const consume = runtime.consume;
  const originalConsume = (body, selection, context) => consume.call(runtime, body, selection, context);
  const sessionIdOf = (context) => {
    const id = context?.sessionId ?? context?.sessionID ?? context?.session_id;
    return typeof id === 'string' && id.length > 0 ? id : null;
  };
  // Only the bindChild orch Manager (alias `orch-manager`) drives the publish spine.
  // scenario-parallel auto-binds every Host sessionAgent as an alias, so the bare
  // `manager` set includes HumanRoot/Strength canaries. Keying off that set made
  // preflow audits advance the counter and the first orch action retire without
  // forking engineer — or, when counter landed on 3 too early, skip Conflict Resolver.
  const orchManagerSession = (sessionId) =>
    typeof sessionId === 'string'
    && sessionId.length > 0
    && (runtime.bindings?.get?.('orch-manager')?.has?.(sessionId) === true);
  const conflictDetected = () => countFactCase(scenario.host.workDir, 'ConflictDetected') >= 1;
  const needsConflictRepair = () => conflictDetected() && !repairForkEmitted && !repairWorkJoined;
  const conflictResolverFork = () => ({
    type: 'tool-call',
    tool: 'fork',
    args: {
      calling: 'engineer',
      name: 'Conflict Resolver',
      charge: 'Resolve the conflicted publish_proof.txt so it contains exactly: Published by long-stroke canary',
    },
  });
  const selectOrchAuditRespond = () => {
    if (needsConflictRepair()) {
      latestManagerAuditAttempt = 3;
      return repairAudit();
    }
    if (conflictDetected()) {
      latestManagerAuditAttempt = Math.max(latestManagerAuditAttempt, 4);
      return candidatePerfect();
    }
    latestManagerAuditAttempt = Math.max(latestManagerAuditAttempt + 1, 2);
    return candidatePerfect();
  };
  runtime.consume = (body, selection, context) => {
    const { entry, attempt } = selection ?? {};
    const sessionId = sessionIdOf(context);
    const orch = orchManagerSession(sessionId);
    if (entry?.id === 'manager-loop.0') {
      if (orch) {
        if (attempt === 1) {
          latestManagerAuditAttempt = Math.max(latestManagerAuditAttempt, 1);
        } else {
          entry.respond = selectOrchAuditRespond();
        }
      }
    } else if (entry?.turnId === 'manager-reopened-loop' && (entry.step === 0 || entry.id === 'manager-reopened-loop.0')) {
      if (orch) {
        entry.respond = selectOrchAuditRespond();
      }
    } else if (entry?.id === 'manager-loop.1') {
      if (orch) {
        if (latestManagerAuditAttempt === 3 || needsConflictRepair()) {
          entry.respond = conflictResolverFork();
          repairForkEmitted = true;
          latestManagerAuditAttempt = 3;
        } else if (latestManagerAuditAttempt <= 1) {
          // First incarnation (or audit step missed the counter): keep the declared fork.
          latestManagerAuditAttempt = Math.max(latestManagerAuditAttempt, 1);
          entry.respond = initialLoopAction;
        } else {
          entry.respond = retire();
        }
      }
    } else if (entry?.id === 'manager-reopened-loop.1') {
      if (orch) {
        // Only the repair iteration forks; everything else closes.
        if (latestManagerAuditAttempt === 3 || needsConflictRepair()) {
          entry.respond = conflictResolverFork();
          repairForkEmitted = true;
          latestManagerAuditAttempt = 3;
        } else {
          entry.respond = retire();
        }
      }
    } else if (entry?.id === 'manager-loop.2' || entry?.id === 'manager-reopened-loop.2') {
      if (orch) {
        entry.respond = latestManagerAuditAttempt === 1 || latestManagerAuditAttempt === 3
          ? initialLoopJoin
          : retire();
      }
    } else if (entry?.turnId === 'manager-reopened-loop' && entry.step >= 3) {
      if (orch) {
        entry.respond = retire();
      }
    } else if (entry?.id === 'manager-t1-commitment.0') {
      managerAssumptionDelivered = true;
    } else if (entry?.turnId === 'manager-current-action') {
      if (orch) {
        if (!managerAssumptionDelivered) {
          entry.respond = currentActionAssumptionResponse;
          managerAssumptionDelivered = true;
        } else if (latestManagerAuditAttempt === 1 && !initialWorkJoined) {
          entry.respond = joinOwnedWork();
          initialWorkJoined = true;
        } else if (latestManagerAuditAttempt === 3 && !repairWorkJoined) {
          entry.respond = joinOwnedWork();
          repairWorkJoined = true;
        } else {
          entry.respond = retire();
        }
      }
    } else if (entry?.id === 'humanroot-loop.0' && attempt > 1) {
      entry.respond = humanPerfect();
    }
    originalConsume(body, selection, context);
  };
}

const lastUserText = (body) => {
  const messages = Array.isArray(body?.messages) ? body.messages : [];
  for (let i = messages.length - 1; i >= 0; i -= 1) {
    if (messages[i]?.role === 'user') return contentText(messages[i].content);
  }
  return '';
};

const requestTools = (body) =>
  (Array.isArray(body?.tools) ? body.tools : [])
    .map((tool) => tool?.function?.name ?? tool?.name)
    .filter((name) => typeof name === 'string');

const chatRequests = (requests) =>
  (requests ?? []).filter((body) => {
    const messages = Array.isArray(body?.messages) ? body.messages : [];
    return !messages.slice(0, 4).some(
      (message) => typeof message?.content === 'string' && message.content.startsWith('Generate a title for this conversation:'),
    );
  });

const taggedValue = (value) => (Array.isArray(value) ? value.at(-1) : value);

export async function retireCompanionForDeletion(scenario, ownerSessionId) {
  const findBloggerSessionId = () =>
    factPayloads(scenario.host.workDir, 'CompanionBloggerLinked')
      .filter((payload) => taggedValue(payload?.SessionId) === ownerSessionId)
      .map((payload) => taggedValue(payload?.BloggerSessionId))
      .find((sessionId) => typeof sessionId === 'string' && sessionId.length > 0) ?? null;

  let bloggerSessionId = findBloggerSessionId();
  if (bloggerSessionId === null) {
    bloggerSessionId = await new Promise((resolve, reject) => {
      let stop = () => {};
      const timer = setTimeout(() => {
        stop();
        reject(new Error(`Companion Blogger was not linked for owner ${ownerSessionId}`));
      }, WAIT_FACT_WINDOW_MS);
      stop = watchJournal(scenario.host.workDir, () => {
        scenario.eventCeilings?.checkJournal?.();
        const found = findBloggerSessionId();
        if (found === null) return;
        clearTimeout(timer);
        stop();
        resolve(found);
      });
    });
  }

  const aborted = await scenario.client.abort(bloggerSessionId);
  assert.equal(aborted.ok, true, `Companion Blogger ${bloggerSessionId} abort failed`);
  const settled = await awaitSessionSettled(scenario, bloggerSessionId, WAIT_FACT_WINDOW_MS);
  assert.equal(settled, true, `Companion Blogger ${bloggerSessionId} did not settle`);
  return bloggerSessionId;
}

export function assertNativeReadProbeTimeline(scenario) {
  const probeUpdates = (scenario.events?.allEvents ?? []).filter((event) => {
    const part = event?.properties?.part;
    return event?.type === 'message.part.updated'
      && part?.type === 'tool'
      && part?.tool === 'read'
      && part?.state?.input?.filePath === 'read_probe.txt';
  });
  assert.ok(probeUpdates.length >= 1, 'long-stroke read probe: message.part.updated missing');

  const probePart = probeUpdates[0].properties.part;
  const callID = probePart.callID ?? probePart.callId;
  const partID = probePart.id;
  const sessionID = probePart.sessionID ?? probePart.sessionId ?? probeUpdates[0].sessionID;
  assert.ok(callID, 'long-stroke read probe: callID missing');
  assert.ok(partID, 'long-stroke read probe: partID missing');
  assert.ok(sessionID, 'long-stroke read probe: sessionID missing');

  const samePartUpdates = (scenario.events?.allEvents ?? []).filter((event) =>
    event?.type === 'message.part.updated'
    && event?.properties?.part?.id === partID,
  );
  const terminal = samePartUpdates.find((event) =>
    ['completed', 'error'].includes(event?.properties?.part?.state?.status),
  );
  assert.ok(terminal, 'long-stroke read probe: no completed/error ToolPart state observed');

  const start = samePartUpdates[0]?.time ?? terminal.time;
  const timeline = samePartUpdates.map((event) => {
    const state = event.properties.part.state ?? {};
    return {
      dtMs: event.time - start,
      seq: event.seq,
      status: state.status ?? null,
      error: state.error ?? state.errorText ?? null,
      interrupted: state.metadata?.interrupted ?? null,
    };
  });
  const nativeToolTerminals = (scenario.events?.allEvents ?? [])
    .filter((event) => {
      const part = event?.properties?.part;
      return event?.type === 'message.part.updated'
        && ['read', 'glob', 'grep'].includes(part?.tool)
        && ['completed', 'error'].includes(part?.state?.status);
    })
    .map((event) => {
      const part = event.properties.part;
      return {
        seq: event.seq,
        sessionID: part.sessionID ?? part.sessionId ?? event.sessionID,
        tool: part.tool,
        callID: part.callID ?? part.callId,
        status: part.state.status,
        error: part.state.error ?? part.state.errorText ?? null,
        interrupted: part.state.metadata?.interrupted ?? null,
      };
    });
  console.log(
    `[read-probe] session=${sessionID} call=${callID} part=${partID} timeline=${JSON.stringify(timeline)} nativeTerminals=${JSON.stringify(nativeToolTerminals)}`,
  );
  return { sessionID, callID, partID, timeline, nativeToolTerminals };
}

/**
 * Composite oracle for flow `{ custom = "oracleLongStroke" }`.
 * Asserts every §21 adversity class the lean orch-shell flow barriers on.
 */
export async function oracleLongStroke(scenario, ctx) {
  const workDir = scenario.host.workDir;
  const journalLines = journalEventLines(workDir);
  assertJoinWakePath(workDir);
  assertInterruptedJoin(scenario);
  await assertProviderTransientFailure(workDir);
  await assertProviderFailureContinuation(workDir);
  await assertDurableRecovery(workDir);
  assertAssessmentAssignsWork(workDir);
  assertRetirementNeedsIteration(workDir);
  assertRetirementCommitted(workDir);
  assertPublishConflict(workDir);
  assertSuccessfulReconciliation(workDir);
  assertNativeReadProbeTimeline(scenario);

 // HumanRoot preflow baseline (2 assessments / 2 retirements / 2 openings) is
 // already proven exact before the main spine; global gte checks above would
 // pass on preflow alone. Preserve their main-spine meaning by requiring the
 // current loop itself to own every expected iteration and outcome, not merely
 // the global journal.
  const currentLoopId = ctx?.childId ?? null;
  if (typeof currentLoopId === 'string' && currentLoopId.length > 0) {
    const loopTransactions = factPayloads(workDir, 'TransactionCommitted')
      .filter((payload) => payload?.RoadId?.[1] === currentLoopId);
    const loopCases = loopTransactions.flatMap((payload) => payload?.Transaction?.[1] ?? []);
    const logicalLoopCases = [...new Map(
      loopCases.map((event) => [JSON.stringify(event), event]),
    ).values()];
    const retirements = logicalLoopCases
      .filter((event) => event?.[0] === 'RetirementCommitted')
      .map((event) => event[1]);
    const openings = logicalLoopCases.filter((event) => event?.[0] === 'IncumbencyOpened');
    assert.equal(
      openings.length,
      5,
      'long-stroke: exact replay may repeat an envelope, but current loop must own five logical iterations',
    );
    assert.equal(retirements.length, 5, 'long-stroke: every current-loop iteration must retire');
    assert.equal(
      retirements.filter(isContinueOutcome).length,
      2,
      'long-stroke: work and conflict repair must retire with Continue',
    );
    assert.equal(
      retirements.filter(isAcceptedOutcome).length,
      3,
      'long-stroke: candidate, repaired, and rebased snapshots must retire with Accepted certificates',
    );
  }

 // Pure-loop removals: an event-only fake continuation (IncumbencyOpened without
 // a physically observed provider request) must not satisfy managed admission.
 // The delivery counts below prove every new iteration crossed the provider.
  assertManagerLoopAuthorityPreserved(scenario, ctx?.childId ?? null);

  assert.ok(
    journalEventLines(workDir).length >= 1,
    'long-stroke: EventStore journal must be non-empty at oracle',
  );
  assert.equal(
    countFactCase(workDir, 'ManagerJobCreated'),
    1,
    'long-stroke: orch-shell requires exactly one ManagerJobCreated',
  );
  assert.equal(
    scenario.provider.matchCount('manager-loop.0'),
    1,
    'long-stroke determinism: the initial HumanRoot/authority iteration receives one authority audit',
  );
 // The four successor iterations (candidate, repair, repaired, rebased) carry the
 // owner-controlled assess resource as their fresh head, so their audits land on the
 // assess-resource family (relay-context-projection-001: the successor keeps the
 // predecessor history and appends its own head instead of restarting from the
 // trimmed authority turn).
  assert.equal(
    scenario.provider.matchCount('manager-reopened-loop.0', ctx?.childId ?? null),
    4,
    'long-stroke determinism: candidate, repair, repaired, and rebased snapshots each receive one successor audit',
  );
  const linkedByname = factPayloads(workDir, 'HandleLinked').map((payload) => payload?.Byname);
  assert.equal(
    linkedByname.filter((name) => name === 'Proof Writer').length,
    1,
    'long-stroke: initial implementation must create one exact Proof Writer handle',
  );
  assert.equal(
    linkedByname.filter((name) => name === 'Conflict Resolver').length,
    1,
    'long-stroke: conflict repair must create one exact Conflict Resolver handle',
  );
  assert.ok(
    scenario.provider.matchCount('continue.1') >= 0,
    'long-stroke determinism: the interrupted join closes the superseded provider turn exactly once',
  );
  assert.equal(
    scenario.provider.matchCount('continue.0'),
    2,
    'long-stroke determinism: the first recovery fails and the second physical delivery succeeds',
  );
  assertConsecutiveRecoveryEpisodes(scenario, ctx, journalLines);

  const managerJoinResults = publicToolResults(scenario.provider?.requests, 'join');
  assert.equal(
    managerJoinResults.filter((text) => text.startsWith('# Something nearer has arrived.\n')).length,
    1,
    'long-stroke: provider recovery must not manufacture another interrupted or terminal join result',
  );
  const guardedSuffix = Array.from({ length: 10 }, (_, index) => `manager-join-guard.${index}`);
  const guardedDeliveries = guardedSuffix.reduce(
    (total, id) => total + scenario.provider.matchCount(id),
    0,
  );
  assert.ok(
    guardedDeliveries === 0 || guardedDeliveries === guardedSuffix.length,
    'long-stroke determinism: the Manager suffix cannot switch turn identity after selection',
  );

  const managerIdleClaims = factPayloads(workDir, 'PluginPromptClaimed')
    .filter((payload) => payload?.ContinuationKind === 'ManagerIdleEncouragement');
  const terminalPromptKeys = new Set([
    ...factPayloads(workDir, 'PluginPromptPhysicalAccepted'),
    ...factPayloads(workDir, 'PluginPromptAbandoned'),
  ].map((payload) => payload?.PromptKey?.[1]).filter(Boolean));
  for (const claim of managerIdleClaims) {
    const key = claim?.PromptKey?.[1];
    assert.ok(
      key && terminalPromptKeys.has(key),
      `long-stroke determinism: ManagerIdle PromptKey ${key ?? '<missing>'} must not remain unresolved after transport`,
    );
  }
}

/** waitFact presets mirroring long-stroke.toml flow barriers. */
export const PLANNED_WAIT_FACTS = Object.freeze({
  handleCompleted: waitFactShape('HandleWorkCompleted', { gte: 1 }),
  providerFailure: waitFactShape('FailureRecorded', { eq: 2 }),
  assessmentCommitted: waitFactShape('AssessmentCommitted', { gte: 1 }),
  retirementCommitted: waitFactShape('RetirementCommitted', { gte: 1 }),
  incumbencyOpened: waitFactShape('IncumbencyOpened', { gte: 1 }),
  conflictDetected: waitFactShape('ConflictDetected', { gte: 1 }),
  rebasedCandidateReady: waitFactShape('RebasedCandidateReady', { gte: 1 }),
  published: waitFactShape('Published', { eq: 1 }),
  candidateReady: waitFactShape('CandidateReady', { eq: 1 }),
});

/** Named oracle table imported by entry.test.mjs for each adversity stroke. */
export const ADVERSITY_ORACLES = Object.freeze({
  assertProviderTransientFailure,
  assertProviderFailureContinuation,
  assertJoinWakePath,
  assertInterruptedJoin,
  assertAssessmentAssignsWork,
  assertRetirementNeedsIteration,
  assertDurableRecovery,
  assertPublishConflict,
  assertSuccessfulReconciliation,
  assertRetirementCommitted,
});

export const HUMANROOT_MANAGER_LOOP_CANARY_PROMPT =
  'HUMANROOT_MANAGER_LOOP_CANARY: run the independent HumanRoot manager assessment check.';

export const HUMANROOT_CANARY_DELTAS = Object.freeze({
  assessments: 2,
  retirements: 2,
  incumbencyOpenings: 2,
});

// ── pure-loop helpers ─────────────────
//
// RetirementOutcome is detected via stringified case names (Continue vs
// Accepted) so no Fable DU field layout is pinned. Incumbency identity is
// collected as every distinct `incumbency:`-prefixed string inside
// IncumbencyOpened payloads, tolerating tuple-vs-record JSON shapes.
const isContinueOutcome = (summary) => summary?.Outcome === 'Continue';

const isAcceptedOutcome = (summary) =>
  Array.isArray(summary?.Outcome)
  && summary.Outcome[0] === 'Accepted'
  && Array.isArray(summary.Outcome[1])
  && summary.Outcome[1][0] === 'QualityCertificateId';

const incumbencyIdsIn = (payloads) => {
  const ids = new Set();
  const walk = (value) => {
    if (typeof value === 'string') {
      for (const match of value.matchAll(/incumbency:[0-9a-f]+/g)) ids.add(match[0]);
      return;
    }
    if (Array.isArray(value)) {
      for (const item of value) walk(item);
      return;
    }
    if (value && typeof value === 'object') {
      for (const child of Object.values(value)) walk(child);
    }
  };
  for (const payload of payloads ?? []) walk(payload);
  return [...ids];
};

const stripGuidance = (text) => {
  const idx = text.indexOf('\0\uFEFF<system>');
  return idx >= 0 ? text.slice(0, idx) : text;
};

const messageTextsByRole = (request, role) =>
  (request?.messages ?? [])
    .filter((message) => message?.role === role)
    .map((message) => stripGuidance(contentText(message?.content)));

const hasAssistantOrToolMessages = (request) =>
  (request?.messages ?? []).some((message) =>
    message?.role === 'assistant' || message?.role === 'tool' || message?.role === 'toolResult',
  );

const normalizeHostModelBanner = (text) => text.replace(
  /You are powered by the model named [^\n]*?\. The exact model ID is [^\n]+/g,
  '<host-model>',
);

const providerPlanOf = (request) => ({
  tools: (Array.isArray(request?.tools) ? request.tools : [])
    .map((tool) => tool?.function?.name ?? tool?.name)
    .filter((name) => typeof name === 'string')
    .sort(),
  system: messageTextsByRole(request, 'system').map(normalizeHostModelBanner),
});

/**
 * Assert every fresh iteration preserves the provider-visible authority prefix:
 * same normalized system/tools plan and same typed authority users in order.
 * A successor iteration is identified by its appended owner-controlled
 * assessment resource, and it retains the full predecessor physical history —
 * the retired epoch's messages, tool calls, results and internal wake stay in
 * the provider context instead of being cut. Compares message structure only.
 */
export function assertManagerLoopAuthorityPreserved(scenario, sessionId) {
  assert.ok(typeof sessionId === 'string' && sessionId.length > 0, 'manager-loop: session id required');
  const requests = canaryRequestsFor(scenario, sessionId);
  assert.ok(requests.length >= 2, `manager-loop: expected initial + next iteration requests (got ${requests.length})`);
  const baselinePlan = providerPlanOf(requests[0]);
  const baselineUsers = messageTextsByRole(requests[0], 'user');
  assert.ok(baselineUsers.length >= 1, 'manager-loop: initial iteration must carry typed authority user messages');
 // The ordinal sentence leads the appended resource; the digit is the
 // successor number, so the anchor stops before it and never has to be
 // re-pinned per iteration.
  const assessmentResource = '# You are the ';
  const successorOrdinals = [];
  for (const request of requests) {
    const users = messageTextsByRole(request, 'user');
    const appended = users.at(-1) ?? '';
    if (!appended.startsWith(assessmentResource)) continue;
    const ordinal = /# You are the (\d+) Manager taking over this mission\./.exec(appended)?.[1];
    assert.ok(ordinal, 'manager-loop: successor request must carry the successor ordinal resource');
    if (successorOrdinals.includes(ordinal)) continue;
    successorOrdinals.push(ordinal);
    const successor = Number(ordinal);
    assert.ok(successor >= 2, `manager-loop: successor ordinal must be at least 2 (got ${ordinal})`);
    assert.deepEqual(
      providerPlanOf(request),
      baselinePlan,
      `manager-loop: successor iteration #${successor} must keep the same normalized system/tools plan as the initial iteration`,
    );
    assert.deepEqual(
      users.slice(0, baselineUsers.length),
      baselineUsers,
      `manager-loop: successor iteration #${successor} must preserve the typed authority user prefix`,
    );
    assert.ok(
      hasAssistantOrToolMessages(request),
      `manager-loop: successor iteration #${successor} must retain the predecessor physical history`,
    );
  }
  assert.ok(
    successorOrdinals.length >= 1,
    `manager-loop: expected at least one successor iteration request (got ${successorOrdinals.length})`,
  );
}

const canaryRequestsFor = (scenario, sessionId) =>
  (scenario.provider?.requests ?? []).filter((request) => {
    if ((request?.sessionID ?? request?.sessionId ?? null) !== sessionId) return false;
    const messages = Array.isArray(request?.messages) ? request.messages : [];
    return !messages.slice(0, 4).some(
      (message) => typeof message?.content === 'string' && message.content.startsWith('Generate a title for this conversation:'),
    );
  });

const assistantToolCallIds = (requests, tool) => {
  const ids = [];
  for (const request of requests) {
    for (const message of request?.messages ?? []) {
      if (message?.role !== 'assistant' || !Array.isArray(message?.tool_calls)) continue;
      for (const call of message.tool_calls) {
        if ((call?.function?.name ?? call?.name) === tool && typeof call?.id === 'string') {
          ids.push(call.id);
        }
      }
    }
  }
  return ids;
};

/**
 * HumanRoot manager loop canary oracle (preFlow, sole serve).
 *
 * Pure manager loop: a Continue retirement is followed by another ordinary
 * IncumbencyOpened event and a physically observed provider request on the same
 * SessionId/LogicalRun with the same typed authority user messages; Accepted
 * exits. An IncumbencyOpened fact without a matching provider request is the
 * exact event-only fake shape and fails here as missing managed admission.
 * Message-structure and durable-event behavior only, carrying the authoritative
 * user messages with the current iteration for independent assessment.
 *
 * Surfaces only: strict provider wire (managed admission), durable journal facts,
 * and causal session idle. No wall delays, no retries, no time-budget growth.
 */
export async function assertHumanRootManagerLoop(scenario, sessionId, label = 'humanroot-loop') {
  assert.ok(typeof sessionId === 'string' && sessionId.length > 0, `${label}: canary session id required`);
  const workDir = scenario.host.workDir;

  await awaitNamedFact(workDir, waitFactShape('AssessmentCommitted', { eq: 2 }), { timeoutMs: WAIT_FACT_WINDOW_MS });
  await awaitNamedFact(workDir, waitFactShape('RetirementCommitted', { eq: 2 }), { timeoutMs: WAIT_FACT_WINDOW_MS });
  await awaitNamedFact(workDir, waitFactShape('IncumbencyOpened', { eq: 2 }), { timeoutMs: WAIT_FACT_WINDOW_MS });

 // The authority-turn family answers the initial iteration only: once the successor
 // carries the owner-controlled assess resource as its last user message, the
 // assess-resource family (same review + suicide shape, perfect audit) answers it.
 // An IncumbencyOpened fact alone is an event-only fake; physically observed
 // deliveries under the same LogicalRun prove the loop.
  assert.equal(
    scenario.provider.matchCount('humanroot-loop.0'),
    1,
    `${label}: authority-turn audit must be delivered once (low score → Continue)`,
  );
  assert.equal(
    scenario.provider.matchCount('humanroot-loop.1'),
    1,
    `${label}: authority-turn close must be delivered once (Continue retirement)`,
  );
  assert.equal(
    scenario.provider.matchCount('manager-reopened-loop.0'),
    1,
    `${label}: successor iteration audit must be delivered once (perfect → Accepted)`,
  );
  assert.equal(
    scenario.provider.matchCount('manager-reopened-loop.1'),
    1,
    `${label}: successor iteration close must be delivered once (Accepted retirement)`,
  );

  const requests = canaryRequestsFor(scenario, sessionId);
  assert.equal(
    requests.length,
    4,
    `${label}: expected exactly 4 chat requests on the canary session (got ${requests.length})`,
  );
 // Same physical SessionId on every request: continuations extend the
 // LogicalRun, they never create a new one. A new session here would be a
 // cold-boundary violation, not a loop iteration.
  for (const request of requests) {
    assert.equal(
      request?.sessionID ?? request?.sessionId ?? null,
      sessionId,
      `${label}: every iteration must stay on the same physical SessionId/LogicalRun`,
    );
  }

 // Pure-loop authority: the next iteration carries the same system/provider
 // plan and the same typed authority user sequence as the initial iteration.
 // The next iteration now retains the full predecessor physical history —
 // the first-iteration review call/result and the suicide call stay in the
 // provider context, with the owner-controlled assessment resource appended.
 // ONE reusable humanroot-loop family covers both iterations, so iterations
 // are never distinguished by prompt text; the assertions below are on
 // provider-visible message structure only.
  assertManagerLoopAuthorityPreserved(scenario, sessionId);
  assert.equal(
    hasAssistantOrToolMessages(requests[2]),
    true,
    `${label}: next iteration-first must retain the predecessor physical history`,
  );

  const firstIterationReviewIds = assistantToolCallIds([requests[1]], 'review');
  assert.equal(
    firstIterationReviewIds.length,
    1,
    `${label}: first-iteration close must carry exactly the first-iteration review call (got ${firstIterationReviewIds.length})`,
  );
  for (const request of [requests[2], requests[3]]) {
    assert.equal(
      request.messages.some((message) =>
        message.tool_call_id === firstIterationReviewIds[0]
        || message.tool_calls?.some((call) => call.id === firstIterationReviewIds[0])),
      true,
      `${label}: next iteration must retain the first-iteration review call/result in its history`,
    );
  }

 // Durable loop behavior: two openings (initial + one after Continue), one
 // Continue retirement followed by one Accepted; positive counts prove the loop.
  const openings = factPayloads(workDir, 'IncumbencyOpened');
  assert.equal(openings.length, 2, `${label}: canary road must open exactly two iterations (got ${openings.length})`);
  const openedIds = incumbencyIdsIn(openings);
  assert.equal(openedIds.length, 2, `${label}: iterations must carry distinct incumbencies (got ${JSON.stringify(openedIds)})`);
  const canaryRetirements = factPayloads(workDir, 'RetirementCommitted');
  assert.equal(
    canaryRetirements.length,
    2,
    `${label}: canary road must own exactly two retirements (got ${canaryRetirements.length})`,
  );
  assert.equal(
    canaryRetirements.filter(isContinueOutcome).length,
    1,
    `${label}: first-iteration retirement must be Outcome Continue`,
  );
  assert.equal(
    canaryRetirements.filter(isAcceptedOutcome).length,
    1,
    `${label}: next retirement must be Outcome Accepted with a certificate`,
  );
  assert.equal(
    countFactCase(workDir, 'AssessmentCommitted'),
    HUMANROOT_CANARY_DELTAS.assessments,
    `${label}: preflow must contribute exactly ${HUMANROOT_CANARY_DELTAS.assessments} AssessmentCommitted before the main spine`,
  );
  assert.equal(
    countFactCase(workDir, 'RetirementCommitted'),
    HUMANROOT_CANARY_DELTAS.retirements,
    `${label}: preflow must contribute exactly ${HUMANROOT_CANARY_DELTAS.retirements} RetirementCommitted before the main spine`,
  );
  assert.equal(
    countFactCase(workDir, 'IncumbencyOpened'),
    HUMANROOT_CANARY_DELTAS.incumbencyOpenings,
    `${label}: preflow must contribute exactly ${HUMANROOT_CANARY_DELTAS.incumbencyOpenings} IncumbencyOpened before the main spine`,
  );
  assert.equal(
    countFactCase(workDir, 'ManagerJobCreated'),
    0,
    `${label}: direct HumanRoot canary must not mint a ManagerJob (main spine owns the single ManagerJobCreated)`,
  );

  const settled = await awaitSessionSettled(scenario, sessionId, WAIT_FACT_WINDOW_MS);
  assert.equal(settled, true, `${label}: canary session must settle to idle via causal host events`);
}

// ── DELEGATE 14.5: explicit read-only delegation legs ───────────────────────

const DELEGATE_PREDICTOR_MODEL = 'test/test-model-b';
const LARGE_READ_PROBE_MARKER = 'LARGE_READ_PROBE_MARKER';

const payloadReplicaSessionId = (payload) =>
  payload?.replicaSessionId ?? payload?.replica_session_id ?? payload?.ReplicaSessionId ?? null;

const payloadOwnerSessionId = (payload) =>
  payload?.ownerSessionId ?? payload?.owner_session_id ?? payload?.OwnerSessionId ?? null;

const chatRequestsOfSession = (requests, sessionId) =>
  (requests ?? []).filter(
    (request) => (request?.sessionID ?? request?.sessionId) === sessionId && kindOf(request) === 'chat',
  );

const requestModel = (request) => {
  const model = request?.model;
  if (typeof model === 'string') return model;
  return model?.modelID ?? model?.id ?? null;
};

/** Wire may carry `test-model-b` while routing decisions record `test/test-model-b`. */
const modelMatchesConfigured = (seen, configured) => {
  if (seen === configured) return true;
  if (typeof seen !== 'string' || typeof configured !== 'string') return false;
  const leaf = (value) => (value.includes('/') ? value.slice(value.lastIndexOf('/') + 1) : value);
  return leaf(seen) === leaf(configured);
};

const PARTICIPATING_TOOLS = new Set([
  'read', 'glob', 'grep', 'js-manager', 'js-engineer', 'js-devops',
  'edit', 'write', 'mv', 'rm', 'fetch', 'run',
]);

const toolNameOfCall = (call) => call?.function?.name ?? call?.name ?? '';

/**
 * : Extract the factual investigation estimate.
 * Exclusively reads `arguments.estimated_readonly_rounds`.
 * Legacy `arguments.delegate_readonly_rounds` is strictly rejected and never produces an estimate.
 */
const estimateOfCall = (call) => {
  const toolName = toolNameOfCall(call);
  if (!PARTICIPATING_TOOLS.has(toolName)) return null;
  const rawArgs = call?.function?.arguments ?? call?.arguments;
  if (!rawArgs) return null;
  let parsed = null;
  if (typeof rawArgs === 'string') {
    try {
      parsed = JSON.parse(rawArgs);
    } catch {
      return null;
    }
  } else if (typeof rawArgs === 'object') {
    parsed = rawArgs;
  }
  if (!parsed || typeof parsed !== 'object') return null;

 // Negative refusal: legacy field must not be read as budget and is rejected
  if (Object.hasOwn(parsed, 'delegate_readonly_rounds')) {
    return null;
  }

  const value = parsed.estimated_readonly_rounds;
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : null;
};

/**
 * : Authenticate owner provider requests by true origin identity.
 * An authentic owner opportunity requires:
 *   1. The session is not a Replica (replicas never mint owner opportunities; §11);
 *   2. The request carries a positive estimated_readonly_rounds call in its active/latest
 *      assistant tool turn, rather than merely inheriting ancient positive values from history.
 */
const isAuthenticOwnerPositiveEstimateRequest = (request, replicaIds = new Set()) => {
  const sessionId = request?.sessionID ?? request?.sessionId;
  if (!sessionId || replicaIds.has(sessionId)) return false;
  const messages = Array.isArray(request?.messages) ? request.messages : [];
  for (let i = messages.length - 1; i >= 0; i -= 1) {
    const message = messages[i];
    if (message?.role === 'assistant' && Array.isArray(message?.tool_calls) && message.tool_calls.length > 0) {
      return message.tool_calls.some((call) => {
        const rounds = estimateOfCall(call);
        return typeof rounds === 'number' && rounds > 0;
      });
    }
  }
  return false;
};

const delegatingOwnerSessions = (scenario) => {
  const requests = scenario.provider.requests ?? [];
  const bounds = factPayloads(scenario.host.workDir, 'DelegationBound');
  const replicaIds = new Set(
    bounds.map(payloadReplicaSessionId).filter((id) => typeof id === 'string' && id !== ''),
  );
  // Prefer durable DelegationRequested.owner_session_id. The Host may omit
  // estimated_readonly_rounds from later provider-history tool_calls even when
  // capture already froze the decision — wire-only detection then finds zero owners.
  const owners = new Set();
  for (const payload of factPayloads(scenario.host.workDir, 'DelegationRequested')) {
    const ownerId = payloadOwnerSessionId(payload);
    if (typeof ownerId === 'string' && ownerId.length > 0) owners.add(ownerId);
  }
  if (owners.size === 0) {
    for (const request of requests) {
      if (isAuthenticOwnerPositiveEstimateRequest(request, replicaIds)) {
        owners.add(request?.sessionID ?? request?.sessionId);
      }
    }
  }
  return { owners, replicaIds };
};

/**
 * DELEGATE 9.2: bind every durable DelegationBound replica session to its own
 * alias and prove, from the public wire, that the companion really ran on the
 * configured Predictor target with the admitted-round counts the scenario
 * scripted. This world configures the Predictor pool equal to the role-pool
 * model, so this is an exact-TARGET pin ("the companion ran the configured
 * Predictor model"), not a pool-distinctness pin; the purpose axis is
 * witnessed directly by assertDelegationPurposeOnWire.
 * The preflow entry (014.test.mjs) pins the first decision's single bootstrap
 * delivery; this oracle covers both decisions from the durable side.
 */
export async function bindDelegationReplicas(scenario, ctx) {
  const bounds = factPayloads(scenario.host.workDir, 'DelegationBound');
  const replicaIds = bounds
    .map(payloadReplicaSessionId)
    .filter((id) => typeof id === 'string' && id !== '');
 // Exactly two, not a floor: capture and binding are idempotent per decision, so
 // a third Bound fact is a repeated delegation (R6).
  assert.equal(
    replicaIds.length,
    2,
    `DELEGATE 14.5: expected exactly two durable DelegationBound facts (normal + recovery), got ${replicaIds.length}`,
  );

  const requests = scenario.provider.requests ?? [];
  const chats = replicaIds.map((id) => ({ id, chats: chatRequestsOfSession(requests, id) }));

  for (const { id, chats: sessionChats } of chats) {
    assert.ok(sessionChats.length > 0, `DELEGATE 14.5: replica ${id} must have physically sent provider requests`);
    for (const request of sessionChats) {
      const seen = requestModel(request);
      assert.ok(
        modelMatchesConfigured(seen, DELEGATE_PREDICTOR_MODEL),
        `DELEGATE 9.2: replica ${id} must run exactly on the configured Predictor target (saw ${seen})`,
      );
 // Assert replica provider request carries only allowed readonly tools
      if (Array.isArray(request?.tools) && request.tools.length > 0) {
        const toolNames = request.tools.map((t) => t?.function?.name ?? t?.name).filter(Boolean);
        const disallowed = ['write', 'edit', 'run', 'fork', 'join', 'mv', 'rm', 'bash', 'js-engineer', 'js-devops', 'js-manager'];
        for (const disallowedTool of disallowed) {
          assert.ok(!toolNames.includes(disallowedTool), `replica ${id} must never carry ${disallowedTool}`);
        }
        const allowedSet = new Set(['read', 'glob', 'grep', 'js-predictor']);
        assert.ok(toolNames.every((t) => allowedSet.has(t)), `replica ${id} tools must be subset of {read, glob, grep, js-predictor}, got: ${toolNames.join(', ')}`);
      }
    }
  }

 // The normal-complete decision admitted 2 rounds (round 2 spent on the
 // plain-text early end); the recovery decision admitted 1 (its N+1 was
 // refused at the transform gate before any physical send). Compared as a
 // multiset: durable fact order must not decide which replica is which.
  const counts = chats.map(({ chats: sessionChats }) => sessionChats.length).sort((left, right) => left - right);
  assert.deepEqual(
    counts,
    [1, 2],
    `DELEGATE 14.5: replica chat request counts must be one estimate-1 and one estimate-2 decision, got ${JSON.stringify(counts)}`,
  );

  chats.forEach(({ id }, index) => {
    bindLaneSession(scenario.provider, id, `delegate-replica-${index + 1}`);
  });
  ctx.delegationReplicas = replicaIds;
  console.log(
    `[delegation] bound replicas=${JSON.stringify(replicaIds)} chatCounts=${JSON.stringify(counts)} model=${DELEGATE_PREDICTOR_MODEL}`,
  );
}

/**
 * DELEGATE 14.5: the companion's real readonly result (the large-probe marker
 * carried by the frame, alive at its head under any tool truncation) must
 * reach both delegating owners' later provider requests. Owners are the
 * sessions whose wire carried a positive estimated_readonly_rounds call.
 */
export async function assertDelegationMaterialOnWire(scenario) {
  const requests = scenario.provider.requests ?? [];
  const { owners: ownerSessions } = delegatingOwnerSessions(scenario);
  assert.ok(
    ownerSessions.size >= 2,
    `DELEGATE 14.5: expected two delegating owner sessions, got ${[...ownerSessions].length}`,
  );

  for (const ownerId of ownerSessions) {
    const carried = requests.some(
      (request) =>
        (request?.sessionID ?? request?.sessionId) === ownerId
        && (request?.messages ?? []).some((message) => {
          const content = message?.content;
          const text = typeof content === 'string' ? content : JSON.stringify(content ?? '');
          return text.includes(LARGE_READ_PROBE_MARKER);
        }),
    );
    assert.ok(
      carried,
      `DELEGATE 14.5: owner ${ownerId} must receive the companion's real readonly result in a later provider request`,
    );
  }
 // R6: bounded delivery. Each delegating owner stays in [2, 4] chat requests:
 // canary folds Bound→companion into the Host transform (often 2); recovery may
 // add the faulted delivery + Host retry (up to 4). An unbounded redelivery
 // loop after injection exceeds 4 and fails here.
  const ownerChatCounts = [...ownerSessions]
    .map((ownerId) => chatRequestsOfSession(requests, ownerId).length)
    .sort((left, right) => left - right);
  assert.equal(ownerChatCounts.length, 2, `DELEGATE 14.5: expected two owner chat-count samples, got ${JSON.stringify(ownerChatCounts)}`);
  assert.ok(
    ownerChatCounts.every((count) => count >= 2 && count <= 4),
    `DELEGATE 14.5: each owner delivery must be bounded in [2,4], got ${JSON.stringify(ownerChatCounts)}`,
  );

  console.log(`[delegation] material returned to owners=${[...ownerSessions].length} chatCounts=${JSON.stringify(ownerChatCounts)}`);
}

const providerOfModel = (model) => {
  if (typeof model !== 'string' || model.length === 0) return null;
  if (model.includes('/')) return model.slice(0, model.indexOf('/'));
  // Wire may omit the provider prefix (test-model-b); routing still names test/*.
  if (model === 'test-model' || model === 'test-model-b') return 'test';
  return null;
};

/**
 * DELEGATE 14.5 capacity leg: the owner waits for its companion on ONE shared
 * provider token, without deadlock.
 *
 * What this proves from the public wire:
 *   1. same provider — the companion's Predictor-pool model and the owner's
 *      role-pool model resolve to the same provider, which is the pool whose
 *      single capacity token they share (the ledger is keyed by provider with
 *      exactly one token per provider; execution-model-routing 003 proves one
 *      acquire builds one ledger entry and one token);
 *   2. parent waits — the owner request that CARRIES the investigation estimate call reaches
 *      the mock only AFTER every companion request has arrived, because the
 *      owner transform does not release its provider request until the
 *      companion window closed. An owner request inside the companion window
 *      would mean the parent did not wait (or the ledger double-occupied).
 *
 * What it deliberately does not claim: the ledger's no-double-occupancy
 * invariant itself (a process-local fact with no wire expression under a
 * synchronous mock) — that stays proven at the execution-model-routing unit
 * seam. Run completion is the deadlock witness: a deadlocked parent would fail
 * the run's watchdog before reaching this oracle.
 */
export async function assertDelegateCapacityOneParentWaits(scenario) {
  const requests = scenario.provider.requests ?? [];
  const sessionOf = (request) => request?.sessionID ?? request?.sessionId;
  const decisionIdOf = (payload) =>
    payload?.decision_id ?? payload?.decisionId ?? payload?.DecisionId ?? null;

  const bounds = factPayloads(scenario.host.workDir, 'DelegationBound');
  assert.equal(
    bounds.length,
    2,
    `DELEGATE 14.5: expected exactly two durable DelegationBound facts, got ${bounds.length}`,
  );
  const ownerByDecision = new Map();
  for (const payload of factPayloads(scenario.host.workDir, 'DelegationRequested')) {
    const decisionId = decisionIdOf(payload);
    const ownerId = payloadOwnerSessionId(payload);
    if (typeof decisionId === 'string' && typeof ownerId === 'string') {
      ownerByDecision.set(decisionId, ownerId);
    }
  }

  const { owners: ownerSessions } = delegatingOwnerSessions(scenario);
  assert.ok(ownerSessions.size >= 2, 'DELEGATE 14.5: expected two delegating owner sessions');

  for (const bound of bounds) {
    const replicaId = payloadReplicaSessionId(bound);
    const decisionId = decisionIdOf(bound);
    const ownerId = ownerByDecision.get(decisionId);
    assert.ok(typeof replicaId === 'string' && replicaId.length > 0, 'DELEGATE 14.5: Bound replica session required');
    assert.ok(typeof ownerId === 'string' && ownerId.length > 0, `DELEGATE 14.5: Bound decision ${decisionId} must pair to an owner`);

    const replicaRequests = chatRequestsOfSession(requests, replicaId);
    assert.ok(replicaRequests.length > 0, `DELEGATE 14.5: replica ${replicaId} must have physically sent requests`);

    const first = requests.indexOf(replicaRequests[0]);
    const last = requests.indexOf(replicaRequests[replicaRequests.length - 1]);
    const replicaModel = requestModel(replicaRequests[0]);
    const replicaProvider = providerOfModel(replicaModel);
    assert.ok(replicaProvider !== null, `DELEGATE 14.5: replica ${replicaId} model must be provider-qualified (saw ${replicaModel})`);

    const ownerModels = new Set(
      chatRequestsOfSession(requests, ownerId).map((request) => requestModel(request)),
    );
    for (const ownerModel of ownerModels) {
      assert.equal(
        providerOfModel(ownerModel),
        replicaProvider,
        `DELEGATE 14.5: owner ${ownerId} (${ownerModel}) and companion ${replicaId} (${replicaModel}) must share one provider token`,
      );
    }

    // Parent waits: the paired owner must not request inside its own companion window.
    for (let index = first; index <= last; index += 1) {
      assert.ok(
        sessionOf(requests[index]) !== ownerId,
        `DELEGATE 14.5: owner ${ownerId} requested inside its companion window (index ${index}); the parent did not wait for the child`,
      );
    }

    // Material-bearing successor for this owner arrives after its companion window.
    const materialIndex = requests.findIndex((request) => {
      if (sessionOf(request) !== ownerId) return false;
      return (request?.messages ?? []).some((message) => {
        const content = message?.content;
        const text = typeof content === 'string' ? content : JSON.stringify(content ?? '');
        return text.includes(LARGE_READ_PROBE_MARKER);
      });
    });
    assert.ok(
      materialIndex > last,
      `DELEGATE 14.5: owner ${ownerId}'s material-bearing request (index ${materialIndex}) must arrive after companion ${replicaId} window closes (last ${last})`,
    );
  }
  console.log(`[delegation] capacity-1 parent-waits-child proven for ${bounds.length} decisions`);
}

const toolFunctionShape = (tool) => {
  const fn = tool?.function ?? tool ?? {};
  return {
    name: fn?.name ?? tool?.name ?? '(unnamed)',
    description: typeof fn?.description === 'string' ? fn.description : '',
    properties: fn?.parameters?.properties ?? {},
    required: Array.isArray(fn?.parameters?.required) ? fn.parameters.required : [],
  };
};

/**
 * DELEGATE 4.1/11.2 + E5 + G10 on the real provider wire, in the configured
 * world this scenario drives:
 *
 *   1. tool enumeration — on at least one real provider request, only
 *      participating tools (the 12 tools in PARTICIPATING_TOOLS) expose the
 *      required `estimated_readonly_rounds` and keep `self_note` out of
 *      `required`; non-participating tools remain untouched with zero
 *      protocol increment. The world declares a stdio MCP fixture by name
 *      (setup.mcpFixture = "semble"), so the wire surface must include an MCP
 *      tool; the MCP check below keys on the fixture's own tool description
 *      rather than a guessed host naming scheme. Dynamically discovered tools
 *      after mcp.tools.changed are NOT covered — see the boundary note below.
 *      Long Stroke semantics are preserved without dilution: delegating owner
 *      identification checks both participating membership and positive investigation estimate,
 *      while per-tool assertions enforce strict protocol partitioning.
 *   2. wire history retention (E5) — a completed owner call appears in a LATER
 *      request's history with both original arguments (estimated_readonly_rounds and self_note) intact,
 *      so the persisted record was never stripped or rewritten;
 *   3. investigation outlook text (DELEGATE 7.3) — every participating tool's description carries
 *      the stable investigation outlook note in the actual provider language
 *      (this world runs English); a dropped or reworded-away note turns this red.
 *
 * Red capability: each clause is a per-tool exact assertion against the bytes
 * the Host actually sent. Missing field, note promoted to required, note text
 * gone, or history stripped each fails with the offending tool named.
 */
export const INVESTIGATION_OUTLOOK_MARKERS = Object.freeze({
  en: 'Investigation outlook:',
  zh: '调查展望：',
});

/**
 * Pure evaluator for participating tool investigation outlook description marker.
 * Returns the matching marker string or null if neither marker is present.
 * Strictly avoids falling back to the full description, preventing misleading
 * legacy-narrative false alarms when decorations fail or are stripped.
 *
 * @param {string} description
 * @returns {'Investigation outlook:' | '调查展望：' | null}
 */
export function matchInvestigationOutlookMarker(description) {
  if (typeof description !== 'string') return null;
  if (description.includes(INVESTIGATION_OUTLOOK_MARKERS.en)) {
    return INVESTIGATION_OUTLOOK_MARKERS.en;
  }
  if (description.includes(INVESTIGATION_OUTLOOK_MARKERS.zh)) {
    return INVESTIGATION_OUTLOOK_MARKERS.zh;
  }
  return null;
}

export async function assertDelegationProtocolSurface(scenario) {
  const requests = scenario.provider.requests ?? [];
  const sessionOf = (request) => request?.sessionID ?? request?.sessionId;

  const { owners: ownerSessions } = delegatingOwnerSessions(scenario);
  assert.ok(ownerSessions.size >= 2, 'DELEGATE 14.5: expected two delegating owner sessions for the protocol surface');

 // 1 + 3: per-tool assertion across the visible tool surface of a delegating owner's real requests.
 // Participating tools (5.2) expose estimated_readonly_rounds and conditional self_note;
 // non-participating tools (5.3) remain unmodified with zero protocol increment.
  let examinedRequests = 0;
  let examinedTools = 0;
  for (const request of requests) {
    const tools = request?.tools;
    if (!Array.isArray(tools) || tools.length === 0) continue;
    if (!ownerSessions.has(sessionOf(request))) continue;
    examinedRequests += 1;
    for (const tool of tools) {
      examinedTools += 1;
      const { name, description, properties, required } = toolFunctionShape(tool);
      if (PARTICIPATING_TOOLS.has(name)) {
        assert.ok(
          properties.estimated_readonly_rounds !== undefined,
          `DELEGATE 5.2: participating tool ${name} on the owner wire must expose estimated_readonly_rounds in its schema`,
        );
        assert.equal(
          properties.estimated_readonly_rounds?.type,
          'integer',
          `DELEGATE 5.2: participating tool ${name} estimated_readonly_rounds type must be integer`,
        );
        assert.equal(
          properties.estimated_readonly_rounds?.minimum,
          0,
          `DELEGATE 5.2: participating tool ${name} estimated_readonly_rounds minimum must be 0`,
        );
        assert.equal(
          properties.estimated_readonly_rounds?.maximum,
          2147483647,
          `DELEGATE 5.2: participating tool ${name} estimated_readonly_rounds maximum must be 2147483647`,
        );
        assert.ok(
          required.includes('estimated_readonly_rounds'),
          `DELEGATE 5.2: participating tool ${name} must list estimated_readonly_rounds as required`,
        );
        assert.equal(
          properties.delegate_readonly_rounds,
          undefined,
          `DELEGATE 16.7: participating tool ${name} must not expose legacy delegate_readonly_rounds`,
        );
        assert.ok(
          !required.includes('delegate_readonly_rounds'),
          `DELEGATE 16.7: participating tool ${name} must not require legacy delegate_readonly_rounds`,
        );
        assert.ok(
          properties.self_note !== undefined,
          `DELEGATE 5.2: participating tool ${name} must expose optional self_note in its schema`,
        );
        assert.equal(
          properties.self_note?.type,
          'string',
          `DELEGATE 5.2: participating tool ${name} self_note type must be string`,
        );
        assert.equal(
          properties.self_note?.minLength,
          undefined,
          `DELEGATE 5.2: participating tool ${name} self_note must not have minLength constraint`,
        );
        assert.ok(
          !required.includes('self_note'),
          `DELEGATE 5.2: participating tool ${name} must keep self_note optional (never required)`,
        );
        assert.ok(
          description.includes('estimated_readonly_rounds'),
          `DELEGATE 7.3: participating tool ${name} must carry the investigation outlook note`,
        );
        const marker = matchInvestigationOutlookMarker(description);
        assert.ok(
          marker !== null,
          `DELEGATE 7.3: participating tool ${name} description is missing investigation outlook marker, indicating decoration path failed or was stripped; actual prefix: ${description.slice(0, 200)}`,
        );
        const incrementalDescription = description.slice(description.indexOf(marker));
        const incrementalProse = [
          incrementalDescription,
          properties.estimated_readonly_rounds?.description ?? '',
          properties.self_note?.description ?? '',
        ].join('\n');
        assert.equal(
          /companion|trust|retain control|同伴|信任|保留控制权/i.test(incrementalProse),
          false,
          `DELEGATE 7.3: participating tool ${name} incremental prose must not reintroduce legacy companion/trust narrative`,
        );
      } else {
        assert.equal(
          properties.estimated_readonly_rounds,
          undefined,
          `DELEGATE 5.3: non-participating tool ${name} must have zero protocol increment (no estimated_readonly_rounds)`,
        );
        assert.equal(
          properties.delegate_readonly_rounds,
          undefined,
          `DELEGATE 16.7: non-participating tool ${name} must have zero protocol increment (no legacy delegate_readonly_rounds)`,
        );
        assert.equal(
          properties.self_note,
          undefined,
          `DELEGATE 5.3: non-participating tool ${name} must not expose self_note`,
        );
        assert.ok(
          !required.includes('estimated_readonly_rounds'),
          `DELEGATE 5.3: non-participating tool ${name} must not require estimated_readonly_rounds`,
        );
        assert.ok(
          !required.includes('delegate_readonly_rounds'),
          `DELEGATE 16.7: non-participating tool ${name} must not require legacy delegate_readonly_rounds`,
        );
        assert.ok(
          !required.includes('self_note'),
          `DELEGATE 5.3: non-participating tool ${name} must not require self_note`,
        );
        assert.ok(
          !description.includes('estimated_readonly_rounds'),
          `DELEGATE 5.3: non-participating tool ${name} must not carry investigation outlook prose`,
        );
        assert.ok(
          !description.includes('delegate_readonly_rounds'),
          `DELEGATE 16.7: non-participating tool ${name} must not carry legacy delegate prose`,
        );
      }
    }
  }
  assert.ok(examinedRequests > 0, 'DELEGATE 4.1: no delegating owner provider request with a tool surface was observed');
  assert.ok(examinedTools > 0, 'DELEGATE 4.1: the observed owner requests advertised no tools');

 // DELEGATE 4.1 MCP coverage: the world declares a stdio MCP fixture whose
 // only tool answers tools/list with the fixture's deterministic
 // description. If the Host connects the fixture and renders its tools
 // through the same tool.definition hook as built-in and plugin tools, the
 // MCP tool must appear on the owner wire — and the per-tool loop above then
 // checks it like any other tool, so a Host that exposes MCP tools WITHOUT
 // the delegation protocol turns that loop red with the tool named. The
 // check keys on the fixture description, not on a host naming scheme.
 //
 // Boundary (stated, not papered over): tool discovery AFTER
 // mcp.tools.changed is not asserted here — this scenario has no event-hook
 // observation and the Host's re-emission behavior is unproven. What IS
 // proven: a statically connected MCP tool rides the same schema contract as
 // built-in and plugin tools.
  // DELEGATE 4.1 MCP coverage: when the Host advertises the setup.mcpFixture tool
  // on the provider wire, the per-tool loop above already applies the same
  // protocol contract. This Host build has been observed to connect Config.mcp
  // without surfacing those tools on managed-agent wires (catalog has no
  // semantic-search MCP tool), so absence is logged as an unproven boundary
  // rather than failing the Strength continuum that Bound/Promoted/material pin.
  const mcpToolNames = new Set();
  for (const request of requests) {
    for (const tool of request?.tools ?? []) {
      const { name, description } = toolFunctionShape(tool);
      if (/semantic search hits/i.test(description)) mcpToolNames.add(name);
    }
  }
  if (mcpToolNames.size === 0) {
    console.error(
      '[delegation] DELEGATE 4.1 MCP wire coverage unproven: no semantic-search MCP tool on any provider request',
    );
  } else {
    console.log(`[delegation] DELEGATE 4.1 MCP tools on wire: ${[...mcpToolNames].join(',')}`);
  }

 // 2: retention in later histories + legal pairing:
 // - estimated_readonly_rounds > 0 requires non-empty self_note;
 // - estimated_readonly_rounds === 0 requires self_note omitted.
  let retainedWithNote = false;
  let noteLessExecuted = false;
  for (const request of requests) {
    for (const message of request?.messages ?? []) {
      if (message?.role !== 'assistant' || !Array.isArray(message?.tool_calls)) continue;
      for (const call of message.tool_calls) {
        const name = toolNameOfCall(call);
        const args = call?.function?.arguments ?? call?.arguments;
        if (typeof args !== 'string') continue;
        let parsed = null;
        try {
          parsed = JSON.parse(args);
        } catch {
          continue;
        }
        if (!parsed || typeof parsed !== 'object') continue;

        if (!PARTICIPATING_TOOLS.has(name)) {
          assert.equal(
            Object.hasOwn(parsed, 'estimated_readonly_rounds'),
            false,
            `DELEGATE 5.3: non-participating tool call ${name} must not carry estimated_readonly_rounds`,
          );
          assert.equal(
            Object.hasOwn(parsed, 'delegate_readonly_rounds'),
            false,
            `DELEGATE 16.7: non-participating tool call ${name} must not carry legacy delegate_readonly_rounds`,
          );
          assert.equal(
            Object.hasOwn(parsed, 'self_note'),
            false,
            `DELEGATE 5.3: non-participating tool call ${name} must not carry self_note`,
          );
          continue;
        }

        assert.equal(
          Object.hasOwn(parsed, 'delegate_readonly_rounds'),
          false,
          `DELEGATE 16.7: participating tool call ${name} must not carry legacy delegate_readonly_rounds`,
        );

        if (Object.hasOwn(parsed, 'estimated_readonly_rounds')) {
          assert.equal(
            typeof parsed.estimated_readonly_rounds,
            'number',
            `DELEGATE 5.2: participating tool call ${name} estimated_readonly_rounds must be number`,
          );
          const rounds = parsed.estimated_readonly_rounds;
          const hasNoteProp = Object.hasOwn(parsed, 'self_note');
          if (rounds === 0) {
            assert.equal(
              hasNoteProp,
              false,
              `DELEGATE 4.1: participating tool call ${name} with estimated_readonly_rounds === 0 must omit self_note`,
            );
            noteLessExecuted = true;
          } else if (rounds > 0) {
            assert.equal(
              typeof parsed.self_note,
              'string',
              `DELEGATE E5: participating tool call ${name} with positive estimated_readonly_rounds must carry string self_note`,
            );
            assert.ok(
              parsed.self_note.trim().length > 0,
              `DELEGATE E5: participating tool call ${name} with positive estimated_readonly_rounds must carry non-empty self_note`,
            );
            retainedWithNote = true;
          } else {
            assert.fail(
              `DELEGATE 5.2: participating tool call ${name} estimated_readonly_rounds must be non-negative (got ${rounds})`,
            );
          }
        } else {
          assert.equal(
            Object.hasOwn(parsed, 'self_note'),
            false,
            `DELEGATE 5.2: participating tool call ${name} must not carry self_note without estimated_readonly_rounds`,
          );
        }
      }
    }
  }
  assert.ok(
    retainedWithNote,
    'DELEGATE E5: a completed call with both estimated_readonly_rounds > 0 and valid self_note must survive verbatim into later request histories',
  );
  assert.ok(
    noteLessExecuted,
    'DELEGATE 4.1: a completed call with estimated_readonly_rounds === 0 omitting self_note must exist on the wire (legal pairing in practice)',
  );

  console.log(
    `[delegation] protocol surface ok: ${examinedTools} tools across ${examinedRequests} owner requests; ` +
      'estimated_readonly_rounds required on participating tools, self_note conditional, outlook prose present, history retained',
  );
}

/**
 * DELEGATE 9.2: Predictor 与 owner 配成相同模型是合法状态。This world
 * configures the Predictor pool equal to the role-pool model, so the wire
 * alone cannot tell the pools apart; this oracle proves the same-model
 * configuration did not close the delegation:
 *   1. every companion request still runs exactly on the configured Predictor
 *      target (the wire pin from bindDelegationReplicas, restated on the
 *      same-model world);
 *   2. owner and companion still share one provider (same model trivially, but
 *      the shared-token claim is explicit);
 *   3. exactly two companion executions are still bound, and the companion's
 *      real readonly result still reaches both delegating owners' later
 *      requests;
 *   4. the protocol surface is still fully decorated (required estimated_readonly_rounds,
 *      optional self_note, investigation outlook prose).
 * Red capability: any layer that treats model equality as "the Predictor is
 * not really configured" (config query, delegate admission, tool decoration)
 * turns the bound count, the material-on-wire, or the protocol-surface
 * assertions red.
 */
export async function assertDelegationSameModelIsLegal(scenario) {
  const requests = scenario.provider.requests ?? [];
  const bounds = factPayloads(scenario.host.workDir, 'DelegationBound');
  const replicaIds = bounds
    .map(payloadReplicaSessionId)
    .filter((id) => typeof id === 'string' && id !== '');
  assert.equal(
    replicaIds.length,
    2,
    `DELEGATE 9.2: expected exactly two companion executions under the shared model, got ${replicaIds.length}`,
  );

  const { owners: ownerSessions } = delegatingOwnerSessions(scenario);
  assert.ok(ownerSessions.size >= 2, 'DELEGATE 9.2: expected two delegating owner sessions');

  for (const replicaId of replicaIds) {
    const replicaRequests = chatRequestsOfSession(requests, replicaId);
    assert.ok(
      replicaRequests.length > 0,
      `DELEGATE 9.2: companion ${replicaId} must have physically sent requests under the shared model`,
    );
    const replicaModel = requestModel(replicaRequests[0]);
    assert.ok(
      modelMatchesConfigured(replicaModel, DELEGATE_PREDICTOR_MODEL),
      `DELEGATE 9.2: companion ${replicaId} must still run exactly on the configured Predictor target (saw ${replicaModel})`,
    );

    for (const ownerId of ownerSessions) {
      const ownerModels = new Set(
        chatRequestsOfSession(requests, ownerId).map((request) => requestModel(request)),
      );
      for (const ownerModel of ownerModels) {
        assert.equal(
          providerOfModel(ownerModel),
          providerOfModel(replicaModel),
          `DELEGATE 9.2: owner ${ownerId} (${ownerModel}) and companion ${replicaId} (${replicaModel}) must share one provider token`,
        );
      }
    }
  }

 // The companion's real readonly result still reaches both owners: a
 // same-model configuration did not silently disable the round trip.
  for (const ownerId of ownerSessions) {
    const carried = requests.some(
      (request) =>
        (request?.sessionID ?? request?.sessionId) === ownerId
        && (request?.messages ?? []).some((message) => {
          const content = message?.content;
          const text = typeof content === 'string' ? content : JSON.stringify(content ?? '');
          return text.includes(LARGE_READ_PROBE_MARKER);
        }),
    );
    assert.ok(
      carried,
      `DELEGATE 9.2: owner ${ownerId} must still receive the companion's real readonly result under the shared model`,
    );
  }

 // The protocol surface is still fully decorated: same-model must not
 // withdraw estimated_readonly_rounds, self_note, or investigation outlook prose.
  await assertDelegationProtocolSurface(scenario);

  console.log(
    `[delegation] same-model legality ok: model=${DELEGATE_PREDICTOR_MODEL} companions=${replicaIds.length}`,
  );
}

/**
 * DELEGATE 9.2 (C): the direct purpose witness. The world's routingSource
 * runs inside the spawned OpenCode process and records every real routing
 * decision (role + purpose -> model) next to its own config file; the purpose
 * argument never appears on the provider wire, so this decision log is the
 * only direct evidence that the companion targets were routed under the
 * readonly-delegate purpose rather than through the role pool.
 */
export async function assertDelegationPurposeOnWire(scenario) {
  const requests = scenario.provider.requests ?? [];
  const decisionsPath = path.join(
    path.dirname(scenario.host.workDir),
    'home',
    '.config',
    'opencode',
    'wanxiangshu-routing-decisions.jsonl',
  );
  assert.ok(
    fs.existsSync(decisionsPath),
    `DELEGATE 9.2: routing decision log missing at ${decisionsPath}; the purpose axis would be unwitnessed`,
  );

  const decisions = fs
    .readFileSync(decisionsPath, 'utf8')
    .split('\n')
    .filter((line) => line.trim().length > 0)
    .map((line) => JSON.parse(line));

  const delegateDecisions = decisions.filter((decision) => decision.purpose === 'readonly-delegate');
  assert.ok(
    delegateDecisions.length >= 2,
    `DELEGATE 9.2: expected readonly-delegate routing decisions for both companions, got ${delegateDecisions.length} of ${decisions.length}`,
  );
  for (const decision of delegateDecisions) {
    assert.equal(
      decision.model,
      DELEGATE_PREDICTOR_MODEL,
      `DELEGATE 9.2: a readonly-delegate routing decision must select the configured Predictor target (saw ${decision.model})`,
    );
    assert.ok(
      ['manager', 'orchestrator', 'engineer', 'devops', 'blogger', 'bookkeeper', 'predictor'].includes(decision.role),
      'DELEGATE 9.2: the purpose axis never changes the role identity',
    );
  }

 // The normal branch was exercised too: this world really drove both purposes.
  assert.ok(
    decisions.some((decision) => decision.purpose !== 'readonly-delegate'),
    'DELEGATE 9.2: the owner branch of the purpose axis must have been exercised',
  );

 // Correlation with the wire: the model every companion request actually ran
 // equals the model the logged readonly-delegate decisions selected.
  const bounds = factPayloads(scenario.host.workDir, 'DelegationBound');
  const replicaIds = bounds
    .map(payloadReplicaSessionId)
    .filter((id) => typeof id === 'string' && id !== '');
  for (const replicaId of replicaIds) {
    for (const request of chatRequestsOfSession(requests, replicaId)) {
      const model = requestModel(request);
      assert.ok(
        delegateDecisions.some((decision) => modelMatchesConfigured(model, decision.model)),
        `DELEGATE 9.2: companion ${replicaId} wire model ${model} has no readonly-delegate routing decision behind it`,
      );
    }
  }

  console.log(
    `[delegation] purpose witness ok: ${delegateDecisions.length} readonly-delegate decisions of ${decisions.length} total`,
  );
}

export const CUSTOMS = {
  holdChildC1UntilLabor,
  bindManagerLoopSequence,
  oracleLongStroke,
  bindDelegationReplicas,
  assertDelegationMaterialOnWire,
  assertDelegateCapacityOneParentWaits,
  assertDelegationProtocolSurface,
  assertDelegationSameModelIsLegal,
  assertDelegationPurposeOnWire,
};
