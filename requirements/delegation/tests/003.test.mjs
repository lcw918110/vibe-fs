import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as forkTool from '../../../dist/Execution/Delegation/Fork/OpenCode/ToolSurface.js'

const schemaNode = (kind, extra = {}) => ({
  kind,
  ...extra,
  describe: () => schemaNode(`${kind}-described`, extra),
  optional: () => schemaNode(`${kind}-optional`, extra),
  int: () => schemaNode(`${kind}-int`, extra),
  nonnegative: () => schemaNode(`${kind}-nonnegative`, extra),
})

const toolModule = {
  tool: {
    schema: {
      string: () => schemaNode('string'),
      number: () => schemaNode('number'),
      enum: (values) => schemaNode('enum', { values }),
      array: (inner) => schemaNode('array', { inner }),
    },
  },
}

const waitForPromptCount = (runtime, count) => forkTool.awaitPromptCount(runtime, count)

const ownerDescriptor = (sessionId) => [{ sessionId, agent: 'manager' }]

const deferred = () => {
  let resolve
  const promise = new Promise(done => { resolve = done })
  return { promise, resolve }
}

test('WHAT[delegation-003] FORK_TOOL_defaults_blank_calling_from_name_and_resume_rejects_calling', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-fork-split-'))
  const owner = 'manager-fork-split'
  const runtime = await forkTool.createRuntime(directory, ownerDescriptor(owner))

  try {
    const derived = forkTool.executeManagerFork(
      runtime,
      toolModule,
      owner,
      '',
      'Ada',
      'FORK-DERIVED-CALLING',
    )
    await waitForPromptCount(runtime, 1)
    assert.equal(forkTool.acceptPrompt(runtime, 0), true)
    assert.match(await derived, /carries this charge now|现已接下这项托付/i)
    assert.equal(forkTool.childCount(runtime), 1, 'a blank calling derives to engineer and places the child')

    const rejectedCalling = await forkTool.executeManagerResume(
      runtime,
      toolModule,
      owner,
      'engineer',
      'Ada',
      'RESUME-REJECTS-CALLING',
    )
    assert.match(rejectedCalling, /never calls a new one|从不叫起新人/i)
    assert.match(rejectedCalling, /use fork|用 fork/i)
    assert.equal(forkTool.childCount(runtime), 1, 'resume must not place a second child')
  } finally {
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[delegation-003] FORK_TOOL_derives_devops_from_a_devops_name_and_refuses_to_fork_it', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-fork-derive-devops-'))
  const owner = 'manager-fork-derive-devops'
  const runtime = await forkTool.createRuntime(directory, ownerDescriptor(owner))

  try {
    const derived = await forkTool.executeManagerFork(
      runtime,
      toolModule,
      owner,
      '',
      'devops',
      'FORK-DERIVES-DEVOPS',
    )
    assert.match(derived, /unknown-calling|only targets Engineer|只能 fork Engineer|未结识的 calling/i)
    assert.equal(forkTool.childCount(runtime), 0, 'a devops name derives devops and cannot be forked')
  } finally {
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[delegation-003] FORK_TOOL_rejects_an_explicit_calling_that_conflicts_with_the_derived_one', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-fork-conflict-'))
  const owner = 'manager-fork-conflict'
  const runtime = await forkTool.createRuntime(directory, ownerDescriptor(owner))

  try {
    const conflict = await forkTool.executeManagerFork(
      runtime,
      toolModule,
      owner,
      'devops',
      'Ada',
      'FORK-CALLING-CONFLICT',
    )
    assert.match(conflict, /conflicts|不一致|只能 fork Engineer/i)
    assert.equal(forkTool.childCount(runtime), 0, 'a conflicting calling must not place any child')

    const reverse = await forkTool.executeManagerFork(
      runtime,
      toolModule,
      owner,
      'engineer',
      'devops',
      'FORK-NAME-CONFLICT',
    )
    assert.match(reverse, /conflicts|不一致|只能 fork Engineer/i)
    assert.equal(forkTool.childCount(runtime), 0, 'an engineer calling for a devops name must not place any child')
  } finally {
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[delegation-003] FORK_TOOL_manager_resume_dispatches_to_bound_fixed_devops_directly', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-mgr-devops-resume-'))
  const owner = 'manager-devops-resume'
  const runtime = await forkTool.createRuntime(directory, ownerDescriptor(owner))

  try {
    await forkTool.injectAcceptedAssessment(runtime, owner)
    const resumed = forkTool.executeManagerResume(
      runtime,
      toolModule,
      owner,
      '',
      'devops',
      'DEVOPS-FIRST-CHARGE',
    )
    await waitForPromptCount(runtime, 1)
    assert.equal(forkTool.acceptPrompt(runtime, 0), true)
    const result = await resumed
    assert.match(result, /devops/)
    assert.match(result, /carries this charge now|现已接下这项托付/i)

    assert.equal(await forkTool.settle(runtime, owner, 'DEVOPS-FIRST-ANSWER', 'devops-run-1'), true)
    const joined = await forkTool.executeJoin(runtime, owner)
    assert.match(joined, /DEVOPS-FIRST-ANSWER/)
  } finally {
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[delegation-003] explicit runtime reopen preserves the companion DevOps route for a new charge', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-mgr-devops-restart-'))
  const owner = 'manager-devops-restart'
  const runtime1 = await forkTool.createRuntime(directory, ownerDescriptor(owner))
  let firstDisposed = false

  try {
    await forkTool.injectAcceptedAssessment(runtime1, owner)
    const first = forkTool.executeManagerResume(
      runtime1,
      toolModule,
      owner,
      '',
      'devops',
      'DEVOPS-FIRST-CHARGE',
    )
    await waitForPromptCount(runtime1, 1)
    assert.equal(forkTool.acceptPrompt(runtime1, 0), true)
    const resumed1 = await first
    assert.match(resumed1, /devops/)
    assert.match(resumed1, /carries this charge now|现已接下这项托付/i)

    assert.equal(await forkTool.settle(runtime1, owner, 'DEVOPS-FIRST-ANSWER', 'devops-run-1'), true)
    const joined1 = await forkTool.executeJoin(runtime1, owner)
    assert.match(joined1, /DEVOPS-FIRST-ANSWER/)

    // Explicit same-process reopen of the durable journal, not an OS crash.
    forkTool.disposeRuntime(runtime1)
    firstDisposed = true
    const runtime2 = await forkTool.createRuntime(directory, ownerDescriptor(owner))

    try {
      // Horizon on fresh runtime is cleared (current process handles empty)
      const horizonView = await forkTool.executeHorizon(runtime2, owner)
      assert.ok(!horizonView.includes('DEVOPS-FIRST-CHARGE'))
      assert.match(horizonView, /devops/)
      assert.equal(forkTool.childCount(runtime2), 0, 'reopen adopts the stable DevOps binding without creating another physical child')
      const firstWork = forkTool.workSnapshot(runtime2, owner)[0]
      assert.equal(firstWork.lifecycle, 'Retired')

      // Companion DevOps is NOT cleaned up, and its state is normalized to accept new charges
      const second = forkTool.executeManagerResume(
        runtime2,
        toolModule,
        owner,
        '',
        'devops',
        'DEVOPS-RESTART-CHARGE',
      )
      await waitForPromptCount(runtime2, 1)
      assert.equal(forkTool.acceptPrompt(runtime2, 0), true)
      const resumed2 = await second
      assert.match(resumed2, /devops/)
      assert.match(resumed2, /carries this charge now|现已接下这项托付/i)
      assert.equal(forkTool.childCount(runtime2), 0, 'a new charge reuses the original physical child after reopening')
      const works = forkTool.workSnapshot(runtime2, owner)
      assert.equal(works.length, 2)
      const secondWork = works.find(work => work.lifecycle === 'Active')
      assert.equal(secondWork.child, firstWork.child)
      assert.equal(secondWork.handle, firstWork.handle)
      assert.notEqual(secondWork.root, firstWork.root)

      assert.equal(await forkTool.settle(runtime2, owner, 'DEVOPS-SECOND-ANSWER', 'devops-run-2'), true)
      const joined2 = await forkTool.executeJoin(runtime2, owner)
      assert.match(joined2, /DEVOPS-SECOND-ANSWER/)
    } finally {
      forkTool.disposeRuntime(runtime2)
    }
  } finally {
    if (!firstDisposed) forkTool.disposeRuntime(runtime1)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[delegation-003] companion devops is preserved and not abandoned when parent session cancels child work', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-mgr-devops-cancel-'))
  const owner = 'manager-devops-cancel'
  const runtime = await forkTool.createRuntime(directory, ownerDescriptor(owner))

  try {
    await forkTool.injectAcceptedAssessment(runtime, owner)
    const first = forkTool.executeManagerResume(
      runtime,
      toolModule,
      owner,
      '',
      'devops',
      'DEVOPS-CHARGE-BEFORE-CANCEL',
    )
    await waitForPromptCount(runtime, 1)
    assert.equal(forkTool.acceptPrompt(runtime, 0), true)
    const resumed = await first
    assert.match(resumed, /devops/)

    // Trigger cancelOwnerChildren
    await forkTool.cancelOwnerChildren(runtime, owner)

    // DevOps handle must remain Active (not Abandoned)
    const lifecycle = forkTool.durableLifecycleByname(runtime, owner, 'devops')
    assert.notEqual(lifecycle, 'Abandoned', 'Companion devops must not be abandoned on parent cancel')
    const firstWork = forkTool.workSnapshot(runtime, owner)[0]
    assert.equal(firstWork.lifecycle, 'Active', 'parent cancel does not terminate the excluded fixed DevOps work')
    assert.equal(await forkTool.settle(runtime, owner, 'DEVOPS-ANSWER-AFTER-CANCEL', 'devops-cancel-run-1'), true)
    const firstJoined = await forkTool.executeJoin(runtime, owner)
    assert.match(firstJoined, /DEVOPS-ANSWER-AFTER-CANCEL/)

    // Verify DevOps is still intact and ready to accept new assignments
    const second = forkTool.executeManagerResume(
      runtime,
      toolModule,
      owner,
      '',
      'devops',
      'DEVOPS-CHARGE-AFTER-CANCEL',
    )
    await waitForPromptCount(runtime, 2)
    assert.equal(forkTool.acceptPrompt(runtime, 1), true)
    const resumedAfterCancel = await second
    assert.match(resumedAfterCancel, /devops/)
    const works = forkTool.workSnapshot(runtime, owner)
    assert.equal(works.length, 2)
    const secondWork = works.find(work => work.lifecycle === 'Active')
    assert.equal(secondWork.child, firstWork.child)
    assert.equal(secondWork.handle, firstWork.handle)
    assert.notEqual(secondWork.root, firstWork.root)
  } finally {
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[delegation-003] parent cancel preserves the busy fixed DevOps work until its real terminal', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-mgr-devops-cancel-busy-'))
  const owner = 'manager-devops-cancel-busy'
  const runtime = await forkTool.createRuntime(directory, ownerDescriptor(owner))
  try {
    await forkTool.injectAcceptedAssessment(runtime, owner)
    const first = forkTool.executeManagerResume(runtime, toolModule, owner, '', 'devops', 'DEVOPS-ACTIVE-CHARGE')
    await waitForPromptCount(runtime, 1)
    assert.equal(forkTool.acceptPrompt(runtime, 0), true)
    assert.match(await first, /carries this charge now|现已接下这项托付/i)
    const originalWork = forkTool.workSnapshot(runtime, owner)

    await forkTool.cancelOwnerChildren(runtime, owner)
    assert.deepEqual(forkTool.workSnapshot(runtime, owner), originalWork)
    const completeOriginal = await forkTool.prepareTerminalDelivery(runtime, owner, 'DEVOPS-OLD-WORK-RETURNED', 'devops-busy-run-1')

    // DevOps 忙时明确拒绝新的 assignment（capability-enforcement 规范），不追加引导
    const busyRejection = await forkTool.executeManagerResume(runtime, toolModule, owner, '', 'devops', 'GUIDANCE-WHILE-BUSY')
    assert.match(busyRejection, /cannot take another charge|尚不能再接下另一项托付|cannot take charge|busy|无法承担新的差事/i, 'Busy DevOps must be rejected')
    assert.equal(forkTool.promptCount(runtime), 1, 'no new prompt when DevOps is busy')
    assert.deepEqual(forkTool.workSnapshot(runtime, owner), originalWork)

    await completeOriginal()
    assert.match(await forkTool.executeJoin(runtime, owner), /DEVOPS-OLD-WORK-RETURNED/)
    assert.equal(forkTool.workSnapshot(runtime, owner)[0].lifecycle, 'Retired')
  } finally {
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[delegation-003] the original fixed DevOps terminal callback survives parent cancel without another tool lookup', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-mgr-devops-original-callback-'))
  const owner = 'manager-devops-original-callback'
  const runtime = await forkTool.createRuntime(directory, ownerDescriptor(owner))
  try {
    await forkTool.injectAcceptedAssessment(runtime, owner)
    forkTool.acceptNextPrompt(runtime)
    assert.match(await forkTool.executeManagerResume(runtime, toolModule, owner, '', 'devops', 'LIVE-DEVOPS'), /carries this charge now|现已接下这项托付/i)
    const devopsTerminal = await forkTool.prepareTerminalDelivery(runtime, owner, 'DEVOPS-ORIGINAL-RETURN', 'devops-original-provider')
    forkTool.acceptNextPrompt(runtime)
    assert.match(await forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', 'Ada', 'CANCELLED-ENGINEER'), /Ada/)
    const engineerTerminal = await forkTool.prepareTerminalDelivery(runtime, owner, 'IGNORED-ENGINEER-RETURN', 'engineer-late-provider')

    await forkTool.cancelOwnerChildren(runtime, owner)
    const cancelled = forkTool.workSnapshot(runtime, owner)
    assert.equal(cancelled.find(work => work.byname === 'devops').lifecycle, 'Active')
    assert.equal(cancelled.find(work => work.byname === 'Ada').lifecycle, 'Abandoned')
    await engineerTerminal()
    assert.deepEqual(forkTool.workSnapshot(runtime, owner), cancelled, 'an ordinary cancelled callback has no completion effect')
    await devopsTerminal()
    const completed = forkTool.workSnapshot(runtime, owner)
    assert.equal(completed.find(work => work.byname === 'devops').lifecycle, 'CompletedAwaitingJoin', 'the original callback writes the exact durable completion without RuntimeFor')
    assert.equal(completed.find(work => work.byname === 'Ada').lifecycle, 'Abandoned')
    assert.deepEqual(await forkTool.coldWorkSnapshot(directory, owner), completed)
    const joined = await forkTool.executeJoin(runtime, owner)
    assert.match(joined, /DEVOPS-ORIGINAL-RETURN/)
    assert.doesNotMatch(joined, /IGNORED-ENGINEER-RETURN/)
  } finally {
    await forkTool.detachToolRuntime(runtime)
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[delegation-003] scope detach after parent cancel releases the preserved fixed DevOps callback', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-mgr-devops-detach-callback-'))
  const owner = 'manager-devops-detach-callback'
  const runtime = await forkTool.createRuntime(directory, ownerDescriptor(owner))
  try {
    await forkTool.injectAcceptedAssessment(runtime, owner)
    forkTool.acceptNextPrompt(runtime)
    assert.match(await forkTool.executeManagerResume(runtime, toolModule, owner, '', 'devops', 'DETACHED-DEVOPS'), /carries this charge now|现已接下这项托付/i)
    const deliver = await forkTool.prepareTerminalDelivery(runtime, owner, 'DETACHED-MUST-NOT-COMPLETE', 'devops-detached-provider')
    await forkTool.cancelOwnerChildren(runtime, owner)
    const active = forkTool.workSnapshot(runtime, owner)
    assert.ok(forkTool.terminalListenerCount(runtime) > 0, 'the fixed callback remains owned after parent cancel')
    await forkTool.detachToolRuntime(runtime)
    assert.equal(forkTool.terminalListenerCount(runtime), 0, 'scope detach physically releases all original Host subscriptions')
    await deliver()
    assert.deepEqual(forkTool.workSnapshot(runtime, owner), active, 'detached terminal subscriptions cannot publish completion')
  } finally {
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[delegation-003] each new ordinary work is cancelled after the fixed road owner survives an earlier cancel', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-mgr-devops-repeat-cancel-'))
  const owner = 'manager-devops-repeat-cancel'
  const runtime = await forkTool.createRuntime(directory, ownerDescriptor(owner))
  try {
    assert.match(await forkTool.executeHorizon(runtime, owner), /devops/)
    for (const byname of ['Ada', 'Bea']) {
      forkTool.acceptNextPrompt(runtime)
      assert.match(await forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', byname, `CHARGE-${byname}`), new RegExp(byname))
      const deliver = await forkTool.prepareTerminalDelivery(runtime, owner, `LATE-${byname}`, `late-provider-${byname}`)
      await forkTool.cancelOwnerChildren(runtime, owner)
      const cancelled = forkTool.workSnapshot(runtime, owner)
      assert.equal(cancelled.find(work => work.byname === byname).lifecycle, 'Abandoned')
      assert.ok(cancelled.every(work => work.lifecycle === 'Abandoned'))
      await deliver()
      assert.deepEqual(forkTool.workSnapshot(runtime, owner), cancelled)
      assert.equal(forkTool.durableLifecycleByname(runtime, owner, 'devops'), 'Active')
    }
    assert.equal(forkTool.workSnapshot(runtime, owner).length, 2)
    assert.equal(forkTool.abortCount(runtime), 2)
  } finally {
    await forkTool.detachToolRuntime(runtime)
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[delegation-003] a parent cancellation episode rejects new ordinary ingress before physical placement', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-mgr-cancel-ingress-'))
  const owner = 'manager-cancel-ingress'
  const abortReached = deferred()
  const releaseAbort = deferred()
  let held = true
  const runtime = await forkTool.createRuntimeWithAbort(directory, ownerDescriptor(owner), async () => {
    if (held) {
      held = false
      abortReached.resolve()
      await releaseAbort.promise
    }
    return { ok: true }
  })
  let cancellation
  try {
    await forkTool.injectAcceptedAssessment(runtime, owner)
    forkTool.acceptNextPrompt(runtime)
    await forkTool.executeManagerResume(runtime, toolModule, owner, '', 'devops', 'FIXED-DURING-CANCEL')
    const fixedTerminal = await forkTool.prepareTerminalDelivery(runtime, owner, 'FIXED-STILL-RETURNS', 'fixed-ingress-provider')
    forkTool.acceptNextPrompt(runtime)
    await forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', 'Ada', 'OLD-ORDINARY')
    cancellation = forkTool.cancelOwnerChildren(runtime, owner)
    const cancelled = Promise.allSettled([cancellation])
    await abortReached.promise
    const childrenBefore = forkTool.childCount(runtime)
    const sendsBefore = forkTool.promptCount(runtime)
    forkTool.acceptNextPrompt(runtime)
    const refused = await forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', 'Bea', 'DURING-CANCEL')
    assert.doesNotMatch(refused, /carries this charge now|现已接下这项托付/i)
    assert.equal(forkTool.childCount(runtime), childrenBefore, 'the active cancellation cut admits no new physical child')
    assert.equal(forkTool.promptCount(runtime), sendsBefore, 'the active cancellation cut sends no new assignment')
    releaseAbort.resolve()
    assert.equal((await cancelled)[0].status, 'fulfilled')
    await fixedTerminal()
    assert.equal(forkTool.workSnapshot(runtime, owner).find(work => work.byname === 'devops').lifecycle, 'CompletedAwaitingJoin')
    const admitted = await forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', 'Bea', 'AFTER-CANCEL')
    assert.match(admitted, /carries this charge now|现已接下这项托付/i)
    await forkTool.cancelOwnerChildren(runtime, owner)
    assert.equal(forkTool.workSnapshot(runtime, owner).find(work => work.byname === 'Bea').lifecycle, 'Abandoned')
  } finally {
    releaseAbort.resolve()
    await Promise.allSettled(cancellation ? [cancellation] : [])
    await forkTool.detachToolRuntime(runtime)
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[delegation-003] a failed in-flight cancellation cannot skip scope detach of the original fixed callback', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-mgr-failed-cancel-detach-'))
  const owner = 'manager-failed-cancel-detach'
  const siblingOwner = 'manager-failed-cancel-detach-sibling'
  const abortReached = deferred()
  const releaseAbort = deferred()
  const runtime = await forkTool.createRuntimeWithAbort(directory, [...ownerDescriptor(owner), ...ownerDescriptor(siblingOwner)], async () => {
    abortReached.resolve()
    await releaseAbort.promise
    return { ok: false, error: 'EXACT-PHYSICAL-ABORT-REJECTED' }
  })
  let pending = Promise.resolve([])
  try {
    await forkTool.injectAcceptedAssessment(runtime, owner)
    forkTool.acceptNextPrompt(runtime)
    await forkTool.executeManagerResume(runtime, toolModule, owner, '', 'devops', 'FIXED-DETACH-ON-FAILURE')
    const deliver = await forkTool.prepareTerminalDelivery(runtime, owner, 'DETACHED-MUST-NOT-PUBLISH', 'failed-detach-provider')
    await forkTool.injectAcceptedAssessment(runtime, siblingOwner)
    forkTool.acceptNextPrompt(runtime)
    await forkTool.executeManagerResume(runtime, toolModule, siblingOwner, '', 'devops', 'SECOND-OWNED-FIXED-CALLBACK')
    const siblingDeliver = await forkTool.prepareTerminalDelivery(runtime, siblingOwner, 'SECOND-DETACHED-MUST-NOT-PUBLISH', 'sibling-detach-provider')
    assert.equal(forkTool.terminalListenerCount(runtime), 2, 'two actual road owners hold independent fixed subscriptions')
    forkTool.acceptNextPrompt(runtime)
    await forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', 'Ada', 'FAILED-PHYSICAL-ABORT')
    const cancellation = forkTool.cancelOwnerChildren(runtime, owner)
    const cancelResult = Promise.allSettled([cancellation])
    await abortReached.promise
    const detachment = forkTool.detachToolRuntime(runtime)
    pending = Promise.allSettled([cancellation, detachment])
    releaseAbort.resolve()
    const results = await pending
    assert.equal((await cancelResult)[0].status, 'rejected')
    assert.ok(results.every(result => result.status === 'rejected'))
    assert.match(String(results[0].reason), /EXACT-PHYSICAL-ABORT-REJECTED/)
    assert.equal(results[1].reason, results[0].reason, 'detach propagates the original cancellation failure')
    const snapshot = forkTool.workSnapshot(runtime, owner)
    const siblingSnapshot = forkTool.workSnapshot(runtime, siblingOwner)
    assert.equal(snapshot.find(work => work.byname === 'devops').lifecycle, 'Active')
    assert.equal(snapshot.find(work => work.byname === 'Ada').lifecycle, 'Abandoned')
    assert.equal(forkTool.terminalListenerCount(runtime), 0, 'detach releases original fixed subscriptions even when physical cancellation rejects')
    await deliver()
    await siblingDeliver()
    assert.deepEqual(forkTool.workSnapshot(runtime, owner), snapshot)
    assert.deepEqual(forkTool.workSnapshot(runtime, siblingOwner), siblingSnapshot)
  } finally {
    releaseAbort.resolve()
    await pending
    await Promise.allSettled([forkTool.detachToolRuntime(runtime)])
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[delegation-003] a synchronous cancellation signal failure releases the episode for a later real cancellation', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-mgr-sync-cancel-failure-'))
  const owner = 'manager-sync-cancel-failure'
  const originalError = new Error('EXACT-SYNCHRONOUS-CANCEL-SIGNAL-FAILURE')
  const signalCalls = []
  const runtime = await forkTool.createRuntimeWithCancelSignals(directory, ownerDescriptor(owner), sessions => {
    signalCalls.push(sessions)
    if (signalCalls.length === 1) throw originalError
  })
  try {
    assert.match(await forkTool.executeHorizon(runtime, owner), /devops/)
    forkTool.acceptNextPrompt(runtime)
    await forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', 'Ada', 'RETRY-CANCELLATION-ONLY')
    const originalWork = forkTool.workSnapshot(runtime, owner)
    await assert.rejects(forkTool.cancelOwnerChildren(runtime, owner), error => error === originalError)
    assert.deepEqual(forkTool.workSnapshot(runtime, owner), originalWork, 'failed signal precedes durable abandonment')
    await forkTool.cancelOwnerChildren(runtime, owner)
    assert.equal(signalCalls.length, 2, 'the original rejected flight is no longer cached')
    assert.equal(forkTool.workSnapshot(runtime, owner)[0].lifecycle, 'Abandoned')
    assert.equal(forkTool.abortCount(runtime), 1)
    assert.equal(forkTool.durableLifecycleByname(runtime, owner, 'devops'), 'Active')
  } finally {
    await Promise.allSettled([forkTool.detachToolRuntime(runtime)])
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})
