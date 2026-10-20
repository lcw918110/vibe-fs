import test from 'node:test'
import assert from 'node:assert/strict'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import * as recovery from '../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import { withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

test('WHAT[managed-chat-execution-003] concurrent exact chat admissions and their replay project one committed target into every Host request', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const input = { sessionID: 'ses-concurrent-admission', messageID: 'msg-concurrent-admission', agent: 'engineer' }
    const outputs = Array.from({ length: 2 }, () => ({
      message: {
        id: input.messageID, sessionID: input.sessionID, role: 'user', agent: input.agent,
        model: { providerID: 'host', modelID: 'placeholder' },
      },
      parts: [],
    }))
    const admissions = await Promise.allSettled(outputs.map((output) => hooks['chat.message'](input, output)))
    assert.deepEqual(admissions.map((admission) => admission.status), ['fulfilled', 'fulfilled'])
    for (const output of outputs) {
      assert.deepEqual({ ...output.message.model }, { providerID: 'provider', modelID: 'engineer-model', variant: 'none' })
    }
    const capacity = routing.sharedCapacitySnapshot()
    assert.equal(capacity.executions.filter((execution) => execution.sessionId === input.sessionID).length, 1)
    assert.equal(capacity.tokens.filter((token) => token.owner.sessionId === input.sessionID).length, 1)
    const replay = { message: { id: input.messageID, sessionID: input.sessionID, role: 'user', agent: input.agent,
      model: { providerID: 'host', modelID: 'replay-placeholder' } }, parts: [] }
    await hooks['chat.message'](input, replay)
    assert.deepEqual({ ...replay.message.model }, { providerID: 'provider', modelID: 'engineer-model', variant: 'none' })
    const replayCapacity = routing.sharedCapacitySnapshot()
    assert.equal(replayCapacity.executions.filter((execution) => execution.sessionId === input.sessionID).length, 1)
    assert.equal(replayCapacity.tokens.filter((token) => token.owner.sessionId === input.sessionID).length, 1)
    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, input.sessionID, input.messageID), {
      phase: 'Accepted', disposition: null,
    })
    for (const agent of ['manager', 42]) {
      const conflict = { message: { id: input.messageID, sessionID: input.sessionID, role: 'user', agent,
        model: { providerID: 'host', modelID: 'conflict-placeholder' } }, parts: [] }
      await assert.rejects(hooks['chat.message']({ ...input, agent }, conflict),
        agent === 'manager' ? /IntentRejected.*AcceptedParticipantConflict/ : /IntentRejected.*MalformedIdentityCarrier/)
      assert.deepEqual(conflict.message.model, { providerID: 'host', modelID: 'conflict-placeholder' })
      assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, input.sessionID, input.messageID), {
        phase: 'Accepted', disposition: null,
      })
    }
    const foreignCarrier = { message: { id: input.messageID, sessionID: 'ses-foreign-carrier', role: 'user', agent: input.agent,
      model: { providerID: 'host', modelID: 'foreign-placeholder' } }, parts: [] }
    await assert.rejects(hooks['chat.message'](input, foreignCarrier), /IntentRejected.*MalformedIdentityCarrier/)
    assert.deepEqual(foreignCarrier.message.model, { providerID: 'host', modelID: 'foreign-placeholder' })
    const fresh = { message: { id: 'msg-fresh-human', sessionID: input.sessionID, role: 'user', agent: input.agent, model: {} }, parts: [] }
    await hooks['chat.message']({ ...input, messageID: fresh.message.id }, fresh)
    assert.deepEqual({ ...fresh.message.model }, { providerID: 'provider', modelID: 'engineer-model', variant: 'none' })
    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, input.sessionID, input.messageID), {
      phase: 'Accepted', disposition: null,
    })
    assert.deepEqual(routing.sharedCapacitySnapshot(), replayCapacity,
      'accepting additional material cannot revoke the lease before the Host selects that material')
    runtime.pushHostMessage(input.sessionID, {
      info: {
        id: 'assistant-fresh-human', sessionID: input.sessionID, parentID: fresh.message.id,
        role: 'assistant', agent: input.agent, providerID: 'provider', modelID: 'engineer-model',
        time: { created: 2 },
      },
      parts: [],
    })
    await hooks['experimental.chat.messages.transform']({}, {
      messages: [{ info: fresh.message, parts: [] }],
    })
    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, input.sessionID, input.messageID), {
      phase: 'Terminal', disposition: 'Cancelled',
    })
    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, input.sessionID, fresh.message.id), {
      phase: 'ProviderStarted', disposition: null,
    })
    const currentCapacity = routing.sharedCapacitySnapshot()
    const currentAuthority = dispatch.projectionObservation(runtime.journal, input.sessionID).activeLogicalRun
    const aborted = [...runtime.abortedIds]
    const lateRoot = { message: { id: input.messageID, sessionID: input.sessionID, role: 'user', agent: input.agent,
      model: { providerID: 'host', modelID: 'late-root-placeholder' } }, parts: [] }
    await assert.rejects(hooks['chat.message'](input, lateRoot), /AlreadyTerminal/)
    assert.deepEqual(lateRoot.message.model, { providerID: 'host', modelID: 'late-root-placeholder' })
    assert.deepEqual(routing.sharedCapacitySnapshot(), currentCapacity)
    assert.deepEqual(dispatch.projectionObservation(runtime.journal, input.sessionID).activeLogicalRun, currentAuthority)
    assert.deepEqual(runtime.abortedIds, aborted)
    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, input.sessionID, input.messageID), {
      phase: 'Terminal', disposition: 'Cancelled',
    })
    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, input.sessionID, fresh.message.id), {
      phase: 'ProviderStarted', disposition: null,
    })
    assert.equal(runtime.prompts.filter(prompt => prompt.path?.id === input.sessionID).length, 0)
    assert.ok(runtime.prompts.every(prompt => prompt.path?.id !== input.sessionID))
  })
})

test('WHAT[managed-chat-execution-003] an exact admission joining after committed Host projection receives its own complete model', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const get = runtime.client.session.get
    runtime.client.session.get = async (args) => args.path.id === 'ses-late-admission'
      ? { data: { id: args.path.id, parentID: 'ses-physical-parent' } }
      : get(args)
    await hooks['chat.message']({ sessionID: 'ses-late-admission', messageID: 'msg-late-root', agent: 'engineer' }, {
      message: { id: 'msg-late-root', sessionID: 'ses-late-admission', role: 'user', agent: 'engineer', model: {} }, parts: [],
    })
    const input = { sessionID: 'ses-late-admission', messageID: 'msg-late-admission', agent: 'engineer' }
    const output = () => ({
      message: { id: input.messageID, sessionID: input.sessionID, role: 'user', agent: input.agent,
        model: { providerID: 'host', modelID: 'placeholder' } },
      parts: [],
    })
    const first = output()
    const second = output()
    const late = output()
    let secondModel = second.message.model
    let lateAdmission
    Object.defineProperty(second.message, 'model', {
      enumerable: true,
      get: () => secondModel,
      set: (model) => {
        secondModel = model
        queueMicrotask(() => { lateAdmission = hooks['chat.message'](input, late) })
      },
    })
    const initial = await Promise.allSettled([hooks['chat.message'](input, first), hooks['chat.message'](input, second)])
    assert.deepEqual(initial.map((admission) => admission.status), ['fulfilled', 'fulfilled'])
    assert.ok(lateAdmission, 'the second committed Host projection schedules the late public hook')
    await lateAdmission
    for (const projected of [first, second, late]) {
      assert.deepEqual({ ...projected.message.model }, { providerID: 'provider', modelID: 'engineer-model', variant: 'none' })
    }
    const capacity = routing.sharedCapacitySnapshot()
    assert.equal(capacity.executions.filter((execution) => execution.sessionId === input.sessionID).length, 1)
    assert.equal(capacity.tokens.filter((token) => token.owner.sessionId === input.sessionID).length, 1)
  })
})

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const transaction = await import("../../../dist/OpenCode/Host/ChatAdmission/TransactionSurface.js");

const evidence = {
  sessionId: 'ses-transaction',
  physicalUserMessageId: 'msg-transaction',
  logicalRunId: 'run-transaction',
  authorityRootUserMessageId: 'root-transaction',
  authorityKind: 'HumanRoot',
  identitySeed: {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      selectedAgent: 'engineer',
      canonicalRole: 'engineer',
      selectedTier: 'deep',
      persona: 'Engineer',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  },
  providerRun: 'provider-transaction',
  origin: 'HumanRoot',
  requestKind: 'work-main',
}
const run = (failurePoint = 'None', state = 'None') =>
  transaction.transactionScenario(evidence, failurePoint, state)

test('WHAT[managed-chat-execution-003] managed admission has one fixed success order', async () => {
  const result = await run()

  assert.equal(result.ok, true, JSON.stringify(result.error))
  assert.equal(result.outcome, 'Settled')
  assert.deepEqual(result.trace, [
    'ResolveState',
    'Accept',
    'AcceptedWitness',
    'AcquireLease',
    'LeaseTarget',
    'ProjectHost',
    'CommitLease',
    'Settled',
  ])
  assert.deepEqual(result.target, { model: 'openai/gpt-5', reasoning: 'high' })
  assert.equal(result.acceptCount, 1)
  assert.equal(result.acquireCount, 1)
    assert.equal(result.hostCount, 1)
  assert.equal(result.commitCount, 1)
  assert.equal(result.releaseCount, 0)
  assert.equal(result.providerCount, 0)
})
test('WHAT[managed-chat-execution-003] append failure performs zero downstream effects', async () => {
  for (const failurePoint of ['AcceptNotAttempted', 'AcceptCommitUnknown']) {
    const result = await run(failurePoint)

    assert.equal(result.ok, false)
    assert.equal(result.error.kind, failurePoint === 'AcceptNotAttempted' ? 'NotAttempted' : 'CommitUnknown')
    assert.deepEqual(result.trace, ['ResolveState', 'Accept'])
    assert.equal(result.acquireCount, 0)
        assert.equal(result.hostCount, 0)
    assert.equal(result.commitCount, 0)
    assert.equal(result.releaseCount, 0)
    assert.equal(result.providerCount, 0)
  }
})
test('WHAT[managed-chat-execution-003] acquisition failure crosses no later boundary', async () => {
  const result = await run('AcquireLease')

  assert.equal(result.ok, false)
  assert.equal(result.error.kind, 'LeaseAcquisitionFailed')
  assert.deepEqual(result.trace, [
    'ResolveState',
    'Accept',
    'AcceptedWitness',
    'AcquireLease',
    'TerminalizeAccepted',
  ])
    assert.equal(result.hostCount, 0)
  assert.equal(result.commitCount, 0)
  assert.equal(result.releaseCount, 0)
  assert.equal(result.providerCount, 0)
})
test('WHAT[managed-chat-execution-003] superseded demand is a typed nonfatal short-circuit', async () => {
  const result = await run('AcquireSuperseded')

  assert.equal(result.ok, true, JSON.stringify(result.error))
  assert.equal(result.outcome, 'Superseded')
  assert.deepEqual(result.trace, [
    'ResolveState',
    'Accept',
    'AcceptedWitness',
    'AcquireLease',
    'TerminalizeAccepted',
  ])
    assert.equal(result.hostCount, 0)
  assert.equal(result.commitCount, 0)
  assert.equal(result.releaseCount, 0)
  assert.equal(result.providerCount, 0)
})
test('WHAT[managed-chat-execution-003] already-started replay performs no duplicate admission effect', async () => {
  const result = await run('None', 'ProviderStarted')

  assert.equal(result.ok, true, JSON.stringify(result.error))
  assert.equal(result.outcome, 'AlreadyStarted')
  assert.deepEqual(result.trace, ['ResolveState'])
  assert.equal(result.acceptCount, 0)
  assert.equal(result.acquireCount, 0)
    assert.equal(result.hostCount, 0)
  assert.equal(result.providerCount, 0)
})
}

test('WHAT[managed-chat-execution-003] HostInternal physical input creates no managed admission or provider send', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const input = { sessionID: 'ses-host-internal', messageID: 'msg-host-internal', agent: 'engineer' }
    const output = {
      message: { id: input.messageID, sessionID: input.sessionID, role: 'user', agent: input.agent,
        model: { providerID: 'host', modelID: 'internal-model' } },
      parts: [{ type: 'text', text: 'Host generated material', synthetic: true }],
    }
    const before = routing.sharedCapacitySnapshot()
    await hooks['chat.message'](input, output)
    assert.deepEqual(output.message.model, { providerID: 'host', modelID: 'internal-model' })
    assert.equal(recovery.journalExecutionStatus(runtime.journal, input.sessionID, input.messageID), null)
    assert.deepEqual(routing.sharedCapacitySnapshot(), before)
    assert.equal(runtime.prompts.length, 0)
  })
})

test('WHAT[managed-chat-execution-003] rejected participant identity stops the registered Host hook before acceptance, capacity or projection', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const input = { sessionID: 'ses-invalid-participant', messageID: 'msg-invalid-participant', agent: 'unregistered-agent' }
    const output = {
      message: { id: input.messageID, sessionID: input.sessionID, role: 'user', agent: input.agent,
        model: { providerID: 'host', modelID: 'placeholder' } },
      parts: [{ type: 'text', text: 'human input' }],
    }
    const before = routing.sharedCapacitySnapshot()
    await assert.rejects(hooks['chat.message'](input, output), /IntentRejected.*InvalidExplicitAgent/)
    assert.deepEqual(output.message.model, { providerID: 'host', modelID: 'placeholder' })
    assert.equal(recovery.journalExecutionStatus(runtime.journal, input.sessionID, input.messageID), null)
    assert.deepEqual(routing.sharedCapacitySnapshot(), before)
    assert.equal(runtime.prompts.length, 0)
  })
})
