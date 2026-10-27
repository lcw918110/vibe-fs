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

const admittedWithPhysical = (physicalMessageId) => ({
  SubscribeTerminal: () => ({ Dispose: () => {} }),
  SendPrompt: async () => dispatch.admittedWithPhysicalMessage(physicalMessageId),
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
