import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const XWireSurface = await import("../../../dist/Context/Prefix/XWireSurface.js");
const RecoveryScope = await import("../../../dist/OpenCode/Host/PluginRecoveryScopeSurface.js");

const candidate = (over = {}) => ({
  ref: 'blob/ref/frozen-1',
  frozenDigest: 'sha256:frozen-1',
  cutoff: 2,
  prefixDigest: 'prefix-digest-1',
  sealRoot: 'seal-1',
  syntheticId: 'synthetic-1',
  ...over,
})
const probeInput = ({ candidate: candidateOverrides, ...over } = {}) => ({
  probeId: 'probe-1',
  basedOnEpoch: 0,
  candidate: candidate(candidateOverrides),
  ...over,
})
const planInput = (over = {}) => ({
  session: 'ses-1',
  logicalRun: 'run-1',
  root: 'root-1',
  physical: 'phys-1',
  kind: 'WorkMain',
  probe: null,
  ...over,
})
const buildPlan = (over) => {
  const built = XWireSurface.pendingPlan(planInput(over))
  assert.equal(built.ok, true, `pendingPlan must accept the input: ${built.error}`)
  return built
}

test('WHAT[provider-attempt-recovery-011] same-key same-plan replays the admitted plan', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const plan = buildPlan({})

  for (const key of ['failures', 'budget', 'consecutiveFailureCount', 'count', 'exhausted']) {
    assert.equal(key in plan.view, false, `pending plan must not carry a budget snapshot (${key})`)
  }

  const first = RecoveryScope.freezeAttemptPlan(scope, 'ses-1', 'phys-1', plan.handle)
  assert.equal(first.outcome, 'Admitted')

  const second = RecoveryScope.freezeAttemptPlan(scope, 'ses-1', 'phys-1', plan.handle)
  assert.equal(second.outcome, 'ReplayedExisting')
  assert.deepEqual(second.view, first.view)

  assert.doesNotThrow(() => {
    RecoveryScope.recordAttemptPlan(scope, 'ses-1', 'phys-1', plan.handle)
  })
})
test('WHAT[provider-attempt-recovery-011] same-key different authority fails closed', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const original = buildPlan({})
  assert.equal(RecoveryScope.freezeAttemptPlan(scope, 'ses-1', 'phys-1', original.handle).outcome, 'Admitted')

  const rival = buildPlan({ logicalRun: 'run-2' })
  const conflict = RecoveryScope.freezeAttemptPlan(scope, 'ses-1', 'phys-1', rival.handle)
  assert.equal(conflict.outcome, 'PlanConflict')
  assert.equal(conflict.existing.logicalRunId, 'run-1')
  assert.equal(conflict.attempted.logicalRunId, 'run-2')

  assert.throws(
    () => RecoveryScope.recordAttemptPlan(scope, 'ses-1', 'phys-1', rival.handle),
    /HOST-BOUNDARY-008/,
  )
})
test('WHAT[provider-attempt-recovery-011] same-key different probe fails closed', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const committed = buildPlan({})
  assert.equal(RecoveryScope.freezeAttemptPlan(scope, 'ses-1', 'phys-1', committed.handle).outcome, 'Admitted')

  const probed = buildPlan({ probe: probeInput({ probeId: 'probe-99' }) })
  const conflict = RecoveryScope.freezeAttemptPlan(scope, 'ses-1', 'phys-1', probed.handle)
  assert.equal(conflict.outcome, 'PlanConflict')
  assert.equal(conflict.existing.choice, 'UseCommittedEpoch')
  assert.equal(conflict.attempted.choice, 'UsePrefixProbe')
  assert.equal(conflict.attempted.probe.probeId, 'probe-99')
})
test('WHAT[provider-attempt-recovery-011] same-key different request kind fails closed', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const work = buildPlan({ probe: probeInput() })
  assert.equal(work.view.choice, 'UsePrefixProbe')
  assert.equal(RecoveryScope.freezeAttemptPlan(scope, 'ses-1', 'phys-1', work.handle).outcome, 'Admitted')

  const repair = buildPlan({ kind: 'InteractionRepair', probe: probeInput() })
  assert.equal(repair.view.choice, 'UseCommittedEpoch')
  const conflict = RecoveryScope.freezeAttemptPlan(scope, 'ses-1', 'phys-1', repair.handle)
  assert.equal(conflict.outcome, 'PlanConflict')
  assert.equal(conflict.existing.choice, 'UsePrefixProbe')
  assert.equal(conflict.attempted.choice, 'UseCommittedEpoch')
})
test('WHAT[provider-attempt-recovery-011] same admitted plan drives binding and the frozen candidate is retained', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const frozen = buildPlan({
    session: 'ses-frozen',
    physical: 'phys-frozen',
    probe: probeInput(),
  })
  const admitted = RecoveryScope.freezeAttemptPlan(scope, 'ses-frozen', 'phys-frozen', frozen.handle)
  assert.equal(admitted.outcome, 'Admitted')

  const bound = RecoveryScope.bindAttempt(scope, 'ses-frozen', 'phys-frozen', 'run-A')
  assert.equal(bound.bound, true)
  assert.equal(bound.view.physical, 'phys-frozen')
  assert.equal(bound.view.providerRun, 'run-A')
  assert.equal(bound.view.probe.probeId, admitted.view.probe.probeId)
  assert.equal(bound.view.probe.cutoff, 2)

  const peeked = RecoveryScope.peekAttempt(scope, 'ses-frozen', 'run-A')
  assert.equal(peeked.found, true)
  assert.deepEqual(peeked.view.probe, bound.view.probe)

  const later = buildPlan({
    session: 'ses-frozen',
    physical: 'phys-frozen',
    probe: probeInput({ probeId: 'probe-later', candidate: candidate({ cutoff: 5 }) }),
  })
  const refused = RecoveryScope.freezeAttemptPlan(scope, 'ses-frozen', 'phys-frozen', later.handle)
  assert.equal(refused.outcome, 'PlanConflict')

  const retained = RecoveryScope.peekAttempt(scope, 'ses-frozen', 'run-A')
  assert.equal(retained.found, true)
  assert.equal(retained.view.probe.probeId, 'probe-1')
  assert.equal(retained.view.probe.cutoff, 2)
})
test('WHAT[provider-attempt-recovery-011] bind to the wrong parent or run is refused', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const plan = buildPlan({ session: 'ses-bind', physical: 'phys-parent-1' })
  assert.equal(RecoveryScope.freezeAttemptPlan(scope, 'ses-bind', 'phys-parent-1', plan.handle).outcome, 'Admitted')

  const bound = RecoveryScope.bindAttempt(scope, 'ses-bind', 'phys-parent-1', 'run-A')
  assert.equal(bound.bound, true)
  assert.equal(bound.view.providerRun, 'run-A')

  const wrongParent = RecoveryScope.bindAttempt(scope, 'ses-bind', 'phys-parent-2', 'run-A')
  assert.equal(wrongParent.bound, false)
  assert.equal(wrongParent.view, null)
  assert.equal(wrongParent.handle, null)

  const other = buildPlan({ session: 'ses-bind', physical: 'phys-parent-2' })
  const rebound = XWireSurface.bindProviderRun(other.handle, 'run-A')
  assert.throws(
    () => RecoveryScope.recordBound(scope, 'ses-bind', 'run-A', rebound.handle),
    /HOST-BOUNDARY-008/,
  )
})
test('WHAT[provider-attempt-recovery-011] terminal consumption is single-shot and never re-promotes', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const plan = buildPlan({
    session: 'ses-term',
    physical: 'phys-term',
    probe: probeInput({ probeId: 'probe-term' }),
  })
  assert.equal(RecoveryScope.freezeAttemptPlan(scope, 'ses-term', 'phys-term', plan.handle).outcome, 'Admitted')
  assert.equal(RecoveryScope.bindAttempt(scope, 'ses-term', 'phys-term', 'run-term').bound, true)

  const first = RecoveryScope.consumeAttempt(scope, 'ses-term', 'run-term')
  assert.equal(first.consumed, true)
  assert.equal(first.view.providerRun, 'run-term')
  assert.equal(first.view.probe.probeId, 'probe-term')

  assert.deepEqual(RecoveryScope.peekAttempt(scope, 'ses-term', 'run-term'), {
    found: false,
    view: null,
  })
  assert.deepEqual(RecoveryScope.consumeAttempt(scope, 'ses-term', 'run-term'), {
    consumed: false,
    view: null,
  })
})
test('WHAT[provider-attempt-recovery-011] freeze under the wrong session key is an identity mismatch', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const plan = buildPlan({})
  assert.equal(plan.view.session, 'ses-1')
  assert.equal(plan.view.agent, 'engineer')
  assert.equal(plan.view.role, 'engineer')

  const mismatch = RecoveryScope.freezeAttemptPlan(scope, 'ses-other', 'phys-1', plan.handle)
  assert.equal(mismatch.outcome, 'IdentityMismatch')
  assert.equal(mismatch.expected.session, 'ses-other')
  assert.equal(mismatch.expected.physical, 'phys-1')
  assert.equal(mismatch.attempted.session, 'ses-1')
  assert.equal(mismatch.attempted.physical, 'phys-1')

  assert.throws(
    () => RecoveryScope.recordAttemptPlan(scope, 'ses-other', 'phys-1', plan.handle),
    /HOST-BOUNDARY-008/,
  )

  const admitted = RecoveryScope.freezeAttemptPlan(scope, 'ses-1', 'phys-1', plan.handle)
  assert.equal(admitted.outcome, 'Admitted')
  assert.equal(admitted.view.session, 'ses-1')
  assert.equal(admitted.view.physical, 'phys-1')
})
test('WHAT[provider-attempt-recovery-011] freeze under the wrong physical key is an identity mismatch', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const plan = buildPlan({ session: 'ses-phys', physical: 'phys-exact' })

  const mismatch = RecoveryScope.freezeAttemptPlan(scope, 'ses-phys', 'phys-other', plan.handle)
  assert.equal(mismatch.outcome, 'IdentityMismatch')
  assert.equal(mismatch.expected.session, 'ses-phys')
  assert.equal(mismatch.expected.physical, 'phys-other')
  assert.equal(mismatch.attempted.physical, 'phys-exact')

  assert.throws(
    () => RecoveryScope.recordAttemptPlan(scope, 'ses-phys', 'phys-other', plan.handle),
    /HOST-BOUNDARY-008/,
  )

  assert.equal(RecoveryScope.freezeAttemptPlan(scope, 'ses-phys', 'phys-exact', plan.handle).outcome, 'Admitted')
})
test('WHAT[provider-attempt-recovery-011] same physical replay after binding returns the admitted plan', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const plan = buildPlan({
    session: 'ses-replay',
    physical: 'phys-replay',
    probe: probeInput({ probeId: 'probe-replay' }),
  })
  const admitted = RecoveryScope.freezeAttemptPlan(scope, 'ses-replay', 'phys-replay', plan.handle)
  assert.equal(admitted.outcome, 'Admitted')

  const bound = RecoveryScope.bindAttempt(scope, 'ses-replay', 'phys-replay', 'run-replay')
  assert.equal(bound.bound, true)
  assert.equal(bound.view.session, 'ses-replay')
  assert.equal(bound.view.physical, 'phys-replay')
  assert.equal(bound.view.providerRun, 'run-replay')
  assert.equal(bound.view.agent, admitted.view.agent)
  assert.equal(bound.view.role, admitted.view.role)

  const replayed = RecoveryScope.freezeAttemptPlan(scope, 'ses-replay', 'phys-replay', plan.handle)
  assert.equal(replayed.outcome, 'ReplayedExisting')
  assert.deepEqual(replayed.view, admitted.view)

  const retained = RecoveryScope.peekAttempt(scope, 'ses-replay', 'run-replay')
  assert.equal(retained.found, true)
  assert.deepEqual(retained.view, bound.view)

  const rebound = RecoveryScope.bindAttempt(scope, 'ses-replay', 'phys-replay', 'run-replay')
  assert.equal(rebound.bound, true)
  assert.deepEqual(rebound.view, bound.view)
})
test('WHAT[provider-attempt-recovery-011] same run and root with a different participant conflicts', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const coderPlan = buildPlan({
    session: 'ses-who',
    logicalRun: 'run-same',
    root: 'root-same',
    physical: 'phys-who',
  })
  assert.equal(coderPlan.view.agent, 'engineer')
  assert.equal(coderPlan.view.role, 'engineer')
  assert.equal(RecoveryScope.freezeAttemptPlan(scope, 'ses-who', 'phys-who', coderPlan.handle).outcome, 'Admitted')

  const inspectorPlan = buildPlan({
    session: 'ses-who',
    logicalRun: 'run-same',
    root: 'root-same',
    physical: 'phys-who',
    agent: 'devops',
  })
  assert.equal(inspectorPlan.ok, true)
  assert.equal(inspectorPlan.view.agent, 'devops')
  assert.equal(inspectorPlan.view.role, 'devops')
  assert.equal(inspectorPlan.view.logicalRunId, 'run-same')
  assert.equal(inspectorPlan.view.root, 'root-same')

  const conflict = RecoveryScope.freezeAttemptPlan(scope, 'ses-who', 'phys-who', inspectorPlan.handle)
  assert.equal(conflict.outcome, 'PlanConflict')
  assert.equal(conflict.existing.agent, 'engineer')
  assert.equal(conflict.attempted.agent, 'devops')
  assert.equal(conflict.existing.logicalRunId, 'run-same')
  assert.equal(conflict.attempted.logicalRunId, 'run-same')

  assert.throws(
    () => RecoveryScope.recordAttemptPlan(scope, 'ses-who', 'phys-who', inspectorPlan.handle),
    /HOST-BOUNDARY-008/,
  )

  const bound = RecoveryScope.bindAttempt(scope, 'ses-who', 'phys-who', 'run-who')
  assert.equal(bound.bound, true)
  assert.equal(bound.view.agent, 'engineer')
})
test('WHAT[provider-attempt-recovery-011] same run and root with a different role conflicts', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const coderPlan = buildPlan({
    session: 'ses-role',
    logicalRun: 'run-role',
    root: 'root-role',
    physical: 'phys-role',
  })
  assert.equal(RecoveryScope.freezeAttemptPlan(scope, 'ses-role', 'phys-role', coderPlan.handle).outcome, 'Admitted')

  const devopsPlan = buildPlan({
    session: 'ses-role',
    logicalRun: 'run-role',
    root: 'root-role',
    physical: 'phys-role',
    role: 'devops',
  })
  assert.equal(devopsPlan.view.role, 'devops')
  const conflict = RecoveryScope.freezeAttemptPlan(scope, 'ses-role', 'phys-role', devopsPlan.handle)
  assert.equal(conflict.outcome, 'PlanConflict')
  assert.equal(conflict.existing.role, 'engineer')
  assert.equal(conflict.attempted.role, 'devops')
})
test('WHAT[provider-attempt-recovery-011] recordBound rejects a conflicting session, run, or physical parent', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const plan = buildPlan({ session: 'ses-rec', physical: 'phys-rec' })
  assert.equal(RecoveryScope.freezeAttemptPlan(scope, 'ses-rec', 'phys-rec', plan.handle).outcome, 'Admitted')
  const bound = RecoveryScope.bindAttempt(scope, 'ses-rec', 'phys-rec', 'run-good')
  assert.equal(bound.bound, true)

  assert.throws(() => RecoveryScope.recordBound(scope, 'ses-wrong', 'run-good', bound.handle), /HOST-BOUNDARY-008/)
  assert.throws(() => RecoveryScope.recordBound(scope, 'ses-rec', 'run-wrong', bound.handle), /HOST-BOUNDARY-008/)

  const other = buildPlan({ session: 'ses-rec', physical: 'phys-other' })
  assert.equal(RecoveryScope.freezeAttemptPlan(scope, 'ses-rec', 'phys-other', other.handle).outcome, 'Admitted')
  const rebound = XWireSurface.bindProviderRun(other.handle, 'run-good')
  assert.equal(rebound.view.physical, 'phys-other')
  assert.throws(() => RecoveryScope.recordBound(scope, 'ses-rec', 'run-good', rebound.handle), /HOST-BOUNDARY-008/)

  assert.doesNotThrow(() => RecoveryScope.recordBound(scope, 'ses-rec', 'run-good', bound.handle))
  const peeked = RecoveryScope.peekAttempt(scope, 'ses-rec', 'run-good')
  assert.equal(peeked.found, true)
  assert.equal(peeked.view.physical, 'phys-rec')
})
test('WHAT[provider-attempt-recovery-011] same candidate probe id with a different digest conflicts', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const first = buildPlan({
    session: 'ses-digest',
    physical: 'phys-digest',
    probe: probeInput({ probeId: 'probe-same', candidate: candidate({ frozenDigest: 'sha256:aaa' }) }),
  })
  assert.equal(RecoveryScope.freezeAttemptPlan(scope, 'ses-digest', 'phys-digest', first.handle).outcome, 'Admitted')

  const second = buildPlan({
    session: 'ses-digest',
    physical: 'phys-digest',
    probe: probeInput({ probeId: 'probe-same', candidate: candidate({ frozenDigest: 'sha256:bbb' }) }),
  })
  const conflict = RecoveryScope.freezeAttemptPlan(scope, 'ses-digest', 'phys-digest', second.handle)
  assert.equal(conflict.outcome, 'PlanConflict')
  assert.equal(conflict.existing.probe.frozenDigest, 'sha256:aaa')
  assert.equal(conflict.attempted.probe.frozenDigest, 'sha256:bbb')
})
test('WHAT[provider-attempt-recovery-011] terminal consume clears both registries so a fresh physical plan may freeze', () => {
  const scope = RecoveryScope.createRecoveryScope()
  const first = buildPlan({
    session: 'ses-clear',
    physical: 'phys-clear',
    probe: probeInput({ probeId: 'probe-old' }),
  })
  assert.equal(RecoveryScope.freezeAttemptPlan(scope, 'ses-clear', 'phys-clear', first.handle).outcome, 'Admitted')
  assert.equal(RecoveryScope.bindAttempt(scope, 'ses-clear', 'phys-clear', 'run-old').bound, true)

  const consumed = RecoveryScope.consumeAttempt(scope, 'ses-clear', 'run-old')
  assert.equal(consumed.consumed, true)
  assert.equal(consumed.view.probe.probeId, 'probe-old')
  assert.deepEqual(RecoveryScope.peekAttempt(scope, 'ses-clear', 'run-old'), { found: false, view: null })

  const fresh = buildPlan({
    session: 'ses-clear',
    physical: 'phys-clear',
    probe: probeInput({ probeId: 'probe-new' }),
  })
  const admitted = RecoveryScope.freezeAttemptPlan(scope, 'ses-clear', 'phys-clear', fresh.handle)
  assert.equal(admitted.outcome, 'Admitted')
  assert.equal(admitted.view.probe.probeId, 'probe-new')

  const rebound = RecoveryScope.bindAttempt(scope, 'ses-clear', 'phys-clear', 'run-new')
  assert.equal(rebound.bound, true)
  assert.equal(rebound.view.probe.probeId, 'probe-new')
  assert.equal(rebound.view.providerRun, 'run-new')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const compression = await import("../../../dist/Context/Companion/CompressionSurface.js");
const attemptPurpose = await import("../../../dist/Participant/Provider/Attempt/PlannerSurface.js");
const failureOwner = await import("../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js");
const reconcile = await import("../../../dist/Composition/Turn/ReconcileSurface.js");

const planner = compression.attemptPlanner
const { budget, providerFailureProjection } = failureOwner
const TOOL_CAPABILITIES = [
  'BashHoneypot',
  'Edit',
  'Fetch',
  'Fission',
  'Glob',
  'Grep',
  'Move',
  'Read',
  'Remove',
  'Write',
]

test('WHAT[provider-attempt-recovery-011] public plan observation exposes no budget snapshot', () => {
  const planned = planner.plan({ role: 'engineer', kind: 'work-main' })

  for (const key of ['failures', 'budget', 'consecutiveFailureCount', 'count', 'exhausted']) {
    assert.equal(key in planned, false, `plan must not carry ${key}`)
  }
  assert.deepEqual(Object.keys(planned).sort(), [
    'canonicalRole',
    'choice',
    'handle',
    'noProbeReason',
    'participant',
    'participantIdentity',
    'probeId',
    'projectionChoice',
    'requestKind',
    'role',
    'systemPromptId',
    'toolCapabilities',
  ])
})
test('WHAT[provider-attempt-recovery-011] an_attempt_without_a_probe_cannot_promote_even_on_success', () => {
  const withoutProbe = planner.plan({ role: 'engineer', kind: 'work-main', policyAllowsProbe: false })
  assert.equal(planner.promotableProbeId(withoutProbe, 'Completed'), null)
})
test('WHAT[provider-attempt-recovery-011] maintenance policy changes with available material', () => {
  // Same failed kind, only the material flag flips the decision: no transient
  // channel participates — the two calls are pure functions of their inputs.
  assert.notEqual(
    compression.nextBloggerRequest('blogger-main', true),
    compression.nextBloggerRequest('blogger-main', false),
  )
  // This pure policy result does not establish physical retry binding.
  assert.match(compression.nextBloggerRequest('blogger-main', true), /blogger-squash/)
})
}

test.todo('WHAT[provider-attempt-recovery-011] actual render observes the admitted immutable plan and cannot consume a transient recovery permission (GAP-139)')

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const XWireSurface = await import("../../../dist/Context/Prefix/XWireSurface.js");

const baseProjection = {
  messages: [
    { role: 'user', parts: [{ kind: 'text', text: 'hello' }] },
    { role: 'assistant', parts: [{ kind: 'text', text: 'answer' }] },
  ],
}
const acceptedRetryInput = (overrides = {}) => ({
  journal: true,
  sessionId: 'ses_x',
  acceptedRetry: true,
  failures: 1,
  prefixEpoch: 0,
  physicalUser: 'user-1',
  acceptedPhysicalUser: 'user-1',
  snapshotPort: true,
  currentProjection: baseProjection,
  committedSnapshot: null,
  coverableCutoff: 2,
  // The Companion's claim is proven at ITS cutoff; the frozen subset ends at the
  // request boundary, which is where this attempt may fold (CTX-029).
  coveredDigest: XWireSurface.coveredPrefixDigest(baseProjection, 2),
  materialCutoff: 1,
  requestStartCutoff: 1,
  frozenRecordPrefixRef: 'blob/ref/frozen-1',
  frozenRecordPrefixDigest: 'sha256:frozen-1',
  frozenRecordPrefixBody: 'frozen record prefix body text',
  memoryPreamble: 'companion memory preamble',
  outcome: null,
  ...overrides,
})

test('WHAT[provider-attempt-recovery-011] XWIRE_accepted_retry_cannot_be_consumed_by_other_physical_material_in_the_same_session', () => {
  const result = XWireSurface.transform(acceptedRetryInput({
    acceptedPhysicalUser: 'retry-user-1',
    physicalUser: 'ordinary-user-2',
  }))

  assert.equal(result.ok, true)
  assert.equal(result.noop, true)
  assert.equal(result.changed, false)
  assert.equal(result.consumed, false)
})
test('WHAT[provider-attempt-recovery-011] XWIRE_missing_current_physical_user_cannot_consume_the_accepted_retry', () => {
  const result = XWireSurface.transform(acceptedRetryInput({ physicalUser: '' }))
  assert.equal(result.ok, true)
  assert.equal(result.noop, true)
  assert.equal(result.consumed, false)
})
test('WHAT[provider-attempt-recovery-011] XWIRE_mutation_sensitive_unrelated_physical_user_must_not_consume_accepted_retry', () => {
  const result = XWireSurface.transform(acceptedRetryInput({
    acceptedPhysicalUser: 'retry-user-1',
    physicalUser: 'unrelated-user-9',
  }))
  assert.equal(result.consumed, false,
    'mutation guard: session presence alone must never consume an accepted physical retry')
})
}
