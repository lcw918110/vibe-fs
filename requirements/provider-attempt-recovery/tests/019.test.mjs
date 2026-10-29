import assert from 'node:assert/strict'
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { tmpdir } from 'node:os'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import { scanRetryOwnership } from '../../../scripts/checks/retry-owner.mjs'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '../../..')

test('WHAT[provider-attempt-recovery-019] current retry ownership heuristics find no known forbidden source pattern', () => {
  assert.deepEqual(scanRetryOwnership(ROOT), [])
})

const fixture = (mutationPath, mutation) => {
  const root = mkdtempSync(join(tmpdir(), 'retry-owner-'))
  const source = join(root, 'src/Wanxiangshu')
  const write = (path, text) => {
    const target = join(source, path)
    mkdirSync(dirname(target), { recursive: true })
    writeFileSync(target, text)
  }

  write('Execution/Failure/Policy.fs', 'module ExecutionFailurePolicy =\n    let decide input = input\n')
  write('Execution/Failure/Model.fs', 'type ProviderRecoveryAuthorization private (value: string) = class end\n')
  write(mutationPath, mutation)
  return { root, close: () => rmSync(root, { recursive: true, force: true }) }
}

test('WHAT[provider-attempt-recovery-019] rejects nested physical retry owner', () => {
  const fx = fixture(
    'Interaction/Dispatch/NestedRetry.fs',
    'let resend port =\n    for attempt in [ 1; 2 ] do\n        port.SendPrompt(sessionId, text, options)\n',
  )
  try {
    assert.match(scanRetryOwnership(fx.root).join('\n'), /nested under a local retry loop/)
  } finally {
    fx.close()
  }
})

test('WHAT[provider-attempt-recovery-019] rejects retry classification from diagnostic text', () => {
  const fx = fixture(
    'Interaction/Dispatch/StringRetry.fs',
    'let retry error =\n    if error.Contains("timeout") then fallback ()\n',
  )
  try {
    assert.match(scanRetryOwnership(fx.root).join('\n'), /must not parse text/)
  } finally {
    fx.close()
  }
})

const owner = await import('../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js')
const { current, input, run } = await import('./support/retry.mjs')

test('WHAT[provider-attempt-recovery-019] real retry engine admits only typed provider failures and passes the same exact licence to redispatch', async () => {
  for (const failure of ['ProviderTransient', 'ProviderPermanent']) {
    const attempt = run(failure, current())
    assert.equal(await attempt.completed, 'Dispatched')
    assert.deepEqual(attempt.events.map(event => event.action), ['admit', 'redispatch'])
    const licence = attempt.events[0].authorization
    assert.deepEqual(attempt.events[1].authorization, licence)
    assert.equal(licence.logicalRun, 'logical-retry')
    assert.equal(licence.providerRun, 'provider-failed')
    assert.equal(licence.requestKind, 'work-main')
    assert.ok(licence.decisionId.length > 0)
  }
  for (const failure of [
    'LocalInvariant', 'ProtocolRejection', 'AuthorizationDenied', 'UserCancelled',
    'Superseded', 'CapacityQueueFull', 'AcceptanceUnknown', 'StreamInterruptedAfterFirstToken',
    ...['NotCommitted', 'Committed', 'Unknown'].map(commitment => ({ kind: 'PersistenceFailure', commitment })),
  ]) {
    const attempt = run(failure, current())
    const verdict = await attempt.completed
    assert.deepEqual(attempt.events, [], JSON.stringify(failure))
    const unknownAcceptance = failure === 'AcceptanceUnknown'
      || (failure && failure.kind === 'PersistenceFailure' && failure.commitment === 'Unknown')
    if (unknownAcceptance) {
      assert.notEqual(verdict, 'Superseded', JSON.stringify(failure))
      assert.notEqual(verdict, 'Dispatched', JSON.stringify(failure))
      assert.equal(verdict, 'Terminal', JSON.stringify(failure))
      assert.notEqual(owner.delegateRetryObservation(verdict), 'Ok')
    }
  }
})

test('WHAT[provider-attempt-recovery-019] real retry engine waits for admission and never redispatches a refused exhausted or superseded episode', async () => {
  for (const outcome of ['RetryAuthorized', 'RetryExhausted', 'EpisodeSuperseded', 'NoActiveRun', 'Rejected']) {
    let resolveAdmission
    let notifyEntered
    const admission = new Promise(resolve => { resolveAdmission = resolve })
    const entered = new Promise(resolve => { notifyEntered = resolve })
    const sends = []
    const completed = owner.retryAttempt(input(), current(), async () => { notifyEntered(); return admission }, async licence => { sends.push(licence) })
    await entered
    assert.deepEqual(sends, [])
    resolveAdmission(outcome)
    const result = await completed
    assert.equal(sends.length, outcome === 'RetryAuthorized' ? 1 : 0, outcome)
    assert.equal(result, outcome === 'RetryAuthorized' ? 'Dispatched' : outcome === 'EpisodeSuperseded' ? 'Superseded' : 'Terminal')
  }
})

test('WHAT[provider-attempt-recovery-019] retry licence changes with the exact provider run and request kind', async () => {
  const licences = []
  for (const [providerRun, requestKind] of [['first', 'WorkMain'], ['second', 'WorkMain'], ['second', 'InteractionRepair']]) {
    const facts = input('ProviderTransient', requestKind)
    facts.provider.providerRun = providerRun
    assert.equal(await owner.retryAttempt(facts, current(), async licence => { licences.push(licence); return 'RetryAuthorized' }, async () => {}), 'Dispatched')
  }
  assert.equal(new Set(licences.map(licence => licence.decisionId)).size, 3)
  assert.deepEqual(licences.map(licence => licence.providerRun), ['first', 'second', 'second'])
  assert.deepEqual(licences.map(licence => licence.requestKind), ['work-main', 'work-main', 'interaction-repair'])
})

test.todo('WHAT[provider-attempt-recovery-019] all production callers and wrong-attempt ledger inputs enforce the opaque licence without alternate retry owners (GAP-139)')
