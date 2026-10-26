import test from 'node:test'
process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'
import {
  agentIdOf,
  childTurn,
  forkCall,
  joinCall,
  observeTerminals,
  physicalUserMessageOf,
  resumeCall,
  workLifecycleOf,
  withRealFork,
} from './support/real-fork.mjs'

{
const { default: assert } = await import("node:assert/strict");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const fork = await import("../../../dist/Execution/Delegation/Fork/Surface.js");
const forkTool = await import("../../../dist/Execution/Delegation/Fork/OpenCode/ToolSurface.js");

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

// A join that must answer inside a bounded window. The window is never
// widened; its timer is always cleared, so a join that answered early never
// keeps the process alive waiting for the deadline.
const boundedJoin = async (runtime, owner, windowMs = 1500) => {
  let timer
  try {
    const deadline = new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error('JOIN_HANG_DETECTED')), windowMs)
    })
    return await Promise.race([forkTool.executeJoin(runtime, owner), deadline])
  } finally {
    clearTimeout(timer)
  }
}

// The resume case runs on the real plugin wiring (support/real-fork.mjs): the
// fork harness's settle notifies a terminal without the child's delivery, and
// since D6 (60812f575) nothing settles without the durable witness that only a
// real this.deliver writes, so the harness case hung. The receipt-only send is
// the Host taking the prompt on the transport while no physical user message
// lands (runtime.nextPromptReceiptOnly).

test('WHAT[delegation-026] RESUME_synchronous_admission_with_async_work_and_join_isolation', async () => {
  const owner = 'manager-resume-sync-admit'
  await withRealFork(owner, async ({ hooks, createdIds, runtime }) => {
    const terminals = await observeTerminals(runtime)
    try {
      // 1. Initial fork, delivered and joined.
      const first = await forkCall(hooks, owner, 'Ada', 'INITIAL-CHARGE')
      assert.match(first, /ok = true/)
      assert.match(first, /Ada/)
      const child = createdIds[0]
      const agentId = agentIdOf(first)
      const firstRoot = physicalUserMessageOf(runtime, child).id
      await childTurn(hooks, runtime, child, `run-${child}-1`, 'FIRST PROSE', 'FIRST-ANSWER', terminals.awaitRun)
      const firstJoined = await joinCall(hooks, owner, 'call-join-first')
      assert.match(firstJoined, /FIRST-ANSWER/)

      // 2. A resume whose send has a transport receipt but no physical
      //    acceptance must not report placement and must not leave a run.
      const sentBefore = runtime.prompts.length
      const previousTimeout = process.env.WANXIANGSHU_ADMISSION_TIMEOUT_MS
      process.env.WANXIANGSHU_ADMISSION_TIMEOUT_MS = '100'
      let unconfirmedResume
      try {
        runtime.nextPromptReceiptOnly()
        unconfirmedResume = await resumeCall(hooks, owner, 'Ada', 'UNCONFIRMED-CHARGE', 'call-resume-unconfirmed')
      } finally {
        if (previousTimeout === undefined) delete process.env.WANXIANGSHU_ADMISSION_TIMEOUT_MS
        else process.env.WANXIANGSHU_ADMISSION_TIMEOUT_MS = previousTimeout
      }
      assert.match(unconfirmedResume, /uncertain|不确定|may already have been accepted|可能已/i)
      assert.doesNotMatch(unconfirmedResume, /ok = true/)
      assert.doesNotMatch(unconfirmedResume, /carries this charge now|现已接下这项托付/i)
      assert.equal(runtime.prompts.length, sentBefore + 1, 'the Host send was attempted exactly once')
      assert.equal(physicalUserMessageOf(runtime, child).id, firstRoot, 'no physical input landed for the unconfirmed resume')

      // 3. Join sees nothing to join: the unconfirmed dispatch installed no pending run.
      const joinResult = await joinCall(hooks, owner, 'call-join-unconfirmed')
      assert.match(joinResult, /There is nothing away to receive/)

      // 4. A confirmed resume is accepted and places new work on the same child.
      const second = await resumeCall(hooks, owner, 'Ada', 'CONFIRMED-CHARGE', 'call-resume-confirmed')
      assert.match(second, /ok = true/)
      assert.match(second, /Ada/)
      assert.doesNotMatch(second, /uncertain/i)
      assert.equal(agentIdOf(second), agentId)
      assert.equal(createdIds.length, 1, 'the resume reuses the same physical child')
      const secondRoot = physicalUserMessageOf(runtime, child).id
      assert.notEqual(secondRoot, firstRoot, 'the confirmed resume roots a new physical input')
      assert.equal(workLifecycleOf(runtime, owner, agentId, child, secondRoot), 'Active')

      // 5. The new work's own delivery settles it, and the join returns it.
      await childTurn(hooks, runtime, child, `run-${child}-2`, 'SECOND PROSE', 'SECOND-ANSWER', terminals.awaitRun)
      assert.equal(workLifecycleOf(runtime, owner, agentId, child, secondRoot), 'CompletedAwaitingJoin')
      const secondJoined = await joinCall(hooks, owner, 'call-join-second')
      assert.match(secondJoined, /SECOND-ANSWER/)
    } finally {
      terminals.dispose()
    }
  })
})
test('WHAT[delegation-026] reused child completes after busy guidance and a fresh assignment', async () => {
  const owner = 'manager-resume-after-guidance'
  await withRealFork(owner, async ({ hooks, createdIds, runtime }) => {
    const terminals = await observeTerminals(runtime)
    try {
      const first = await forkCall(hooks, owner, 'Ada', 'INITIAL-CHARGE')
      const child = createdIds[0]
      const agentId = agentIdOf(first)
      await resumeCall(hooks, owner, 'Ada', 'GUIDANCE-WHILE-ACTIVE')
      await childTurn(hooks, runtime, child, `run-${child}-1`, 'GUIDED ANSWER', null, terminals.awaitRun)
      assert.match(await joinCall(hooks, owner), /GUIDED ANSWER/)

      const send = runtime.client.session.promptAsync
      let root
      let overlap = true
      runtime.client.session.promptAsync = async (args) => {
        const result = await send(args)
        if (overlap) {
          overlap = false
          root = physicalUserMessageOf(runtime, child).id
          await resumeCall(hooks, owner, 'Ada', 'GUIDANCE-BEFORE-SEND-RETURNS')
        }
        return result
      }
      await resumeCall(hooks, owner, 'Ada', 'FRESH-CHARGE')
      await childTurn(hooks, runtime, child, `run-${child}-2`, 'FRESH ANSWER', null, terminals.awaitRun)
      assert.equal(workLifecycleOf(runtime, owner, agentId, child, root), 'CompletedAwaitingJoin')
      assert.match(await joinCall(hooks, owner), /FRESH ANSWER/)
    } finally {
      terminals.dispose()
    }
  })
})
test('WHAT[delegation-021] queued guidance cannot restart completed work or interrupt a fresh assignment', async () => {
  const owner = 'manager-late-guidance'
  await withRealFork(owner, async ({ hooks, createdIds, runtime }) => {
    const terminals = await observeTerminals(runtime)
    try {
      const receipt = await forkCall(hooks, owner, 'Ada', 'INITIAL-CHARGE')
      const child = createdIds[0]
      const agentId = agentIdOf(receipt)
      const root = physicalUserMessageOf(runtime, child).id
      const event = hooks.event
      hooks.event = async (args) => {
        if (args.event.type === 'message.updated') {
          await resumeCall(hooks, owner, 'Ada', 'QUEUED-ASYNC-GUIDANCE')
          await runtime.awaitIngress()
        }
        return event(args)
      }
      await childTurn(hooks, runtime, child, `run-${child}-1`, 'FINISHED ANSWER', null, terminals.awaitRun)
      hooks.event = event
      assert.equal(workLifecycleOf(runtime, owner, agentId, child, root), 'CompletedAwaitingJoin')
      assert.match(await joinCall(hooks, owner), /FINISHED ANSWER/)

      const history = await runtime.client.session.messages({ path: { id: child } })
      const output = { messages: history.data.map(message => ({
        ...message, info: { ...(message.info ?? message), sessionID: child },
      })) }
      const aborts = runtime.abortedIds.length
      await hooks['experimental.chat.messages.transform']({ sessionID: child }, output)
      assert.deepEqual(output.messages, [], 'the completed work must not reach the provider again')
      assert.deepEqual(runtime.abortedIds.slice(aborts), [child], 'interrupt the stale continuation attempt')
      const retained = await runtime.client.session.messages({ path: { id: child } })
      assert.ok(retained.data.some(message => message.parts.some(part => /QUEUED-ASYNC-GUIDANCE/.test(part.text ?? ''))),
        'the suppression changes only the request, not the stored guidance')

      await resumeCall(hooks, owner, 'Ada', 'FRESH-CHARGE')
      const beforeFresh = runtime.abortedIds.length
      const transform = hooks['experimental.chat.messages.transform']
      hooks['experimental.chat.messages.transform'] = async (input, request) => {
        await transform(input, request)
        assert.match(JSON.stringify(request.messages), /QUEUED-ASYNC-GUIDANCE/,
          'the new task can still use guidance retained in the conversation')
      }
      await childTurn(hooks, runtime, child, `run-${child}-2`, 'FRESH ANSWER', null, terminals.awaitRun)
      assert.equal(runtime.abortedIds.length, beforeFresh, 'do not interrupt the new assignment')
      assert.match(await joinCall(hooks, owner), /FRESH ANSWER/)
    } finally {
      terminals.dispose()
    }
  })
})
test('WHAT[delegation-026] FORK_TOOL_acceptance_unknown_never_claims_charge_was_not_placed', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-fork-unknown-'))
  const owner = 'manager-unknown'
  const runtime = await forkTool.createRuntime(directory, ownerDescriptor(owner))

  try {
    forkTool.nextPromptAcceptanceUnknown(runtime, 'connection closed after request write')

    const result = await forkTool.executeManagerFork(
      runtime,
      toolModule,
      owner,
      'engineer',
      'Ada',
      'UNCERTAIN-FORK-CHARGE',
    )

    assert.doesNotMatch(result, /could not be placed|无法托付/i)
    assert.match(result, /uncertain|不确定/i)
    assert.match(result, /may already have been accepted|可能已/i)
    assert.equal(forkTool.childCount(runtime), 1)
    assert.equal(forkTool.promptCount(runtime), 1, 'physical Host send was attempted exactly once')
    assert.equal(forkTool.durableLifecycleByname(runtime, owner, 'Ada'), 'Active')

    // An uncertain dispatch must leave no ghost run behind: join closes inside
    // the bounded window instead of waiting on a run that was never confirmed.
    const joinResult = await boundedJoin(runtime, owner)
    assert.match(joinResult, /NothingToJoin|无可等待|没有|nothing away to receive/i)
  } finally {
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})
test('WHAT[delegation-026] FORK_TOOL_unconfirmed_dispatch_reports_uncertain_and_never_leaves_ghost_run_for_join', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-fork-unconfirmed-'))
  const owner = 'manager-unconfirmed'
  const runtime = await forkTool.createRuntime(directory, ownerDescriptor(owner))

  try {
    forkTool.nextPromptAdmittedWithReceipt(runtime, 'accepted-without-message-id')

    const result = await forkTool.executeManagerFork(
      runtime,
      toolModule,
      owner,
      'engineer',
      'Ada',
      'RECEIPT-PENDING-FORK-CHARGE',
    )

    // Unconfirmed dispatch (transport receipt without physical acceptance) must report uncertain,
    // never claim placement success (carries this charge now)
    assert.match(result, /uncertain|不确定|may already have been accepted|可能已/i)
    assert.doesNotMatch(result, /carries this charge now|现已接下这项托付/i)

    // Join must NOT hang waiting on an unconfirmed dispatch; it must report NothingToJoin / no active runs
    const joinResult = await boundedJoin(runtime, owner)
    assert.match(joinResult, /NothingToJoin|无可等待|没有|nothing away to receive/i)

    assert.equal(forkTool.promptCount(runtime), 1)
  } finally {
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})
test('WHAT[delegation-026] IDLE_ROAD_DEVOPS_COMPANION_does_not_block_join_with_hang', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-idle-devops-'))
  const owner = 'manager-idle-devops'
  const runtime = await forkTool.createRuntime(directory, ownerDescriptor(owner))

  try {
    // 1. Manager calls horizon which automatically binds the companion devops session
    const horizonResult = await forkTool.executeHorizon(runtime, owner)
    assert.match(horizonResult, /devops/)
    assert.equal(forkTool.durableLifecycleByname(runtime, owner, 'devops'), 'Active')
    assert.deepEqual(forkTool.workSnapshot(runtime, owner), [])
    assert.equal(forkTool.promptCount(runtime), 0)

    // 2. devops is idle (never received any prompt or active run)
    // Join must NOT hang waiting on the idle devops handle; it must report NothingToJoin immediately
    const joinResult = await boundedJoin(runtime, owner)
    assert.match(joinResult, /NothingToJoin|无可等待|没有|nothing away to receive/i)
  } finally {
    forkTool.disposeRuntime(runtime)
    rmSync(directory, { recursive: true, force: true })
  }
})
test('WHAT[delegation-026] RESTART_ADOPTED_IDLE_DEVOPS_does_not_block_join_with_hang', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-restart-idle-devops-'))
  const owner = 'manager-restart-idle-devops'
  const first = await forkTool.createRuntime(directory, ownerDescriptor(owner))

  try {
    // 1. First process binds the durable devops handle (Active in the journal).
    const horizon1 = await forkTool.executeHorizon(first, owner)
    assert.match(horizon1, /devops/)
    assert.equal(forkTool.durableLifecycleByname(first, owner, 'devops'), 'Active')

    // 2. Simulate an OS restart: dispose and reopen the same durable journal.
    forkTool.disposeRuntime(first)
    const restarted = await forkTool.createRuntime(directory, ownerDescriptor(owner))

    try {
      // 3. Horizon on the fresh runtime re-adopts the durable Active devops handle
      //    (syncAdoptDevOps → AdoptChild: identity registration only). devops stays
      //    idle: no prompt, no pending host run, nothing joinable in the journal.
      const horizon2 = await forkTool.executeHorizon(restarted, owner)
      assert.match(horizon2, /devops/)
      assert.equal(forkTool.durableLifecycleByname(restarted, owner, 'devops'), 'Active')
      assert.deepEqual(forkTool.workSnapshot(restarted, owner), [])
      assert.equal(forkTool.promptCount(restarted), 0)

      // 4. Join must NOT hang on the restored-but-idle devops handle.
      const joinResult = await boundedJoin(restarted, owner)
      assert.match(joinResult, /NothingToJoin|无可等待|没有|nothing away to receive/i)
    } finally {
      forkTool.disposeRuntime(restarted)
    }
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})
test('WHAT[delegation-026] RESTART_PENDING_CHILD_WORK_is_voided_at_load_and_join_reports_it_once_without_hang', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-restart-pending-'))
  const owner = 'manager-restart-pending'

  try {
    const first = await forkTool.createRuntime(directory, ownerDescriptor(owner))

    try {
      const pending = forkTool.executeManagerFork(first, toolModule, owner, 'engineer', 'Ada', 'PENDING-CHARGE')
      await waitForPromptCount(first, 1)
      assert.equal(forkTool.acceptPrompt(first, 0), true)
      await pending
      assert.equal(forkTool.durableLifecycleByname(first, owner, 'Ada'), 'Active')
    } finally {
      forkTool.disposeRuntime(first)
    }

    // Restart: the orphaned child work can no longer be delivered, so load
    // settlement voids it. A void is not a completion, but it settles the
    // parent's outstanding obligation: it stays visible until it is reported
    // once, and the handle retires after that report.
    const restarted = await forkTool.createRuntime(directory, ownerDescriptor(owner))

    try {
      // 1. The voided work is visible in the horizon as voided, not as still-away.
      const horizon = await forkTool.executeHorizon(restarted, owner)
      assert.match(horizon, /Ada/)
      assert.match(horizon, /was voided: this charge can no longer return, so it is settled rather than completed\.|已被作废：此项任务已无法返回，因此以作废结清，而非完成。/)
      assert.match(horizon, /voided|作废/i)

      const horizonReceipt = await forkTool.executeHorizonReceipt(restarted, owner)
      assert.equal(horizonReceipt.ok, true)
      const voidedRow = horizonReceipt.agents.find((row) => row.label === 'Ada')
      assert.ok(voidedRow, 'the voided work keeps a row in the horizon roster')
      assert.equal(voidedRow.status, 'voided')

      // 2. Join reports the void once — it must resolve, never hang.
      const joinPromise = forkTool.executeJoin(restarted, owner)
      const timeout = new Promise((_, reject) => setTimeout(() => reject(new Error('JOIN_HANG_DETECTED')), 15000))
      const joinResult = await Promise.race([joinPromise, timeout])
      assert.match(joinResult, /Ada/)
      assert.match(joinResult, /was voided|已作废|已撤销/)
      assert.doesNotMatch(joinResult, /\[\[data\.records\]\]/)


      // 3. The second join has nothing left to receive: the report happened once.
      const secondJoinPromise = forkTool.executeJoin(restarted, owner)
      const secondTimeout = new Promise((_, reject) => setTimeout(() => reject(new Error('JOIN_HANG_DETECTED')), 15000))
      const secondJoin = await Promise.race([secondJoinPromise, secondTimeout])
      assert.match(secondJoin, /NothingToJoin|无可等待|没有|nothing away to receive/i)

      // 4. Reported work leaves the roster.
      assert.doesNotMatch(await forkTool.executeHorizon(restarted, owner), /Ada/)

      // 5. The handle retires after the single report.
      assert.equal(forkTool.durableLifecycleByname(restarted, owner, 'Ada'), 'Retired')
    } finally {
      forkTool.disposeRuntime(restarted)
    }

    // 6. A further process on the same directory does not report the void again.
    const third = await forkTool.createRuntime(directory, ownerDescriptor(owner))

    try {
      const thirdJoinPromise = forkTool.executeJoin(third, owner)
      const thirdTimeout = new Promise((_, reject) => setTimeout(() => reject(new Error('JOIN_HANG_DETECTED')), 1500))
      const thirdJoin = await Promise.race([thirdJoinPromise, thirdTimeout])
      assert.match(thirdJoin, /NothingToJoin|无可等待|没有|nothing away to receive/i)
      assert.doesNotMatch(await forkTool.executeHorizon(third, owner), /Ada/)
    } finally {
      forkTool.disposeRuntime(third)
    }
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})
}

test.todo('WHAT[delegation-026] actual admission and checkpoint fault cuts preserve exact durable claim and effect truth without replay or durable program counters (GAP-153)')
