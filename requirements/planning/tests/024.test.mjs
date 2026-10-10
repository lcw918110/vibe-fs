import test from 'node:test';
import assert from 'node:assert/strict';
import { PlanningSurface, decideAction } from '../../../dist/Mission/Planning/Surface.js';
PlanningSurface.decideAction = decideAction;

// Physical Host Canary: Continuous three-tenure relay in a single physical session.
// Scenario: S1 (facts & tradeoffs) -> handoff -> S2 (understanding & paths) -> handoff -> S3 (procedure & pseudocode) -> deliver.
// Note: Pure contract assertions below verify the exact invariant ladder on PlanningSurface.
// Live daemon execution requires physical OpenCode host with configured Predictor/ModelTarget.

test('WHAT[planning-024] continuous three-tenure relay across single session contract verification', async () => {
  const workId = 'work-canary-relay-1';
  const root = '/workspace/test';

  // --- TENURE 1: Stage S1 ---
  let state = PlanningSurface.empty();
  state = PlanningSurface.applyWorkEvent(PlanningSurface.workOpened(workId, root), state).state;
  state = PlanningSurface.applyWorkEvent(PlanningSurface.incumbencyOpened(workId, 'inc-s1', 'S1', 10n), state).state;

  // 1. S1 ActionGate: only handoff allowed; deliver is strictly rejected
  const s1HandoffDecision = PlanningSurface.decideAction('S1', 'handoff', false, false);
  assert.equal(s1HandoffDecision.ok, true);

  const s1DeliverDecision = PlanningSurface.decideAction('S1', 'deliver', false, false);
  assert.equal(s1DeliverDecision.ok, false);
  assert.equal(s1DeliverDecision.rejection, 'DeliverForbiddenInS1');

  // 2. S1 Tenure isolation: first request triggers prefix reanchor
  const s1Messages = [
    { role: 'user', content: 'Create a plan for caching architecture', id: 'u-1', cursor: 5n },
    { role: 'assistant', content: 'Analyzing existing facts in S1...', id: 'a-1', cursor: 12n },
  ];
  const s1TenureObj = {
    workId: workId,
    incumbencyId: 'inc-s1',
    stage: 'S1',
    openingCursor: 10n,
    previousRange: null,
    isFreshHandover: true,
  };
  const s1Assembled = PlanningSurface.assembleTenureMessages(s1Messages, s1TenureObj, () => '');
  assert.equal(s1Assembled.reanchorRequested, true);
  assert.equal(s1Assembled.messages.length, 2);

  // Handoff from S1 -> S2
  state = PlanningSurface.applyWorkEvent(PlanningSurface.incumbencyRetired('inc-s1', PlanningSurface.PlanRetirementOutcome.Continue, 20n), state).state;
  state = PlanningSurface.applyWorkEvent(PlanningSurface.incumbencyOpened(workId, 'inc-s2', 'S2', 21n), state).state;

  // --- TENURE 2: Stage S2 ---
  // Both handoff and deliver are allowed at S2
  const s2HandoffDecision = PlanningSurface.decideAction('S2', 'handoff', false, false);
  assert.equal(s2HandoffDecision.ok, true);
  const s2DeliverDecision = PlanningSurface.decideAction('S2', 'deliver', false, false);
  assert.equal(s2DeliverDecision.ok, true);

  // S2 Tenure isolation: prior assistant/tool messages stripped; LWR_prev synthesized; reanchor requested
  const s2Messages = [
    { role: 'user', content: 'Create a plan for caching architecture', id: 'u-1', cursor: 5n },
    { role: 'assistant', content: 'S1 old assistant message', id: 'a-old-1', cursor: 15n },
    { role: 'tool', name: 'js-plan', content: 'S1 old draft', id: 't-old-1', cursor: 18n },
  ];
  const s2TenureObj = {
    workId: workId,
    incumbencyId: 'inc-s2',
    stage: 'S2',
    openingCursor: 21n,
    previousRange: [10n, 20n],
    isFreshHandover: true,
  };
  const s2Assembled = PlanningSurface.assembleTenureMessages(s2Messages, s2TenureObj, (r) => `Summary of S1 (${r.start}-${r.end})`);
  assert.equal(s2Assembled.reanchorRequested, true);
  // Prior S1 assistant & tool messages must be stripped completely
  const s2Roles = s2Assembled.messages.map(m => m.role);
  assert.equal(s2Roles.includes('assistant'), false);
  assert.equal(s2Roles.includes('tool'), false);
  // Synthetic LWR_prev inserted after original U message
  assert.equal(s2Assembled.messages[0].content, 'Create a plan for caching architecture');
  assert.equal(s2Assembled.messages[1].id, 'lwr-prev-inc-s2');
  assert.equal(s2Assembled.messages[1].content, 'Summary of S1 (10-20)');

  // Handoff from S2 -> S3
  state = PlanningSurface.applyWorkEvent(PlanningSurface.incumbencyRetired('inc-s2', PlanningSurface.PlanRetirementOutcome.Continue, 30n), state).state;
  state = PlanningSurface.applyWorkEvent(PlanningSurface.incumbencyOpened(workId, 'inc-s3', 'S3', 31n), state).state;

  // --- TENURE 3: Stage S3 ---
  // S3 ActionGate: handoff is rejected; deliver is allowed
  const s3HandoffDecision = PlanningSurface.decideAction('S3', 'handoff', false, false);
  assert.equal(s3HandoffDecision.ok, false);
  assert.equal(s3HandoffDecision.rejection, 'HandoffForbiddenInS3');
  const s3DeliverDecision = PlanningSurface.decideAction('S3', 'deliver', false, false);
  assert.equal(s3DeliverDecision.ok, true);

  // Retire S3 with Delivered
  state = PlanningSurface.applyWorkEvent(PlanningSurface.incumbencyRetired('inc-s3', PlanningSurface.PlanRetirementOutcome.Delivered, 40n), state).state;
  const digest = 'sha256:7f83b1657ff1fc53b92dc18148a1d65dfc2d4b1fa3d677284addd200126d9069';
  const deliveryPath = 'plan/work-canary-relay-1/plan.md';
  state = PlanningSurface.applyWorkEvent(PlanningSurface.delivered('inc-s3', workId, digest, deliveryPath), state).state;

  // 3. PlanDelivered recorded with digest and receipt
  assert.ok(state.Delivered != null);
  assert.equal(state.Delivered.Digest, digest);
  assert.equal(state.Delivered.Path, deliveryPath);

  // 4. Post-delivery lockdown: ask, resume, and handoff are all rejected
  const postHandoff = PlanningSurface.decideAction('S3', 'handoff', true, false);
  assert.equal(postHandoff.ok, false);
  const postAsk = PlanningSurface.decideAction('S3', 'ask', true, false);
  assert.equal(postAsk.ok, false);
  const postResume = PlanningSurface.decideAction('S3', 'resume', true, false);
  assert.equal(postResume.ok, false);

  // 5. Crash recovery idempotency: recovers Delivered receipt without guessing stage from text
  const view = {
    WorkId: workId,
    ActiveIncumbencyId: null,
    ActiveStage: null,
    ActivePhase: null,
    RetiredCount: 3,
    LatestRetirementOutcome: 'Delivered',
    Delivered: true,
    DeliveryDigest: digest,
    DeliveryPath: deliveryPath,
    BoundDevOpsId: null,
  };
  const recoveryPos = PlanningSurface.planRecoveryPosition(view, () => '# Text containing misleading S1 S2 S3 keywords');
  assert.equal(recoveryPos.kind, 'Delivered');
  assert.equal(recoveryPos.digest, digest);
  assert.equal(recoveryPos.path, deliveryPath);
  assert.ok(recoveryPos.receipt);
});

// Physical OpenCode daemon integration requirement mark:
test.todo('WHAT[planning-024] physical OpenCode host canary live execution across 3 continuous tenures in single session');
