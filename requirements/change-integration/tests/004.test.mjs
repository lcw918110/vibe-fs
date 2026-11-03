import test from 'node:test'

{
const { default: assert } = await import('node:assert/strict')
const change = await import('../../../dist/Change/Surface.js')

test('WHAT[change-integration-004] rebase work holds the gate only for the ff mutation', async () => {
  const observation = await change.observeRelayProgram('fresh')

  assert.deepEqual(observation.rebaseGateHeld, [false])
  assert.deepEqual(observation.ffGateHeld, [true])
  assert.deepEqual(observation.ffExpectedHeads, ['target-1'])
  assert.equal(observation.gateAcquireCount, 1)
  assert.equal(observation.gateReleaseCount, 1)
  assert.equal(observation.gateHeldAfterRun, false)
})
test('WHAT[change-integration-004] conflict resolution never acquires the publish gate', async () => {
  const observation = await change.observeRelayProgram('rebase-conflict')

  assert.deepEqual(observation.rebaseGateHeld, [false])
  assert.deepEqual(observation.ffGateHeld, [])
  assert.equal(observation.gateAcquireCount, 0)
  assert.equal(observation.gateHeldAfterRun, false)
})
test('WHAT[change-integration-004] 10,000 Continue signals complete the real manager loop with exact effects and balanced resources', async () => {
  const observation = await change.observeManagerLoopBurst(10000)

  assert.deepEqual(observation.verdict, { kind: 'IntegrationFailed', detail: 'burst-complete' })
  assert.equal(observation.continuationCount, 10000)
  assert.equal(observation.signalCount, 10001)
  assert.equal(observation.gateAcquireCount, 0)
  assert.equal(observation.gateReleaseCount, 0)
  assert.equal(observation.gateHeldAfterRun, false)
  assert.equal(observation.factCount, 0)
  assert.equal(observation.gitCallCount, 0)
  assert.equal(observation.continuations, undefined)
  assert.equal(observation.timeline, undefined)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { mkdtempSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");

const change = await import('../../../dist/Change/Surface.js')
const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-gate-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}

test('WHAT[change-integration-004] GATE_lock_path_is_stable_per_repo_and_branch', () => {
  const first = change.lockPath('/repo/a', 'main')
  const second = change.lockPath('/repo/a', 'main')
  const otherBranch = change.lockPath('/repo/a', 'dev')
  const otherRepo = change.lockPath('/repo/b', 'main')

  assert.equal(first, second, 'same repo+branch must map to the same lock file')
  assert.notEqual(first, otherBranch)
  assert.notEqual(first, otherRepo)
  assert.match(first, /wanxiangshu-publish-[0-9a-f]{64}$/)
})
test('WHAT[change-integration-004] GATE_acquire_and_release_round_trips', async () => {
  const { dir, cleanup } = sandbox()
  const lockTarget = join(dir, 'target.lock')
  writeFileSync(lockTarget, '')

  const gate = await change.acquireGate(lockTarget)
  await change.releaseGate(gate)
  await change.releaseGate(gate)

  const second = await change.acquireGate(lockTarget)
  await change.releaseGate(second)
  cleanup()
})
test('WHAT[change-integration-004] GATE_dispose_releases_the_lock', async () => {
  const { dir, cleanup } = sandbox()
  const lockTarget = join(dir, 'target.lock')
  writeFileSync(lockTarget, '')

  const gate = await change.acquireGate(lockTarget)
  await change.disposeGate(gate)

  const second = await change.acquireGate(lockTarget)
  await change.releaseGate(second)
  cleanup()
})
test('WHAT[change-integration-004] GATE_competing_acquire_waits_for_holder_release', async () => {
  const { dir, cleanup } = sandbox()
  const lockTarget = join(dir, 'target.lock')
  writeFileSync(lockTarget, '')
  let gate
  let nextGate
  let competing
  let settled = false
  try {
    gate = await change.acquireGate(lockTarget)
    competing = change.acquireGate(lockTarget).then(acquired => {
      nextGate = acquired
      settled = true
    })

    await new Promise((resolve) => setTimeout(resolve, 300))
    assert.equal(settled, false, 'a held lock must not be acquired concurrently')
    await change.releaseGate(gate)
    gate = undefined
    await competing
    assert.equal(settled, true, 'the competitor acquires after the holder releases')
  } finally {
    try {
      if (gate !== undefined) await change.releaseGate(gate)
      if (competing !== undefined) await competing.catch(() => {})
      if (nextGate !== undefined) await change.releaseGate(nextGate)
    } finally {
      cleanup()
    }
  }
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

test('WHAT[change-integration-004] ORCH_004_multiple_jobs_are_active_at_once_and_terminal_ones_drop_out', () => {
  let projection = created()
  projection = change.createJob(projection, payload({ jobId: 'job_2', managerSessionId: 'ses_m2' }))
  projection = change.createJob(projection, payload({ jobId: 'job_3', managerSessionId: 'ses_m3' }))
  assert.equal(change.activeJobs(projection).length, 3)

  const finished = change.recordFact(projection, 'job_2', fact.published())
  assert.deepEqual(change.activeJobs(finished).map((job) => job.jobId).sort(), ['job_1', 'job_3'])
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const change = await import('../../../dist/Change/Surface.js')
const JOB_A = 'job_a'
const JOB_B = 'job_b'
const createEvent = (jobId, managerSessionId, worktreeIdentity = `wt_${jobId.slice(-1)}`) => ({
  kind: 'ManagerJobCreated',
  payload: {
    jobId,
    managerSessionId,
    managerAgent: 'manager',
    byname: 'Road',
    worktreeIdentity,
    worktreePath: `/tmp/${worktreeIdentity}`,
    targetRef: 'refs/heads/main',
    targetBranchFrozen: 'refs/heads/main'}})
const candidateEvent = (
  jobId,
  candidateCommit = 'c1') => ({
  kind: 'CandidateReady',
  payload: { jobId, candidateCommit }})
const conflictEvent = (jobId, { candidateCommit = 'c1', targetHeadSnapshot = 'h1', conflictFiles = ['publish_proof.txt'] } = {}) => ({
  kind: 'ConflictDetected',
  payload: {
    jobId,
    candidateCommit,
    targetHeadSnapshot,
    conflictFiles,
    diagnosticsDigest: 'conflict-digest'}})
const rebasedEvent = (
  jobId,
  { rebasedCommit = 'r1', targetHeadSnapshot = 'h1' } = {}) => ({
  kind: 'RebasedCandidateReady',
  payload: { jobId, rebasedCommit, targetHeadSnapshot }})
const publishClaimedEvent = (jobId, expectedHead = 'h1') => ({
  kind: 'PublishClaimed',
  payload: {
    jobId,
    targetRef: 'refs/heads/main',
    rebasedCommit: 'r1',
    expectedHead}})
const publishedEvent = (jobId, candidateCommit = 'c1', resultingTargetHead = 'r1') => ({
  kind: 'Published',
  payload: { jobId, candidateCommit, resultingTargetHead }})
const foldEvents = (events) => {
  const result = change.fold(events)
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.value
}
const factsOf = (projection, jobId) => change.find(projection, jobId).facts
const classifyRebased = (head, rebasedCommit = 'r1', snapshot = 'h1') =>
  change.classifyRebasedCandidate(head ?? null, rebasedCommit, snapshot)
const classifyClaim = (head, rebasedCommit = 'r1', expectedHead = 'h1') =>
  change.classifyPublishClaim(head ?? null, rebasedCommit, expectedHead)

test('WHAT[change-integration-004] THEOREM_orchestrator_independent_jobs_confluent_across_interleavings', () => {
  const seqA = [createEvent(JOB_A, 'ses_orch_a'), candidateEvent(JOB_A, 'ca', 'snapshot-a', 'certificate-a')]
  const seqB = [createEvent(JOB_B, 'ses_orch_b'), candidateEvent(JOB_B, 'cb', 'snapshot-b', 'certificate-b')]
  const foldAB = foldEvents([...seqA, ...seqB])
  const foldBA = foldEvents([...seqB, ...seqA])

  assert.deepEqual(factsOf(foldAB, JOB_A), ['CandidateReady'])
  assert.deepEqual(factsOf(foldAB, JOB_B), ['CandidateReady'])
  assert.deepEqual(factsOf(foldBA, JOB_A), ['CandidateReady'])
  assert.deepEqual(factsOf(foldBA, JOB_B), ['CandidateReady'])

  for (const interleaving of [
    [...seqA, ...seqB],
    [seqA[0], seqB[0], seqA[1], seqB[1]],
    [seqB[0], seqA[0], seqB[1], seqA[1]],
    [...seqB, ...seqA],
  ]) {
    const folded = foldEvents(interleaving)
    assert.deepEqual(factsOf(folded, JOB_A), factsOf(foldAB, JOB_A))
    assert.deepEqual(factsOf(folded, JOB_B), factsOf(foldAB, JOB_B))
  }
})
}

test('WHAT[change-integration-004] actual publish program releases the gate before cleaning Road resources exactly once', async () => {
  const assert = (await import('node:assert/strict')).default
  const change = await import('../../../dist/Change/Surface.js')
  const observation = await change.observeRelayProgram('fresh')
  const release = observation.timeline.indexOf('gate:release')
  const terminations = observation.timeline.flatMap((event, index) => event === 'relay:terminate' ? [index] : [])
  assert.ok(release >= 0)
  assert.equal(terminations.length, 1)
  assert.ok(terminations[0] > release)
  assert.equal(observation.verdict.kind, 'Published')
})

test.todo('WHAT[change-integration-004] GAP-215 / 55-D1: determine how durable claim and Published writes fit the stated ref-only critical section; actual program currently performs them under the gate')
