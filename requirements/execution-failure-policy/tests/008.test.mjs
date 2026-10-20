import assert from 'node:assert/strict'
import test from 'node:test'
import * as signals from '../../../dist/OpenCode/Host/HostSignalSurface.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import * as policy from '../../../dist/Execution/Failure/Surface.js'
import * as provider from '../../../dist/Participant/Provider/Attempt/FailureSurface.js'
import { input, provider as recoveryFacts } from './support/policy-input.mjs'
const sessionError = (error) => ({ type: 'session.error', properties: { sessionID: 'session-1', error } })

test('WHAT[execution-failure-policy-008] Host and persistence classification ignore contradictory diagnostic wording', () => {
  const baseline = signals.tryDecode(sessionError({ name: 'TimeoutError', message: 'permission denied forever' }))
  const rewritten = signals.tryDecode(sessionError({ name: 'TimeoutError', message: 'please retry' }))
  assert.equal(baseline.failure, 'ProviderTransient')
  assert.equal(rewritten.failure, baseline.failure)
  assert.notEqual(rewritten.diagnostic, baseline.diagnostic)
  for (const diagnostic of ['definitely succeeded', 'definitely failed', 'retry me']) {
    assert.equal(journal.JournalSurface_mapAppendFailure({ kind: 'WriteUnknown', diagnostic }).commitment, 'Unknown')
    assert.equal(provider.classify({
      providerRun: 'run-20', requestKind: 'WorkMain', status: 'Transient', firstTokenObserved: false, diagnostic,
    }).failure, 'ProviderTransient')
  }
})

test('WHAT[execution-failure-policy-008] JS adapter ignores extraneous temporal decoration while consuming actual retry facts', () => {
  const baseline = policy.decide(input({ failure: 'ProviderTransient' }))
  assert.deepEqual(policy.decide(input({
    failure: 'ProviderTransient', diagnostic: 'timeout, cancelled',
    elapsedMilliseconds: Number.MAX_SAFE_INTEGER, retryCount: Number.MAX_SAFE_INTEGER,
  })), baseline)
  assert.deepEqual(policy.decide(input({ failure: 'ProviderTransient' })), baseline)
  const changed = policy.decide(input({ failure: 'ProviderTransient', provider: { ...recoveryFacts, retryBudget: 'Exhausted' } }))
  assert.equal(changed.resolution, 'TerminalizeProviderStarted')
  assert.notDeepEqual(changed, baseline)
})

test.todo('WHAT[execution-failure-policy-008] GAP-120 actual policy and interpreter dependency boundary excludes clock polling or sleep authority; JS ignored fields alone do not prove this')
