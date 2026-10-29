import assert from 'node:assert/strict'
import test from 'node:test'
import { PendingRunStore } from 'wanxiangli/handle-lifecycle'

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

