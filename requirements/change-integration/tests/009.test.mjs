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
  publishClaimed: () =>
    change.fact('PublishClaimed', {
      targetRef: 'refs/heads/main',
      rebasedCommit: 'r1',
      expectedHead: 'h1'}),
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
// The durable loop signal Change derives for one Manager road. An active tenure
// holds no signal. Completion with no active tenure holds Candidate. A relay
// leaves its successor active, so the signal stays None.
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");

const hostSurface = await import('../../../dist/Change/Host/Surface.js')
const journalSurface = await import('../../../dist/Persistence/Journal/Surface.js')
const lifecycle = await import('../../../dist/Mission/Lifecycle/Surface.js')
const surface = lifecycle.LifecycleSurface ?? lifecycle

const withRoad = async (body) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-loop-signal-'))
  const stateRoot = await journalSurface.JournalSurface_initializeStateRoot(root)
  try {
    await body({ root, stateRoot, signalFor: async (workId) => hostSurface.loopSignalOf(root, workId) })
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

const review = (stateRoot, workId, requestId, updates) => {
  const receipt = surface.submitReview(stateRoot, workId, requestId, 'Manager', 'run-1', requestId, updates, [], '')
  assert.equal(receipt.status, 'Committed', receipt.reason)
  return receipt
}

// A nonempty review then clearing that key relays. The successor stays active,
// so the loop signal stays None.
const relayedRoad = (stateRoot, workId) => {
  const reviewed = review(stateRoot, workId, 'review', [{ key: 'job', value: 'do' }])
  assert.equal(reviewed.kind, 'Reviewed', reviewed.reason)
  assert.equal(reviewed.milestone, '执行')
  const relay = review(stateRoot, workId, 'clear', [{ key: 'job', value: null }])
  assert.equal(relay.kind, 'Relayed', relay.reason)
}

const completedRoad = (stateRoot, workId) => {
  relayedRoad(stateRoot, workId)
  const advanced = review(stateRoot, workId, 'advance', [])
  assert.equal(advanced.kind, 'Advanced', advanced.reason)
  assert.equal(advanced.milestone, '收尾')
  const completed = review(stateRoot, workId, 'close', [])
  assert.equal(completed.kind, 'Completed', completed.reason)
  assert.equal(completed.milestone, '已完成')
}

test('WHAT[change-integration-009] a relayed retirement whose successor already walks holds no fresh loop signal', async () => {
  await withRoad(async ({ stateRoot, signalFor }) => {
    const workId = 'ses-loop-relayed'
    relayedRoad(stateRoot, workId)

    const view = surface.inspected(stateRoot, workId)
    assert.equal(typeof view.ActiveId, 'string', 'the relay left its successor incumbency active')
    assert.notEqual(view.ActiveId.length, 0, 'the successor incumbency has an identity')
    assert.equal(view.Reviewed, false, 'the successor tenure is not reviewed yet')
    assert.equal(view.Delivered, false, 'a relay is not a completed tenure')
    assert.equal(Array.from(view.Retirements ?? []).at(-1).Outcome, 'Relayed')

    // The successor is already walking, so this retirement is not a fresh signal.
    assert.equal((await signalFor(workId)).kind, 'None')
    assert.equal((await signalFor(workId)).detail, '')
  })
})

test('WHAT[change-integration-009] a completed tenure with no successor holds the publication candidate signal once', async () => {
  await withRoad(async ({ stateRoot, signalFor }) => {
    const workId = 'ses-loop-delivered'
    completedRoad(stateRoot, workId)

    const view = surface.inspected(stateRoot, workId)
    assert.equal(view.Delivered, true, 'the completed tenure is delivered')
    assert.equal(view.ActiveId ?? null, null, 'completion leaves no open tenure')
    const retirement = Array.from(view.Retirements ?? []).at(-1)
    assert.equal(retirement.Outcome, 'Completed')
    assert.notEqual(retirement.Outcome, 'Delivered')

    const delivered = await signalFor(workId)
    assert.equal(delivered.kind, 'Candidate')
    assert.equal(delivered.detail, '')
  })
})

test('WHAT[change-integration-009] no durable work and an open tenure both hold no loop signal', async () => {
  await withRoad(async ({ stateRoot, signalFor }) => {
    assert.equal((await signalFor('ses-loop-absent')).kind, 'NoWork')

    const workId = 'ses-loop-open'
    const opened = review(stateRoot, workId, 'review', [{ key: 'job', value: 'do' }])
    assert.equal(opened.kind, 'Reviewed', opened.reason)
    assert.equal(opened.milestone, '执行')
    const view = surface.inspected(stateRoot, workId)
    assert.equal(typeof view.ActiveId, 'string')
    assert.notEqual(view.ActiveId.length, 0)
    assert.equal(view.Delivered, false)
    assert.equal((await signalFor(workId)).kind, 'None')
  })
})
}
