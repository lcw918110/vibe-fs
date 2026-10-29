import assert from 'node:assert/strict'
import test from 'node:test'
import { longWorkContinuationPath, longWorkContinuationPathForReason } from '../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js'

test('WHAT[provider-attempt-recovery-024] long-running roles continue their work when retry gives up on a transient failure', () => {
  assert.equal(longWorkContinuationPath('manager', 'ProviderTransient'), 'work/manager-continue')
  assert.equal(longWorkContinuationPath('plan', 'ProviderTransient'), 'work/plan-continue')
  assert.equal(longWorkContinuationPath('orchestrator', 'ProviderTransient'), 'work/orchestrator-continue')
})

test('WHAT[provider-attempt-recovery-024] short roles and non-transient failures keep the terminal stop', () => {
  for (const role of ['devops', 'engineer', 'advisor']) {
    assert.equal(longWorkContinuationPath(role, 'ProviderTransient'), '', role)
  }
  assert.equal(longWorkContinuationPath('manager', 'ProviderPermanent'), '')
  assert.equal(longWorkContinuationPath('manager', 'UserCancelled'), '')
})

test('WHAT[provider-attempt-recovery-024] a context-window overflow is not a gateway transient and earns no continuation', () => {
  const overflow = 'invalid-argument: Failed to start sampling: [input_too_large] The prompt is too long for this model\'s context window (716458 tokens > 500000 tokens)'
  assert.equal(longWorkContinuationPathForReason('manager', 'ProviderTransient', overflow), '')
  assert.equal(longWorkContinuationPathForReason('manager', 'ProviderTransient', '502 Bad Gateway'), 'work/manager-continue')
})
