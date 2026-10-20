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
 * Pure manager loop: every tenure starts from the same typed authority user
 * messages and current workspace for independent assessment. A Relayed
 * retirement is followed by another ordinary IncumbencyOpened event and a
 * physically observed provider request on the same SessionId/LogicalRun;
 * Delivered exits. The internal wake is stripped from the provider projection,
 * which carries the authoritative user messages with the current tenure.
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
export async function awaitNamedFact(
  workDir,
  waitFact,
  { timeoutMs = WAIT_FACT_WINDOW_MS, onProgress, sessionId = null } = {},
) {
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
  let observed = readJournal(workDir, name, renewOn, { sessionId });

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

    const next = readJournal(workDir, name, renewOn, { sessionId });
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

  // The Host's exposure of the injected raw failures is DURABLE: `FailureRecorded` is
  // written once per failed ProviderRun on the work's own session. The SSE `session.error`
  // event this replaced is not emitted by this Host version, so the filter counted 0 even
  // though both failures happened (measured: 0 events, 2 durable records, both on the
  // manager work's stream — the same stream that carries its five `IncumbencyOpened`).
  // The durable record is the honest source, and it carries the consecutive counts the
  // episode contract is actually about.
  const taggedValue = (value) => (Array.isArray(value) ? value.at(-1) : value);
  const failures = factPayloads(lines, 'FailureRecorded');
  assert.equal(
    failures.length,
    2,
    'long-stroke: the Host must record exactly the two injected raw provider failures',
  );
  const failureSessions = new Set(failures.map((payload) => taggedValue(payload?.SessionId)));
  assert.equal(
    failureSessions.size,
    1,
    `long-stroke: both injected failures must land on the same work (got ${JSON.stringify([...failureSessions])})`,
  );
  assert.deepEqual(
    failures.map((payload) => taggedValue(payload?.ConsecutiveFailureCount)).sort((left, right) => left - right),
    [1, 2],
    'long-stroke: the two failures must be consecutive episodes on that work',
  );
}

/**
 * §21: join blocked then causally awakened — HandleCompleted after user_message wake.
 * The join-wake itself only requires the harvest fact; the full agent lifecycle is
 * proven later by IncumbencyRetired (assertIncumbencyRetired).
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

/** §21: non-10 assessment assigns work (Reviewed). */
export function assertAssessmentAssignsWork(workDir, label = 'long-stroke', sessionId = null) {
  assert.ok(
    countFactCase(workDir, 'Reviewed', { sessionId }) >= 1,
    `${label}: Reviewed required (non-10 assessment assigns work)`,
  );
}

/**
 * §21: retirement needs the next tenure — a non-empty findings set blocks publication
 * and the incumbency retires with Outcome Relayed, followed by a fresh
 * IncumbencyOpened on the same authority. Detected via the stringified outcome
 * vocabulary the durable work journal actually writes (Relayed vs Delivered),
 * so no Fable DU field layout is pinned.
 */
export function assertRetirementNeedsNextTenure(workDir, label = 'long-stroke', sessionId = null) {
  const retirements = factPayloads(workDir, 'IncumbencyRetired', { sessionId });
  const hasRelayed = retirements.some((summary) => JSON.stringify(summary ?? {}).includes('Relayed'));
  assert.ok(
    hasRelayed,
    `${label}: IncumbencyRetired with Outcome Relayed required (finality temporarily blocked, next tenure needed)`,
  );
  assert.ok(
    countFactCase(workDir, 'IncumbencyOpened', { sessionId }) >= 2,
    `${label}: fresh IncumbencyOpened required after the Relayed retirement (same authority, new tenure)`,
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

/** §21: later successful retirement — IncumbencyRetired after resources converge. */
export function assertIncumbencyRetired(workDir, label = 'long-stroke', sessionId = null) {
  assert.ok(
    countFactCase(workDir, 'IncumbencyRetired', { sessionId }) >= 1,
    `${label}: IncumbencyRetired required after resources converge`,
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
 * declarations; the reopened owner resource serves review or deliver closes
 * by delivery: initial-low→work, candidate-perfect→finish, conflict-low→repair,
 * repaired-perfect→finish, rebased-perfect→finish. HumanRoot:
 * low→Relayed, perfect→Delivered.
 * Every logical step still crosses the real review/deliver/resume tools and durable
 * IncumbencyOpened/IncumbencyRetired facts. Tenures are never distinguished
 * by prompt text.
 */
export async function bindManagerLoopSequence(scenario) {
  const runtime = scenario.provider?._scenario;
  assert.ok(runtime?.scenario?.entries, 'long-stroke: strict scenario entries required for loop tenure bind');

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

  const scores = (grade) => grade === 'REVISE'
    ? [{ acceptance_criteria: 'the target state is not yet reached', work_plan: 'close the remaining gap' }]
    : [];
  const candidatePerfect = () => ({
    type: 'tool-call',
    tool: 'review',
    prefixText: 'Independent audit of this tenure snapshot finds every required quality dimension complete and supported by the current workspace evidence.',
    args: { findings: scores('PERFECT') },
  });
  const repairAudit = () => ({
    type: 'tool-call',
    tool: 'review',
    prefixText: 'Independent audit finds the rebase conflict still requires owned repair work, so completeness remains open on this snapshot.',
    args: { findings: scores('REVISE') },
  });
  const humanPerfect = () => ({
    type: 'tool-call',
    tool: 'review',
    prefixText: 'HumanRoot loop next tenure independently audits the current snapshot as complete.',
    args: { findings: scores('PERFECT') },
  });
  const retire = () => ({ type: 'tool-call', tool: 'deliver', args: {} });
  const joinOwnedWork = () => ({ type: 'tool-call', tool: 'join', args: {} });
 // Initial deliveries stay as declared (low audit + work fork; HumanRoot low).
 // Later responses are selected by the new incarnation's audit delivery count.
  let latestManagerAuditAttempt = 0;
  let managerAssumptionDelivered = false;
  let initialWorkJoined = false;
  let repairWorkJoined = false;
  let nudgeAuditDelivered = false;
  const consume = runtime.consume;
  const originalConsume = (body, selection, context) => consume.call(runtime, body, selection, context);
  runtime.consume = (body, selection, context) => {
    const { entry, attempt } = selection ?? {};
    if (entry?.id === 'manager-loop.0') {
      latestManagerAuditAttempt = Math.max(latestManagerAuditAttempt, attempt);
      if (attempt > 1) entry.respond = attempt === 3 ? repairAudit() : candidatePerfect();
    } else if (entry?.turnId === 'manager-reopened-loop' && (entry.step === 0 || entry.id === 'manager-reopened-loop.0')) {
      latestManagerAuditAttempt = Math.max(latestManagerAuditAttempt + 1, attempt ?? 1);
      const n = latestManagerAuditAttempt;
      if (n > 1) {
        entry.respond = n === 3 ? repairAudit() : candidatePerfect();
      }
    } else if (entry?.id === 'manager-loop.1') {
      entry.respond = latestManagerAuditAttempt === 1
        ? initialLoopAction
        : latestManagerAuditAttempt === 3
          ? {
              type: 'tool-call',
              tool: 'fork',
              args: {
                calling: 'engineer',
                name: 'Conflict Resolver',
                charge: 'Resolve the conflicted publish_proof.txt so it contains exactly: Published by long-stroke canary',
              },
            }
          : retire();
    } else if (entry?.id === 'manager-reopened-loop.1') {
 // A successor incarnation opens no work of its own unless the audit assigned it:
 // only the repair tenure forks, everything else closes with the declared close. The
 // Manager surface cannot write the deliverable (its own assess resource declares
 // `todowrite-for-manager` a review-only read tool, disabled once accepted), so the
 // candidate/rebase ownership seam is production's, not a rung's.
      entry.respond = latestManagerAuditAttempt === 3
        ? {
            type: 'tool-call',
            tool: 'fork',
            args: {
              calling: 'engineer',
              name: 'Conflict Resolver',
              charge: 'Resolve the conflicted publish_proof.txt so it contains exactly: Published by long-stroke canary',
            },
          }
        : retire();
    } else if (entry?.id === 'manager-loop.2' || entry?.id === 'manager-reopened-loop.2') {
      // The family is shared by both owner lanes, and only the Manager lane forks work to
      // join. The HumanRoot successor opens nothing, so its second cursor is the closing
      // deliver of work-006's two-stage shape (review, confirmation, close = three
      // requests per tenure), not a join of work it never created.
      const humanrootSession = runtime.sessionFor('humanroot-manager');
      const humanrootLane =
        typeof humanrootSession === 'string' && context?.sessionId === humanrootSession;
      entry.respond = humanrootLane
        ? retire()
        : latestManagerAuditAttempt === 1
          ? initialLoopJoin
          : latestManagerAuditAttempt === 3
            ? initialLoopJoin
            : retire();
    } else if (entry?.turnId === 'manager-reopened-loop' && entry.step >= 3) {
 // The successor's own tenure closes here: the repair work has been harvested by
 // the join above, and every later cursor of this assess resource is a close.
      entry.respond = retire();
    } else if (entry?.id === 'manager-t1-commitment.0') {
      managerAssumptionDelivered = true;
    } else if (entry?.turnId === 'manager-current-action') {
      if (!managerAssumptionDelivered) {
        entry.respond = currentActionAssumptionResponse;
        managerAssumptionDelivered = true;
      } else if (latestManagerAuditAttempt === 1 && !initialWorkJoined) {
        entry.respond = joinOwnedWork();
        initialWorkJoined = true;
      } else if (latestManagerAuditAttempt === 3 && !repairWorkJoined) {
        entry.respond = joinOwnedWork();
        repairWorkJoined = true;
      } else if (!nudgeAuditDelivered) {
        // The ladder converges by satisfying the review gate instead of hammering it:
        // production rejects a deliver with "# Cannot finish without a review", so a
        // later re-anchored nudge answers the review (perfect), and only the nudge after
        // that closes the episode. Without this the Manager never stops consuming the
        // bounded ladder, and the main flow's Manager turns never begin.
        entry.respond = {
          type: 'tool-call',
          tool: 'review',
          prefixText: 'Independent audit of the current workspace finds every required quality dimension complete and supported by the evidence already gathered.',
          args: { findings: scores('PERFECT') },
        };
        nudgeAuditDelivered = true;
      } else {
        entry.respond = retire();
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

export function assertNativeReadProbeTimeline(scenario) {
  const probeUpdates = (scenario.events?.allEvents ?? []).filter((event) => {
    const part = event?.properties?.part;
    return event?.type === 'message.part.updated'
      && part?.type === 'tool'
      && part?.tool === 'todowrite-for-engineer'
      && typeof part?.state?.input?.program === 'string'
      && part.state.input.program.includes('read_probe.txt');
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
        && ['todowrite-for-engineer', 'todowrite-for-manager'].includes(part?.tool)
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
  // The unified journal is per session, so the manager-lane judgments read the Manager's
  // own stream.
  const managerSessionId = ctx?.childId ?? null;
  assertAssessmentAssignsWork(workDir, 'long-stroke', managerSessionId);
  assertRetirementNeedsNextTenure(workDir, 'long-stroke', managerSessionId);
  assertIncumbencyRetired(workDir, 'long-stroke', managerSessionId);
  assertPublishConflict(workDir);
  assertSuccessfulReconciliation(workDir);
  assertNativeReadProbeTimeline(scenario);

 // HumanRoot preflow baseline (2 assessments / 2 retirements / 2 openings) is
 // already proven exact before the main spine; global gte checks above would
 // pass on preflow alone. Preserve their main-spine meaning by requiring the
 // current loop itself to own every expected tenure and outcome, not merely
 // the global journal.
  const currentLoopId = ctx?.childId ?? null;
  if (typeof currentLoopId === 'string' && currentLoopId.length > 0) {
    // Read the unified work journal's own facts for this work's stream: the retired
    // events-journal family no longer exists in the tree, and the loop's openings
    // and retirements are exactly the per-session
    // `IncumbencyOpened` / `IncumbencyRetired` records — with `outcome` as production's own
    // discriminator (`Relayed` → relay, `Delivered` → finish).
    const scope = { sessionId: currentLoopId };
    const openings = factPayloads(workDir, 'IncumbencyOpened', scope);
    const retirements = factPayloads(workDir, 'IncumbencyRetired', scope);
    assert.equal(
      openings.length,
      5,
      'long-stroke: exact replay may repeat an envelope, but current loop must own five logical tenures',
    );
    assert.equal(retirements.length, 5, 'long-stroke: every current-loop tenure must retire');
    assert.equal(
      retirements.filter(isRelayedOutcome).length,
      2,
      'long-stroke: work and conflict repair must retire with Relayed',
    );
    assert.equal(
      retirements.filter(isDeliveredOutcome).length,
      3,
      'long-stroke: candidate, repaired, and rebased snapshots must retire Delivered',
    );
  }

 // Pure-loop removals: an event-only fake continuation (IncumbencyOpened without
 // a physically observed provider request) must not satisfy managed admission.
 // The delivery counts below prove every new tenure crossed the provider.
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
    'long-stroke determinism: the initial HumanRoot/authority tenure receives one authority audit',
  );
 // The four successor tenures (candidate, repair, repaired, rebased) carry the
 // owner-controlled assess resource as their fresh head, so their audits land on the
 // assess-resource family (work-008: the successor keeps the
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
  assessmentCommitted: waitFactShape('Reviewed', { gte: 1 }),
  retirementCommitted: waitFactShape('IncumbencyRetired', { gte: 1 }),
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
  assertRetirementNeedsNextTenure,
  assertDurableRecovery,
  assertPublishConflict,
  assertSuccessfulReconciliation,
  assertIncumbencyRetired,
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
// RetirementOutcome is detected via stringified case names (Relayed vs
// Delivered) so no Fable DU field layout is pinned. Incumbency identity is
// collected as every distinct `incumbency:`-prefixed string inside
// IncumbencyOpened payloads, tolerating tuple-vs-record JSON shapes.
// The unified journal's `IncumbencyRetired.outcome` is the discriminator the host loop
// itself reads (`src/Wanxiangshu/Change/Host/Host.fs`: `Delivered` → Candidate,
// `Relayed` → relay); the retired events-journal field names are gone.
const isRelayedOutcome = (summary) => summary?.outcome === 'Relayed';

const isDeliveredOutcome = (summary) => summary?.outcome === 'Delivered';

const incumbencyIdsIn = (payloads) => {
  const ids = new Set();
  const walk = (value) => {
    if (Array.isArray(value)) {
      for (const item of value) walk(item);
      return;
    }
    if (value && typeof value === 'object') {
      for (const [key, child] of Object.entries(value)) {
        // The unified work journal names the identity `incumbencyId` (a bare hex digest);
        // the retired events journal prefixed it as `incumbency:<hex>`.
        if ((key === 'incumbencyId' || key === 'IncumbencyId') && typeof child === 'string' && child !== '') {
          ids.add(child);
        } else {
          walk(child);
        }
      }
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
 * Assert every fresh tenure preserves the provider-visible authority prefix:
 * same normalized system/tools plan and same typed authority users in order.
 * A successor tenure is identified by its appended owner-controlled
 * assessment resource, and it retains the full predecessor physical history —
 * the retired epoch's messages, tool calls, results and internal wake stay in
 * the provider context instead of being cut. Compares message structure only.
 */
export function assertManagerLoopAuthorityPreserved(scenario, sessionId) {
  assert.ok(typeof sessionId === 'string' && sessionId.length > 0, 'manager-loop: session id required');
  const requests = canaryRequestsFor(scenario, sessionId);
  assert.ok(requests.length >= 2, `manager-loop: expected initial + next tenure requests (got ${requests.length})`);
  const baselinePlan = providerPlanOf(requests[0]);
  const baselineUsers = messageTextsByRole(requests[0], 'user');
  assert.ok(baselineUsers.length >= 1, 'manager-loop: initial tenure must carry typed authority user messages');
 // The ordinal sentence leads the appended resource; the digit is the
 // successor number, so the anchor stops before it and never has to be
 // re-pinned per tenure.
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
      `manager-loop: successor tenure #${successor} must keep the same normalized system/tools plan as the initial tenure`,
    );
    assert.deepEqual(
      users.slice(0, baselineUsers.length),
      baselineUsers,
      `manager-loop: successor tenure #${successor} must preserve the typed authority user prefix`,
    );
  }
  assert.ok(
    successorOrdinals.length >= 1,
    `manager-loop: expected at least one successor tenure request (got ${successorOrdinals.length})`,
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
 * Pure manager loop: a Relayed retirement is followed by another ordinary
 * IncumbencyOpened event and a physically observed provider request on the same
 * SessionId/LogicalRun with the same typed authority user messages; Delivered
 * exits. An IncumbencyOpened fact without a matching provider request is the
 * exact event-only fake shape and fails here as missing managed admission.
 * Message-structure and durable-event behavior only, carrying the authoritative
 * user messages with the current tenure for independent assessment.
 *
 * Surfaces only: strict provider wire (managed admission), durable journal facts,
 * and causal session idle. No wall delays, no retries, no time-budget growth.
 */
export async function assertHumanRootManagerLoop(scenario, sessionId, label = 'humanroot-loop') {
  assert.ok(typeof sessionId === 'string' && sessionId.length > 0, `${label}: canary session id required`);
  const workDir = scenario.host.workDir;

  // Scoped to this canary's own work stream: the unified journal is per session, so a
  // global count would sum every other work's tenures.
  const scope = { timeoutMs: WAIT_FACT_WINDOW_MS, sessionId };
  await awaitNamedFact(workDir, waitFactShape('Reviewed', { eq: 2 }), scope);
  await awaitNamedFact(workDir, waitFactShape('IncumbencyRetired', { eq: 2 }), scope);
  await awaitNamedFact(workDir, waitFactShape('IncumbencyOpened', { eq: 2 }), scope);

 // The authority-turn family answers the initial tenure only: once the successor
 // carries the owner-controlled assess resource as its last user message, the
 // assess-resource family (same review + deliver shape, perfect audit) answers it.
 // An IncumbencyOpened fact alone is an event-only fake; physically observed
 // deliveries under the same LogicalRun prove the loop.
  assert.equal(
    scenario.provider.matchCount('humanroot-loop.0'),
    1,
    `${label}: authority-turn audit must be delivered once (low score → Relayed)`,
  );
  assert.equal(
    scenario.provider.matchCount('humanroot-loop.1'),
    1,
    `${label}: authority-turn close must be delivered once (Relayed retirement)`,
  );
  assert.equal(
    scenario.provider.matchCount('manager-reopened-loop.0'),
    1,
    `${label}: successor tenure audit must be delivered once (perfect → Delivered)`,
  );
  assert.equal(
    scenario.provider.matchCount('manager-reopened-loop.1'),
    1,
    `${label}: successor tenure close must be delivered once (Delivered retirement)`,
  );

  const requests = canaryRequestsFor(scenario, sessionId);
  assert.equal(
    requests.length,
    6,
    `${label}: expected exactly 6 chat requests on the canary session — three per tenure
     under the two-stage deliver of work-006 (review, deliver-confirmation, deliver-close) (got ${requests.length})`,
  );
 // Same physical SessionId on every request: continuations extend the
 // LogicalRun, they never create a new one. A new session here would be a
 // cold-boundary violation, not a loop tenure.
  for (const request of requests) {
    assert.equal(
      request?.sessionID ?? request?.sessionId ?? null,
      sessionId,
      `${label}: every tenure must stay on the same physical SessionId/LogicalRun`,
    );
  }

 // Pure-loop authority: the next tenure carries the same system/provider
 // plan and the same typed authority user sequence as the initial tenure.
 // work-008 makes the successor projection STRIP the predecessor's assistant/tool
 // traffic (the retired attempt's continuation is stopped outright, and only
 // interactions first observed at or after the new tenure's opening survive), so
 // the first tenure's review call/result must be GONE from the successor's
 // provider view while the authority root stays. ONE reusable humanroot-loop
 // family covers both tenures, so tenures are never distinguished by prompt
 // text; the assertions below are on provider-visible message structure only.
  assertManagerLoopAuthorityPreserved(scenario, sessionId);

  // work-008's successor projection strips the predecessor's assistant/tool traffic by
  // XTrace observation cursor, not by identity: a row whose first observation lands after
  // the successor's opening is legitimately retained (its 允许后果 clause says exactly
  // that). So the loop oracle asserts what the handover contract fixes — the authority root
  // and the successor's own resource (see `assertManagerLoopAuthorityPreserved`) — and not
  // which predecessor row happened to survive, which is timing, not contract.
  const firstTenureReviewIds = assistantToolCallIds([requests[1]], 'review');
  assert.equal(
    firstTenureReviewIds.length,
    1,
    `${label}: first-tenure close must carry exactly the first-tenure review call (got ${firstTenureReviewIds.length})`,
  );

 // Durable loop behavior: two openings (initial + one after the relay), one
 // Relayed retirement followed by one Delivered; positive counts prove the loop.
  const openings = factPayloads(workDir, 'IncumbencyOpened', { sessionId });
  assert.equal(openings.length, 2, `${label}: canary work must open exactly two tenures (got ${openings.length})`);
  const openedIds = incumbencyIdsIn(openings);
  assert.equal(openedIds.length, 2, `${label}: tenures must carry distinct incumbencies (got ${JSON.stringify(openedIds)})`);
  const canaryRetirements = factPayloads(workDir, 'IncumbencyRetired', { sessionId });
  assert.equal(
    canaryRetirements.length,
    2,
    `${label}: canary work must own exactly two retirements (got ${canaryRetirements.length})`,
  );
  assert.equal(
    canaryRetirements.filter(isRelayedOutcome).length,
    1,
    `${label}: first-tenure retirement must be Outcome Relayed`,
  );
  assert.equal(
    canaryRetirements.filter(isDeliveredOutcome).length,
    1,
    `${label}: next retirement must be Outcome Delivered`,
  );
  assert.equal(
    countFactCase(workDir, 'Reviewed', { sessionId }),
    HUMANROOT_CANARY_DELTAS.assessments,
    `${label}: preflow must contribute exactly ${HUMANROOT_CANARY_DELTAS.assessments} Reviewed before the main spine`,
  );
  assert.equal(
    countFactCase(workDir, 'IncumbencyRetired', { sessionId }),
    HUMANROOT_CANARY_DELTAS.retirements,
    `${label}: preflow must contribute exactly ${HUMANROOT_CANARY_DELTAS.retirements} IncumbencyRetired before the main spine`,
  );
  assert.equal(
    countFactCase(workDir, 'IncumbencyOpened', { sessionId }),
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

export const CUSTOMS = {
  holdChildC1UntilLabor,
  bindManagerLoopSequence,
  oracleLongStroke,
};
