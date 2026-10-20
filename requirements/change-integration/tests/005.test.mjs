import test from 'node:test'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const change = await import('../../../dist/Change/Surface.js')

test('WHAT[change-integration-005] rebase conflict records machine fact and continues the loop outside the gate', async () => {
  const observation = await change.observeRelayProgram('rebase-conflict')

  assert.deepEqual(observation.facts, ['CandidateReady', 'ConflictDetected'])
  assert.deepEqual(observation.continuations, ['surface-loop-1'])
  assert.equal(observation.gateAcquireCount, 0)
  assert.equal(observation.ffCalls, 0)
  const conflict = observation.timeline.indexOf('fact:ConflictDetected')
  const continuation = observation.timeline.indexOf('continue:surface-loop-1')
  assert.ok(conflict >= 0 && continuation > conflict)
})
test('WHAT[change-integration-005] artifact conflict continues the loop outside the gate', async () => {
  const observation = await change.observeRelayProgram('artifact-conflict')

  assert.deepEqual(observation.facts, ['ConflictDetected'])
  assert.deepEqual(observation.continuations, ['surface-loop-1'])
  assert.equal(observation.gateAcquireCount, 0)
  assert.equal(observation.ffCalls, 0)
  const conflict = observation.timeline.indexOf('fact:ConflictDetected')
  const continuation = observation.timeline.indexOf('continue:surface-loop-1')
  assert.ok(conflict >= 0 && continuation > conflict)
})
}

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

test('WHAT[change-integration-005] GIT_conflicted_files_parses_lines', async () => {
  const result = await change.gitConflictedFiles(git(fakeRunner([['diff --name-only --diff-filter=U', [0, 'a.fs\nb.fs\n', '']]]).runner), WORKTREE)
  assert.deepEqual(ok(result), ['a.fs', 'b.fs'])
})
test('WHAT[change-integration-005] GIT_conflicted_files_error_propagates', async () => {
  const result = await change.gitConflictedFiles(git(fakeRunner([['diff --name-only --diff-filter=U', [1, '', 'not a repo']]]).runner), WORKTREE)
  assert.equal(result.ok, false)
  assert.equal(result.error, 'not a repo')
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

test('WHAT[change-integration-005] ConflictDetected_preserves_machine_conflict_evidence_on_the_same_Road_worktree', () => {
  const { job } = jobAt(fact.conflictDetected(['src/a.fs', 'src/b.fs']))
  assert.deepEqual(job.facts, ['ConflictDetected'])
  const conflict = change.fact('ConflictDetected', {
    candidateCommit: 'c1',
    targetHeadSnapshot: 'h1',
    conflictFiles: ['src/a.fs', 'src/b.fs'],
    diagnosticsDigest: 'digest'})
  assert.deepEqual(conflict.payload.conflictFiles, ['src/a.fs', 'src/b.fs'])
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
    expectedHead,
    authorityRevision: 'authority-1'}})
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

test('WHAT[change-integration-005] THEOREM_conflict_detected_remains_independent_durable_evidence', () => {
  const folded = foldEvents([
    createEvent(JOB_A, 'ses_orch_a'),
    candidateEvent(JOB_A),
    conflictEvent(JOB_A, { conflictFiles: ['publish_proof.txt', 'src/a.fs'] }),
  ])
  assert.deepEqual(factsOf(folded, JOB_A), ['CandidateReady', 'ConflictDetected'])
})
test('WHAT[change-integration-005] THEOREM_drop_ephemeral_preserves_conflict_evidence', () => {
  const durable = [createEvent(JOB_A, 'ses_orch_a'), conflictEvent(JOB_A)]
  const before = foldEvents(durable)
  assert.deepEqual(factsOf(before, JOB_A), ['ConflictDetected'])

  const after = foldEvents(durable)
  assert.deepEqual(factsOf(after, JOB_A), ['ConflictDetected'])
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const change = await import('../../../dist/Change/Surface.js')

test('WHAT[change-integration-005] cleanup failure after landed FF preserves published fact as PublishedPendingCleanup', async () => {
  const observation = await change.observeRelayProgram('cleanup-failed')

  // In R12/R13, cleanup failure after FF succeeds must NEVER masquerade as IntegrationFailed
  // It must preserve the publication outcome and report PublishedPendingCleanup
  assert.equal(observation.verdict.kind, 'PublishedPendingCleanup')
  assert.match(observation.verdict.detail, /Published rebased-1/)
  assert.match(observation.verdict.detail, /cleanup pending:/)
  assert.match(observation.verdict.detail, /disk detached/)
  assert.ok(observation.facts.includes('Published'))
  assert.ok(!observation.facts.includes('JobFailed'))
})
test('WHAT[change-integration-005] reentry cleanup failure preserves PublishedPendingCleanup', async () => {
  const observation = await change.observeRelayProgram('reentry-cleanup-failed')

  assert.equal(observation.verdict.kind, 'PublishedPendingCleanup')
  assert.match(observation.verdict.detail, /Published rebased-1/)
  assert.match(observation.verdict.detail, /cleanup pending:/)
  assert.match(observation.verdict.detail, /disk detached/)
  assert.equal(observation.ffCalls, 1)
  assert.deepEqual(observation.ffPinnedCandidates, ['rebased-1'])
  assert.ok(observation.facts.includes('Published'))
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

test('WHAT[change-integration-005] WORKTREE_create_returns_owned_resource_and_marks_path_identity', async () => {
  const { git, calls } = fakeGit()
  const resource = await valueOf(change.worktreeCreate(git, 'job-9', PATH))

  assertOpaque(resource, 'owned worktree resource')
  assert.equal(change.worktreePath(resource), PATH)
  assert.equal(change.worktreeIdentity(resource), 'manager/job-9')
  assert.deepEqual(calls, [{ args: ['worktree', 'add', PATH, '-b', 'manager/job-9'], cwd: '/repo' }])
})
test('WHAT[change-integration-005] WORKTREE_release_removes_worktree_and_branch_once', async () => {
  const { git, calls } = fakeGit()
  const resource = await valueOf(change.worktreeCreate(git, 'job-9', PATH))

  const first = await change.worktreeRelease(resource)
  const second = await change.worktreeRelease(resource)
  assert.equal(first.ok, true)
  assert.equal(second.ok, true, 'release is idempotent')
  assert.equal(calls.filter(({ args }) => args[1] === 'remove').length, 1)
  assert.deepEqual(
    calls.filter(({ args }) => args[0] === 'branch'),
    [{ args: ['branch', '-D', 'manager/job-9'], cwd: '/repo' }])
})
test('WHAT[change-integration-005] WORKTREE_release_aggregates_both_failures', async () => {
  const { git } = fakeGit([
    ['worktree remove', [1, '', 'rm failed']],
    ['branch -D', [1, '', 'branch failed']],
  ])
  const resource = await valueOf(change.worktreeCreate(git, 'job-9', PATH))
  const result = await change.worktreeRelease(resource)
  assert.equal(result.ok, false)
  assert.equal(result.error, 'worktree=rm failed; branch=branch failed')
})
test('WHAT[change-integration-005] WORKTREE_release_reports_single_failure_side', async () => {
  const first = fakeGit([['worktree remove', [1, '', 'rm failed']]])
  const resource = await valueOf(change.worktreeCreate(first.git, 'job-9', PATH))
  const result = await change.worktreeRelease(resource)
  assert.equal(result.error, 'worktree=rm failed')

  const second = fakeGit([['branch -D', [1, '', 'branch failed']]])
  const adopted = change.worktreeAdopt(second.git, 'manager/job-9', PATH)
  const result2 = await change.worktreeRelease(adopted)
  assert.equal(result2.error, 'branch=branch failed')
})
test('WHAT[change-integration-005] WORKTREE_unreleased_resource_disposes_by_releasing', async () => {
  const { git, calls } = fakeGit()
  const resource = await valueOf(change.worktreeCreate(git, 'job-9', PATH))
  await change.worktreeDispose(resource)
  assert.equal(calls.filter(({ args }) => args[1] === 'remove').length, 1)
})
test('WHAT[change-integration-005] WORKTREE_CMD_remove_force_flag_and_no_cwd', async () => {
  const fake = fakeRunner([])
  const git = change.createGit('/repo', fake.runner)
  const result = await change.gitRemoveWorktree(git, PATH)
  assert.equal(result.ok, true)
  assert.deepEqual(fake.calls[0].args, ['worktree', 'remove', '--force', PATH])
  assert.equal(fake.calls[0].cwd, undefined)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { execFileSync, spawnSync } = await import("node:child_process");
const { chmodSync, mkdtempSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const change = await import('../../../dist/Change/Surface.js');

const seedRepo = (repo) => {
  execFileSync('git', ['init', '--quiet', repo])
  execFileSync('git', ['config', 'user.email', 'test@example.com'], { cwd: repo })
  execFileSync('git', ['config', 'user.name', 'test'], { cwd: repo })
  writeFileSync(join(repo, 'seed.txt'), 'seed\n')
  execFileSync('git', ['add', 'seed.txt'], { cwd: repo })
  execFileSync('git', ['commit', '--quiet', '-m', 'seed'], { cwd: repo })
}

const realRunner = (command) => {
  const result = spawnSync(command.fileName, command.args, {
    cwd: command.workingDirectory,
    encoding: 'utf8'})
  return Promise.resolve([result.status ?? 1, result.stdout ?? '', result.stderr ?? ''])
}

const createRealWorktree = async (repo, child, job) => {
  const git = change.createGit(repo, realRunner)
  return change.gitCreateWorktree(git, job, child)
}

integrationTest('WHAT[change-integration-005] CHGINT_worktree_create_argv_is_accepted_by_real_git', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-worktree-real-'))
  const repo = join(root, 'repo')
  const child = join(root, 'manager-job-real')

  try {
    seedRepo(repo)
    const created = await createRealWorktree(repo, child, 'job-real')
    assert.equal(created.ok, true, created.ok ? '' : created.error)
    assert.equal(created.value, 'manager/job-real')
    assert.equal(execFileSync('git', ['-C', child, 'branch', '--show-current'], { encoding: 'utf8' }).trim(), 'manager/job-real')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

integrationTest('WHAT[change-integration-005] CHGINT_worktree_create_survives_installed_wanxiang_reference_transaction_hook', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-worktree-hooked-'))
  const repo = join(root, 'repo')
  const child = join(root, 'manager-job-hooked')

  try {
    seedRepo(repo)
    const hook = join(repo, '.git', 'hooks', 'reference-transaction')
    writeFileSync(hook, '#!/bin/sh\nexit 0\n')
    chmodSync(hook, 0o755)

    const created = await createRealWorktree(repo, child, 'job-hooked')
    assert.equal(created.ok, true, created.ok ? '' : created.error)
    assert.equal(execFileSync('git', ['-C', child, 'branch', '--show-current'], { encoding: 'utf8' }).trim(), 'manager/job-hooked')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
}

{
// change-integration-005/013: a delivered Manager life whose candidate lost to a
// machine conflict must not re-prepare the refused candidate. Change records the
// conflict outside the gate and hands the same Work to its next tenure — the
// `Reopened` + `IncumbencyOpened` pair — which opens read-only and unreviewed, so
// the road re-enters review instead of re-reporting the delivered candidate.
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");

const hostSurface = await import('../../../dist/Change/Host/Surface.js')
const journalSurface = await import('../../../dist/Persistence/Journal/Surface.js')
const lifecycle = await import('../../../dist/Mission/Lifecycle/Surface.js')
const workspaceSurface = await import('../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js')

const withSeededRoad = async (sessionId, outcome, body) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-conflict-handoff-'))
  const stateRoot = await journalSurface.JournalSurface_initializeStateRoot(root)
  const store = workspaceSurface.acquire(stateRoot)
  try {
    const opened = await lifecycle.seedOpen(store, 'S4', sessionId, root, 0n)
    assert.equal(opened.ok, true, opened.error)
    const reviewed = await lifecycle.seedReview(store, sessionId, outcome)
    assert.equal(reviewed.ok, true, reviewed.error)
    const confirmed = await lifecycle.seedDeliver(store, sessionId, 'run-1', 'call-1', 1n)
    assert.equal(confirmed.ok, true, confirmed.error)
    // The closing deliver commits the Delivered retirement first and then closes the
    // session's authority logical run. The closure needs a booted shared journal
    // runtime, which this probe deliberately does not own, so the returned receipt is
    // not the evidence — the durable work state is.
    await lifecycle.seedDeliver(store, sessionId, 'run-2', 'call-2', 2n)
    const view = lifecycle.inspected(stateRoot, sessionId)
    assert.equal(typeof view, 'object', 'the seeded road must be inspectable')
    assert.equal(view.Delivered || outcome === 'Stayed', true, 'the seeded road must own its closing retirement')
    await body({ root, stateRoot, view })
  } finally {
    workspaceSurface.release(stateRoot)
    rmSync(root, { recursive: true, force: true })
  }
}

test('WHAT[change-integration-005] a delivered tenure hands the road on instead of re-preparing the refused candidate', async () => {
  await withSeededRoad('ses-conflict-handoff', 'Promoted', async ({ root, stateRoot, view }) => {
    assert.equal(view.Delivered, true, 'the hand-off starts from a delivered retirement')
    assert.equal(view.ActiveId ?? null, null, 'a delivery leaves no open tenure')

    const receipt = await hostSurface.handOffDeliveredTenure(root, 'ses-conflict-handoff', 'hand-off:conflict-1', 3)
    assert.equal(receipt.ok, true, receipt.error)
    assert.equal(receipt.kind, 'HandOff')

    const next = lifecycle.inspected(stateRoot, 'ses-conflict-handoff')
    assert.equal(next.Delivered, false, 'the hand-off leaves the delivered state')
    assert.equal(next.ActiveId, receipt.incumbencyId, 'the next tenure is the new active incumbency')
    assert.notEqual(next.ActiveId, view.ActiveId ?? null, 'the hand-off opens a tenure, it revives none')
    assert.equal(next.Reviewed, false, 'the next tenure owes its own review first')
    assert.equal(next.OpeningStage, 'S4', 'the hand-off keeps the sequence projection')
    assert.equal(next.Authority, view.Authority, 'the hand-off keeps the same authority')
    assert.equal(next.Retirements.length, view.Retirements.length, 'retired history is not rewritten')

    // The program reads this signal before it waits. The delivered candidate is
    // consumed by the open next tenure, so a conflict continuation cannot
    // re-prepare the same refused candidate in an unbounded loop.
    assert.equal((await hostSurface.loopSignalOf(root, 'ses-conflict-handoff')).kind, 'None')

    const again = await hostSurface.handOffDeliveredTenure(root, 'ses-conflict-handoff', 'hand-off:conflict-1', 3)
    assert.equal(again.ok, true, again.error)
    assert.equal(again.kind, 'AlreadyWalking', 'a repeated hand-off opens no second tenure')
    assert.equal(again.incumbencyId, receipt.incumbencyId)
    assert.equal(lifecycle.inspected(stateRoot, 'ses-conflict-handoff').ActiveId, receipt.incumbencyId)
  })
})

test('WHAT[change-integration-005] the hand-off refuses to invent the cursor it never observed', async () => {
  await withSeededRoad('ses-conflict-no-cursor', 'Promoted', async ({ root, stateRoot, view }) => {
    const refused = await hostSurface.handOffDeliveredTenure(root, 'ses-conflict-no-cursor', 'hand-off:conflict-2', null)
    assert.equal(refused.ok, false)
    assert.equal(refused.kind, 'Refused')
    assert.match(refused.error, /observed head cursor/)

    const after = lifecycle.inspected(stateRoot, 'ses-conflict-no-cursor')
    assert.equal(after.Delivered, view.Delivered, 'a refused hand-off writes nothing')
    assert.equal(after.ActiveId ?? null, null)
    assert.equal(after.Retirements.length, view.Retirements.length)
  })
})

test('WHAT[change-integration-005] a relayed tenure needs no hand-off: its successor already walks', async () => {
  await withSeededRoad('ses-conflict-relayed', 'Stayed', async ({ root, stateRoot, view }) => {
    assert.equal(view.ActiveId.length > 0, true, 'a relay opens its successor in the same record')
    const observed = await hostSurface.handOffDeliveredTenure(root, 'ses-conflict-relayed', 'hand-off:relayed', 3)
    assert.equal(observed.ok, true, observed.error)
    assert.equal(observed.kind, 'AlreadyWalking')
    assert.equal(observed.incumbencyId, view.ActiveId, 'the relay successor stays the active tenure')
    const after = lifecycle.inspected(stateRoot, 'ses-conflict-relayed')
    assert.equal(after.ActiveId, view.ActiveId)
    assert.equal(after.Retirements.length, view.Retirements.length)
  })
})
}

{
// change-integration-005/013: publishing happens after the qualifying tenure has
// delivered, so the open-tenure review is absent exactly when the gate reads it.
// The gate must read the delivering round's own review, and must keep refusing a
// road whose only retired round was a relay or whose new tenure has not reviewed.
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");

const hostSurface = await import('../../../dist/Change/Host/Surface.js')
const journalSurface = await import('../../../dist/Persistence/Journal/Surface.js')
const lifecycle = await import('../../../dist/Mission/Lifecycle/Surface.js')
const workspaceSurface = await import('../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js')

test('WHAT[change-integration-005] the publish gate reads the review of the tenure that delivered the candidate', async () => {
  const sessionId = 'ses-conflict-publish-review'
  const root = mkdtempSync(join(tmpdir(), 'wxs-conflict-publish-'))
  const stateRoot = await journalSurface.JournalSurface_initializeStateRoot(root)
  const store = workspaceSurface.acquire(stateRoot)
  try {
    const opened = await lifecycle.seedOpen(store, 'S4', sessionId, root, 0n)
    assert.equal(opened.ok, true, opened.error)
    assert.equal((await lifecycle.seedReview(store, sessionId, 'Promoted')).ok, true)
    assert.equal((await lifecycle.seedDeliver(store, sessionId, 'run-1', 'call-1', 1n)).ok, true)

    // The qualifying tenure reviews, then delivers: after the delivery no tenure is
    // open, and the gate must still see the Promoted review that produced it.
    await lifecycle.seedDeliver(store, sessionId, 'run-2', 'call-2', 2n)
    assert.equal(lifecycle.inspected(stateRoot, sessionId).Delivered, true)
    const delivered = await hostSurface.publishReviewOf(root, sessionId)
    assert.equal(delivered.kind, 'Reviewed', 'the delivering round review must qualify publishing')
    assert.equal(delivered.outcome, 'Promoted')

    // The hand-off opens the next tenure; until that tenure reviews, the gate refuses.
    const receipt = await hostSurface.handOffDeliveredTenure(root, sessionId, 'hand-off:publish-review', 3)
    assert.equal(receipt.kind, 'HandOff', receipt.error)
    assert.equal((await hostSurface.publishReviewOf(root, sessionId)).kind, 'Refused', 'an unreviewed next tenure cannot publish')

    // Re-reviewed next tenure: its own review qualifies, and after it delivers the
    // review survives its retirement again.
    assert.equal((await lifecycle.seedReview(store, sessionId, 'Promoted')).ok, true)
    assert.equal((await hostSurface.publishReviewOf(root, sessionId)).outcome, 'Promoted')
    assert.equal((await lifecycle.seedDeliver(store, sessionId, 'run-3', 'call-3', 4n)).ok, true)
    await lifecycle.seedDeliver(store, sessionId, 'run-4', 'call-4', 5n)
    assert.equal(lifecycle.inspected(stateRoot, sessionId).Delivered, true)
    assert.equal(
      (await hostSurface.publishReviewOf(root, sessionId)).kind,
      'Reviewed',
      'the second delivery must not erase the review evidence the gate needs',
    )
  } finally {
    workspaceSurface.release(stateRoot)
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[change-integration-005] a relayed round review never qualifies a publish', async () => {
  const sessionId = 'ses-conflict-relay-review'
  const root = mkdtempSync(join(tmpdir(), 'wxs-conflict-relay-review-'))
  const stateRoot = await journalSurface.JournalSurface_initializeStateRoot(root)
  const store = workspaceSurface.acquire(stateRoot)
  try {
    assert.equal((await lifecycle.seedOpen(store, 'S4', sessionId, root, 0n)).ok, true)
    assert.equal((await lifecycle.seedReview(store, sessionId, 'Stayed')).ok, true)
    assert.equal((await lifecycle.seedDeliver(store, sessionId, 'run-1', 'call-1', 1n)).ok, true)
    assert.equal((await lifecycle.seedDeliver(store, sessionId, 'run-2', 'call-2', 2n)).ok, true)

    const view = lifecycle.inspected(stateRoot, sessionId)
    assert.equal(view.ActiveId.length > 0, true, 'the relay opened its successor')
    assert.equal(
      (await hostSurface.publishReviewOf(root, sessionId)).kind,
      'Refused',
      'a relay hand-off has no delivery whose review could qualify publishing',
    )
  } finally {
    workspaceSurface.release(stateRoot)
    rmSync(root, { recursive: true, force: true })
  }
})
}
