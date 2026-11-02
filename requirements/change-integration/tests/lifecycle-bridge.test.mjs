import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { GitWorktrees } from 'wanxiangli/git'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import * as change from '../../../dist/Change/Surface.js'

const ok = value => ({ ok: true, value })
const firstVerdict = result => {
  assert.equal(result.kind, 'ResultsAvailable', 'publication must return a result batch')
  return result.verdicts[0]
}

// This exercises the production Runtime and Journal adapter. The Relay is a
// controlled participant, so this is not a Host session/retirement scenario.
test('actual Change Runtime persists publication and cold recovery only retries resource cleanup', { timeout: 15000 }, async () => {
  const root = mkdtempSync(join(tmpdir(), 'change-journal-bridge-'))
  const id = 'journal-bridge'
  const path = join(root, 'manager-worktree')
  const counters = { create: 0, activate: 0, prepare: 0, signal: 0, continue: 0, ff: 0, remove: 0, terminate: 0 }
  let handle
  let cleanupFails = true
  let landed = false
  const record = () => change.lifecycleJob(handle, id)
  const open = async suffix => {
    const result = await journal.JournalSurface_bootWithWriterId(root, `writer-${suffix}`, `runtime-${suffix}`, process.pid, new Date().toISOString())
    assert.equal(result.ok, true, result.error)
    handle = result.journal
  }
  const resources = new GitWorktrees(root, async command => {
    if (command.args[0] === 'worktree' && command.args[1] === 'add') counters.create += 1
    if (command.args[0] === 'worktree' && command.args[1] === 'remove') {
      counters.remove += 1
      if (cleanupFails) return { code: 1, stdout: '', stderr: 'controlled cleanup failure' }
    }
    return { code: 0, stdout: '', stderr: '' }
  })
  const git = {
    isDirty: async () => ok([]), freezeTarget: async () => ok('refs/heads/main'),
    rebase: async () => ok(undefined), ffMerge: async (_path, _target, expected, candidate) => {
      const pending = record()
      assert.ok(pending.claim, 'claim must already be durable before FF')
      assert.equal(pending.terminal, null)
      assert.equal(expected, 'base-head')
      assert.equal(candidate, 'candidate-head')
      counters.ff += 1
      landed = true
      return ok('candidate-head')
    }, conflictedFiles: async () => ok([]), hasRebaseHead: async () => false,
    readHead: async () => ok('candidate-head'), targetHead: async () => ok(landed ? 'candidate-head' : 'base-head'),
  }
  const relay = {
    createSession: async () => ok('manager-bridge'),
    activate: async () => { counters.activate += 1; assert.ok(record()); return ok(undefined) },
    signal: async () => { counters.signal += 1; return ok({ kind: 'Candidate' }) },
    continue: async () => { counters.continue += 1; return ok('review-rebased-candidate') },
    prepare: async () => { counters.prepare += 1; return ok('candidate-head') },
    terminate: async () => { counters.terminate += 1; assert.equal(record().terminal.kind, 'Published') },
  }
  const engine = () => change.createLifecycle(handle, { git, relay, root, target: 'refs/heads/main', worktrees: resources })
  const wait = async runtime => firstVerdict(await change.collectLifecycle(runtime))
  try {
    await open('first')
    const runtime = engine()
    const admitted = await change.startLifecycle(runtime, id, 'manager', 'complete the change', path)
    assert.equal(admitted.ok, true, JSON.stringify(admitted))
    const first = await wait(runtime)
    assert.equal(first.kind, 'PublishedPendingCleanup', JSON.stringify(first))
    assert.match(first.detail, /controlled cleanup failure/)
    assert.ok(record().candidate)
    assert.ok(record().claim)
    assert.equal(record().terminal.kind, 'Published')
    assert.equal(counters.ff, 1)
    journal.JournalSurface_dispose(handle)
    handle = undefined

    await open('cold')
    const persisted = record()
    assert.ok(persisted.claim)
    assert.equal(persisted.terminal.kind, 'Published')
    const before = { ...counters }
    cleanupFails = false
    const reopened = engine()
    change.recoverLifecycle(reopened, persisted)
    const second = await wait(reopened)
    assert.equal(second.kind, 'Empty', JSON.stringify(second))
    assert.equal(counters.remove, before.remove + 1)
    assert.equal(counters.terminate, before.terminate + 1)
    for (const key of ['create', 'activate', 'prepare', 'signal', 'continue', 'ff']) assert.equal(counters[key], before[key], `${key} must not repeat after durable publication`)
  } finally {
    if (handle) journal.JournalSurface_dispose(handle)
    rmSync(root, { recursive: true, force: true })
  }
})
