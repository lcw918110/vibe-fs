import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const target = (model = 'provider/model') => ({ model, reasoning: 'none' })
const begin = (runtime, session, physical, role, participant, lenderSessionId = null) =>
  routing.beginExecutionAdmission(runtime, session, physical, role, participant, lenderSessionId)
const awaitQueued = (outcome) => routing.awaitQueuedExecutionAdmission(outcome.queue)
const fill = async (runtime, count, prefix = 'waiting') => {
  const queued = []
  for (let index = 0; index < count; index += 1) {
    const outcome = await begin(runtime, `${prefix}-${index}`, `physical-${index}`, 'engineer', `${prefix}-owner-${index}`)
    assert.equal(outcome.kind, 'Queued')
    queued.push(outcome)
  }
  return queued
}

test('WHAT[execution-model-routing-004] an ineligible head does not block a later eligible demand', async () => {
  let freeEnabled = false
  const runtime = routing.createRuntime((role) => {
    if (role === 'devops') return target('provider/trigger')
    if (role === 'manager' && freeEnabled) return target('provider/free')
    return null
  })

  const blocked = await begin(runtime, 'blocked-session', 'physical-blocked', 'engineer', 'blocked-owner')
  const free = await begin(runtime, 'free-session', 'physical-free', 'manager', 'free-owner')
  assert.equal(blocked.kind, 'Queued')
  assert.equal(free.kind, 'Queued')

  freeEnabled = true
  assert.equal((await begin(runtime, 'trigger', 'physical-trigger', 'devops', 'trigger-owner')).kind, 'Acquired')
  assert.equal((await awaitQueued(free)).kind, 'Acquired')
  assert.equal(routing.pendingCount(runtime), 1)

  routing.cancelPendingExecution(runtime, 'blocked-session')
  assert.equal((await awaitQueued(blocked)).kind, 'Cancelled')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const {
  createRuntime,
  acquireExecutionAdmission,
  beginExecutionAdmission,
  awaitQueuedExecutionAdmission,
  executionAdmissionTarget,
  commitExecutionAdmission,
  tryReserveManaged,
  tryLease,
  releasePhysicalExecution,
  cancelPendingExecution,
  enterProviderStep,
  endProviderStep,
  takeProviderRunTarget,
  suppressProviderStep,
  snapshotOccupied,
  capacitySnapshot,
  pendingCount,
} = routing
const target = (model = 'provider/shared', reasoning = 'none') => ({ model, reasoning })
const key = (value) => `${value.model}|${value.reasoning}`
const acquireManaged = async (runtime, sessionId, physicalUserMessageId, role, participant, lenderSessionId = null) => {
  const acquisition = await acquireExecutionAdmission(
    runtime,
    sessionId,
    physicalUserMessageId,
    role,
    participant,
    lenderSessionId,
  )
  if (acquisition.kind !== 'Acquired') return { kind: acquisition.kind, target: null }

  const projected = executionAdmissionTarget(runtime, acquisition.lease)
  const observed = {
    sessionId,
    physicalUserMessageId,
    role,
    participant,
    target: projected,
  }
  const settlement = commitExecutionAdmission(runtime, acquisition.lease, observed)
  assert.ok(['Applied', 'AlreadyApplied'].includes(settlement.kind))
  return { kind: 'Acquired', target: projected }
}
const acquireTarget = async (...args) => {
  const outcome = await acquireManaged(...args)
  assert.equal(outcome.kind, 'Acquired')
  return outcome.target
}
const provider = (model) => model.slice(0, model.indexOf('/'))
const providerLimited = (limits, routes) => (role, running, previous) => {
  const candidates = routes[role] ?? []
  const count = (name) => running.filter((item) => provider(item.model) === name).length
  const available = (candidate) => count(provider(candidate.model)) < (limits[provider(candidate.model)] ?? 0)
  if (previous && candidates.some((candidate) => key(candidate) === key(previous)) && available(previous)) return previous
  return candidates.find(available) ?? null
}

test('WHAT[execution-model-routing-004] EMR_004_required_null_waits_for_an_occupancy_event_then_retries', async () => {
  const route = (_role, running) => running.filter((item) => item.model === 'provider/only').length < 1
    ? target('provider/only')
    : null
  const runtime = createRuntime(route)

  await acquireTarget(runtime, 'holder', 'msg-holder', 'engineer', 'alice')
  let settled = false
  const waiting = acquireTarget(runtime, 'waiter', 'msg-waiter', 'engineer', 'bob').then((value) => {
    settled = true
    return value
  })
  await Promise.resolve()

  assert.equal(settled, false)
  assert.equal(pendingCount(runtime), 1)

  releasePhysicalExecution(runtime, 'holder', 'msg-holder')
  assert.equal(key(await waiting), 'provider/only|none')
  assert.equal(pendingCount(runtime), 0)
  assert.equal(snapshotOccupied(runtime).length, 1)
})
test('WHAT[execution-model-routing-004] EMR_004_newer_physical_message_cancels_superseded_pending_demand', async () => {
  const runtime = createRuntime((role) => role === 'devops' ? null : target(`provider/${role}`))

  const old = acquireManaged(runtime, 'same-session', 'msg-old', 'devops', 'alice')
  await Promise.resolve()
  assert.equal(pendingCount(runtime), 1)

  const fresh = await acquireTarget(runtime, 'same-session', 'msg-new', 'engineer', 'alice')
  assert.equal(fresh.model, 'provider/engineer')
  const oldOutcome = await old
  assert.equal(oldOutcome.kind, 'Superseded')
  assert.equal(oldOutcome.target, null)
  assert.equal(pendingCount(runtime), 0)
  assert.equal(snapshotOccupied(runtime).length, 1)
})
test('WHAT[execution-model-routing-004] EMR_004_an_earlier_null_waiter_does_not_head_of_line_block_another_role', async () => {
  const runtime = createRuntime((role) => role === 'devops' ? null : target(`provider/${role}`))

  const blocked = acquireManaged(runtime, 'blocked-session', 'msg-blocked', 'devops', 'alice')
  await Promise.resolve()
  assert.equal(pendingCount(runtime), 1)

  const free = await acquireTarget(runtime, 'free-session', 'msg-free', 'engineer', 'bob')
  assert.equal(key(free), 'provider/engineer|none')
  assert.equal(pendingCount(runtime), 1)

  cancelPendingExecution(runtime, 'blocked-session')
  const blockedOutcome = await blocked
  assert.equal(blockedOutcome.kind, 'Cancelled')
  assert.equal(blockedOutcome.target, null)
})
test('WHAT[execution-model-routing-004] EMR_004_optional_null_is_k0_not_a_pending_demand', () => {
  const runtime = createRuntime(() => null)
  assert.equal(tryReserveManaged(runtime, 'replica', 'engineer', null), null)
  assert.equal(pendingCount(runtime), 0)
  assert.deepEqual(snapshotOccupied(runtime), [])
})
test('WHAT[execution-model-routing-004] local reservation adoption reuses one token and scheduler result', async () => {
  let calls = 0
  const runtime = createRuntime(() => {
    calls += 1
    return target('provider/replica')
  })

  const reserved = tryReserveManaged(runtime, 'replica', 'engineer', null)
  assert.equal(key(reserved), 'provider/replica|none')
  assert.equal(snapshotOccupied(runtime).length, 1)

  const adopted = await acquireTarget(runtime, 'replica', 'msg-replica', 'engineer', 'alice')
  assert.equal(key(adopted), 'provider/replica|none')
  assert.equal(calls, 1, 'physical acceptance adopts the reservation without another scheduler decision')
  assert.equal(snapshotOccupied(runtime).length, 1, 'reservation and physical execution are one capacity occurrence')
  assert.equal(key(tryLease(runtime, 'replica', 'msg-replica', 'engineer', 'alice', null)), 'provider/replica|none')
})

test('WHAT[execution-model-routing-004] unaccepted speculative intent must not preoccupy capacity', { todo: 'GAP-130: optional reservation currently occupies a token before chat.message; 34-D2 needs decision' }, () => {
  const runtime = createRuntime(() => target('provider/replica'))
  try {
    tryReserveManaged(runtime, 'unaccepted', 'engineer', null)
    assert.deepEqual(snapshotOccupied(runtime), [])
  } finally {
    routing.releaseExecution(runtime, 'unaccepted')
  }
})
test('WHAT[execution-model-routing-004] EMR_004_reservation_adoption_binds_role_and_participant', async () => {
  const runtime = createRuntime(() => target('provider/replica'))
  tryReserveManaged(runtime, 'replica', 'engineer', null)

  await acquireTarget(runtime, 'replica', 'msg-replica', 'engineer', 'alice')
  await assert.rejects(
    acquireTarget(runtime, 'replica', 'msg-replica', 'engineer', 'bob'),
    /physical execution .* changed participant/i,
    'the first adopter binds the participant for that exact physical execution',
  )

  const rerouted = createRuntime(() => target('provider/replica'))
  tryReserveManaged(rerouted, 'replica', 'engineer', null)
  await assert.rejects(
    acquireTarget(rerouted, 'replica', 'msg-replica', 'devops', 'alice'),
    /physical execution .* changed role/i,
    'a reservation carries its Role until adoption',
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const { createSdkClientPort, sendPrompt } = routing
const promptOptions = (overrides = {}) => ({
  model: undefined,
  agent: undefined,
  directory: undefined,
  metadata: undefined,
  tools: undefined,
    ...overrides,
})

test('WHAT[execution-model-routing-004] EMR_004_sdk_prompt_async_awaits_host_enqueue_and_surfaces_enqueue_rejection', async () => {
  let releaseHost
  let rejectHost
  let invoked = false
  const hostRun = new Promise((resolve) => {
    releaseHost = resolve
  })
  const failingRun = new Promise((_, reject) => { rejectHost = reject })
  const client = {
    session: {
      promptAsync: () => {
        invoked = true
        return hostRun
      },
    },
  }
  const port = createSdkClientPort(client)


  const sending = sendPrompt(
    port,
    'session-detached',
    'start child work',
    promptOptions({ agent: 'devops' }),
  )
  releaseHost({})
  const result = await sending
  assert.equal(invoked, true, 'the Host enqueue API is invoked')
  assert.match(JSON.stringify(result), /AdmittedWithReceipt/i)

  const failingPort = createSdkClientPort({ session: { promptAsync: () => failingRun } })
  rejectHost(new Error('Host enqueue refused'))
  const failed = await sendPrompt(failingPort, 'session-failing', 'refused work', promptOptions({ agent: 'devops' }))
  assert.match(JSON.stringify(failed), /Fatal|refused/i)
})
}
