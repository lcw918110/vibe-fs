import assert from 'node:assert/strict'
import test from 'node:test'
import { PendingRunStore } from 'wanxiangli/handle-lifecycle'
import * as join from '../../../dist/Execution/Delegation/Fork/JoinSurface.js'

test('WHAT[delegation-025] completion claimed with exact causal token and rejects duplicate claim', () => {
  const store = new PendingRunStore()
  const token = { id: 'run-token-1' }
  const runPayload = {
    agentId: 'engineer-1',
    token,
    role: 'Engineer',
    authorityRoot: 'root-1'
  }

  // 1. In-flight registration
  const reg = store.register('engineer-1', token, 'child-1', 'Engineer', runPayload)
  assert.equal(reg.registered, true)
  assert.equal(store.containsAgent('engineer-1'), true)
  assert.equal(store.count, 1)

  // 2. Token mismatch rejected
  const wrongTokenVerdict = store.claim('engineer-1', { id: 'wrong-token' })
  assert.equal(wrongTokenVerdict.claimed, false)
  assert.equal(wrongTokenVerdict.reason, 'TokenMismatch')

  // 3. Exact causal token claims successfully
  const claimVerdict = store.claim('engineer-1', token)
  assert.equal(claimVerdict.claimed, true)
  assert.equal(claimVerdict.payload, runPayload)

  // Invariant: allPayloads keeps claimed runs for reentrancy guard until physical removal
  assert.equal(store.allPayloads().length, 1)
  assert.equal(store.activePayloads().length, 0)
  assert.equal(store.containsAgent('engineer-1'), true)

  // 4. Duplicate claim rejected (AlreadyFinished)
  const dupClaim = store.claim('engineer-1', token)
  assert.equal(dupClaim.claimed, false)
  assert.equal(dupClaim.reason, 'AlreadyFinished')

  // 5. Final committed outcome physically removes
  assert.equal(store.removeClaimed('engineer-1', token), true)
  assert.equal(store.containsAgent('engineer-1'), false)
  assert.equal(store.count, 0)
  assert.equal(store.allPayloads().length, 0)
})

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

  // Settle parent cancellation on engineer-1
  const settleRes = store.settleParentCancelled('engineer-1', token1)
  assert.equal(settleRes.claimed, true)
  assert.equal(settleRes.subscription, sub1)
  assert.equal(store.count, 1)
  assert.equal(store.containsAgent('engineer-1'), false)
  assert.equal(store.containsAgent('devops'), true)

  // Second settle on cancelled agent is NotFound
  const secondSettle = store.settleParentCancelled('engineer-1', token1)
  assert.equal(secondSettle.claimed, false)
  assert.equal(secondSettle.reason, 'NotFound')

  // drainAll drains remaining devops run on detach/teardown
  const drained = store.drainAll()
  assert.equal(drained.length, 1)
  assert.equal(drained[0].agentId, 'devops')
  assert.equal(store.count, 0)
  assert.equal(store.allPayloads().length, 0)
})

test('WHAT[delegation-026] HostForkRuntime integrates PendingRunStore with initial zero counts and probe observation', async () => {
  const probe = join.createJoinProbe()
  const counts = join.joinProbeCounts(probe)

  // Runtime initial count reflects PendingRunStore.count = 0
  assert.equal(counts.pendingRuns, 0)
  assert.equal(counts.pendingCompletions, 0)
  assert.equal(counts.pendingPtys, 0)

  // Pulse wake and cancel on probe runtime are clean
  join.joinProbePulseWake(probe)
  join.joinProbeCancel(probe)

  const countsAfter = join.joinProbeCounts(probe)
  assert.equal(countsAfter.pendingRuns, 0)
})
