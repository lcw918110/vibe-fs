import assert from 'node:assert/strict'
import { writeFileSync } from 'node:fs'
import * as sync from '../../../../dist/Execution/Delegation/SyncDelegate/Surface.js'
import * as dispatch from '../../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as runtimeSurface from '../../../../dist/Repository/Programming/Js/RuntimeSurface.js'

const [directory, scenario, receiptPath] = process.argv.slice(2)
const owner = `repair-owner-${scenario}`
const finish = scenario.includes('incomplete') ? 'tool-calls' : 'length'
const delivery = scenario.includes('incomplete') ? 'idle' : 'observation'
const lateAcceptance = scenario.includes('late')
const fallback = scenario.startsWith('fallback-')
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

for (const name of ['createForProviderRecovery', 'observeRepairTurn', 'confirmManagedPromptPhysical', 'closeRecovery']) {
  assert.equal(typeof sync[name], 'function', `missing repair fixture capability: ${name}`)
}

const runtime = await sync.createForProviderRecovery(directory, [{ sessionId: owner, agent: 'manager' }])
try {
  const execution = sync.startObserved(runtime, owner, 'REPAIR-CHARGE')
  const completion = sync.observedCompletion(execution)
  await sync.awaitPromptCount(runtime, owner, 'Engineer', 1)
  const initialPrompt = sync.promptIdentity(runtime, owner, 'Engineer', 0)
  assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 0, dispatch.admittedWithReceipt('initial-repair-receipt')), true)
  const initial = await sync.confirmManagedPromptPhysical(runtime, owner, 'Engineer', 0, 'initial-repair-physical')
  assert.equal(initial.ok, true, JSON.stringify(initial))
  const admission = await sync.observedAdmission(execution)
  assert.equal(admission.physicalUserMessageId, initial.physicalUserMessageId)
  assert.equal(admission.authorityRootUserMessageId, initial.authorityRootUserMessageId)
  const repairRun = 'actual-incomplete-provider-run'
  const observe = idle => sync.observeRepairTurn(runtime, initial.sessionId, initial.physicalUserMessageId,
    initial.authorityRootUserMessageId, repairRun, finish, delivery, idle)
  await observe(false)
  assert.equal(sync.promptCount(runtime, owner, 'Engineer'), 1, 'No idle permit authorizes no repair send')
  const repair = observe(true)
  await sync.awaitPromptCount(runtime, owner, 'Engineer', 2)
  const repairPrompt = sync.promptIdentity(runtime, owner, 'Engineer', 1)
  assert.equal(repairPrompt.sessionId, initial.sessionId)
  assert.notEqual(repairPrompt.promptKey, initialPrompt.promptKey)
  assert.equal(sync.promptOrigin(runtime, owner, 'Engineer', 1), 'InteractionRepair')
  const duplicate = observe(true)
  assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 1, dispatch.admittedWithReceipt('actual-repair-receipt')), true)
  assert.equal(await pendingAfterTurn(repair), 'pending', 'repair waits for physical acceptance, not its transport receipt')
  assert.equal(sync.promptCount(runtime, owner, 'Engineer'), 2)
  assert.equal(sync.promptClaimState(runtime, owner, 'Engineer', 1).kind, 'Pending')
  assert.equal(await pendingAfterTurn(completion), 'pending')
  const repeated = observe(true)
  await pendingAfterTurn(repeated)
  assert.deepEqual(sync.promptIdentity(runtime, owner, 'Engineer', 1), repairPrompt)
  assert.equal(sync.promptCount(runtime, owner, 'Engineer'), 2)
  let successor
  let result
  if (lateAcceptance) {
    deliverTo(initial.sessionId, 'initial-formal-provider-run', 'INITIAL-FORMAL-ANSWER')
    assert.equal(await sync.settleExactTerminal(runtime, initial.sessionId, initial.physicalUserMessageId,
      initial.authorityRootUserMessageId, 'initial-formal-provider-run', 'INITIAL-FORMAL-ANSWER', ''), true)
    result = await completion
    assert.equal(result.value.physicalUserMessageId, initial.physicalUserMessageId)
  } else {
    successor = await sync.confirmManagedPromptPhysical(runtime, owner, 'Engineer', 1, 'actual-repair-physical')
    assert.equal(successor.ok, true, JSON.stringify(successor))
    assert.equal(successor.origin, 'InteractionRepair')
    assert.equal(successor.authorityRootUserMessageId, initial.authorityRootUserMessageId)
    await Promise.all([repair, duplicate, repeated])
    assert.equal(sync.promptClaimState(runtime, owner, 'Engineer', 1).kind, 'Accepted')
    await observe(true)
    assert.equal(sync.promptCount(runtime, owner, 'Engineer'), 2)
    deliverTo(successor.sessionId, 'actual-repair-provider-run', 'REPAIR-FORMAL-ANSWER')
    assert.equal(await sync.settleExactTerminal(runtime, successor.sessionId, successor.physicalUserMessageId,
      successor.authorityRootUserMessageId, 'actual-repair-provider-run', 'REPAIR-FORMAL-ANSWER', ''), true)
    result = await completion
    assert.deepEqual(result, { ok: true, value: {
      sessionId: successor.sessionId, physicalUserMessageId: successor.physicalUserMessageId,
      authorityRootUserMessageId: successor.authorityRootUserMessageId,
      providerRun: 'actual-repair-provider-run', formalText: 'REPAIR-FORMAL-ANSWER',
    } })
  }
  assert.equal(sync.childCount(runtime), 1)
  assert.equal(sync.terminalListenerCount(runtime), 0)
  if (!scenario.startsWith('own-')) {
    const second = sync.startObserved(runtime, owner, 'SECOND-REPAIR-CHARGE')
    const secondCompletion = sync.observedCompletion(second)
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 3)
    assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 2, dispatch.admittedWithReceipt('second-repair-receipt')), true)
    const secondAccepted = await sync.confirmManagedPromptPhysical(runtime, owner, 'Engineer', 2, 'second-assignment-physical')
    assert.equal(secondAccepted.ok, true, JSON.stringify(secondAccepted))
    assert.equal(secondAccepted.origin, 'ManagedDelegationAssignment')
    assert.equal(secondAccepted.sessionId, initial.sessionId)
    assert.equal(secondAccepted.authorityRootUserMessageId, initial.authorityRootUserMessageId)
    const secondAdmission = await sync.observedAdmission(second)
    assert.equal(secondAdmission.physicalUserMessageId, secondAccepted.physicalUserMessageId)
    if (lateAcceptance) {
      successor = await sync.confirmManagedPromptPhysical(runtime, owner, 'Engineer', 1, 'late-repair-physical')
      assert.equal(successor.ok, true, JSON.stringify(successor))
      assert.equal(successor.origin, 'InteractionRepair')
      await Promise.all([repair, duplicate, repeated])
    }
    const staleText = lateAcceptance ? 'LATE-REPAIR-ANSWER' : 'REPAIR-FORMAL-ANSWER'
    const staleAccepted = fallback
      ? sync.settleExactFallback(runtime, successor.sessionId, successor.physicalUserMessageId,
        successor.authorityRootUserMessageId, 'actual-repair-provider-run', staleText)
      : await sync.settleExactTerminal(runtime, successor.sessionId, successor.physicalUserMessageId,
        successor.authorityRootUserMessageId, 'actual-repair-provider-run', staleText, '')
    assert.equal(staleAccepted, false, 'An actually accepted old repair cannot settle the next same-root invocation')
    assert.equal(await pendingAfterTurn(secondCompletion), 'pending')
    const secondRepair = sync.observeRepairTurn(runtime, secondAccepted.sessionId, secondAccepted.physicalUserMessageId,
      secondAccepted.authorityRootUserMessageId, 'second-incomplete-provider-run', finish, delivery, true)
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 4)
    assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 3, dispatch.admittedWithReceipt('second-actual-repair-receipt')), true)
    const secondSuccessor = await sync.confirmManagedPromptPhysical(runtime, owner, 'Engineer', 3, 'second-repair-physical')
    await secondRepair
    assert.equal(secondSuccessor.ok, true, JSON.stringify(secondSuccessor))
    assert.equal(secondSuccessor.origin, 'InteractionRepair')
    deliverTo(secondSuccessor.sessionId, 'second-formal-provider-run', 'SECOND-REPAIR-FORMAL-ANSWER')
    assert.equal(await sync.settleExactTerminal(runtime, secondSuccessor.sessionId, secondSuccessor.physicalUserMessageId,
      secondSuccessor.authorityRootUserMessageId, 'second-formal-provider-run', 'SECOND-REPAIR-FORMAL-ANSWER', ''), true)
    const secondResult = await secondCompletion
    assert.equal(secondResult.value.physicalUserMessageId, secondSuccessor.physicalUserMessageId)
    assert.equal(secondResult.value.formalText, 'SECOND-REPAIR-FORMAL-ANSWER')
    assert.equal(sync.childCount(runtime), 1)
    assert.equal(sync.terminalListenerCount(runtime), 0)
  }
  writeFileSync(receiptPath, JSON.stringify({ pid: process.pid, scenario, initial, initialPrompt,
    repairPrompt, successor, result, promptCount: sync.promptCount(runtime, owner, 'Engineer'),
    childCount: sync.childCount(runtime), terminalListenerCount: sync.terminalListenerCount(runtime) }))
} finally {
  await sync.closeRecovery(runtime)
}

