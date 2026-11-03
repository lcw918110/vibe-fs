import test from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createCompaction, refuseCompactionAppendOnce as refuseOnce } from '../../../dist/OpenCode/Host/PluginHooksSurface.js'
import { JournalSurface_boot, JournalSurface_dispose, JournalSurface_snapshot } from '../../../dist/Persistence/Journal/Surface.js'

const message = (id, completed = true, compaction = true) =>
  ({ id, completed, compaction })

test('OpenCode compaction owns per-session bypass and stops temporary overflow before product contribution', async () => {
  const owner = createCompaction(undefined)
  const calls = []
  const product = {
    OnCompacting: async sid => { calls.push(sid); return ['product context'] },
    AutoContinue: async () => true,
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
  await owner.ObserveSnapshot('one', [message('journal-less')])
})

test('OpenCode compaction waits for a completed first assistant and consumes its configuration observation once', async () => {
  const owner = createCompaction(undefined)
  owner.RecordSettingGap({ Path: ['compaction', 'auto'], Required: true, Clause: 'fixture', Reason: 'setting was not available' })
  await owner.ObserveSnapshot('one', [message('streaming', false)])
  await assert.rejects(owner.ObserveSnapshot('two', [message('finished')]), /could not be set to true/)
  await owner.ObserveSnapshot('one', [message('next')])
})

test('OpenCode compaction retries a known unattempted reanchor from the committed Prefix history', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'compaction-history-'))
  const opened = await JournalSurface_boot(directory, 'compaction-history', process.pid, new Date().toISOString())
  assert.equal(opened.ok, true)
  const handle = opened.journal
  const refusal = refuseOnce(handle)
  try {
    const owner = createCompaction(handle)
    const snapshot = [message('compaction-1')]
    await owner.ObserveSnapshot('one', snapshot)
    assert.equal(JournalSurface_snapshot(handle).sessionProjections.one?.prefixEpoch ?? null, null)
    await owner.ObserveSnapshot('one', snapshot)
    assert.equal(JournalSurface_snapshot(handle).sessionProjections.one.prefixEpoch.epochId, 1n)
    await owner.ObserveSnapshot('one', snapshot)
    assert.equal(refusal.attempts(), 2, 'committed history suppresses a third append')
  } finally {
    refusal.restore()
    JournalSurface_dispose(handle)
    rmSync(directory, { recursive: true, force: true })
  }
})
