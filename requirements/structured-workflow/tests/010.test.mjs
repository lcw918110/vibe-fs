import assert from 'node:assert/strict'
import test from 'node:test'
import * as reconcileSurface from '../../../dist/Composition/Turn/ReconcileSurface.js'

test('WHAT[structured-workflow-010] RECONCILE_SCHEDULER_BOUND_001: same-session burst coalesces and stale work is invalidated', async () => {
  const result = await reconcileSurface.schedulerBurstBoundScenario()

  assert.equal(result.kickCount, 3000)
  assert.equal(result.edgeCount, 3000)
  // 同一会话的突发唤醒合并为一个活跃读取；ClearSession 淘汰旧世代。
  assert.equal(result.snapshotReads, 1)
  assert.ok(result.snapshotReads <= 2, 'at most one extra read per edge batch')
  assert.equal(result.delivered, 0)
})

test('WHAT[structured-workflow-010] RECONCILE_SCHEDULER_BOUND_002: stop waits for the live pass and drains to zero observable work', async () => {
  const result = await reconcileSurface.schedulerBurstBoundScenario()

  assert.equal(result.stopWaited, true)
  assert.equal(result.delivered, 0)
})

test('WHAT[structured-workflow-010] RECONCILE_SCHEDULER_BOUND_003: kicks after stop do nothing', async () => {
  const result = await reconcileSurface.schedulerBurstBoundScenario()

  assert.equal(result.postStopRejected, true)
})
