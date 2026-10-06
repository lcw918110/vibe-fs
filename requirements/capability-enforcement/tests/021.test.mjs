import assert from 'node:assert/strict'
import test, { afterEach } from 'node:test'
import fc from 'fast-check'
import { accessSync, chmodSync, mkdtempSync, rmSync, constants } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as blog from '../../../dist/Enforcer/BlogSurface.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as runtime from '../../../dist/Context/Companion/RuntimeSurface.js'
import * as turns from '../../../dist/Interaction/Repair/CompletedTurnSurface.js'
import * as resources from '../../../dist/Resources/PromptSurface.js'
import * as ownership from '../../verification-system/tests/support/blogger-ownership.mjs'

// Deliver completed verdicts between the synchronous journal operations.
afterEach(() => new Promise(resolve => setImmediate(resolve)))

// Match plugin initialization: the continuation decodes chronicle calls
// against the installed enforcer catalog.
resources.runtimeInstallFromPackage()

const NUDGE_KIND = 'blogger-missing-tool'

const AABB_KIND = 'blogger-aabb'

const freshCalls = () => ({
  sendPrompt: [],
  subscribe: [],
  subscribeFuture: [],
  rootRead: [],
  eventSubscribe: [],
  eventFuture: [],
  eventNotify: [],
})

const sessionPort = (calls) => ({
  SubscribeTerminal: (...args) => {
    calls.subscribe.push(args)
    return { Dispose: () => {} }
  },
  SubscribeFutureTerminal: (...args) => {
    calls.subscribeFuture.push(args)
    return { Dispose: () => {} }
  },
  SendPrompt: async (sessionId, text, options) => {
    calls.sendPrompt.push({ sessionId, text, options })
    return dispatch.admittedWithReceipt(`accepted-${calls.sendPrompt.length}`)
  },
})

const rootPort = (calls) => ({
  TryRead: () => {
    calls.rootRead.push([])
    return undefined
  },
})

const eventPort = (calls) => ({
  SubscribeTerminalListener: (...args) => {
    calls.eventSubscribe.push(args)
    return { Dispose: () => {} }
  },
  SubscribeFutureTerminalListener: (...args) => {
    calls.eventFuture.push(args)
    return { Dispose: () => {} }
  },
  NotifyTerminal: (...args) => {
    calls.eventNotify.push(args)
    return false
  },
})

let ownerCounter = 0

// The owner request is proven through real durable evidence
// (context-compression-024): its dispatch landed as `ids.physical` and is
// bound as the durable open request's PromptKey. Unproven terminals are never
// repaired.
const setupOwner = async (t) => {
  ownerCounter += 1
  const n = ownerCounter
  const ids = {
    main: `ses-main-repair-${n}`,
    blogger: `ses-blogger-repair-${n}`,
    request: `req-blog-${n}`,
    root: `msg-root-blog-${n}`,
    physical: `msg-phys-blog-${n}`,
  }
  const dir = mkdtempSync(join(tmpdir(), 'wxs-blogger-repair-'))
  const opened = await journal.JournalSurface_bootWithWriterId(
    dir,
    `writer-blog-${n}`,
    `rt-blog-${n}`,
    4242,
    '2026-01-01T00:00:00Z',
  )
  assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
  // BlogSurface.journalOf reads a structural `Journal` member or the raw
  // AgentJournal; the boot handle wraps the live AgentJournal in `.journal`.
  const durable = opened.journal.journal
  await ownership.linkBlogger(opened.journal, ids.main, ids.blogger)
  const profile = await ownership.rootBlogger(opened.journal, ids.blogger, ids.root)
  // Real process-local owner scope (also isolates the shared flight registry)
  // and the exact live flight for this request.
  const scope = runtime.createScope()
  const request = runtime.main({
    requestId: ids.request,
    mainSession: ids.main,
    bloggerSession: ids.blogger,
    toml: 'repair-toml',
  })
  assert.equal(runtime.claimCurrentRequest(scope, ids.blogger, request), 'Claimed')
  await ownership.ownRequest({
    handle: opened.journal,
    durable,
    scope,
    bloggerSession: ids.blogger,
    profile,
    request,
    physical: ids.physical,
  })
  t.after(() => {
    try {
      runtime.dispose(scope)
    } catch {}
    try {
      journal.JournalSurface_dispose(opened.journal)
    } catch {}
    rmSync(dir, { recursive: true, force: true })
  })
  const calls = freshCalls()
  const ports = {
    session: sessionPort(calls),
    root: rootPort(calls),
    events: eventPort(calls),
  }
  return {
    ids,
    durable,
    handle: opened.journal,
    scope,
    request,
    ports,
    calls,
    dir,
    writerFile: join(dir, 'wanxiangshu', 'events', `writer-blog-${n}.ndjson`),
  }
}

const idleObservation = (ports, ids, run, { quiescent = true } = {}) => ({
  quiescent,
  context: {
    sessionId: ids.blogger,
    physicalUserMessageId: ids.physical,
    authorityRoot: ids.root,
    providerRun: run,
  },
  sessionPort: ports.session,
  rootWorkspace: ports.root,
  eventPort: ports.events,
})

// Host transcript of one owned provider step: the request's physical message
// and the assistant `run` that answered it.
const ownedTerminal = (ids, run, parts = []) => [
  ownership.userMessage(ids.physical),
  ownership.assistantMessage(run, ids.physical, parts),
]

const captureFatal = async (work) => {
  const previousExit = process.env.WANXIANGSHU_NO_FATAL_EXIT
  const previousError = console.error
  const records = []
  process.env.WANXIANGSHU_NO_FATAL_EXIT = '1'
  console.error = (...args) => records.push(args.join(' '))
  try {
    return { value: await work(), records }
  } finally {
    console.error = previousError
    if (previousExit === undefined) delete process.env.WANXIANGSHU_NO_FATAL_EXIT
    else process.env.WANXIANGSHU_NO_FATAL_EXIT = previousExit
  }
}

test('WHAT[capability-enforcement-021] repeat_terminal_idle_is_idempotent_no_duplicate_nudge', async (t) => {
  const { ids, durable, scope, request, ports, calls } = await setupOwner(t)

  const first = await blog.observeIdleRepair(
    scope,
    durable,
    request,
    idleObservation(ports, ids, 'run-1'),
  )
  assert.equal(first.outcome, 'NudgeSent')
  assert.equal(typeof first.promptKey, 'string')
  assert.equal(calls.sendPrompt.length, 1)
  assert.equal(blog.repairClaimedForKind(durable, ids.blogger, ids.request, 'run-1', NUDGE_KIND), true)
  assert.equal(blog.repairIssuedForKind(durable, ids.blogger, ids.request, 'run-1', NUDGE_KIND), true)

  const second = await blog.observeIdleRepair(
    scope,
    durable,
    request,
    idleObservation(ports, ids, 'run-1'),
  )
  assert.equal(second.outcome, 'PendingRepairWait')
  assert.equal(calls.sendPrompt.length, 1)
})

test('WHAT[capability-enforcement-021] idle_without_quiescence_permit_spends_no_budget', async (t) => {
  const { ids, durable, scope, request, ports, calls } = await setupOwner(t)

  const res = await blog.observeIdleRepair(
    scope,
    durable,
    request,
    idleObservation(ports, ids, 'run-1', { quiescent: false }),
  )
  assert.equal(res.outcome, 'PendingRepairWait')
  assert.equal(calls.sendPrompt.length, 0)
  assert.equal(blog.repairClaimedForKind(durable, ids.blogger, ids.request, 'run-1', NUDGE_KIND), false)
})

test('WHAT[capability-enforcement-021] next_terminal_sends_at_most_one_aabb_then_abandons', async (t) => {
  const { ids, durable, scope, request, ports, calls } = await setupOwner(t)

  const nudge = await blog.observeIdleRepair(
    scope,
    durable,
    request,
    idleObservation(ports, ids, 'run-1'),
  )
  assert.equal(nudge.outcome, 'NudgeSent')
  assert.equal(calls.sendPrompt.length, 1)

  const aabb = await blog.observeIdleRepair(
    scope,
    durable,
    request,
    idleObservation(ports, ids, 'run-2'),
  )
  assert.equal(aabb.outcome, 'AabbSent')
  assert.equal(typeof aabb.promptKey, 'string')
  assert.equal(calls.sendPrompt.length, 2)
  assert.equal(blog.repairClaimedForKind(durable, ids.blogger, ids.request, 'run-2', AABB_KIND), true)

  const repeat = await blog.observeIdleRepair(
    scope,
    durable,
    request,
    idleObservation(ports, ids, 'run-2'),
  )
  assert.equal(repeat.outcome, 'PendingRepairWait')
  assert.equal(calls.sendPrompt.length, 2)

  const exhausted = await blog.observeIdleRepair(
    scope,
    durable,
    request,
    idleObservation(ports, ids, 'run-3'),
  )
  assert.equal(exhausted.outcome, 'AbandonedExhausted')
  assert.equal(calls.sendPrompt.length, 2)
  assert.equal(calls.eventNotify.length, 1)
  assert.equal(typeof calls.eventNotify[0][0], 'string')
  assert.equal(calls.eventNotify[0][0], ids.blogger)
  assert.equal(runtime.tryGetFlight(scope, ids.blogger), null)
})

test('WHAT[capability-enforcement-021] exhausted repair stops its real continuation without a process fatal', async (t) => {
  const { ids, durable, scope, ports, calls, request } = await setupOwner(t)
  await blog.observeIdleRepair(scope, durable, request, idleObservation(ports, ids, 'run-1'))
  await blog.observeIdleRepair(scope, durable, request, idleObservation(ports, ids, 'run-2'))
  const messages = ownedTerminal(ids, 'run-3')

  const { value, records } = await captureFatal(() =>
    blog.continueTransform(scope, durable, ids.blogger, messages),
  )

  assert.equal(value.kind, 'StopPhysicalRun')
  assert.deepEqual(value.messages, messages)
  assert.deepEqual(records, [])
  assert.equal(calls.sendPrompt.length, 2)
  assert.equal(calls.eventNotify.length, 1)
  assert.equal(runtime.tryGetFlight(scope, ids.blogger), null)
})

test('WHAT[capability-enforcement-021] terminal without provider identity stops only the exact request', async (t) => {
  const { durable, scope, ids } = await setupOwner(t)
  const messages = [ownership.userMessage(ids.physical), ownership.assistantMessage(undefined, ids.physical)]
  const { value, records } = await captureFatal(() =>
    blog.continueTransform(scope, durable, ids.blogger, messages),
  )
  assert.equal(value.kind, 'StopPhysicalRun')
  assert.deepEqual(records, [])
  assert.equal(runtime.tryGetFlight(scope, ids.blogger), null)
})

test('WHAT[capability-enforcement-021] failed durable abandon retains its flight and never reports settlement', async (t) => {
  const { durable, handle, scope, ids } = await setupOwner(t)
  journal.JournalSurface_dispose(handle)
  const messages = [ownership.userMessage(ids.physical), ownership.assistantMessage(undefined, ids.physical)]
  const { records } = await captureFatal(async () => {
    await assert.rejects(() => blog.continueTransform(scope, durable, ids.blogger, messages))
  })
  assert.deepEqual(records, [])
  assert.equal(runtime.tryGetFlight(scope, ids.blogger).requestId, ids.request)
})

test('WHAT[capability-enforcement-021] repair settlement failure rejects every waiting observer without fatal or release', async (t) => {
  const { durable, handle, scope, request, ids, ports, calls } = await setupOwner(t)
  await blog.observeIdleRepair(scope, durable, request, idleObservation(ports, ids, 'run-1'))
  await blog.observeIdleRepair(scope, durable, request, idleObservation(ports, ids, 'run-2'))
  journal.JournalSurface_dispose(handle)
  const { value, records } = await captureFatal(() => Promise.allSettled([
    blog.observeTransformRepair(scope, durable, request, 'run-3', ownedTerminal(ids, 'run-3')),
    blog.observeTransformRepair(scope, durable, request, 'run-4', ownedTerminal(ids, 'run-4')),
  ]))
  assert.deepEqual(value.map(result => result.status), ['rejected', 'rejected'])
  assert.equal(value[0].reason, value[1].reason, 'all observers receive the exact same failure')
  assert.deepEqual(records, [])
  assert.equal(calls.eventNotify.length, 0)
  assert.equal(runtime.tryGetFlight(scope, ids.blogger).requestId, ids.request)
})

test('WHAT[capability-enforcement-021] unknown abandon commit still rejects every observer without release', async (t) => {
  const { durable, scope, request, ids, ports, calls, writerFile } = await setupOwner(t)
  await blog.observeIdleRepair(scope, durable, request, idleObservation(ports, ids, 'run-1'))
  await blog.observeIdleRepair(scope, durable, request, idleObservation(ports, ids, 'run-2'))
  // Physical write failure mid-append: the durable outcome is Unknown, not
  // NotAttempted — the abandon may or may not be recorded.
  chmodSync(writerFile, 0o400)
  // Root bypasses permission bits, so chmod 0o400 cannot make the writer
  // unwritable there. When the file is still writable, the failure path is
  // not constructible in this environment; skip rather than false-green.
  let stillWritable = true
  try {
    accessSync(writerFile, constants.W_OK)
  } catch {
    stillWritable = false
  }
  if (stillWritable) return
  const { value, records } = await captureFatal(() => Promise.allSettled([
    blog.observeTransformRepair(scope, durable, request, 'run-3', ownedTerminal(ids, 'run-3')),
    blog.observeTransformRepair(scope, durable, request, 'run-4', ownedTerminal(ids, 'run-4')),
  ]))
  assert.deepEqual(value.map(result => result.status), ['rejected', 'rejected'])
  assert.equal(value[0].reason, value[1].reason, 'all observers receive the exact same failure')
  assert.match(String(value[0].reason), /append outcome unknown/)
  assert.deepEqual(records, [])
  assert.equal(calls.eventNotify.length, 0)
  assert.equal(runtime.tryGetFlight(scope, ids.blogger).requestId, ids.request)
})

test('WHAT[capability-enforcement-021] late observers after settlement failure get the same failure and no new budget', async (t) => {
  const { durable, handle, scope, request, ids, ports, calls } = await setupOwner(t)
  await blog.observeIdleRepair(scope, durable, request, idleObservation(ports, ids, 'run-1'))
  await blog.observeIdleRepair(scope, durable, request, idleObservation(ports, ids, 'run-2'))
  journal.JournalSurface_dispose(handle)
  const { value: first, records } = await captureFatal(() => Promise.allSettled([
    blog.observeTransformRepair(scope, durable, request, 'run-3', ownedTerminal(ids, 'run-3')),
  ]))
  assert.equal(first[0].status, 'rejected')
  const failure = first[0].reason

  const late = await Promise.allSettled([
    blog.observeTransformRepair(scope, durable, request, 'run-5', ownedTerminal(ids, 'run-5')),
    blog.observeIdleRepair(scope, durable, request, idleObservation(ports, ids, 'run-5')),
  ])
  assert.deepEqual(late.map(result => result.status), ['rejected', 'rejected'])
  assert.equal(late[0].reason, failure, 'late transform posts replay the stored failure')
  assert.equal(late[1].reason, failure, 'late idle posts replay the stored failure')
  assert.match(
    runtime.claimRepairEpisode(scope, 'req-blog-superseding', ids.physical, ids.main, ids.blogger),
    /^Error:Claimed request req-blog-superseding does not match active flight/,
  )
  assert.deepEqual(records, [])
  assert.equal(calls.sendPrompt.length, 2, 'a failed episode never re-opens the repair budget')
  assert.equal(calls.eventNotify.length, 0)
  assert.equal(runtime.tryGetFlight(scope, ids.blogger).requestId, ids.request)
})

test('WHAT[capability-enforcement-021] generated duplicate and interleaved repair observations preserve bounded effects', async (t) => {
  let traceNumber = 0
  await fc.assert(fc.asyncProperty(
    fc.array(fc.record({ idle: fc.boolean(), duplicate: fc.boolean(), quiescent: fc.boolean() }), { maxLength: 20 }),
    async (trace) => {
      const proof = (async () => {
        const cleanups = []
        const owner = await setupOwner({ after: fn => cleanups.push(fn) })
        const { durable, scope, request, ids, ports, calls } = owner
        try {
          const { records } = await captureFatal(async () => {
            let run = 0
            for (const observation of trace) {
              if (!observation.duplicate) run += 1
              const providerRun = `generated-run-${run}`
              if (observation.idle) {
                await blog.observeIdleRepair(scope, durable, request,
                  idleObservation(ports, ids, providerRun, observation))
              } else {
                await blog.continueTransform(scope, durable, ids.blogger, ownedTerminal(ids, providerRun))
              }
              assert.ok(calls.sendPrompt.length <= 2, 'one nudge and at most one physical AABB')
              assert.ok(calls.eventNotify.length <= 1, 'one terminal per exact repair episode')
              if (calls.eventNotify.length > 0) assert.equal(runtime.tryGetFlight(scope, ids.blogger), null)
            }
          })
          assert.deepEqual(records, [])
        } finally {
          for (const cleanup of cleanups.reverse()) await cleanup()
        }
      })()
      await t.test(`WHAT[capability-enforcement-021] generated repair trace ${++traceNumber}`, () => proof)
      // A failed subtest does not reject t.test; preserve FastCheck's shrink input.
      await proof
    },
  ), { seed: 20260914, numRuns: 60 })
})

test('WHAT[capability-enforcement-021] transform_and_idle_interleave_resolves_to_single_owner', async (t) => {
  const { ids, durable, scope, request, ports, calls } = await setupOwner(t)

  const before = await blog.observeTransformRepair(scope, durable, request, 'run-1', ownedTerminal(ids, 'run-1'))
  assert.equal(before.outcome, 'PendingRepairWait')
  assert.equal(calls.sendPrompt.length, 0)

  const nudge = await blog.observeIdleRepair(
    scope,
    durable,
    request,
    idleObservation(ports, ids, 'run-1'),
  )
  assert.equal(nudge.outcome, 'NudgeSent')
  assert.equal(calls.sendPrompt.length, 1)

  const sameRun = await blog.observeTransformRepair(scope, durable, request, 'run-1', ownedTerminal(ids, 'run-1'))
  assert.equal(sameRun.outcome, 'PendingRepairWait')
  assert.equal(calls.sendPrompt.length, 1)

  const secondTerminal = ownedTerminal(ids, 'run-2')
  const injected = await blog.observeTransformRepair(scope, durable, request, 'run-2', secondTerminal)
  assert.equal(injected.outcome, 'RepairInjected')
  assert.equal(injected.messages.length, secondTerminal.length + 1)
  assert.equal(injected.messages.at(-1).info.source, 'interaction-repair')
  assert.equal(calls.sendPrompt.length, 1)

  const settled = await blog.observeIdleRepair(
    scope,
    durable,
    request,
    idleObservation(ports, ids, 'run-2'),
  )
  assert.equal(settled.outcome, 'PendingRepairWait')
  assert.equal(calls.sendPrompt.length, 1)

  const exhausted = await blog.observeTransformRepair(scope, durable, request, 'run-3', ownedTerminal(ids, 'run-3'))
  assert.equal(exhausted.outcome, 'AbandonedExhausted')
  assert.equal(calls.sendPrompt.length, 1)
  assert.equal(calls.eventNotify.length, 1)
})

test('WHAT[capability-enforcement-021] transform_on_aabb_claimed_terminal_waits_without_double_spend', async (t) => {
  const { ids, durable, scope, request, ports, calls } = await setupOwner(t)

  const nudge = await blog.observeIdleRepair(
    scope,
    durable,
    request,
    idleObservation(ports, ids, 'run-1'),
  )
  assert.equal(nudge.outcome, 'NudgeSent')

  const secondTerminal = ownedTerminal(ids, 'run-2')
  const injected = await blog.observeTransformRepair(scope, durable, request, 'run-2', secondTerminal)
  assert.equal(injected.outcome, 'RepairInjected')
  assert.equal(injected.messages.length, secondTerminal.length + 1)

  const pending = await blog.observeTransformRepair(scope, durable, request, 'run-2', injected.messages)
  assert.equal(pending.outcome, 'PendingRepairWait')
  assert.equal(calls.sendPrompt.length, 1)

  const exhausted = await blog.observeTransformRepair(
    scope,
    durable,
    request,
    'run-3',
    [...injected.messages, ownership.assistantMessage('run-3', ids.physical)],
  )
  assert.equal(exhausted.outcome, 'AbandonedExhausted')
  assert.equal(calls.sendPrompt.length, 1)
})

test('WHAT[capability-enforcement-021] repair_without_journal_abandons_without_physical_sends', async (t) => {
  const { ids, scope, request, ports, calls } = await setupOwner(t)

  const idle = await blog.observeIdleRepair(
    scope,
    null,
    request,
    idleObservation(ports, ids, 'run-1'),
  )
  assert.equal(idle.outcome, 'AbandonedExhausted')

  const transform = await blog.observeTransformRepair(scope, null, request, 'run-1', ownedTerminal(ids, 'run-1'))
  assert.equal(transform.outcome, 'AbandonedExhausted')

  assert.equal(calls.sendPrompt.length, 0)
})

test('WHAT[capability-enforcement-021] a transform or idle terminal without ownership proof spends no budget', async (t) => {
  const { ids, durable, scope, request, ports, calls } = await setupOwner(t)
  const unlanded = { ...ids, physical: 'msg-never-landed' }

  const transform = await blog.observeTransformRepair(scope, durable, request, 'run-1', ownedTerminal(unlanded, 'run-1'))
  assert.equal(transform.outcome, 'UnprovenIgnored')
  const idle = await blog.observeIdleRepair(scope, durable, request, idleObservation(ports, unlanded, 'run-1'))
  assert.equal(idle.outcome, 'UnprovenIgnored')
  const unparented = await blog.observeTransformRepair(scope, durable, request, 'run-2', [])
  assert.equal(unparented.outcome, 'UnprovenIgnored')

  assert.equal(calls.sendPrompt.length, 0)
  assert.equal(blog.repairClaimedForKind(durable, ids.blogger, ids.request, 'run-1', NUDGE_KIND), false)
  assert.equal(runtime.tryGetFlight(scope, ids.blogger).requestId, ids.request)
})

test('WHAT[capability-enforcement-021] an aborted Blogger turn still reaches the repair owner at idle', () => {
  const prose = [{ type: 'text', text: 'prose instead of a chronicle call' }]
  const chronicle = [{ type: 'tool', tool: 'chronicle', callID: 'c1', state: { status: 'completed', input: {} } }]

  // The continuation's own stop and an external abort both leave the live
  // request with idle as its only wake.
  assert.equal(turns.bloggerIdleRoute(true, false, 'TurnAborted', []), 'RepairThenObserve')
  assert.equal(turns.bloggerIdleRoute(true, false, 'TurnAborted', chronicle), 'RepairThenObserve')
  // A degeneration-guard abort already owns its successor.
  assert.equal(turns.bloggerIdleRoute(true, true, 'TurnAborted', []), 'Observe')
  assert.equal(turns.bloggerIdleRoute(true, false, 'TurnCompleted', prose), 'Repair')
  assert.equal(turns.bloggerIdleRoute(true, false, 'TurnCompleted', chronicle), 'Observe')
  // Provider failures belong to provider-attempt recovery.
  assert.equal(turns.bloggerIdleRoute(true, false, 'TurnFailed', prose), 'Observe')
  assert.equal(turns.bloggerIdleRoute(false, false, 'TurnAborted', []), 'Observe')
})

test('WHAT[capability-enforcement-021] shutdown_rejects_new_repair_episode_before_drain', async (t) => {
  const { ids, scope } = await setupOwner(t)

  runtime.beginBloggerShutdown(scope)

  assert.equal(
    runtime.claimRepairEpisode(scope, ids.request, ids.physical, ids.main, ids.blogger),
    'Error:Blogger runtime is shutting down',
  )

  await runtime.drainRepairEpisodes(scope)
})
