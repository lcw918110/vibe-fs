import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as failurePolicy from '../../../dist/Execution/Failure/Surface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import * as failureOwner from '../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'

const FIRST_TARGET = { model: 'first/model', reasoning: 'high' }
const SECOND_TARGET = { model: 'second/model', reasoning: 'none' }

const admit = async (runtime, sessionId, physicalUserMessageId) => {
  const acquisition = await routing.acquireExecutionAdmission(
    runtime,
    sessionId,
    physicalUserMessageId,
    'engineer',
    'alice',
    '',
  )
  assert.equal(acquisition.kind, 'Acquired', `admission for ${physicalUserMessageId} must acquire`)
  const target = routing.executionAdmissionTarget(runtime, acquisition.lease)

  const settlement = routing.commitExecutionAdmission(runtime, acquisition.lease, {
    sessionId,
    physicalUserMessageId,
    role: 'engineer',
    participant: 'alice',
    target,
  })
  assert.ok(['Applied', 'AlreadyApplied'].includes(settlement.kind))
  return target
}

const release = (runtime, sessionId, physicalUserMessageId) => {
  const outcome = routing.releasePhysicalExecution(runtime, sessionId, physicalUserMessageId)
  assert.ok(['Applied', 'AlreadyApplied'].includes(outcome.kind), `release of ${physicalUserMessageId} must apply`)
}

const loadedTemplate = async () => {
  const failed = new Set()
  const providerOf = target => target.model.split('/')[0]
  const scheduler = (_role, running, previous) => {
    const available = target => !failed.has(providerOf(target)) && !running.some(active => active.model === target.model)
    if (previous && available(previous)) return previous
    return [FIRST_TARGET, SECOND_TARGET].find(available) ?? null
  }
  scheduler.markProviderFailed = provider => { failed.add(provider) }
  scheduler.hasTheoreticalCapacity = () => failed.size < 2
  const template = {
    clearFailedProviders: () => failed.clear(),
    providerCapacity: provider => failed.has(provider) ? 0 : 1,
  }
  return { template, runtime: routing.createRuntime(scheduler) }
}

const firstFailureKeptTarget = async () => {
  const { template, runtime } = await loadedTemplate()
  const holders = []

  for (let index = 0; index < 1; index += 1) {
    holders.push(await admit(runtime, `holder-${index}`, `msg-holder-${index}`))
    assert.deepEqual(holders[index], FIRST_TARGET)
  }

  const failedTarget = await admit(runtime, 'ses-retry', 'msg-first')
  assert.deepEqual(failedTarget, SECOND_TARGET, 'the occupied first candidate pushes the attempt to the second')

  routing.endProviderStep(runtime, 'ses-retry', 'msg-first', 'run-first')
  release(runtime, 'ses-retry', 'msg-first')

  for (let index = 0; index < 1; index += 1) {
    release(runtime, `holder-${index}`, `msg-holder-${index}`)
  }

  return { template, runtime, failedTarget }
}

test('WHAT[provider-attempt-recovery-021] recoveryStep pure policy: first failure retries same provider, verbatim retry failure blacklists', () => {
  assert.equal(typeof failurePolicy.recoveryStep, 'function')
  assert.equal(failurePolicy.recoveryStep(false), 'RetrySameProvider')
  assert.equal(failurePolicy.recoveryStep(true), 'BlacklistProviderThenRetry')
})

const admittedWithPhysical = (physicalMessageId) => ({
  SubscribeTerminal: () => ({ Dispose: () => {} }),
  SendPrompt: async () => dispatch.admittedWithPhysicalMessage(physicalMessageId),
})

test('WHAT[provider-attempt-recovery-021] explicit retain settlement preserves the exact failed target for one fresh admission', async () => {
  const { template, runtime, failedTarget } = await firstFailureKeptTarget()

  try {
    // The first target is free again: without a binding fresh admission takes the
    // pool's first candidate, so a retained second candidate proves the binding.
    const retained = routing.retainFailedTargetForRetry(runtime, 'ses-retry', 'run-first')
    assert.deepEqual(retained, failedTarget, 'the settlement resolves the exact failed target')
    assert.equal(
      routing.retainFailedTargetForRetry(runtime, 'ses-retry', 'run-first'),
      null,
      'the failed witness is single consumption',
    )

    const retryTarget = await admit(runtime, 'ses-retry', 'msg-retry')
    assert.deepEqual(retryTarget, failedTarget, 'the recovery retry returns to the original target')
    assert.equal(template.providerCapacity('second'), 1, 'retaining a target condemns nothing')
  } finally {
    template.clearFailedProviders()
  }
})

test('WHAT[provider-attempt-recovery-021] retry preference is consumed once and does not pin future admissions', async () => {
  const { template, runtime, failedTarget } = await firstFailureKeptTarget()

  try {
    routing.retainFailedTargetForRetry(runtime, 'ses-retry', 'run-first')
    const retryTarget = await admit(runtime, 'ses-retry', 'msg-retry')
    assert.deepEqual(retryTarget, failedTarget)

    release(runtime, 'ses-retry', 'msg-retry')
    const next = await admit(runtime, 'ses-retry', 'msg-next')
    assert.deepEqual(next, FIRST_TARGET, 'the one-use preference has been consumed')
    assert.equal(template.providerCapacity('second'), 1)
  } finally {
    template.clearFailedProviders()
  }
})

test('WHAT[provider-attempt-recovery-021] explicit condemnation consumes an exact witness once and rotates to a healthy target', async () => {
  const { template, runtime, failedTarget } = await firstFailureKeptTarget()

  try {
    routing.retainFailedTargetForRetry(runtime, 'ses-retry', 'run-first')
    assert.deepEqual(await admit(runtime, 'ses-retry', 'msg-retry'), failedTarget)

    // The test selects condemnation explicitly; workflow selection is separate.
    routing.endProviderStep(runtime, 'ses-retry', 'msg-retry', 'run-retry')
    const condemned = routing.condemnFailedTarget(runtime, 'run-retry')
    assert.deepEqual(condemned, failedTarget, 'the condemning witness is the failed same-provider retry target')
    assert.equal(template.providerCapacity('second'), 0)
    assert.equal(
      routing.condemnFailedTarget(runtime, 'run-retry'),
      null,
      'one witness condemns at most once',
    )

    const rotated = await admit(runtime, 'ses-retry', 'msg-after-condemn')
    assert.deepEqual(rotated, FIRST_TARGET, 'the dispatch after the condemnation rotates')
  } finally {
    template.clearFailedProviders()
  }
})

test('WHAT[provider-attempt-recovery-021] the_settlement_fact_is_the_durable_provider_retry_attempt_acceptance', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-retry-fact-'))

  const created = await journal.JournalSurface_bootWithWriterId(
    directory,
    'writer-retry-fact',
    'rt-retry-fact',
    1,
    '2026-01-01T00:00:00Z',
  )
  assert.equal(created.ok, true, created.ok ? '' : JSON.stringify(created.error))

  try {
    const handle = created.journal
    const accepted = await failureOwner.acceptHumanRoot(handle, 'ses_retry_fact', 'msg_root', 'engineer')
    assert.equal(accepted.ok, true, accepted.ok ? '' : accepted.error)


    // One physical message drives several provider steps: the fact binds the
    // continuation acceptance AND the exact run that established the request's
    // durable ProviderStarted. A run that did not establish it is a later step.
    assert.equal(
      failureOwner.wasSameProviderRetryAttempt(handle, 'ses_retry_fact', 'msg_root', 'run-any'),
      false,
      'an authority root is not a recovery dispatch',
    )
    assert.equal(failureOwner.wasSameProviderRetryAttempt(handle, 'ses_retry_fact', 'msg_absent', 'run-any'), false)

    const sent = await dispatch.sendContinuation(
      admittedWithPhysical('msg_retry'),
      handle,
      'ses_retry_fact',
      'continue after the confirmed provider failure',
      // IA-017 / dispatch-protocol [010]: the retry continuation carries the
      // durable active profile itself; its acceptance fact is bound to the
      // physical message and provider run, not to a re-derived profile.
      'ProviderRetryAttempt',
      dispatch.projectionObservation(handle, 'ses_retry_fact').activeLogicalRun,
      'Await',
    )
    assert.equal(sent.ok, true, sent.ok ? '' : sent.error)

    assert.equal(
      failureOwner.wasSameProviderRetryAttempt(handle, 'ses_retry_fact', 'msg_root', 'run-any'),
      false,
      'the root request stays an ordinary attempt',
    )

    const established = await failureOwner.establishProviderRun(handle, 'ses_retry_fact', 'msg_retry', 'pr-run-1')
    assert.equal(established.ok, true, 'the retry request establishes its durable provider run')

    assert.equal(
      failureOwner.wasSameProviderRetryAttempt(handle, 'ses_retry_fact', 'msg_retry', 'pr-run-1'),
      true,
      'the accepted retry continuation established by that run is the durable same-provider retry fact',
    )
    assert.equal(
      failureOwner.wasSameProviderRetryAttempt(handle, 'ses_retry_fact', 'msg_retry', 'pr-run-2'),
      false,
      'a later step of the retry episode is not the same-provider retry itself',
    )
  } finally {
    journal.JournalSurface_dispose(created.journal)
    rmSync(directory, { recursive: true, force: true })
  }
})

test.todo('WHAT[provider-attempt-recovery-021] actual ordinary and sync-delegate recovery combine exact durable same-provider retry evidence and target settlement after licensing and before send (GAP-139)')
