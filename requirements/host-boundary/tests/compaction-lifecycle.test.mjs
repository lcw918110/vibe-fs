import test from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { OpenCodeCompaction } from '../../../dist/wanxiangtong/opencode/compaction/OpenCodeCompaction.js'
import { CompactionContribution, ContinueVerdict } from '../../../dist/Host/Ports/Compaction.js'
import { SessionMessage } from '../../../dist/Foundation/HostSessionSnapshot.js'
import { SessionIdModule_create as sessionId, EventIdModule_create as eventId } from '../../../dist/Foundation/Identity.js'
import { ofArray } from '../../../dist/fable_modules/fable-library-js.5.13.0/List.js'
import { JournalHandle__get_Journal, JournalSurface_boot, JournalSurface_dispose, JournalSurface_snapshot } from '../../../dist/Persistence/Journal/Surface.js'
import { CommitResult$1, JournalUnavailable } from '../../../dist/Persistence/Journal/Outcome.js'

const message = (id, completed = true, compaction = true) =>
  new SessionMessage(id, 'assistant', undefined, undefined, undefined, undefined, undefined, undefined, completed, compaction, undefined, [], [], [])

test('OpenCode compaction owns per-session bypass and stops temporary overflow before product contribution', async () => {
  const owner = new OpenCodeCompaction(undefined)
  const calls = []
  const product = {
    OnCompacting: async sid => { calls.push(sid); return new CompactionContribution(1, [ofArray(['product context'])]) },
    AutoContinue: async () => ContinueVerdict.Allow,
  }
  const isTemporary = id => id === 'temporary'
  const aborted = []
  const client = { session: { abort: async input => { aborted.push(input.path.id) } } }
  const first = { context: ['host context'] }
  await owner.OnCompacting(product, isTemporary, client, { sessionID: 'one' }, first)
  await owner.OnCompacting(product, isTemporary, client, { sessionID: 'two' }, null)
  assert.deepEqual(first.context, ['host context', 'product context'])
  assert.equal(owner.IsCompacting('one'), true)
  assert.equal(owner.TryConsumeHistoryBypass({}, { messages: [{ info: { sessionID: 'one' } }] }), true)
  assert.equal(owner.TryConsumeHistoryBypass({ sessionID: 'one' }, {}), false)
  assert.equal(owner.IsCompacting('two'), true)
  owner.ForgetSession('one')
  assert.equal(owner.IsCompacting('two'), true)
  owner.ForgetSession('two')
  assert.equal(owner.IsCompacting('two'), false)
  const output = { context: ['original'] }
  await owner.OnCompacting(product, isTemporary, client, { sessionID: 'temporary' }, output)
  const continuation = {}
  await owner.OnAutoContinue(product, isTemporary, { sessionID: 'temporary' }, continuation)
  assert.deepEqual(output, { context: ['original'] })
  assert.deepEqual(continuation, { enabled: false })
  assert.deepEqual(aborted, ['temporary'])
  assert.equal(calls.length, 2)
  assert.equal(owner.TemporaryOverflowed('temporary'), true)
  assert.equal(owner.IsCompacting('temporary'), false)
  owner.FinishTemporary('temporary')
  assert.equal(owner.TemporaryOverflowed('temporary'), false)
  await owner.OnAutoContinue(product, isTemporary, { sessionID: 'one' }, continuation)
  assert.equal(continuation.enabled, true)
  await owner.ObserveSnapshot(sessionId('one'), ofArray([message('journal-less')]))
})

test('OpenCode compaction waits for a completed first assistant and consumes its configuration observation once', async () => {
  const owner = new OpenCodeCompaction(undefined)
  owner.RecordSettingGap({ Path: ofArray(['compaction', 'auto']), Required: true, Clause: 'fixture', Reason: 'setting was not available' })
  await owner.ObserveSnapshot(sessionId('one'), ofArray([message('streaming', false)]))
  await assert.rejects(owner.ObserveSnapshot(sessionId('two'), ofArray([message('finished')])), /could not be set to true/)
  await owner.ObserveSnapshot(sessionId('one'), ofArray([message('next')]))
})

test('OpenCode compaction retries a known unattempted reanchor from the committed Prefix history', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'compaction-history-'))
  const opened = await JournalSurface_boot(directory, 'compaction-history', process.pid, new Date().toISOString())
  assert.equal(opened.ok, true)
  const handle = opened.journal
  const journal = JournalHandle__get_Journal(handle)
  const original = journal.writer.Append
  let attempts = 0
  journal.writer.Append = function (...args) {
    attempts += 1
    if (attempts === 1) return Promise.resolve(new CommitResult$1(2, [eventId('not-attempted'), new JournalUnavailable(1, ['fixture refusal'])]))
    return original.apply(this, args)
  }
  try {
    const owner = new OpenCodeCompaction(journal)
    const snapshot = ofArray([message('compaction-1')])
    await owner.ObserveSnapshot(sessionId('one'), snapshot)
    assert.equal(JournalSurface_snapshot(handle).sessionProjections.one?.prefixEpoch ?? null, null)
    await owner.ObserveSnapshot(sessionId('one'), snapshot)
    assert.equal(JournalSurface_snapshot(handle).sessionProjections.one.prefixEpoch.epochId, 1n)
    await owner.ObserveSnapshot(sessionId('one'), snapshot)
    assert.equal(attempts, 2, 'committed history suppresses a third append')
  } finally {
    journal.writer.Append = original
    JournalSurface_dispose(handle)
    rmSync(directory, { recursive: true, force: true })
  }
})
