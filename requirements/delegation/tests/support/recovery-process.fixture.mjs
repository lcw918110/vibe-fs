import assert from 'node:assert/strict'
import { writeFileSync } from 'node:fs'
import * as sync from '../../../../dist/Execution/Delegation/SyncDelegate/Surface.js'
import * as dispatch from '../../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as runtimeSurface from '../../../../dist/Repository/Programming/Js/RuntimeSurface.js'

const [directory, scenario, receiptPath] = process.argv.slice(2)
const owner = `recovery-owner-${scenario}`
const pendingAfterTurn = promise => Promise.race([
  promise.then(() => 'settled'),
  new Promise(resolve => setImmediate(() => resolve('pending'))),
])

// The delivered view is the settlement: a terminal that carries no fenced
// delivery is observation only and the obligation stays open. The harness
// registers the delivery of the exact provider run it is about to settle.
const deliverTo = (sessionId, providerRun, material) =>
  runtimeSurface.recordFencedDeliveredView(
    sessionId,
    providerRun,
    `call-${providerRun}`,
    [material],
  )

for (const name of ['createForProviderRecovery', 'confirmManagedPromptPhysical', 'observeProviderFailureStop', 'observeExactProviderFailure', 'recoveryStopSnapshot']) {
  assert.equal(typeof sync[name], 'function', `missing recovery fixture capability: ${name}`)
}

const runtime = await sync.createForProviderRecovery(directory, [{ sessionId: owner, agent: 'manager' }])
try {
  const execution = sync.startObserved(runtime, owner, 'RECOVERY-CHARGE')
  const admission = sync.observedAdmission(execution)
  const completion = sync.observedCompletion(execution)
  await sync.awaitPromptCount(runtime, owner, 'Engineer', 1)
  const initialPrompt = sync.promptIdentity(runtime, owner, 'Engineer', 0)
  assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 0, dispatch.admittedWithReceipt('initial-host-receipt')), true)
  const initial = await sync.confirmManagedPromptPhysical(runtime, owner, 'Engineer', 0, 'initial-recovery-physical')
  assert.equal(initial.ok, true, JSON.stringify(initial))
  assert.equal(initial.origin, 'AgentOwnerRoot')
  assert.equal(initial.role, 'engineer')
  const admitted = await admission
  assert.equal(admitted.kind, 'Accepted')
  assert.equal(admitted.promptKey, initialPrompt.promptKey)
  assert.equal(admitted.physicalUserMessageId, initial.physicalUserMessageId)
  assert.equal(admitted.authorityRootUserMessageId, initial.authorityRootUserMessageId)

  const failedRun = 'actual-failed-provider-run'
  assert.equal(sync.observeProviderFailureStop(runtime, initial.sessionId, failedRun), true)
  const failed = sync.observeExactProviderFailure(runtime, initial.sessionId, initial.physicalUserMessageId,
    initial.authorityRootUserMessageId, failedRun, 'confirmed provider failure')
  await sync.awaitPromptCount(runtime, owner, 'Engineer', 2)
  const retryPrompt = sync.promptIdentity(runtime, owner, 'Engineer', 1)
  assert.equal(retryPrompt.sessionId, initial.sessionId)
  assert.notEqual(retryPrompt.promptKey, initialPrompt.promptKey)
  assert.equal(sync.promptOrigin(runtime, owner, 'Engineer', 1), 'ProviderRetryAttempt')
  assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 1, dispatch.admittedWithReceipt('retry-host-receipt')), true)
  assert.equal(await pendingAfterTurn(failed), 'pending', 'recovery waits for physical acceptance, not its transport receipt')
  assert.equal(sync.promptClaimState(runtime, owner, 'Engineer', 1).kind, 'Pending')
  assert.equal(await pendingAfterTurn(completion), 'pending', 'a receipt does not complete the invocation')

  const duplicateFailure = sync.observeExactProviderFailure(runtime, initial.sessionId, initial.physicalUserMessageId,
    initial.authorityRootUserMessageId, failedRun, 'confirmed provider failure')
  await pendingAfterTurn(duplicateFailure)
  assert.equal(sync.promptCount(runtime, owner, 'Engineer'), 2, 'Pending recovery reentry preserves one physical send')
  assert.deepEqual(sync.promptIdentity(runtime, owner, 'Engineer', 1), retryPrompt)
  const lateAcceptance = scenario === 'late-retry' || scenario === 'fallback-late-retry'
  let successor
  let result
  if (lateAcceptance) {
    deliverTo(initial.sessionId, 'initial-completion-provider-run', 'INITIAL-COMPLETES')
    assert.equal(await sync.settleExactTerminal(runtime, initial.sessionId, initial.physicalUserMessageId,
      initial.authorityRootUserMessageId, 'initial-completion-provider-run', 'INITIAL-COMPLETES', ''), true)
    result = await completion
    assert.equal(result.ok, true)
    assert.equal(result.value.physicalUserMessageId, initial.physicalUserMessageId)
  } else {
    successor = await sync.confirmManagedPromptPhysical(runtime, owner, 'Engineer', 1, 'actual-retry-physical')
    assert.equal(successor.ok, true, JSON.stringify(successor))
    assert.equal(successor.origin, 'ProviderRetryAttempt')
    assert.notEqual(successor.physicalUserMessageId, initial.physicalUserMessageId)
    assert.equal(successor.authorityRootUserMessageId, initial.authorityRootUserMessageId)
    assert.deepEqual(await Promise.all([failed, duplicateFailure]), [true, true])
    assert.equal(sync.promptClaimState(runtime, owner, 'Engineer', 1).kind, 'Accepted')
    assert.equal(await pendingAfterTurn(completion), 'pending')
    assert.equal(await sync.observeExactProviderFailure(runtime, initial.sessionId, initial.physicalUserMessageId,
      initial.authorityRootUserMessageId, failedRun, 'confirmed provider failure'), true)
    assert.equal(sync.promptCount(runtime, owner, 'Engineer'), 2, 'Accepted recovery reentry preserves the original PromptKey')
    assert.deepEqual(sync.promptIdentity(runtime, owner, 'Engineer', 1), retryPrompt)

    deliverTo(successor.sessionId, 'actual-retry-provider-run', 'RECOVERY-FORMAL-ANSWER')
    assert.equal(await sync.settleExactTerminal(runtime, successor.sessionId, successor.physicalUserMessageId,
      successor.authorityRootUserMessageId, 'actual-retry-provider-run', 'RECOVERY-FORMAL-ANSWER', ''), true)
    result = await completion
    assert.deepEqual(result, {
      ok: true,
      value: {
        sessionId: successor.sessionId,
        physicalUserMessageId: successor.physicalUserMessageId,
        authorityRootUserMessageId: successor.authorityRootUserMessageId,
        providerRun: 'actual-retry-provider-run',
        formalText: 'RECOVERY-FORMAL-ANSWER',
      },
    })
  }
  assert.equal(sync.childCount(runtime), 1)
  assert.equal(sync.terminalListenerCount(runtime), 0)
  assert.equal(sync.recoveryStopSnapshot(runtime).waiting, 0)
  if (scenario !== 'own-retry') {
    const second = sync.startObserved(runtime, owner, 'SECOND-RECOVERY-CHARGE')
    const secondCompletion = sync.observedCompletion(second)
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 3)
    assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 2, dispatch.admittedWithReceipt('second-host-receipt')), true)
    const secondAccepted = await sync.confirmManagedPromptPhysical(runtime, owner, 'Engineer', 2, 'second-assignment-physical')
    assert.equal(secondAccepted.ok, true, JSON.stringify(secondAccepted))
    assert.equal(secondAccepted.origin, 'ManagedDelegationAssignment')
    assert.equal(secondAccepted.sessionId, initial.sessionId)
    assert.equal(secondAccepted.authorityRootUserMessageId, initial.authorityRootUserMessageId)
    const secondAdmission = await sync.observedAdmission(second)
    assert.equal(secondAdmission.physicalUserMessageId, secondAccepted.physicalUserMessageId)
    if (lateAcceptance) {
      successor = await sync.confirmManagedPromptPhysical(runtime, owner, 'Engineer', 1, 'late-retry-physical')
      assert.equal(successor.ok, true, JSON.stringify(successor))
      assert.equal(successor.origin, 'ProviderRetryAttempt')
      assert.notEqual(successor.physicalUserMessageId, initial.physicalUserMessageId)
      assert.equal(successor.authorityRootUserMessageId, initial.authorityRootUserMessageId)
      assert.deepEqual(await Promise.all([failed, duplicateFailure]), [false, true],
        'a retry accepted after its invocation completed cannot handle that closed invocation')
    }
    const staleText = lateAcceptance ? 'LATE-RETRY-ANSWER' : 'RECOVERY-FORMAL-ANSWER'
    const staleAccepted = scenario.startsWith('fallback-')
      ? sync.settleExactFallback(runtime, successor.sessionId, successor.physicalUserMessageId,
        successor.authorityRootUserMessageId, 'actual-retry-provider-run', staleText)
      : await sync.settleExactTerminal(runtime, successor.sessionId, successor.physicalUserMessageId,
        successor.authorityRootUserMessageId, 'actual-retry-provider-run', staleText, '')
    assert.equal(staleAccepted, false, 'an actually accepted old retry cannot settle the new same-root call')
    assert.equal(await pendingAfterTurn(secondCompletion), 'pending')
    const secondFailedRun = 'second-failed-provider-run'
    assert.equal(sync.observeProviderFailureStop(runtime, secondAccepted.sessionId, secondFailedRun), true)
    const secondFailed = sync.observeExactProviderFailure(runtime, secondAccepted.sessionId,
      secondAccepted.physicalUserMessageId, secondAccepted.authorityRootUserMessageId, secondFailedRun, 'second confirmed failure')
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 4)
    assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 3, dispatch.admittedWithReceipt('second-retry-receipt')), true)
    const secondSuccessor = await sync.confirmManagedPromptPhysical(runtime, owner, 'Engineer', 3, 'second-retry-physical')
    assert.equal(await secondFailed, true)
    assert.equal(secondSuccessor.ok, true, JSON.stringify(secondSuccessor))
    assert.notEqual(secondSuccessor.physicalUserMessageId, secondAccepted.physicalUserMessageId)
    assert.equal(secondSuccessor.origin, 'ProviderRetryAttempt')
    deliverTo(secondSuccessor.sessionId, 'second-provider-run', 'SECOND-FORMAL-ANSWER')
    assert.equal(await sync.settleExactTerminal(runtime, secondSuccessor.sessionId, secondSuccessor.physicalUserMessageId,
      secondSuccessor.authorityRootUserMessageId, 'second-provider-run', 'SECOND-FORMAL-ANSWER', ''), true)
    const secondResult = await secondCompletion
    assert.equal(secondResult.ok, true)
    assert.equal(secondResult.value.physicalUserMessageId, secondSuccessor.physicalUserMessageId)
    assert.equal(secondResult.value.providerRun, 'second-provider-run')
    assert.equal(secondResult.value.formalText, 'SECOND-FORMAL-ANSWER')
    assert.equal(sync.childCount(runtime), 1)
    assert.equal(sync.terminalListenerCount(runtime), 0)
  }
  writeFileSync(receiptPath, JSON.stringify({ pid: process.pid, scenario, initialPrompt, initial, admitted,
    retryPrompt, successor, result, promptCount: sync.promptCount(runtime, owner, 'Engineer'), childCount: sync.childCount(runtime) }))
} finally {
  await sync.closeRecovery(runtime)
}

