import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { Work } from 'wanxiangli/work'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'

const surface = lifecycle.LifecycleSurface ?? lifecycle

const review = (root, workId, id, updates) =>
  surface.submitReview(root, workId, id, 'Plan', 'run-' + id, 'call-' + id, updates, null, '')

test('WHAT[work-023] Open, Deliver, and Advance write nothing', () => {
  const work = new Work('work-23')
  for (const command of [
    { type: 'Open', root: '/root', sequence: ['S1', 'S2', 'S3'], ownerId: 'work-23', openingCursor: 0 },
    { type: 'Deliver', incumbencyId: 'inc', providerRunId: 'run-1', toolCallId: 'call-1' },
    { type: 'Advance', incumbencyId: 'inc', expected: 'a', next: 'b' },
  ]) {
    const before = work.records()
    assert.throws(() => work.execute(command.type, command), /Unknown work command/)
    assert.deepEqual(work.records(), before)
  }
  assert.throws(() => new Work('legacy', ['{"type":"Open","sequence":["S1"]}\n']))
})

test('WHAT[work-023] review records, relays, and advances by the ledger', () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-work-023-'))
  try {
    const recorded = review(root, 'work-1', 'r1', [{ key: 'tradeoff', value: 'compare stores' }])
    assert.equal(recorded.status, 'Committed', recorded.reason)
    assert.equal(recorded.kind, 'Reviewed')
    assert.equal(recorded.milestone, '事实与取舍')

    const kept = review(root, 'work-1', 'r2', [])
    assert.equal(kept.kind, 'Reviewed', 'an empty array does not clear an existing key')
    assert.equal(kept.milestone, '事实与取舍')

    const relayed = review(root, 'work-1', 'r3', [{ key: 'tradeoff', value: null }])
    assert.equal(relayed.status, 'Committed', relayed.reason)
    assert.equal(relayed.kind, 'Relayed')

    const advanced = review(root, 'work-1', 'r4', [])
    assert.equal(advanced.status, 'Committed', advanced.reason)
    assert.equal(advanced.kind, 'Advanced')
    assert.equal(advanced.milestone, '理解与路径')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[work-023] a legacy line is not a work record', () => {
  assert.throws(() => new Work('refused', ['REFUSE the first review opens the work']))
  assert.throws(() => new Work('blank', ['']))
  assert.throws(() => new Work('missing', [null]))
})
