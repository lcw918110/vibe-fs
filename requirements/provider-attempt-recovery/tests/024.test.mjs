import assert from 'node:assert/strict'
import test from 'node:test'
import { confirmedFailureIsHardStop, longWorkContinuationPath, longWorkContinuationPathForReason } from '../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js'
import * as hostSignals from '../../../dist/OpenCode/Host/HostSignalSurface.js'

test('WHAT[provider-attempt-recovery-024] long-running roles continue their work when retry gives up on a transient failure', () => {
  assert.equal(longWorkContinuationPath('manager', 'ProviderTransient'), 'work/manager-continue')
  assert.equal(longWorkContinuationPath('plan', 'ProviderTransient'), 'work/plan-continue')
  assert.equal(longWorkContinuationPath('orchestrator', 'ProviderTransient'), 'work/orchestrator-continue')
})

test('WHAT[provider-attempt-recovery-024] short roles and non-transient failures keep the terminal stop', () => {
  for (const role of ['devops', 'engineer']) {
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

// Verbatim from 10022 (2026-10-30): OpenCode APIError, statusCode 403, isRetryable false.
const spendingLimit = 'personal-team-blocked:spending-limit: You have run out of credits or need a Grok subscription. Add credits at https://grok.com/?_s=usage or upgrade at https://grok.com/supergrok.'

const terminal = (error) => ({
  type: 'message.updated',
  properties: {
    info: {
      sessionID: 'ses-credit', id: 'run-credit', role: 'assistant', parentID: 'msg-credit',
      time: { created: 1, completed: 2 }, error,
    },
  },
})
const sessionError = (error) => ({ type: 'session.error', properties: { sessionID: 'ses-credit', error } })
const apiError = (message, statusCode) => ({ name: 'APIError', data: { message, statusCode, isRetryable: false } })

test('WHAT[provider-attempt-recovery-024] exhausted credits are a permanent provider failure at the host boundary', () => {
  assert.equal(hostSignals.tryDecodeExactProviderTerminal(terminal(apiError(spendingLimit, 403))).failure, 'ProviderPermanent')
  assert.equal(hostSignals.tryDecode(sessionError(apiError(spendingLimit, 403))).failure, 'ProviderPermanent')
  assert.equal(
    hostSignals.tryDecode(sessionError(apiError('Error code: 429 - insufficient_quota: You exceeded your current quota', 429))).failure,
    'ProviderPermanent')
})

test('WHAT[provider-attempt-recovery-024] real transients stay transient', () => {
  for (const message of ['502 Bad Gateway', 'fetch failed: ECONNRESET', 'Rate limit reached, retry in 2s']) {
    assert.equal(hostSignals.tryDecodeExactProviderTerminal(terminal(apiError(message, 502))).failure, 'ProviderTransient')
    assert.equal(hostSignals.tryDecode(sessionError(apiError(message, 502))).failure, 'ProviderTransient')
  }
})

test('WHAT[provider-attempt-recovery-024] exhausted credits earn no long-work continuation nudge', () => {
  for (const role of ['manager', 'plan', 'orchestrator']) {
    assert.equal(longWorkContinuationPathForReason(role, 'ProviderTransient', spendingLimit), '')
    assert.equal(longWorkContinuationPathForReason(role, 'ProviderPermanent', spendingLimit), '')
  }
  assert.equal(longWorkContinuationPathForReason('manager', 'ProviderTransient', '502 Bad Gateway'), 'work/manager-continue')
})

test('WHAT[provider-attempt-recovery-024] a permanent or credit-exhausted confirmed failure stops the turn without retry', () => {
  assert.equal(confirmedFailureIsHardStop('ProviderPermanent', 'APIError'), true)
  assert.equal(confirmedFailureIsHardStop('ProviderTransient', spendingLimit), true)
  assert.equal(confirmedFailureIsHardStop('ProviderTransient', '502 Bad Gateway'), false)
  assert.equal(confirmedFailureIsHardStop('ProviderTransient', 'APIError'), false)
})
