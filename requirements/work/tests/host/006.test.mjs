import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as lifecycle from '../../../../dist/Mission/Lifecycle/Surface.js'

const surface = lifecycle.LifecycleSurface ?? lifecycle

test('WHAT[work-006] one review relays and another closes cleanup through the work entry', () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-review-006-'))
  const workId = 'manager-session'
  const review = (id, updates) =>
    surface.submitReview(root, workId, id, 'Manager', 'run-' + id, 'call-' + id, updates, [], '')
  try {
    const opened = review('r1', [{ key: 'goal', value: 'ship the protocol' }])
    assert.equal(opened.status, 'Committed', opened.reason)
    assert.equal(opened.kind, 'Reviewed')
    assert.equal(opened.milestone, '执行')
    const relayed = review('r2', [{ key: 'goal', value: null }])
    assert.equal(relayed.status, 'Committed', relayed.reason)
    assert.equal(relayed.kind, 'Relayed')
    const again = review('r2', [{ key: 'goal', value: null }])
    assert.equal(again.status, 'Replayed')
    assert.equal(again.kind, 'Relayed')
    const advanced = review('r3', [])
    assert.equal(advanced.status, 'Committed', advanced.reason)
    assert.equal(advanced.kind, 'Advanced')
    assert.equal(advanced.milestone, '收尾')
    const closed = review('r4', [])
    assert.equal(closed.status, 'Committed', closed.reason)
    assert.equal(closed.kind, 'Completed')
    assert.equal(closed.milestone, '已完成')
    const refused = review('r5', [])
    assert.equal(refused.status, 'Conflict')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
