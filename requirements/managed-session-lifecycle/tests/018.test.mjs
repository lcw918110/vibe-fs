import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { setImmediate } from 'node:timers/promises'
import * as forkTool from '../../../dist/Execution/Delegation/Fork/OpenCode/ToolSurface.js'
import * as status from '../../../dist/Execution/Session/ChatExecution/StatusSurface.js'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import { withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

const schemaNode = (kind, extra = {}) => ({
  kind, ...extra,
  describe: () => schemaNode(`${kind}-described`, extra),
  optional: () => schemaNode(`${kind}-optional`, extra),
  int: () => schemaNode(`${kind}-int`, extra),
  nonnegative: () => schemaNode(`${kind}-nonnegative`, extra),
})
const toolModule = {
  tool: { schema: {
    string: () => schemaNode('string'), number: () => schemaNode('number'),
    enum: (values) => schemaNode('enum', { values }),
    array: (inner) => schemaNode('array', { inner }),
  } },
}

const journalBytes = directory => {
  const events = join(directory, 'wanxiangshu', 'events')
  const names = readdirSync(events).filter(name => name.endsWith('.ndjson')).sort()
  assert.ok(names.length > 0, 'the original Fork journal has event files')
  return names.map(name => ({ name, bytes: readFileSync(join(events, name)) }))
}

test('WHAT[managed-session-lifecycle-018] the original captured Fork sender rejects after runtime detach without claiming or sending', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-fork-captured-closed-'))
  const owner = 'manager-captured-closed'
  const runtime = await forkTool.createRuntime(directory, [{ sessionId: owner, agent: 'manager' }])
  const seen = []
  try {
    const placed = forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', 'Ada', 'PRESERVE-THIS-ACTIVE-HANDLE')
    await forkTool.awaitPromptCount(runtime, 1)
    assert.equal(forkTool.acceptPrompt(runtime, 0), true)
    assert.match(await placed, /Ada/)
    assert.equal(forkTool.durableLifecycleByname(runtime, owner, 'Ada'), 'Active')

    const captured = forkTool.captureChildPromptSender(runtime, owner, 'Ada', physical => seen.push(physical))
    const openText = 'Actual captured sender open-owner Unknown probe'
    forkTool.nextPromptAcceptanceUnknown(runtime, 'the actual Host cannot prove this open probe landed')
    const open = await forkTool.sendCapturedChildPrompt(captured, openText)
    assert.equal(open.kind, 'AcceptanceUncertain', open.reason)
    assert.equal(forkTool.promptCount(runtime), 2, 'the same captured sender reaches the actual Host while its Runtime is open')
    const evidence = forkTool.promptEvidence(runtime, 1)
    assert.equal(evidence.text, openText)
    assert.equal(evidence.sessionId, forkTool.child(runtime))
    assert.equal(evidence.agent, 'engineer')
    assert.ok(evidence.promptKey.length > 0)
    assert.deepEqual(forkTool.physicalAcceptanceObservation(runtime, 1, 'unproven-open-probe-physical'), {
      pending: true, landedPhysical: null, managedAccepted: false,
    })

    const bytes = journalBytes(directory)
    const work = forkTool.workSnapshot(runtime, owner)
    await forkTool.detachToolRuntime(runtime)
    assert.deepEqual(journalBytes(directory), bytes, 'local detach appends no durable cancellation or prompt fact')
    assert.deepEqual(forkTool.workSnapshot(runtime, owner), work)

    forkTool.nextPromptAcceptanceUnknown(runtime, 'a closed Runtime must never consume this Host outcome')
    const closed = await forkTool.sendCapturedChildPrompt(captured, 'A fresh prompt after the captured Runtime was closed')
    assert.equal(closed.kind, 'Rejected', closed.reason)
    assert.match(closed.reason, /Fork runtime observers are closed/)
    assert.equal(forkTool.promptCount(runtime), 2, 'the original inner Runtime rejects before a new physical send')
    assert.deepEqual(journalBytes(directory), bytes, 'the closed actual sender must not persist a fresh claim')
    assert.deepEqual(forkTool.workSnapshot(runtime, owner), work)
    assert.equal(forkTool.durableLifecycleByname(runtime, owner, 'Ada'), 'Active')
    assert.equal(forkTool.abortCount(runtime), 0)
    assert.deepEqual(seen, [])
  } finally {
    await forkTool.detachToolRuntime(runtime)
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[managed-session-lifecycle-018] detaching a pending Fork observer preserves its exact claim and unrelated physical waiter', async t => {
  for (const detach of [false, true]) {
    const label = detach ? 'detached' : 'open'
    const directory = mkdtempSync(join(tmpdir(), `wxs-fork-pending-${label}-`))
    const owner = `manager-pending-${label}`
    const runtime = await forkTool.createRuntime(directory, [{ sessionId: owner, agent: 'manager' }])
    const seen = []
    let other
    let mocked = false
    try {
      forkTool.nextPromptAcceptanceUnknown(runtime, 'first native Host outcome remains honestly unknown')
      const first = forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', 'Ada', 'CREATE-A-REAL-CHILD-WITHOUT-AN-ACCEPTED-ROOT')
      await forkTool.awaitPromptCount(runtime, 1)
      assert.match(await first, /Ada/)

      const captured = forkTool.captureChildPromptSender(runtime, owner, 'Ada', physical => seen.push(physical))
      const text = `Actual ${label} captured Fork owner pending dispatch`
      forkTool.nextPromptAcceptanceUnknown(runtime, 'this second independent native Host request is also unknown')
      const sent = await forkTool.sendCapturedChildPrompt(captured, text)
      assert.equal(sent.kind, 'AcceptanceUncertain', sent.reason)
      assert.equal(forkTool.promptCount(runtime), 2)
      const evidence = forkTool.promptEvidence(runtime, 1)
      assert.equal(evidence.text, text)
      assert.equal(evidence.agent, 'engineer')
      assert.equal(evidence.sessionId, forkTool.child(runtime))
      assert.notEqual(evidence.promptKey, forkTool.promptEvidence(runtime, 0).promptKey,
        'the observation uses this actual independent Host key, not the original Unknown request')
      const physical = `managed-fork-pending-${label}-physical`
      assert.deepEqual(forkTool.physicalAcceptanceObservation(runtime, 0, physical), {
        pending: true, landedPhysical: null, managedAccepted: false,
      })
      assert.deepEqual(forkTool.physicalAcceptanceObservation(runtime, 1, physical), {
        pending: true, landedPhysical: null, managedAccepted: false,
      })

      t.mock.timers.enable({ apis: ['setTimeout'] })
      mocked = true
      other = dispatch.awaitPhysicalConfirmation(evidence.promptKey, 50)
      const bytes = journalBytes(directory)
      const work = forkTool.workSnapshot(runtime, owner)
      if (detach) await forkTool.detachToolRuntime(runtime)
      assert.deepEqual(journalBytes(directory), bytes, 'closing local observers does not change either honest Unknown claim')
      assert.deepEqual(forkTool.workSnapshot(runtime, owner), work)
      assert.deepEqual(forkTool.physicalAcceptanceObservation(runtime, 0, physical), {
        pending: true, landedPhysical: null, managedAccepted: false,
      }, 'the original Unknown A claim is intact after local detach and before B managed ingress')
      assert.deepEqual(forkTool.physicalAcceptanceObservation(runtime, 1, physical), {
        pending: true, landedPhysical: null, managedAccepted: false,
      })
      assert.deepEqual(seen, [])
      assert.equal(forkTool.abortCount(runtime), 0)

      const accepted = await forkTool.confirmPromptPhysical(runtime, 1, physical)
      assert.deepEqual(accepted, {
        ok: true, error: null, sessionId: evidence.sessionId, physicalUserMessageId: physical,
      })
      t.mock.timers.tick(50)
      assert.deepEqual(await other, { kind: 'Accepted', physical, reason: null },
        'actual managed ingress settles the independently owned waiter with exact physical identity')
      assert.deepEqual(forkTool.physicalAcceptanceObservation(runtime, 1, physical), {
        pending: false, landedPhysical: physical, managedAccepted: true,
      })
      assert.deepEqual(seen, detach ? [] : [physical],
        'the same original Runtime notifies while open and releases only its own observer on detach')
      assert.equal(forkTool.promptCount(runtime), 2, 'actual ingress performs no additional Host send')
      assert.equal(forkTool.abortCount(runtime), 0)
      assert.equal(forkTool.physicalAcceptanceObservation(runtime, 0, physical).pending, true,
        'the unrelated first Unknown claim is not abandoned or rewritten for fixture cleanup')
    } finally {
      if (mocked) t.mock.timers.tick(50)
      if (other) await other
      if (mocked) t.mock.timers.reset()
      await forkTool.detachToolRuntime(runtime)
      forkTool.disposeRuntime(runtime)
      rmSync(directory, { recursive: true, force: true })
    }
  }
})

test('WHAT[managed-session-lifecycle-018] actual tool runtime detach preserves durable Active child without physical abort', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-fork-process-detach-'))
  const owner = 'manager-process-detach'
  const runtime = await forkTool.createRuntime(directory, [{ sessionId: owner, agent: 'manager' }])
  try {
    const placed = forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', 'Ada', 'SURVIVE-PLUGIN-RELOAD')
    await forkTool.awaitPromptCount(runtime, 1)
    assert.equal(forkTool.acceptPrompt(runtime, 0), true)
    assert.match(await placed, /Ada/)
    assert.equal(forkTool.durableLifecycleByname(runtime, owner, 'Ada'), 'Active')
    await forkTool.detachToolRuntime(runtime)
    assert.equal(forkTool.durableLifecycleByname(runtime, owner, 'Ada'), 'Active')
    assert.equal(forkTool.abortCount(runtime), 0)
    assert.equal(forkTool.childCount(runtime), 1)
  } finally {
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[managed-session-lifecycle-018] real TurnAborted, retry, Fission and unknown stops preserve durable handles and permit re-enlist after process restart (GAP-133)', async () => {
  const scenarios = [
    { label: 'turn-aborted', expected: 'Active',
      emit: (runtime, owner, root) => forkTool.emitStopForRoot(runtime, owner, root, 'Aborted') },
    { label: 'provider-retry', expected: 'Active',
      emit: (runtime, owner, root) => forkTool.emitStopWithReason(runtime, owner, root, 'Failed', 'MISSING_FINAL_REPORT: bounded retry continues') },
    { label: 'fission-external-abort', expected: 'CompletedAwaitingJoin',
      emit: (runtime, owner) => forkTool.emitStopWithReason(runtime, owner, '', 'Failed', 'fission external abort') },
    { label: 'unknown-stop', expected: 'Active',
      emit: (runtime, owner, root) => forkTool.emitStopForRoot(runtime, owner, 'a-root-this-run-never-accepted', 'Failed') },
  ]

  for (const scenario of scenarios) {
    const owner = `manager-stop-${scenario.label}`
    const directory = mkdtempSync(join(tmpdir(), `wxs-stop-preserve-${scenario.label}-`))
    let first
    let durableAtExit

    const a = await forkTool.createRuntime(directory, [{ sessionId: owner, agent: 'manager' }])
    try {
      const invocation = forkTool.executeManagerFork(a, toolModule, owner, 'engineer', 'Ada', `CHARGE-${scenario.label}`)
      await forkTool.awaitPromptCount(a, 1)
      assert.equal(forkTool.acceptPrompt(a, 0), true)
      assert.match(await invocation, /Ada/)
      first = forkTool.workSnapshot(a, owner).find(work => work.lifecycle === 'Active')
      assert.equal(forkTool.durableLifecycleByname(a, owner, 'Ada'), 'Active')

      await scenario.emit(a, owner, first.root)

      const afterStop = forkTool.workSnapshot(a, owner)
      const stopped = afterStop.find(work => work.root === first.root)
      assert.equal(stopped.lifecycle, scenario.expected,
        `${scenario.label}: the stop settles per its real semantics, never as an unauthorized abandon`)
      assert.notEqual(stopped.lifecycle, 'Abandoned')
      assert.notEqual(stopped.lifecycle, 'Retired')
      assert.equal(forkTool.abortCount(a), 0,
        `${scenario.label}: a non-authorized stop must not physically abort the child session`)
      durableAtExit = afterStop
    } finally {
      forkTool.disposeRuntime(a)
    }

    try {
      assert.deepEqual(await forkTool.coldWorkSnapshot(directory, owner), durableAtExit,
        `${scenario.label}: a cold replay of the journal folds the exact same durable handle facts`)

      const b = await forkTool.createRuntime(directory, [{ sessionId: owner, agent: 'manager' }])
      try {
        const reEnlist = forkTool.executeManagerResume(b, toolModule, owner, '', 'Ada', `RE-ENLIST-${scenario.label}`)
        await forkTool.awaitPromptCount(b, 1)
        assert.equal(forkTool.acceptPrompt(b, 0), true)
        assert.match(await reEnlist, /Ada/)

        assert.equal(forkTool.childCount(b), 0,
          `${scenario.label}: re-enlist adopts the durable binding instead of forking a new child`)
        assert.equal(forkTool.child(b), first.child)
        const works = forkTool.workSnapshot(b, owner)
        const next = works.find(work => work.lifecycle === 'Active')
        assert.equal(next.handle, first.handle)
        assert.equal(next.child, first.child)
        assert.notEqual(next.root, first.root)
        assert.equal(next.lifecycle, 'Active')
      } finally {
        forkTool.disposeRuntime(b)
      }
    } finally {
      rmSync(directory, { recursive: true, force: true })
    }
  }
})
test('WHAT[managed-session-lifecycle-018] authorized owner cancellation durably abandons only its child and awaits the held Host abort', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-fork-logical-cancel-'))
  const entered = Promise.withResolvers()
  const release = Promise.withResolvers()
  const aborts = []
  const owners = ['manager-cancel', 'manager-preserved']
  let runtime
  let cancellation
  try {
    runtime = await forkTool.createRuntimeWithAbort(directory,
      owners.map(sessionId => ({ sessionId, agent: 'manager' })),
      async sessionId => {
        aborts.push(sessionId)
        entered.resolve()
        await release.promise
        return { ok: true }
      })
    const children = []
    for (const owner of owners) {
      forkTool.acceptNextPrompt(runtime)
      assert.match(await forkTool.executeManagerFork(runtime, toolModule, owner, 'engineer', 'Ada', 'REVIEW-ONE-CHANGE'), /Ada/)
      children.push(forkTool.child(runtime))
      assert.equal(forkTool.durableLifecycleByname(runtime, owner, 'Ada'), 'Active')
    }
    let completed = false
    cancellation = forkTool.cancelOwnerChildren(runtime, owners[0])
    cancellation.then(() => { completed = true }, () => { completed = true })
    await entered.promise
    await setImmediate()
    assert.equal(completed, false, 'logical cancellation must retain the pending Host cleanup')
    assert.deepEqual(aborts, [children[0]])
    assert.equal(forkTool.durableLifecycleByname(runtime, owners[0], 'Ada'), 'Abandoned')
    assert.equal(forkTool.durableLifecycleByname(runtime, owners[1], 'Ada'), 'Active')

    release.resolve()
    await cancellation
    assert.equal(completed, true)
    assert.deepEqual(aborts, [children[0]])
    assert.equal(forkTool.durableLifecycleByname(runtime, owners[0], 'Ada'), 'Abandoned')
    assert.equal(forkTool.durableLifecycleByname(runtime, owners[1], 'Ada'), 'Active')
  } finally {
    release.resolve()
    try {
      if (cancellation) await cancellation
    } finally {
      try {
        if (runtime) await forkTool.detachToolRuntime(runtime)
      } finally {
        try {
          if (runtime) forkTool.disposeRuntime(runtime)
        } finally {
          rmSync(directory, { recursive: true, force: true })
        }
      }
    }
  }
})

test.todo('WHAT[managed-session-lifecycle-018] plugin shutdown waits for already admitted provider transforms and terminal callbacks without logical cancellation (GAP-133)')

test('WHAT[managed-session-lifecycle-018] actual plugin dispose detaches capacity without cancelling durable Accepted executions', async () => {
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const sessionId = 'ses-dispose-accepted'
    const physicalUserMessageId = 'msg-dispose-accepted'
    await hooks['chat.message']({ sessionID: sessionId, messageID: physicalUserMessageId, agent: 'engineer' }, {
      message: { id: physicalUserMessageId, sessionID: sessionId, role: 'user', agent: 'engineer', model: { providerID: 'host', modelID: 'placeholder' } },
      parts: [],
    })
    const before = status.query(runtime.journal, sessionId, physicalUserMessageId)
    assert.deepEqual(before, { accepted: true, providerStarted: false, terminal: false, disposition: null })
    assert.equal(routing.sharedCapacitySnapshot().executions.some((execution) => execution.sessionId === sessionId), true)

    await hooks.dispose()

    assert.deepEqual(status.query(runtime.journal, sessionId, physicalUserMessageId), before)
    const capacity = routing.sharedCapacitySnapshot()
    assert.equal(capacity.executions.some((execution) => execution.sessionId === sessionId), false)
    assert.equal(capacity.tokens.some((token) => token.owner.sessionId === sessionId), false)
    assert.equal(capacity.waiters.some((waiter) => waiter.sessionId === sessionId), false)
  })
})
