import assert from 'node:assert/strict'
import test from 'node:test'
import * as sync from '../../../dist/Execution/Delegation/SyncDelegate/Surface.js'
import * as runtimeSurface from '../../../dist/Repository/Programming/Js/RuntimeSurface.js'
import * as events from '../../../dist/OpenCode/Host/EventsSurface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import { withSyncRuntime } from './support/sync-runtime.mjs'
import { runRecoveryProcess } from './support/recovery-process.mjs'
import { runRepairRecoveryProcess } from './support/repair-recovery-process.mjs'

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

test('WHAT[delegation-025] an invocation completes from its own genuinely accepted production retry successor', async t => {
  const receipt = await runRecoveryProcess('own-retry', t.signal, message => t.diagnostic(message))
  assert.notEqual(receipt.retryPrompt.promptKey, receipt.initialPrompt.promptKey)
  assert.notEqual(receipt.successor.physicalUserMessageId, receipt.initial.physicalUserMessageId)
  assert.equal(receipt.successor.authorityRootUserMessageId, receipt.initial.authorityRootUserMessageId)
  assert.equal(receipt.result.value.physicalUserMessageId, receipt.successor.physicalUserMessageId)
  assert.equal(receipt.result.value.formalText, 'RECOVERY-FORMAL-ANSWER')
  assert.equal(receipt.promptCount, 2)
  assert.equal(receipt.childCount, 1)
})

for (const scenario of ['old-retry', 'fallback-old-retry', 'late-retry', 'fallback-late-retry']) {
  test(`WHAT[delegation-025] ${scenario} cannot deliver an earlier genuinely accepted retry to a reused same-root invocation`, async t => {
    await runRecoveryProcess(scenario, t.signal, message => t.diagnostic(message))
  })
}

for (const scenario of ['own-incomplete', 'old-incomplete', 'late-incomplete', 'fallback-old-incomplete', 'fallback-late-incomplete']) {
  test(`WHAT[delegation-025] production repair ${scenario} preserves invocation-owned accepted successors`, async t => {
    await runRepairRecoveryProcess(scenario, t.signal, message => t.diagnostic(message))
  })
}

test('WHAT[delegation-025] late failure from a previous root cannot settle a reused sync call', async () => {
  const owner = 'owner-failure-causality'
  await withSyncRuntime(owner, async runtime => {
    const first = sync.invoke(runtime, owner, 'Engineer', 'FIRST')
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 1)
    assert.equal(sync.acceptPrompt(runtime, owner, 'Engineer', 0), true)
    deliverTo(sync.child(runtime, owner, 'Engineer'), 'run-first', 'FIRST-ANSWER')
    assert.equal(await sync.settle(runtime, owner, 'Engineer', 'FIRST-ANSWER', 'run-first'), true)
    assert.equal((await first).ok, true)
    const second = sync.invoke(runtime, owner, 'Engineer', 'SECOND')
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 2)
    assert.equal(sync.acceptPrompt(runtime, owner, 'Engineer', 1), true)
    let resolved = false
    second.then(() => { resolved = true })
    for (const message of ['late previous failure', 'repeated stale failure']) {
      assert.equal(await sync.failWithAuthorityRoot(runtime, owner, 'Engineer', message, 'msg-physical-1'), 'Ignored')
      await new Promise(resolve => setImmediate(resolve))
      assert.equal(resolved, false)
    }
    assert.equal(await sync.observeTurn(runtime, owner, 'Engineer', 'TurnFailed', 'current failure', 'run-second'), true)
    assert.deepEqual(await second, { ok: false, error: 'SyncDelegate run failed: current failure' })
  })
})

test('WHAT[delegation-025] future Host subscriber excludes old terminal and retains new execution identity', () => {
  const port = events.create()
  events.notify(port, 'reused', 'Completed', 'run-old', 'old result')
  const seen = []
  const subscription = events.subscribeFuture(port, (session, outcome) => seen.push({ session, outcome }))
  try {
    assert.deepEqual(seen, [])
    events.notify(port, 'reused', 'Completed', 'run-new', 'new result')
    assert.equal(seen.length, 1)
    assert.equal(seen[0].session, 'reused')
    assert.equal(seen[0].outcome.providerRun, 'run-new')
    events.notifyForAuthority(port, 'reused', 'Failed', 'root-2', 'provider exhausted')
    assert.equal(seen.length, 2)
    assert.equal(seen[1].outcome.kind, 'Failed')
    assert.equal(seen[1].outcome.text, 'provider exhausted')
    assert.equal(seen[1].outcome.authorityRoot, 'root-2')
  } finally {
    events.dispose(subscription)
  }
})

test.todo('WHAT[delegation-025] actual fork execution rejects late run-scoped success failure and abort while preserving legitimate session-wide failures (GAP-153)')

const observedApiRequired = () => assert.equal(
  typeof sync.startObserved,
  'function',
  'new observed-execution API is not implemented; this red proves missing new capability, not an existing business failure',
)
const stillPendingObserved = async promise => Promise.race([
  promise.then(() => 'settled'),
  new Promise(resolve => setImmediate(() => resolve('pending'))),
])

test('WHAT[delegation-025] observed Engineer execution publishes the exact admitted prompt and causal formal terminal after the existing workflow', async () => {
  observedApiRequired()
  const owner = 'observed-engineer-exact'
  await withSyncRuntime(owner, async runtime => {
    assert.equal(sync.terminalListenerCount(runtime), 0, 'cold Host has no invocation terminal registration')
    await sync.captureOwnerOpening(runtime, owner, 'OBSERVED-PARENT-OPENING')
    const execution = sync.startObserved(runtime, owner, 'OBSERVED-CHARGE')
    const admission = sync.observedAdmission(execution)
    const completion = sync.observedCompletion(execution)
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 1)
    const sent = sync.promptIdentity(runtime, owner, 'Engineer', 0)
    assert.equal(sent.sessionId, sync.child(runtime, owner, 'Engineer'))
    assert.equal(typeof sent.promptKey, 'string')
    assert.ok(sent.promptKey.length > 0)
    assert.ok(sync.terminalListenerCount(runtime) > 0)
    assert.equal(await stillPendingObserved(admission), 'pending')
    assert.equal(await stillPendingObserved(completion), 'pending')
    assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 0, dispatch.admittedWithPhysicalMessage('actual-observed-physical-1')), true)
    const accepted = await admission
    assert.deepEqual(accepted, {
      kind: 'Accepted',
      sessionId: sent.sessionId,
      promptKey: sent.promptKey,
      hostOutcome: { kind: 'AdmittedWithPhysicalMessage', value: 'actual-observed-physical-1' },
      physicalUserMessageId: 'actual-observed-physical-1',
      authorityRootUserMessageId: 'actual-observed-physical-1',
    })
    assert.equal(await stillPendingObserved(completion), 'pending')
    deliverTo(sent.sessionId, 'actual-observed-provider-run-1', 'FORMAL-ONLY')
    assert.equal(await sync.settleExactTerminal(runtime, sent.sessionId, accepted.physicalUserMessageId, accepted.authorityRootUserMessageId, 'actual-observed-provider-run-1', 'FORMAL-ONLY', 'REASONING-ONLY'), true)
    assert.deepEqual(await completion, {
      ok: true,
      value: {
        sessionId: sent.sessionId,
        physicalUserMessageId: 'actual-observed-physical-1',
        authorityRootUserMessageId: 'actual-observed-physical-1',
        providerRun: 'actual-observed-provider-run-1',
        formalText: 'FORMAL-ONLY',
      },
    })
    assert.equal(sync.terminalListenerCount(runtime), 0)
  })
})

test('WHAT[delegation-025] observed invocations reuse a child while preserving distinct prompt and terminal identities and rejecting previous terminals', async () => {
  observedApiRequired()
  const owner = 'observed-engineer-reuse'
  await withSyncRuntime(owner, async runtime => {
    const first = sync.startObserved(runtime, owner, 'FIRST-OBSERVED')
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 1)
    assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 0, dispatch.admittedWithPhysicalMessage('observed-reuse-physical-1')), true)
    const firstAdmission = await sync.observedAdmission(first)
    assert.equal(firstAdmission.kind, 'Accepted')
    deliverTo(firstAdmission.sessionId, 'observed-reuse-run-1', 'FIRST-FORMAL')
    assert.equal(await sync.settleExactTerminal(runtime, firstAdmission.sessionId, firstAdmission.physicalUserMessageId, firstAdmission.authorityRootUserMessageId, 'observed-reuse-run-1', 'FIRST-FORMAL', ''), true)
    const firstResult = await sync.observedCompletion(first)
    assert.equal(firstResult.ok, true)
    const second = sync.startObserved(runtime, owner, 'SECOND-OBSERVED')
    const secondCompletion = sync.observedCompletion(second)
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 2)
    assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 1, dispatch.admittedWithPhysicalMessage('observed-reuse-physical-2')), true)
    const secondAdmission = await sync.observedAdmission(second)
    assert.equal(secondAdmission.kind, 'Accepted')
    assert.equal(secondAdmission.sessionId, firstAdmission.sessionId)
    assert.notEqual(secondAdmission.promptKey, firstAdmission.promptKey)
    assert.notEqual(secondAdmission.physicalUserMessageId, firstAdmission.physicalUserMessageId)
    assert.equal(await sync.settleExactTerminal(runtime, firstAdmission.sessionId, firstAdmission.physicalUserMessageId, firstAdmission.authorityRootUserMessageId, firstResult.value.providerRun, 'STALE-FORMAL', ''), false)
    assert.equal(await stillPendingObserved(secondCompletion), 'pending')
    assert.equal(await sync.failWithAuthorityRoot(runtime, owner, 'Engineer', 'stale coarse failure', firstAdmission.authorityRootUserMessageId), 'Ignored')
    assert.equal(await stillPendingObserved(secondCompletion), 'pending')
    deliverTo(secondAdmission.sessionId, 'observed-reuse-run-2', 'SECOND-FORMAL')
    assert.equal(await sync.settleExactTerminal(runtime, secondAdmission.sessionId, secondAdmission.physicalUserMessageId, secondAdmission.authorityRootUserMessageId, 'observed-reuse-run-2', 'SECOND-FORMAL', ''), true)
    const secondResult = await secondCompletion
    assert.equal(secondResult.ok, true)
    assert.equal(secondResult.value.formalText, 'SECOND-FORMAL')
    assert.equal(secondResult.value.physicalUserMessageId, 'observed-reuse-physical-2')
    assert.equal(secondResult.value.providerRun, 'observed-reuse-run-2')
    assert.notEqual(secondResult.value.providerRun, firstResult.value.providerRun)
    assert.equal(sync.childCount(runtime), 1)
    assert.equal(sync.terminalListenerCount(runtime), 0)
  })
})

test('WHAT[delegation-025] rejection before dispatch settles both observed tasks without another Host prompt', async () => {
  observedApiRequired()
  const owner = 'observed-active-batch'
  await withSyncRuntime(owner, async runtime => {
    const first = sync.startObserved(runtime, owner, 'FIRST-ACTIVE')
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 1)
    const rejected = sync.startObserved(runtime, owner, 'REJECTED-CONCURRENT')
    const admission = await sync.observedAdmission(rejected)
    assert.equal(admission.kind, 'NotDispatched')
    assert.match(admission.reason, /active batch/)
    assert.equal((await sync.observedCompletion(rejected)).ok, false)
    assert.equal(sync.promptCount(runtime, owner, 'Engineer'), 1)
    assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 0, dispatch.admittedWithPhysicalMessage('observed-active-first-physical')), true)
    const accepted = await sync.observedAdmission(first)
    deliverTo(accepted.sessionId, 'observed-active-first-run', 'FIRST-DONE')
    assert.equal(await sync.settleExactTerminal(runtime, accepted.sessionId, accepted.physicalUserMessageId, accepted.authorityRootUserMessageId, 'observed-active-first-run', 'FIRST-DONE', ''), true)
    assert.equal((await sync.observedCompletion(first)).ok, true)
    assert.equal(sync.terminalListenerCount(runtime), 0)
  })
})

test('WHAT[delegation-025] owner disposal after proven admission fails the existing observed completion without inventing physical abort', async () => {
  observedApiRequired()
  const owner = 'observed-dispose-after-admission'
  await withSyncRuntime(owner, async runtime => {
    const execution = sync.startObserved(runtime, owner, 'DISPOSE-AFTER-ADMISSION')
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 1)
    assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 0, dispatch.admittedWithPhysicalMessage('physical-before-owner-dispose')), true)
    const admission = await sync.observedAdmission(execution)
    assert.equal(admission.kind, 'Accepted')
    sync.dispose(runtime)
    const completion = await sync.observedCompletion(execution)
    assert.equal(completion.ok, false)
    assert.equal((await sync.observedAdmission(execution)).physicalUserMessageId, 'physical-before-owner-dispose')
    assert.equal(sync.terminalListenerCount(runtime), 0)
  })
})

for (const defect of ['wrong-root', 'previous-physical']) {
  test(`WHAT[delegation-025] production completed-turn fallback rejects ${defect} instead of settling the reused observed invocation`, async () => {
    observedApiRequired()
    const owner = `observed-fallback-${defect}`
    await withSyncRuntime(owner, async runtime => {
      const first = sync.startObserved(runtime, owner, 'FALLBACK-FIRST')
      await sync.awaitPromptCount(runtime, owner, 'Engineer', 1)
      assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 0, dispatch.admittedWithPhysicalMessage(`fallback-first-${defect}`)), true)
      const firstAdmission = await sync.observedAdmission(first)
      deliverTo(firstAdmission.sessionId, `fallback-first-run-${defect}`, 'FIRST-DONE')
      assert.equal(await sync.settleExactTerminal(runtime, firstAdmission.sessionId, firstAdmission.physicalUserMessageId, firstAdmission.authorityRootUserMessageId, `fallback-first-run-${defect}`, 'FIRST-DONE', ''), true)
      assert.equal((await sync.observedCompletion(first)).ok, true)
      const second = sync.startObserved(runtime, owner, 'FALLBACK-SECOND')
      const completion = sync.observedCompletion(second)
      await sync.awaitPromptCount(runtime, owner, 'Engineer', 2)
      assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 1, dispatch.admittedWithPhysicalMessage(`fallback-second-${defect}`)), true)
      const accepted = await sync.observedAdmission(second)
      assert.equal(accepted.sessionId, firstAdmission.sessionId)
      const physical = defect === 'previous-physical' ? firstAdmission.physicalUserMessageId : accepted.physicalUserMessageId
      const root = defect === 'wrong-root' ? `${accepted.authorityRootUserMessageId}-wrong` : accepted.authorityRootUserMessageId
      assert.equal(sync.settleExactFallback(runtime, accepted.sessionId, physical, root, `fallback-stale-run-${defect}`, 'STALE-FALLBACK'), false)
      assert.equal(await stillPendingObserved(completion), 'pending')
      deliverTo(accepted.sessionId, `fallback-current-run-${defect}`, 'CURRENT-FALLBACK')
      assert.equal(sync.settleExactFallback(runtime, accepted.sessionId, accepted.physicalUserMessageId, accepted.authorityRootUserMessageId, `fallback-current-run-${defect}`, 'CURRENT-FALLBACK'), true)
      const result = await completion
      assert.equal(result.ok, true)
      assert.equal(result.value.formalText, 'CURRENT-FALLBACK')
      assert.equal(result.value.physicalUserMessageId, accepted.physicalUserMessageId)
      assert.equal(result.value.providerRun, `fallback-current-run-${defect}`)
      assert.equal(sync.terminalListenerCount(runtime), 0)
    })
  })
}

test('WHAT[delegation-025] production completed-turn fallback settles its invocation once and preserves the first causal response', async () => {
  observedApiRequired()
  const owner = 'observed-fallback-single-assignment'
  await withSyncRuntime(owner, async runtime => {
    const execution = sync.startObserved(runtime, owner, 'FALLBACK-SINGLE-ASSIGNMENT')
    const completion = sync.observedCompletion(execution)
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 1)
    assert.equal(sync.returnPromptOutcome(runtime, owner, 'Engineer', 0, dispatch.admittedWithPhysicalMessage('fallback-single-physical')), true)
    const accepted = await sync.observedAdmission(execution)
    deliverTo(accepted.sessionId, 'fallback-first-provider-run', 'FIRST-FALLBACK')
    assert.equal(sync.settleExactFallback(runtime, accepted.sessionId, accepted.physicalUserMessageId, accepted.authorityRootUserMessageId, 'fallback-first-provider-run', 'FIRST-FALLBACK'), true)
    assert.equal(sync.settleExactFallback(runtime, accepted.sessionId, accepted.physicalUserMessageId, accepted.authorityRootUserMessageId, 'fallback-late-provider-run', 'LATE-FALLBACK'), false)
    const result = await completion
    assert.equal(result.ok, true)
    assert.equal(result.value.formalText, 'FIRST-FALLBACK')
    assert.equal(result.value.physicalUserMessageId, accepted.physicalUserMessageId)
    assert.equal(result.value.providerRun, 'fallback-first-provider-run')
    assert.equal(sync.terminalListenerCount(runtime), 0)
  })
})

test('WHAT[delegation-025] a real preparation exception settles observed admission and completion without a Host send', async () => {
  observedApiRequired()
  const owner = 'observed-preparation-throws'
  await withSyncRuntime(owner, async runtime => {
    const execution = sync.startObservedWithPreparationFailure(runtime, owner, 'THROWING-PREPARATION', 'actual preparation rejection')
    const first = await Promise.race([
      sync.observedAdmission(execution).then(admission => ({ kind: 'Admission', admission })),
      sync.awaitPromptCount(runtime, owner, 'Engineer', 1).then(() => ({ kind: 'UnexpectedHostSend' })),
    ])
    assert.equal(first.kind, 'Admission', 'failed observed preparation must not dispatch a fallback charge')
    const admission = first.admission
    assert.deepEqual(admission, { kind: 'NotDispatched', reason: 'actual preparation rejection' })
    assert.deepEqual(await sync.observedCompletion(execution), { ok: false, error: 'actual preparation rejection' })
    assert.equal(sync.promptCount(runtime, owner, 'Engineer'), 0)
    assert.equal(sync.terminalListenerCount(runtime), 0)
  })
})

test('WHAT[delegation-025] a real Host send promise exception preserves its actual key without fabricating an outcome', async () => {
  observedApiRequired()
  const owner = 'observed-send-promise-throws'
  await withSyncRuntime(owner, async runtime => {
    const execution = sync.startObserved(runtime, owner, 'THROWING-HOST-SEND')
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 1)
    const sent = sync.promptIdentity(runtime, owner, 'Engineer', 0)
    assert.equal(sync.rejectPrompt(runtime, owner, 'Engineer', 0, 'actual Host promise rejection'), true)
    const admission = await sync.observedAdmission(execution)
    assert.deepEqual(admission, {
      kind: 'Unconfirmed',
      sessionId: sent.sessionId,
      promptKey: sent.promptKey,
      hostOutcome: null,
      reason: 'actual Host promise rejection',
    })
    assert.deepEqual(await sync.observedCompletion(execution), { ok: false, error: 'actual Host promise rejection' })
    assert.equal(sync.promptClaimState(runtime, owner, 'Engineer', 0).kind, 'Pending')
    assert.equal(sync.promptCount(runtime, owner, 'Engineer'), 1)
    assert.equal(sync.terminalListenerCount(runtime), 0)
  })
})

test('WHAT[delegation-025] the natural-stop handback prose excludes thinking and tool material', async () => {
  const owner = 'owner-natural-stop-prose-025'
  await withSyncRuntime(owner, async runtime => {
    const work = sync.invoke(runtime, owner, 'Engineer', 'SYNC-PROSE-CHARGE')
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 1)
    assert.equal(sync.acceptPrompt(runtime, owner, 'Engineer', 0), true)
    const child = sync.child(runtime, owner, 'Engineer')

    // The terminal turn mixes thinking, body prose and a settled tool result;
    // only the body prose may settle the call.
    assert.equal(
      await sync.settleExactTerminal(
        runtime,
        child,
        'msg-physical-1',
        'msg-physical-1',
        'run-prose-025',
        'BODY-PROSE-025',
        'SECRET-THOUGHT-025',
      ),
      true,
    )

    const result = await work
    assert.equal(result.ok, true)
    assert.match(String(result.value), /BODY-PROSE-025/, 'the body prose settles the call')
    assert.doesNotMatch(String(result.value), /SECRET-THOUGHT-025/, 'thinking never settles the call')
    assert.doesNotMatch(String(result.value), /TOOL-RESULT-ONLY/, 'tool material never settles the call')
  })
})

