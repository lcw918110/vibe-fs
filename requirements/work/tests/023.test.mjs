import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'

const surface = lifecycle.LifecycleSurface ?? lifecycle

const review = (root, workId, id, updates) =>
  surface.submitReview(root, workId, id, 'Plan', 'run-' + id, 'call-' + id, updates, null, '')

test('WHAT[work-023] opening, deliver, and stage probes refuse without a parse crash', () => {
  const opened = surface.encodeOpening('plan', 'work-1', '/root', 0n)
  assert.match(opened, /^REFUSE /)
  const probe = surface.readProbe(opened)
  assert.equal(probe.ok, false)
  assert.equal(probe.opened, false)
  assert.equal(probe.next, '')
  assert.doesNotMatch(String(probe.reason), /SyntaxError/)

  const reviewed = surface.reviewProbe(opened, 'inc', 'Promoted')
  assert.equal(reviewed.decision, 'Refuse')
  assert.match(reviewed.detail, /review is the only lifecycle entry/)
  assert.equal(reviewed.bytes, '')

  const delivered = surface.deliverProbe(opened, 'run-1', 'call-1', 10n, null)
  assert.equal(delivered.decision, 'Refuse')
  assert.match(delivered.detail, /review is the only entry/)
  assert.equal(delivered.bytes, '')

  const reopened = surface.reopenProbe(opened, 'inc')
  assert.equal(reopened.decision, 'Refuse')
  assert.match(reopened.detail, /operator message/)
  assert.equal(surface.finalPromotionProbe(opened).authorized, false)
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

test('WHAT[work-023] a refused probe is not parsed as a document', () => {
  const refused = surface.readProbe('REFUSE the first review opens the work')
  assert.equal(refused.ok, false)
  assert.equal(refused.stage, '')
  assert.equal(refused.sequence, '')
  const blank = surface.readProbe('')
  assert.equal(blank.ok, false)
  const missing = surface.readProbe(null)
  assert.equal(missing.ok, false)
})
