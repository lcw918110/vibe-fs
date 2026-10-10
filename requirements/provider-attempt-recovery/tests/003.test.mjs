import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const failureOwner = await import("../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js");

const {
  budget,
  providerFailureProjection,
  fold: foldFactsThroughOwner,
  authorityRootAccepted,
  providerFailureRecorded,
  providerRetryExhausted,
  providerSuccessRecorded,
  envelope: ownerEnvelope,
  providerFailureFactCaseNames,
} = failureOwner
const SESSION = 'ses_a'
const RUN = 'run_L'
const ROOT = 'msg_u1'
const ROOT_SELECTION_IDENTITY_SEED = {
  kind: 'RootSelection',
  participantIdentity: {
    selectedAgent: 'engineer',
    canonicalRole: 'engineer',
    persona: 'Engineer',
    personaCatalogVersion: 1,
    origin: 'ResolvedAtRoot',
  },
}
const identityFor = (run, { logical = RUN, root = ROOT } = {}) =>
  budget.attemptIdentity(SESSION, logical, root, run)
const rootFact = ({ kind = 'HumanRoot', logical = RUN, root = ROOT } = {}) =>
  authorityRootAccepted({
    session: SESSION,
    logicalRun: logical,
    authorityRoot: root,
    authorityKind: kind,
    identitySeed: ROOT_SELECTION_IDENTITY_SEED,
  })
const failureFact = ({ run, count, logical = RUN, root = ROOT, reason = 'provider_error' }) =>
  providerFailureRecorded({
    session: SESSION,
    logicalRun: logical,
    authorityRoot: root,
    providerRun: run,
    consecutiveFailureCount: count,
    reason,
  })
const exhaustedFact = ({ count }) =>
  providerRetryExhausted({
    session: SESSION,
    logicalRun: RUN,
    authorityRoot: ROOT,
    finalConsecutiveFailureCount: count,
  })
const foldFacts = (facts) =>
  foldFactsThroughOwner(facts.map((value, index) => ownerEnvelope({ seq: index + 1, session: SESSION, fact: value })))
const budgetOf = (projection) => providerFailureProjection.read(projection)

test('WHAT[provider-attempt-recovery-003] the_same_attempt_observed_twice_advances_once', () => {
  let current = providerFailureProjection.forAuthority(RUN, ROOT)
  const first = providerFailureProjection.applyFailure(identityFor('run_1'), 1, current)
  assert.equal(first.ok, true)
  current = first.value

  assert.deepEqual(providerFailureProjection.applyFailure(identityFor('run_1'), 2, current), {
    ok: false,
    error: 'AlreadyObserved',
  })

  const second = providerFailureProjection.applyFailure(identityFor('run_2'), 2, current)
  assert.equal(second.ok, true)
  assert.equal(providerFailureProjection.read(second.value).failures, 2)
})
test('WHAT[provider-attempt-recovery-003] the_dedupe_window_is_bounded', () => {
  let current = providerFailureProjection.forAuthority(RUN, ROOT)

  for (let attempt = 1; attempt <= 60; attempt += 1) {
    const applied = providerFailureProjection.applyFailure(identityFor(`run_${attempt}`), attempt, current)
    assert.equal(applied.ok, true, applied.ok ? '' : `advance ${attempt}: ${applied.error}`)
    current = applied.value
  }

  const state = providerFailureProjection.read(current)
  assert.equal(state.failures, 60)
  assert.equal(state.dedupeKeys, 32)
})
test('WHAT[provider-attempt-recovery-003] a_duplicate_line_is_absorbed_because_replay_produces_it', () => {
  const folded = foldFacts([
    rootFact(),
    failureFact({ run: 'run_1', count: 1 }),
    failureFact({ run: 'run_1', count: 2 }),
  ])

  assert.equal(folded.ok, true, folded.ok ? '' : JSON.stringify(folded.error))
  assert.deepEqual({ failures: budgetOf(folded.value).failures }, { failures: 1 })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { JournalSurface_bootWithWriterId: bootWithWriterId, JournalSurface_dispose: dispose } = await import("../../../dist/Persistence/Journal/Surface.js");
const failureOwner = await import("../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js");

const { budget, acceptHumanRoot: failureAcceptHumanRoot } = failureOwner
const SESSION = 'ses_ledger'
async function acceptHumanRoot(journal, userMessageId) {
  const accepted = await failureAcceptHumanRoot(journal, SESSION, userMessageId, 'engineer')
  assert.equal(accepted.ok, true, `AcceptHumanRoot failed: ${accepted.error}`)
}
async function admit(journal, providerRunName) {
  const recorded = await failureOwner.recordConfirmedFailure(
    journal,
    budget.defaultBudget,
    SESSION,
    providerRunName,
    'provider_error',
  )

  if (!recorded.ok) return recorded
  return {
    ok: true,
    value: recorded.outcome === 'RetryExhausted' ? 'RetryExhausted' : 'RetryAuthorized',
  }
}

test('WHAT[provider-attempt-recovery-003] same_failure_observed_twice_advances_once', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-ledger-dedupe-'))
  const created = await bootWithWriterId(directory, 'writer-ledger-dedupe', 'rt_ledger_dedupe', 1, '2026-01-01T00:00:00Z')
  assert.equal(created.ok, true, created.ok ? '' : created.error)

  try {
    const journal = created.journal
    await acceptHumanRoot(journal, 'msg_u_dup')

    const first = await failureOwner.recordConfirmedFailure(
      journal,
      budget.defaultBudget,
      SESSION,
      'msg_asst_1',
      'provider_error',
    )
    assert.deepEqual(first, { ok: true, outcome: 'RetryAuthorized' })

    // A second observe replays the same recovery authorization. The exact
    // prompt gate dedupes its physical send; the failure count stays one.
    const second = await failureOwner.recordConfirmedFailure(
      journal,
      budget.defaultBudget,
      SESSION,
      'msg_asst_1',
      'provider_error',
    )
    assert.deepEqual(second, { ok: true, outcome: 'RetryAuthorized' })

    const state = failureOwner.snapshot(journal, SESSION)
    assert.deepEqual(
      { failures: state.failures, exhausted: state.exhausted },
      { failures: 1, exhausted: false },
    )
  } finally {
    dispose(created.journal)
    rmSync(directory, { recursive: true, force: true })
  }
})
test('WHAT[provider-attempt-recovery-003] an_older_failed_run_is_absorbed_after_its_successor_advances', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-ledger-superseded-'))
  const created = await bootWithWriterId(directory, 'writer-ledger-superseded', 'rt_ledger_superseded', 1, '2026-01-01T00:00:00Z')
  assert.equal(created.ok, true, created.ok ? '' : created.error)

  try {
    const journal = created.journal
    await acceptHumanRoot(journal, 'msg_u_superseded')

    assert.deepEqual(
      await failureOwner.recordConfirmedFailure(journal, budget.defaultBudget, SESSION, 'run-1', 'provider_error'),
      { ok: true, outcome: 'RetryAuthorized' },
    )
    assert.deepEqual(
      await failureOwner.recordConfirmedFailure(journal, budget.defaultBudget, SESSION, 'run-2', 'provider_error'),
      { ok: true, outcome: 'RetryAuthorized' },
    )
    assert.deepEqual(
      await failureOwner.recordConfirmedFailure(journal, budget.defaultBudget, SESSION, 'run-1', 'provider_error'),
      { ok: true, outcome: 'EpisodeSuperseded' },
    )

    const state = failureOwner.snapshot(journal, SESSION)
    assert.deepEqual(
      { failures: state.failures, exhausted: state.exhausted },
      { failures: 2, exhausted: false },
    )
  } finally {
    dispose(created.journal)
    rmSync(directory, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { budget: providerFailureBudget, providerFailureProjection } = await import("../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js");

const SESSION = 'ses_meta'

test('WHAT[provider-attempt-recovery-003] ProviderFailure_owner_exposes_budget_and_dedupe_state', () => {
  const initial = providerFailureProjection.forAuthority('run_L', 'msg_u1')
  const ownerIdentity = providerFailureBudget.attemptIdentity(SESSION, 'run_L', 'msg_u1', 'run_owner')
  const secondIdentity = providerFailureBudget.attemptIdentity(SESSION, 'run_L', 'msg_u1', 'run_second')
  const ownerAdvance = providerFailureProjection.applyFailure(ownerIdentity, 1, initial)
  assert.equal(ownerAdvance.ok, true, ownerAdvance.ok ? '' : ownerAdvance.error)
  assert.deepEqual(providerFailureProjection.read(ownerAdvance.value), {
    logicalRun: 'run_L',
    authorityRoot: 'msg_u1',
    failures: 1,
    dedupeKeys: 1,
    exhausted: false,
  })

  const secondAdvance = providerFailureProjection.applyFailure(secondIdentity, 2, ownerAdvance.value)
  assert.equal(secondAdvance.ok, true, secondAdvance.ok ? '' : secondAdvance.error)
  assert.deepEqual(providerFailureProjection.read(secondAdvance.value), {
    logicalRun: 'run_L',
    authorityRoot: 'msg_u1',
    failures: 2,
    dedupeKeys: 2,
    exhausted: false,
  })

  const duplicate = providerFailureProjection.applyFailure(ownerIdentity, 2, secondAdvance.value)
  assert.equal(duplicate.ok, false)
  assert.equal(duplicate.error, 'AlreadyObserved')

  assert.deepEqual(providerFailureProjection.read(providerFailureProjection.recordSuccess(ownerAdvance.value)), {
    logicalRun: 'run_L',
    authorityRoot: 'msg_u1',
    failures: 0,
    dedupeKeys: 0,
    exhausted: false,
  })
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

test('WHAT[provider-attempt-recovery-003] each_retry_binds_a_fresh_physical_identity', () => {
  // Fresh physical identity is a ledger requirement: two distinct ProviderRun
  // identities both advance; observing one twice never advances twice.
  const first = budget.attemptIdentity('ses_a', 'run_L', 'msg_u1', 'provider-1')
  const second = budget.attemptIdentity('ses_a', 'run_L', 'msg_u1', 'provider-2')
  assert.notEqual(budget.dedupeKey(first), budget.dedupeKey(second))

  let current = providerFailureProjection.forAuthority('run_L', 'msg_u1')
  current = providerFailureProjection.applyFailure(first, 1, current).value
  current = providerFailureProjection.applyFailure(second, 2, current).value
  assert.equal(providerFailureProjection.read(current).failures, 2)

  assert.deepEqual(providerFailureProjection.applyFailure(first, 2, current), {
    ok: false,
    error: 'AlreadyObserved',
  })
})
}
