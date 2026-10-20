import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const change = await import('../../../dist/Change/Surface.js')
const job = (id, path = `/tmp/${id}`) => ({
  jobId: id,
  managerSessionId: `ses-${id}`,
  managerAgent: 'manager',
  byname: id,
  worktreeIdentity: `manager/${id}`,
  worktreePath: path,
  targetRef: 'refs/heads/main',
  targetBranchFrozen: 'refs/heads/main'})

test('WHAT[change-integration-009] manager loop keeps the durable job worktree', () => {
  let projection = change.createJob(change.empty(), job('hostfw8', '/tmp/wt-hostfw8'))
  projection = change.recordFact(projection, 'hostfw8', change.fact('CandidateReady', {
    candidateCommit: 'c1'}))
  const continued = change.find(projection, 'hostfw8')
  assert.equal(continued.worktreePath, '/tmp/wt-hostfw8')
  assert.deepEqual(continued.facts, ['CandidateReady'])
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const change = await import('../../../dist/Change/Surface.js')
const JOB = 'job_1'
const MANAGER = 'ses_m'
const payload = (overrides = {}) => ({
  jobId: JOB,
  managerSessionId: MANAGER,
  managerAgent: 'manager',
  byname: 'Road',
  worktreeIdentity: 'wt_1',
  worktreePath: '/tmp/wt1',
  targetRef: 'refs/heads/main',
  targetBranchFrozen: 'refs/heads/main',
  ...overrides})
const created = (overrides) => change.createJob(change.empty(), payload(overrides))
const fact = {
  candidateReady: (candidateCommit = 'c1') =>
    change.fact('CandidateReady', { candidateCommit }),
  conflictDetected: (conflictFiles = ['a.fs', 'b.fs']) =>
    change.fact('ConflictDetected', {
      candidateCommit: 'c1',
      targetHeadSnapshot: 'h1',
      conflictFiles,
      diagnosticsDigest: 'digest'}),
  rebased: (targetHeadSnapshot = 'h1') =>
    change.fact('RebasedCandidateReady', {
      rebasedCommit: 'r1',
      targetHeadSnapshot}),
  publishClaimed: (
    authorityRevision = 'authority-1') =>
    change.fact('PublishClaimed', {
      targetRef: 'refs/heads/main',
      rebasedCommit: 'r1',
      expectedHead: 'h1',
      authorityRevision}),
  published: () => change.fact('Published', { candidateCommit: 'c1', resultingTargetHead: 'r1' }),
  failed: (reason = 'boom') => change.fact('JobFailed', { reason }),
  abandoned: () => change.fact('JobAbandoned', null)}
const jobAt = (value, projection = created()) => {
  const next = change.recordFact(projection, JOB, value)
  return { projection: next, job: change.find(next, JOB) }
}
const classifyRebased = (head, rebasedCommit = 'r1', snapshot = 'h1') =>
  change.classifyRebasedCandidate(head ?? null, rebasedCommit, snapshot)
const classifyClaim = (head, rebasedCommit = 'r1', expectedHead = 'h1') =>
  change.classifyPublishClaim(head ?? null, rebasedCommit, expectedHead)
const createdEvent = {
  kind: 'ManagerJobCreated',
  payload: payload()}
const candidateEvent = {
  kind: 'CandidateReady',
  payload: {
    jobId: JOB,
    candidateCommit: 'c1'}}
const publishedEvent = {
  kind: 'Published',
  payload: { jobId: JOB, candidateCommit: 'c1', resultingTargetHead: 'r1' }}
const foldProjection = (events) => {
  const result = change.fold(events)
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.value
}
const worktreeRequested = { kind: 'WorktreeCreateRequested', payload: { jobId: JOB, worktreeIdentity: 'manager/job_1', worktreePath: '/tmp/wt1' } }
const worktreeCreated = { kind: 'WorktreeCreated', payload: { jobId: JOB, worktreeIdentity: 'manager/job_1', worktreePath: '/tmp/wt1' } }

test('WHAT[change-integration-009] ORCH_006_the_worktree_is_located_by_identity_and_the_path_is_only_diagnostic', () => {
  const job = change.find(created(), JOB)
  assert.notEqual(job.worktreeIdentity, job.worktreePath)
  assert.equal(job.worktreeIdentity, 'wt_1')
})
test('WHAT[change-integration-009] ORCH_003_durable_facts_do_not_change_job_identity', () => {
  const before = change.find(created(), JOB)
  const after = jobAt(fact.candidateReady())
  const identity = (job) => ({
    jobId: job.jobId,
    managerSessionId: job.managerSessionId,
    managerAgent: job.managerAgent,
    worktreeIdentity: job.worktreeIdentity,
    worktreePath: job.worktreePath,
    targetRef: job.targetRef,
    targetBranchFrozen: job.targetBranchFrozen})
  assert.deepEqual(identity(after.job), identity(before))
  assert.deepEqual(after.job.facts, ['CandidateReady'])
})
test('WHAT[change-integration-009] ORCH_003_a_manager_session_resolves_to_its_one_job', () => {
  const first = created()
  const second = change.createJob(first, payload({ jobId: 'job_2', managerSessionId: 'ses_m2', worktreeIdentity: 'wt_2' }))
  assert.equal(change.find(second, 'job_2').managerSessionId, 'ses_m2')
  assert.equal(change.find(second, 'ses_zz'), null)
})
test('WHAT[change-integration-009] ORCH_003_a_second_create_for_one_job_id_cannot_change_its_manager_or_worktree', () => {
  const again = change.createJob(created(), payload({ managerAgent: 'coder', worktreeIdentity: 'wt_other', worktreePath: '/tmp/wt_other' }))
  assert.deepEqual(
    { agent: change.find(again, JOB).managerAgent, worktree: change.find(again, JOB).worktreeIdentity },
    { agent: 'manager', worktree: 'wt_1' })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { assertOpaque } = await import("../../verification-system/tests/support/js-contract.mjs");

const change = await import('../../../dist/Change/Surface.js')
const PATH = '/repo/.worktrees/job-9'
const fakeRunner = (answers = []) => {
  const calls = []
  const runner = (command) => {
    const args = command.args
    calls.push({ args, cwd: command.workingDirectory })
    const key = args.join(' ')
    for (const [prefix, response] of answers) {
      if (key.startsWith(prefix)) return Promise.resolve(response)
    }
    return Promise.resolve([0, '', ''])
  }
  return { runner, calls }
}
const fakeGit = (answers = []) => {
  const fake = fakeRunner(answers)
  return { ...fake, git: change.createGit('/repo', fake.runner) }
}
const valueOf = async (promise) => {
  const result = await promise
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.value
}

test('WHAT[change-integration-009] WORKTREE_CMD_identity_of_is_manager_slash_job', () => {
  assert.equal(change.worktreeIdentityOf('job-7'), 'manager/job-7')
})
}

{
// The durable loop signal Change derives for one Manager road. The acquisition
// program blocks on it, so a signal that cannot change would spin the program:
// `Deliver` commits a Relay together with its successor incumbency in one record,
// and the hand-off's single physical prompt belongs to ManagerWorkflow's loop gate,
// not to a second reading of the same retirement.
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");

const hostSurface = await import('../../../dist/Change/Host/Surface.js')
const journalSurface = await import('../../../dist/Persistence/Journal/Surface.js')
const lifecycle = await import('../../../dist/Mission/Lifecycle/Surface.js')
const workspaceSurface = await import('../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js')

const withRoad = async (body) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-loop-signal-'))
  const stateRoot = await journalSurface.JournalSurface_initializeStateRoot(root)
  const store = workspaceSurface.acquire(stateRoot)
  try {
    await body({ root, stateRoot, store, signalFor: async (workId) => hostSurface.loopSignalOf(root, workId) })
  } finally {
    workspaceSurface.release(stateRoot)
    rmSync(root, { recursive: true, force: true })
  }
}

// A relayed retirement: the reviewed-but-not-promoted work hands the road to the
// successor incumbency it opened in the same record.
const relayedRoad = async (store, workId, root) => {
  const opened = await lifecycle.seedOpen(store, 'S4', workId, root, 0n)
  assert.equal(opened.ok, true, opened.error)
  const reviewed = await lifecycle.seedReview(store, workId, 'Stayed')
  assert.equal(reviewed.ok, true, reviewed.error)
  const confirmed = await lifecycle.seedDeliver(store, workId, 'run-1', 'call-1', 1n)
  assert.equal(confirmed.ok, true, confirmed.error)
  const relay = await lifecycle.seedDeliver(store, workId, 'run-2', 'call-2', 2n)
  assert.equal(relay.ok, true, relay.error)
}

test('WHAT[change-integration-009] a relayed retirement whose successor already walks holds no fresh loop signal', async () => {
  await withRoad(async ({ root, stateRoot, store, signalFor }) => {
    const workId = 'ses-loop-relayed'
    await relayedRoad(store, workId, root)

    const view = lifecycle.inspected(stateRoot, workId)
    assert.equal(typeof view.ActiveId, 'string', 'the relay opened its successor incumbency')
    assert.notEqual(view.ActiveId.length, 0, 'the successor incumbency has an identity')
    assert.equal(view.Reviewed, false, 'the successor tenure is not reviewed yet')
    assert.equal(view.Delivered, false, 'a relay is not a delivered tenure')

    // Reporting the consumed hand-off again would drive the program through
    // AwaitLoopSignal -> ContinueLoop without ever blocking. The road must wait on
    // durable change instead: the successor's own retirement is the next occasion.
    assert.equal((await signalFor(workId)).kind, 'None')
    assert.equal((await signalFor(workId)).detail, '')
  })
})

test('WHAT[change-integration-009] a delivered tenure with no successor holds the publication candidate signal once', async () => {
  await withRoad(async ({ root, stateRoot, store, signalFor }) => {
    const workId = 'ses-loop-delivered'
    const opened = await lifecycle.seedOpen(store, 'S4', workId, root, 0n)
    assert.equal(opened.ok, true, opened.error)
    const reviewed = await lifecycle.seedReview(store, workId, 'Promoted')
    assert.equal(reviewed.ok, true, reviewed.error)
    const confirmed = await lifecycle.seedDeliver(store, workId, 'run-1', 'call-1', 1n)
    assert.equal(confirmed.ok, true, confirmed.error)
    // The promoted tenure's closing deliver writes the Delivered retirement. Its
    // trailing logical-run closure needs a booted shared journal runtime, which this
    // probe deliberately does not own, so the durable work state is the evidence.
    await lifecycle.seedDeliver(store, workId, 'run-2', 'call-2', 2n)

    const view = lifecycle.inspected(stateRoot, workId)
    assert.equal(view.Delivered, true, 'the promoted tenure delivered')
    assert.equal(view.ActiveId ?? null, null, 'a final delivery leaves no open tenure')

    const delivered = await signalFor(workId)
    assert.equal(delivered.kind, 'Candidate')
    assert.equal(delivered.detail, '')
  })
})

test('WHAT[change-integration-009] no durable work and an unretired tenure both hold no loop signal', async () => {
  await withRoad(async ({ root, store, signalFor }) => {
    assert.equal((await signalFor('ses-loop-absent')).kind, 'NoWork')

    const workId = 'ses-loop-open'
    const opened = await lifecycle.seedOpen(store, 'S4', workId, root, 0n)
    assert.equal(opened.ok, true, opened.error)
    assert.equal((await signalFor(workId)).kind, 'None')
  })
})
}
