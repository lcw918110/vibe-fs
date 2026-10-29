import assert from 'node:assert/strict'
import test from 'node:test'
import { PendingRunStore } from 'wanxiangli/handle-lifecycle'

test('WHAT[delegation-031] parent cancellation cascades atomic settlement and clears pending runs without orphan handles', () => {
  const store = new PendingRunStore()
  const token1 = { id: 'tok-eng' }
  const token2 = { id: 'tok-devops' }
  let sub1Disposed = false
  const sub1 = { dispose: () => { sub1Disposed = true } }
  const run1 = { agentId: 'engineer-1', token: token1 }
  const run2 = { agentId: 'devops', token: token2 }
  store.register('engineer-1', token1, 'child-1', 'Engineer', run1, sub1)
  store.register('devops', token2, 'child-2', 'DevOps', run2)
  assert.equal(store.count, 2)
  assert.equal(store.allPayloads().length, 2)
  const settleRes = store.settleParentCancelled('engineer-1', token1)
  assert.equal(settleRes.claimed, true)
  assert.equal(settleRes.subscription, sub1)
  assert.equal(store.count, 1)
  assert.equal(store.containsAgent('engineer-1'), false)
  assert.equal(store.containsAgent('devops'), true)
  const secondSettle = store.settleParentCancelled('engineer-1', token1)
  assert.equal(secondSettle.claimed, false)
  assert.equal(secondSettle.reason, 'NotFound')
  const drained = store.drainAll()
  assert.equal(drained.length, 1)
  assert.equal(drained[0].agentId, 'devops')
  assert.equal(store.count, 0)
  assert.equal(store.allPayloads().length, 0)
})
