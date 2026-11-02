import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { GitWorktrees } from 'wanxiangli/git'
import { FSharpResult$2 } from '../../../dist/fable_modules/fable-library-js.5.13.0/Result.js'
import { empty } from '../../../dist/fable_modules/fable-library-js.5.13.0/List.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import { forEngine } from '../../../dist/Change/Orchestrator/OrchestratorJournalAdapter.js'
import { Orchestrator, Orchestrator__ForkManager_Z1B92A869 as start,
  Orchestrator__RecoverManagerJob_Z4D20F2DB as recover,
  Orchestrator__JoinPublishedBatch_Z6BED3BEF as collect } from '../../../dist/Change/Runtime.js'
import { GitPort, RelayPort, ManagerLoopSignal } from '../../../dist/Change/Types.js'
import { OrchestratorProjectionModule_tryFind as findJob } from '../../../dist/Change/Projection.js'
import { GitWorktrees_FromNative_4E60E31B as worktreesBridge } from '../../../dist/Git/Worktrees.js'
import { IncumbencyIdModule_create as incumbencyId } from '../../../dist/Mission/Work/Contract.js'
import { CausalWaitRuntime_$ctor_71136F3F as createWait,
  CausalWaitRuntime__get_Observer as waitObserver } from '../../../dist/Execution/Session/Wait/Registry.js'
import { ManagerJobIdModule_create as jobId, SessionIdModule_create as sessionId,
  WorktreePathModule_create as worktreePath, TargetRefModule_create as targetRef,
  CommitHashModule_create as commit } from '../../../dist/Foundation/Identity.js'

const ok = value => new FSharpResult$2(0, [value])
const firstVerdict = result => {
  assert.equal(result.tag, 0, 'publication must return a result batch')
  return result.fields[0].fields[0]
}

// This exercises the production Runtime and Journal adapter. The Relay is a
// controlled participant, so this is not a Host session/retirement scenario.
test('actual Change Runtime persists publication and cold recovery only retries resource cleanup', { timeout: 15000 }, async () => {
  const root = mkdtempSync(join(tmpdir(), 'change-journal-bridge-'))
  const id = jobId('journal-bridge')
  const path = join(root, 'manager-worktree')
  const counters = { create: 0, activate: 0, prepare: 0, signal: 0, continue: 0, ff: 0, remove: 0, terminate: 0 }
  let handle
  let port
  let cleanupFails = true
  let landed = false
  const record = () => findJob(id, port.Snapshot().AgentProjections.Orchestrator)
  const open = async suffix => {
    const result = await journal.JournalSurface_bootWithWriterId(root, `writer-${suffix}`, `runtime-${suffix}`, process.pid, new Date().toISOString())
    assert.equal(result.ok, true, result.error)
    handle = result.journal
    port = forEngine(journal.JournalHandle__get_Journal(handle))
  }
  const resources = new GitWorktrees(root, async command => {
    if (command.args[0] === 'worktree' && command.args[1] === 'add') counters.create += 1
    if (command.args[0] === 'worktree' && command.args[1] === 'remove') {
      counters.remove += 1
      if (cleanupFails) return { code: 1, stdout: '', stderr: 'controlled cleanup failure' }
    }
    return { code: 0, stdout: '', stderr: '' }
  })
  const git = new GitPort(
    async () => ok(empty()), worktreesBridge(resources), async () => ok(targetRef('refs/heads/main')),
    async () => ok(undefined), async (_path, _target, expected, candidate) => {
      const pending = record()
      assert.ok(pending.PublishClaimed, 'claim must already be durable before FF')
      assert.equal(pending.Terminal, undefined)
      assert.equal(expected.fields[0], 'base-head')
      assert.equal(candidate.fields[0], 'candidate-head')
      counters.ff += 1
      landed = true
      return ok(commit('candidate-head'))
    }, async () => ok(empty()), async () => false,
    async () => ok(commit('candidate-head')), async () => ok(commit(landed ? 'candidate-head' : 'base-head')),
  )
  const relay = new RelayPort(
    async () => ok(sessionId('manager-bridge')),
    async () => { counters.activate += 1; assert.ok(record()); return ok(undefined) },
    async () => { counters.signal += 1; return ok(ManagerLoopSignal.Candidate) },
    async () => { counters.continue += 1; return ok(incumbencyId('review-rebased-candidate')) },
    async () => { counters.prepare += 1; return ok(commit('candidate-head')) },
    async () => { counters.terminate += 1; assert.equal(record().Terminal.tag, 0) },
  )
  const engine = () => new Orchestrator(waitObserver(createWait()), git, relay, root, targetRef('refs/heads/main'), port)
  const wait = async runtime => firstVerdict(await collect(runtime, 1, new Promise(() => {})))
  try {
    await open('first')
    const runtime = engine()
    const admitted = await start(runtime, id, 'manager', 'complete the change', worktreePath(path))
    assert.equal(admitted.tag, 0, JSON.stringify(admitted))
    const first = await wait(runtime)
    assert.equal(first.tag, 1, JSON.stringify(first))
    assert.match(first.fields[2], /controlled cleanup failure/)
    assert.ok(record().CandidateReady)
    assert.ok(record().PublishClaimed)
    assert.equal(record().Terminal.tag, 0)
    assert.equal(counters.ff, 1)
    journal.JournalSurface_dispose(handle)
    handle = undefined

    await open('cold')
    const persisted = record()
    assert.ok(persisted.PublishClaimed)
    assert.equal(persisted.Terminal.tag, 0)
    const before = { ...counters }
    cleanupFails = false
    const reopened = engine()
    recover(reopened, persisted)
    const second = await wait(reopened)
    assert.equal(second.tag, 5, JSON.stringify(second))
    assert.equal(counters.remove, before.remove + 1)
    assert.equal(counters.terminate, before.terminate + 1)
    for (const key of ['create', 'activate', 'prepare', 'signal', 'continue', 'ff']) assert.equal(counters[key], before[key], `${key} must not repeat after durable publication`)
  } finally {
    if (handle) journal.JournalSurface_dispose(handle)
    rmSync(root, { recursive: true, force: true })
  }
})
