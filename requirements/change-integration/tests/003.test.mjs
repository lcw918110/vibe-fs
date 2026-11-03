import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const change = await import('../../../dist/Change/Surface.js')
const hostSurface = await import('../../../dist/Change/Host/Surface.js')
const fakeRunner = (answers) => {
  const calls = []
  const runner = (command) => {
    const args = command.args
    calls.push({ file: command.fileName, args, cwd: command.workingDirectory })
    const key = args.join(' ')
    for (const [prefix, response] of answers) {
      if (!key.startsWith(prefix)) continue
      if (Array.isArray(response) && Array.isArray(response[0])) {
        const triple = response.length > 1 ? response.shift() : response[0]
        return Promise.resolve(triple)
      }
      return Promise.resolve(response)
    }
    return Promise.resolve([0, '', ''])
  }
  return { runner, calls }
}
const REPO = '/repo'
const WORKTREE = '/repo/.worktrees/job-1'
const git = (runner) => change.createGit(REPO, runner)
const ok = (result) => {
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.value
}
const ffAnswers = ({ candidate = 'cafe01', targetHead = 'beef02', branch = 'main' } = {}) => [
  ['symbolic-ref --short HEAD', [0, `${branch}\n`, '']],
  ['rev-parse HEAD', [0, `${candidate}\n`, '']],
  ['rev-parse refs/heads/main', [0, `${targetHead}\n`, '']],
  ['merge-base --is-ancestor', [0, '', '']],
  ['status --porcelain', [0, '', '']],
  ['merge --ff-only', [0, '', '']],
]
const ff = (answers) => change.gitFfMerge(git(fakeRunner(answers).runner), WORKTREE, 'main', 'beef02', 'cafe01')

test('WHAT[change-integration-003] GIT_rebase_ok_on_zero_exit', async () => {
  const result = await change.gitRebase(git(fakeRunner([['rebase main', [0, '', '']]]).runner), WORKTREE, 'main')
  assert.equal(result.ok, true)
})
test('WHAT[change-integration-003] GIT_rebase_stale_rebase_head_is_cleared_before_fresh_rebase', async () => {
  const fake = fakeRunner([])
  await change.gitRebase(git(fake.runner), WORKTREE, 'main')
  assert.deepEqual(fake.calls.map((call) => call.args.join(' ')), [
    'rev-parse --git-path rebase-merge',
    'rev-parse --git-path rebase-apply',
    'update-ref -d REBASE_HEAD',
    'rebase main',
  ])
})
test('WHAT[change-integration-003] GIT_rebase_in_progress_stages_and_continues', async () => {
  const { mkdtempSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const dir = mkdtempSync(join(tmpdir(), 'wxs-rebase-'))
  const fake = fakeRunner([['rev-parse --git-path rebase-merge', [0, `${dir}\n`, '']]])
  const result = await change.gitRebase(git(fake.runner), WORKTREE, 'main')
  assert.equal(result.ok, true)
  assert.deepEqual(fake.calls.map((call) => call.args.join(' ')), [
    'rev-parse --git-path rebase-merge',
    'rev-parse --git-path rebase-apply',
    'add -A',
    '-c core.editor=true rebase --continue',
  ])
  rmSync(dir, { recursive: true, force: true })
})
test('WHAT[change-integration-003] GIT_rebase_continue_failure_surfaces_stderr', async () => {
  const { mkdtempSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const dir = mkdtempSync(join(tmpdir(), 'wxs-rebase-fail-'))
  const fake = fakeRunner([
    ['rev-parse --git-path rebase-apply', [0, `${dir}\n`, '']],
    ['-c core.editor=true rebase --continue', [1, '', 'conflict remains']],
  ])
  const result = await change.gitRebase(git(fake.runner), WORKTREE, 'main')
  assert.equal(result.ok, false)
  assert.equal(result.error, 'conflict remains')
  rmSync(dir, { recursive: true, force: true })
})
test('WHAT[change-integration-003] GIT_rebase_stage_failure_is_an_error', async () => {
  const { mkdtempSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const dir = mkdtempSync(join(tmpdir(), 'wxs-rebase-stage-'))
  const fake = fakeRunner([
    ['rev-parse --git-path rebase-merge', [0, `${dir}\n`, '']],
    ['add -A', [1, '', 'index locked']],
  ])
  const result = await change.gitRebase(git(fake.runner), WORKTREE, 'main')
  assert.equal(result.ok, false)
  assert.equal(result.error, 'index locked')
  rmSync(dir, { recursive: true, force: true })
})
test('WHAT[change-integration-003] GIT_rebase_surfaces_stderr_on_failure', async () => {
  const result = await change.gitRebase(git(fakeRunner([['rebase main', [1, 'stdout-noise', 'CONFLICT (content)']]]).runner), WORKTREE, 'main')
  assert.equal(result.ok, false)
  assert.equal(result.error, 'CONFLICT (content)')
})
test('WHAT[change-integration-003] GIT_candidate_commit_deletes_stale_rebase_head_before_commit_and_surfaces_failure', async () => {
  const fake = fakeRunner([['commit -m candidate: manager-1', [1, '', 'commit rejected']]])
  const result = await hostSurface.finalizeWorktree(fake.runner, 'manager-1', WORKTREE)
  assert.equal(result.ok, false)
  assert.equal(result.error, 'git commit failed: commit rejected')
  assert.deepEqual(fake.calls.slice(-2).map((call) => call.args.join(' ')), [
    'update-ref -d REBASE_HEAD',
    'commit -m candidate: manager-1',
  ])
})
test('WHAT[change-integration-003] GIT_has_rebase_head_true_only_when_git_path_dir_exists', async () => {
  const { mkdtempSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const dir = mkdtempSync(join(tmpdir(), 'wxs-rebase-head-'))
  const fake = fakeRunner([['rev-parse --git-path rebase-merge', [0, `${dir}\n`, '']]])
  assert.equal(await change.gitHasRebaseHead(git(fake.runner), WORKTREE), true)
  assert.deepEqual(fake.calls.map((call) => call.args.join(' ')), [
    'rev-parse --git-path rebase-merge',
    'rev-parse --git-path rebase-apply',
  ])
  assert.equal(await change.gitHasRebaseHead(git(fakeRunner([['rev-parse --git-path rebase-merge', [0, `${dir}-gone\n`, '']]]).runner), WORKTREE), false)
  assert.equal(await change.gitHasRebaseHead(git(fakeRunner([]).runner), WORKTREE), false)
  rmSync(dir, { recursive: true, force: true })
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

test('WHAT[change-integration-003] ORCH_007_each_durable_fact_has_one_projection_slot', () => {
  let projection = created()
  for (const value of [fact.candidateReady(), fact.conflictDetected(), fact.rebased(), fact.publishClaimed()]) {
    projection = change.recordFact(projection, JOB, value)
  }
  assert.deepEqual(change.find(projection, JOB).facts, [
    'CandidateReady',
    'ConflictDetected',
    'RebasedCandidateReady',
    'PublishClaimed',
  ])
})
test('WHAT[change-integration-003] ORCH_007_incomplete_publish_claimed_evidence_is_rejected', () => {
  const incomplete = change.fact('PublishClaimed', { rebasedCommit: 'r1', expectedHead: 'h1' })
  assert.throws(() => change.recordFact(created(), JOB, incomplete), /Incomplete PublishClaimed payload/)
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

test('WHAT[change-integration-003] a claim without a rebased candidate is rejected before recovery', () => {
  const result = change.fold([createEvent(JOB_A, 'ses_orch_a'), candidateEvent(JOB_A), publishClaimedEvent(JOB_A)])
  assert.equal(result.ok, false)
  assert.match(result.error, /no rebased candidate/)
})
test('WHAT[change-integration-003] THEOREM_publish_claimed_with_incomplete_evidence_is_rejected', () => {
  // A claim must still pin all three Git binding fields; dropping expectedHead
  // makes the durable claim incomplete.
  const incomplete = {
    kind: 'PublishClaimed',
    payload: { jobId: JOB_A, targetRef: 'refs/heads/main', rebasedCommit: 'r1' }}
  const result = change.fold([
    createEvent(JOB_A, 'ses_orch_a'),
    candidateEvent(JOB_A),
    rebasedEvent(JOB_A),
    incomplete,
  ])
  assert.equal(result.ok, false)
  assert.match(result.error, /Incomplete PublishClaimed payload/)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const change = await import('../../../dist/Change/Surface.js')

test('WHAT[change-integration-003] reentry with valid complete claim reenters without CandidateReady and publishes on the pin', async () => {
  const observation = await change.observeRelayProgram('reentry-valid')

  assert.deepEqual(observation.verdict, { kind: 'Published', detail: 'rebased-1' })
  assert.deepEqual(observation.ffExpectedHeads, ['target-1'])
  assert.deepEqual(observation.ffPinnedCandidates, ['rebased-1'])
  assert.equal(observation.ffCalls, 1)
  assert.ok(observation.ffCalls <= 1)
  assert.equal(observation.ffGateHeld.length, 1)
  assert.equal(observation.ffGateHeld[0], true)
  assert.ok(observation.facts.includes('Published'))
})
test('WHAT[change-integration-003] reentry with conflicting claim snapshot sends zero FF', async () => {
  const observation = await change.observeRelayProgram('reentry-stale-claim-superseded')

  assert.equal(observation.ffCalls, 0)
  assert.deepEqual(observation.ffPinnedCandidates, [])
  assert.equal(observation.facts.includes('Published'), false)
  // A claim whose pin no longer matches the admitted rebased record is expired
  // evidence. Reentry continues the loop and consumes the terminal signal
  // instead of fast-forwarding the old pin.
  assert.deepEqual(observation.verdict, { kind: 'IntegrationFailed', detail: 'scenario-complete' })
  assert.equal(observation.signalCount, 2)
  assert.equal(observation.gateAcquireCount, 0)
})
test('WHAT[change-integration-003] reentry with conflicting claim target sends zero FF', async () => {
  const observation = await change.observeRelayProgram('reentry-claim-target-mismatch')

  assert.equal(observation.ffCalls, 0)
  assert.deepEqual(observation.ffPinnedCandidates, [])
  assert.equal(observation.facts.includes('Published'), false)
  assert.equal(observation.verdict.kind, 'IntegrationFailed')
  // Hard target mismatch refuses publish recovery without entering the loop.
  assert.equal(observation.signalCount, 0)
})
test('WHAT[change-integration-003] old incomplete claim decode fails closed in fold and recordFact', () => {
  const incomplete = change.fact('PublishClaimed', { rebasedCommit: 'r1', expectedHead: 'h1' })
  assert.throws(() => change.recordFact(change.empty(), 'job_1', incomplete), /Incomplete PublishClaimed payload/)

  const foldResult = change.fold([
    {
      kind: 'ManagerJobCreated',
      payload: {
        jobId: 'job_1',
        managerSessionId: 'ses_1',
        managerAgent: 'manager',
        byname: 'Road',
        worktreeIdentity: 'wt_1',
        worktreePath: '/tmp/wt1',
        targetRef: 'refs/heads/main',
        targetBranchFrozen: 'refs/heads/main'}},
    {
      kind: 'RebasedCandidateReady',
      payload: {
        jobId: 'job_1',
        rebasedCommit: 'r1',
        targetHeadSnapshot: 'h1'}},
    {
      kind: 'PublishClaimed',
      payload: { jobId: 'job_1', rebasedCommit: 'r1', expectedHead: 'h1' }},
  ])
  assert.equal(foldResult.ok, false)
  assert.match(foldResult.error, /Incomplete PublishClaimed payload/)
})
test('WHAT[change-integration-003] reentry FF-success with Published-append failure never masquerades as success', async () => {
  const observation = await change.observeRelayProgram('reentry-published-append-failed')

  assert.equal(observation.ffCalls, 1)
  assert.deepEqual(observation.ffPinnedCandidates, ['rebased-1'])
  assert.equal(observation.facts.includes('Published'), false)
  assert.equal(observation.verdict.kind, 'IntegrationFailed')
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

test('WHAT[change-integration-003] WORKTREE_create_propagates_port_error', async () => {
  const { git } = fakeGit([['worktree add', [1, '', 'worktree add exploded']]])
  const result = await change.worktreeCreate(git, 'job-9', PATH, 'refs/heads/main')
  assert.equal(result.ok, false)
  assert.equal(result.error, 'worktree add exploded')
})
test('WHAT[change-integration-003] WORKTREE_CMD_create_returns_identity_on_success', async () => {
  const { git, calls } = fakeGit()
  const result = await change.gitCreateWorktree(git, 'job-3', PATH, 'refs/heads/main')
  assert.equal(result.ok, true)
  assert.equal(result.value, 'manager/job-3')
  assert.deepEqual(calls[0].args, ['worktree', 'add', PATH, '-b', 'manager/job-3', 'refs/heads/main'])
  assert.equal(calls[0].cwd, '/repo')
})
test('WHAT[change-integration-003] WORKTREE_CMD_create_surfaces_stderr_on_failure', async () => {
  const { git } = fakeGit([['worktree add', [1, '', 'already exists']]])
  const result = await change.gitCreateWorktree(git, 'job-3', PATH, 'refs/heads/main')
  assert.equal(result.error, 'already exists')
})
}

test('WHAT[change-integration-003] publication claim requires every explicit binding field even when candidate facts already exist', async () => {
  const assert = (await import('node:assert/strict')).default
  const change = await import('../../../dist/Change/Surface.js')
  let projection = change.createJob(change.empty(), {
    jobId: 'binding-probe', managerSessionId: 'manager-probe', managerAgent: 'manager', byname: 'Road',
    worktreeIdentity: 'worktree-probe', worktreePath: '/tmp/worktree-probe',
    targetRef: 'refs/heads/main', targetBranchFrozen: 'refs/heads/main'})
  projection = change.recordFact(projection, 'binding-probe', change.fact('CandidateReady', {
    candidateCommit: 'rebased'}))
  projection = change.recordFact(projection, 'binding-probe', change.fact('RebasedCandidateReady', {
    rebasedCommit: 'rebased', targetHeadSnapshot: 'target'}))
  const claim = {
    targetRef: 'refs/heads/main', rebasedCommit: 'rebased', expectedHead: 'target'}
  assert.ok(change.find(change.recordFact(projection, 'binding-probe', change.fact('PublishClaimed', claim)), 'binding-probe').facts.includes('PublishClaimed'))
  for (const field of Object.keys(claim)) {
    const incomplete = { ...claim }
    delete incomplete[field]
    assert.throws(() => change.recordFact(projection, 'binding-probe', change.fact('PublishClaimed', incomplete)), /Incomplete PublishClaimed payload/, field)
  }
})

{
const { default: assert } = await import('node:assert/strict')
const { default: test } = await import('node:test')
const factCodec = await import('../../../dist/Persistence/Journal/FactCodecSurface.js')

test('WHAT[change-integration-003] legacy PublishClaimed with an extra AuthorityRevision still decodes without fabricating an authority', () => {
  const line = factCodec.encode({
    family: 'Orchestrator',
    case: 'PublishClaimed',
    payload: {
      ManagerJobId: 'job-legacy',
      TargetRef: 'refs/heads/main',
      RebasedCommit: 'rebased-1',
      ExpectedHead: 'target-1',
    },
  })
  const decoded = factCodec.decode(line)
  assert.equal(decoded.ok, true, decoded.ok ? '' : decoded.error)
  assert.equal(decoded.case, 'PublishClaimed')

  // Re-inject the retired field the old code wrote, then decode again: the
  // extra key is ignored, and nothing reads it back as a qualification.
  const wire = JSON.parse(line)
  const payload = wire[1][1][1]
  assert.equal(typeof payload, 'object', 'the PublishClaimed payload is the 3rd nesting level')
  payload.AuthorityRevision = 'authority-1'
  const legacy = factCodec.decode(JSON.stringify(wire))
  assert.equal(legacy.ok, true, legacy.ok ? '' : legacy.error)
  assert.equal(legacy.case, 'PublishClaimed')
})
}

test('WHAT[change-integration-003] real git finalization refuses conflict markers even when the scan output is spooled', async () => {
  const assert = (await import('node:assert/strict')).default
  const { mkdtempSync, rmSync, writeFileSync, readdirSync } = await import('node:fs')
  const { execFileSync } = await import('node:child_process')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const hostSurface = await import('../../../dist/Change/Host/Surface.js')
  const repo = mkdtempSync(join(tmpdir(), 'wxs-change-003-spool-'))
  const spoolDirectory = mkdtempSync(join(tmpdir(), 'wxs-change-003-spool-files-'))
  const previousTmp = process.env.TMPDIR
  try {
    execFileSync('git', ['init', '-q', repo])
    execFileSync('git', ['-C', repo, '-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '--allow-empty', '-m', 'base'])
    // More than 200 KiB of `git grep` matches: the scan output crosses the memory
    // bound and arrives as a spooled outcome. Before CUT-20 the Git runner turned
    // that outcome into empty stdout, so the markers were committed.
    writeFileSync(join(repo, 'conflicted.txt'), Array.from({ length: 6000 }, (_, i) => `<<<<<<< ours ${i}\n`).join(''))
    process.env.TMPDIR = spoolDirectory
    const result = await hostSurface.finalizeWorktreeWithGit('manager-spool', repo)
    assert.equal(result.ok, false)
    assert.match(result.error, /^conflict markers remain in worktree:\nconflicted\.txt:1:<<<<<<< ours 0/)
    assert.match(result.error, /conflicted\.txt:6000:<<<<<<< ours 5999$/)
    assert.equal(execFileSync('git', ['-C', repo, 'rev-list', '--count', 'HEAD'], { encoding: 'utf8' }).trim(), '1')
    assert.deepEqual(readdirSync(spoolDirectory), [], 'the Git runner removes the spool file it consumed')
  } finally {
    if (previousTmp === undefined) delete process.env.TMPDIR
    else process.env.TMPDIR = previousTmp
    rmSync(repo, { recursive: true, force: true })
    rmSync(spoolDirectory, { recursive: true, force: true })
  }
})
