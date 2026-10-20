import test from 'node:test'

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

test('WHAT[execution-model-routing-006] EMR_006_same_physical_message_retry_reuses_target_without_scheduler_rerun', async () => {
  const seen = []
  const runtime = createRuntime((_role, running) => {
    seen.push(running.map((item) => `${item.model}|${item.reasoning}`))
    return target()
  })

  await acquireTarget(runtime, 'session-a', 'msg-1', 'engineer', 'alice')
  await acquireTarget(runtime, 'session-a', 'msg-1', 'engineer', 'alice')
  await acquireTarget(runtime, 'session-a', 'msg-2', 'engineer', 'alice')

  assert.deepEqual(seen, [[], ['provider/shared|none']], 'same physical material reuses without a scheduler rerun; the superseding fresh schedule observes the replaced occupancy')
})

for (const borrowed of [false, true]) {
for (const completed of [false, true]) {
test(`WHAT[execution-model-routing-006] guidance transfers the ${borrowed ? 'borrowed' : 'owned'} credit ${completed ? 'after terminal output' : 'after a tool step'} and stale callbacks cannot release its next step`, async () => {
  let changedPolicy = false
  const runtime = createRuntime(() => target(changedPolicy ? 'other/new-policy' : 'provider/shared'))
  if (borrowed) await acquireTarget(runtime, 'lender', 'lender-root', 'manager', 'commissioner')
  const old = await acquireExecutionAdmission(runtime, 'child', 'child-root', 'engineer', 'Ada', borrowed ? 'lender' : null)
  const observed = {
    sessionId: 'child', physicalUserMessageId: 'child-root', role: 'engineer', participant: 'Ada',
    target: executionAdmissionTarget(runtime, old.lease),
  }
  assert.deepEqual(commitExecutionAdmission(runtime, old.lease, observed), { kind: 'Applied' })
  await enterProviderStep(runtime, 'child', 'child-root', [])
  assert.deepEqual(routing.retainContinuationInput(runtime, old.lease, 'child-guidance'), observed.target)
  endProviderStep(runtime, 'child', 'child-root', 'old-provider-run')
  if (completed) {
    assert.deepEqual(releasePhysicalExecution(runtime, 'child', 'child-root'), { kind: 'HeldForInput' })
  }
  const original = capacitySnapshot(runtime)
  assert.equal(original.tokens.length, 1)
  const credit = original.tokens[0].credit

  changedPolicy = true
  const next = await routing.continueExecutionAdmission(runtime, old.lease, 'child-guidance')
  const nextObserved = { ...observed, physicalUserMessageId: 'child-guidance' }
  assert.deepEqual(executionAdmissionTarget(runtime, next.lease), observed.target,
    'guidance continues the bound model even if the fresh-routing policy changed')
  assert.deepEqual(commitExecutionAdmission(runtime, next.lease, nextObserved), { kind: 'Applied' })
  await enterProviderStep(runtime, 'child', 'child-guidance', [])
  const running = capacitySnapshot(runtime)
  assert.equal(running.activeCount, 1)
  assert.equal(running.tokens.length, 1)
  assert.equal(running.tokens[0].credit, credit)
  assert.equal(running.custodies.some(custody =>
    custody.credit === credit && custody.owner.sessionId === 'child'
    && custody.owner.physicalUserMessageId === 'child-guidance'), true)
  assert.equal(running.tokens[0].owner.sessionId, borrowed ? 'lender' : 'child')

  endProviderStep(runtime, 'child', 'child-root', 'old-provider-run')
  routing.releaseExecutionAdmissionBeforeProvider(runtime, old.lease, observed)
  await assert.rejects(routing.continueExecutionAdmission(runtime, old.lease, 'stale-guidance'))
  assert.deepEqual(capacitySnapshot(runtime).tokens, running.tokens)
  assert.equal(capacitySnapshot(runtime).activeCount, 1)
  endProviderStep(runtime, 'child', 'child-guidance', 'next-provider-run')
  releasePhysicalExecution(runtime, 'child', 'child-guidance')
  if (borrowed) releasePhysicalExecution(runtime, 'lender', 'lender-root')
  assert.deepEqual(snapshotOccupied(runtime), [])
})
}
}

for (const borrowed of [false, true]) {
test(`WHAT[execution-model-routing-006] cancelling deferred guidance returns the held ${borrowed ? 'borrowed' : 'owned'} credit without retiring its lender`, async () => {
  const runtime = createRuntime(() => target())
  if (borrowed) await acquireTarget(runtime, 'lender', 'lender-root', 'manager', 'commissioner')
  const old = await acquireExecutionAdmission(runtime, 'child', 'child-root', 'engineer', 'Ada', borrowed ? 'lender' : null)
  const observed = {
    sessionId: 'child', physicalUserMessageId: 'child-root', role: 'engineer', participant: 'Ada',
    target: executionAdmissionTarget(runtime, old.lease),
  }
  assert.deepEqual(commitExecutionAdmission(runtime, old.lease, observed), { kind: 'Applied' })
  routing.retainContinuationInput(runtime, old.lease, 'child-guidance')
  assert.deepEqual(releasePhysicalExecution(runtime, 'child', 'child-root'), { kind: 'HeldForInput' })
  releasePhysicalExecution(runtime, 'child', 'child-guidance')
  const released = capacitySnapshot(runtime)
  assert.equal(released.tokens.length, borrowed ? 1 : 0)
  assert.equal(released.executions.some(owner => owner.sessionId === 'child'), false)
  if (borrowed) {
    await enterProviderStep(runtime, 'lender', 'lender-root', [])
    assert.equal(capacitySnapshot(runtime).activeCount, 1)
    assert.equal(capacitySnapshot(runtime).tokens[0].owner.sessionId, 'lender')
    endProviderStep(runtime, 'lender', 'lender-root', 'lender-after-guidance-cancel')
    releasePhysicalExecution(runtime, 'lender', 'lender-root')
  }
  assert.deepEqual(snapshotOccupied(runtime), [])
})
}

test('WHAT[execution-model-routing-006] EMR_006_new_physical_message_supersedes_old_A_B_occupancy_without_idle', async () => {
  const runtime = createRuntime((role) => target(`provider/${role}`))

  const a = await acquireTarget(runtime, 'session', 'msg-a', 'engineer', 'alice')
  assert.equal(a.model, 'provider/engineer')
  assert.equal(snapshotOccupied(runtime).length, 1)

  const b = await acquireTarget(runtime, 'session', 'msg-b', 'devops', 'alice')
  assert.equal(b.model, 'provider/devops')
  assert.equal(snapshotOccupied(runtime).length, 1, 'one reusable session can own only one current physical execution slot')
  assert.equal(tryLease(runtime, 'session', 'msg-a', 'engineer', 'alice', null), null, 'superseded physical material no longer owns a lease')
  assert.equal(key(tryLease(runtime, 'session', 'msg-b', 'devops', 'alice', null)), 'provider/devops|none')
})
test('WHAT[execution-model-routing-006] EMR_006_supersede_replaces_the_exact_capacity_owner', async () => {
  const runtime = createRuntime((role) => target(`provider/${role}`))

  await acquireTarget(runtime, 'session', 'msg-a', 'engineer', 'alice')
  await acquireTarget(runtime, 'session', 'msg-b', 'devops', 'alice')

  const snapshot = capacitySnapshot(runtime)
  assert.equal(snapshot.tokens.length, 1)
  assert.deepEqual(snapshot.tokens[0].owner, {
    sessionId: 'session',
    physicalUserMessageId: 'msg-b',
    role: 'devops',
    participant: 'alice',
  })
  assert.deepEqual(snapshot.executions, [
    {
      sessionId: 'session',
      physicalUserMessageId: 'msg-b',
      role: 'devops',
      participant: 'alice',
    },
  ])
})
test('WHAT[execution-model-routing-006] EMR_006_only_the_current_run_witness_resolves_a_target', async () => {
  let scheduled = 0
  const runtime = createRuntime(() => target(`provider-${++scheduled}/model`))

  await acquireTarget(runtime, 'session', 'msg-a', 'manager', 'alice')
  endProviderStep(runtime, 'session', 'msg-a', 'run-a')

  const current = await acquireTarget(runtime, 'session', 'msg-b', 'manager', 'alice')
  endProviderStep(runtime, 'session', 'msg-b', 'run-b')

  assert.equal(takeProviderRunTarget(runtime, 'run-a'), null, 'superseded run cannot poison the current target')
  assert.equal(takeProviderRunTarget(runtime, 'run-b').model, current.model)
  assert.equal(takeProviderRunTarget(runtime, 'run-b'), null, 'the exact witness is single-consumption')
  assert.equal(takeProviderRunTarget(runtime, 'no-such-run'), null, 'an unknown run resolves nothing')
})
test('WHAT[execution-model-routing-006] EMR_006_same_physical_message_cannot_change_role', async () => {
  const runtime = createRuntime(() => target())
  await acquireTarget(runtime, 'session', 'msg-1', 'engineer', 'alice')

  await assert.rejects(
    acquireTarget(runtime, 'session', 'msg-1', 'devops', 'alice'),
    /physical execution .* changed role/i,
  )
  assert.equal(snapshotOccupied(runtime).length, 1)
})
test('WHAT[execution-model-routing-006] EMR_006_same_physical_message_cannot_change_participant', async () => {
  const runtime = createRuntime(() => target())
  await acquireTarget(runtime, 'session', 'msg-1', 'engineer', 'alice')

  await assert.rejects(
    acquireTarget(runtime, 'session', 'msg-1', 'engineer', 'bob'),
    /physical execution .* changed participant/i,
  )
  assert.equal(snapshotOccupied(runtime).length, 1)
})
test('WHAT[execution-model-routing-006] EMR_006_lease_is_stable_only_for_one_physical_user_material', async () => {
  let calls = 0
  const runtime = createRuntime(() => target(`provider/model-${++calls}`, 'low'))

  const first = await acquireTarget(runtime, 'session', 'msg-1', 'engineer', 'alice')
  const retry = await acquireTarget(runtime, 'session', 'msg-1', 'engineer', 'alice')

  assert.equal(first.model, 'provider/model-1')
  assert.equal(retry.model, 'provider/model-1')
  assert.equal(calls, 1)

  const nextMaterial = await acquireTarget(runtime, 'session', 'msg-2', 'engineer', 'alice')
  assert.equal(nextMaterial.model, 'provider/model-2', 'new physical material gets a fresh lease even without idle')
  assert.equal(calls, 2)
})
test('WHAT[execution-model-routing-006] EMR_006_fresh_physical_receives_the_replaced_target_as_previous', async () => {
  const seenPrevious = []
  let next = 0
  const runtime = createRuntime((_role, _running, previous) => {
    seenPrevious.push(previous)
    return previous ?? target(`provider/model-${++next}`, 'low')
  })

  const first = await acquireTarget(runtime, 'continued', 'msg-1', 'engineer', 'alice')
  assert.equal(first.model, 'provider/model-1')

  const continued = await acquireTarget(runtime, 'continued', 'msg-2', 'engineer', 'alice')
  assert.deepEqual(continued, first, 'the atomically replaced active physical supplies the previous target')
  assert.deepEqual(seenPrevious, [null, first])
})
test('WHAT[execution-model-routing-006] EMR_006_released_session_supplies_no_previous_target', async () => {
  const seenPrevious = []
  let next = 0
  const runtime = createRuntime((_role, _running, previous) => {
    seenPrevious.push(previous)
    return previous ?? target(`provider/model-${++next}`, 'low')
  })

  await acquireTarget(runtime, 'continued', 'msg-1', 'engineer', 'alice')
  releasePhysicalExecution(runtime, 'continued', 'msg-1')

  const rebuilt = await acquireTarget(runtime, 'continued', 'msg-2', 'engineer', 'alice')
  assert.equal(rebuilt.model, 'provider/model-2', 'no session-history cache survives an exact terminal release')
  assert.deepEqual(seenPrevious, [null, null])
})
test('WHAT[execution-model-routing-006] EMR_006_unrelated_session_supplies_no_previous_target', async () => {
  const seenPrevious = []
  let next = 0
  const runtime = createRuntime((_role, _running, previous) => {
    seenPrevious.push(previous)
    return previous ?? target(`provider/model-${++next}`, 'low')
  })

  const first = await acquireTarget(runtime, 'session-a', 'msg-1', 'engineer', 'alice')
  assert.equal(first.model, 'provider/model-1')

  const fresh = await acquireTarget(runtime, 'session-b', 'msg-1', 'engineer', 'bob')
  assert.equal(fresh.model, 'provider/model-2', 'another session never inherits a previous target')
  assert.deepEqual(seenPrevious, [null, null])
})
}

{
const { default: assert } = await import("node:assert/strict");
const { readFile } = await import("node:fs/promises");
const { default: test } = await import("node:test");

const templateUrl = new URL('../../../resources/wanxiangshu.mjs', import.meta.url)
const MANAGED = [
  'engineer',
  'manager',
  'orchestrator',
  'devops',
]

test('WHAT[execution-model-routing-006] EMR_006_recommended_template_prefers_previous_candidate_when_provider_has_capacity', async () => {
  const { default: scheduler } = await import(`${templateUrl.href}?previous=${Date.now()}`)
  const { invokeScheduler } = await import('../../../dist/OpenCode/Host/ModelRoutingSurface.js')
  const route = (role, running, previous = null) => invokeScheduler(scheduler, role, running, previous)
  const previous = { model: 'neuralwatt/glm-5.2-flex', reasoning: 'high' }

  assert.deepEqual(route('engineer', [], previous), previous)

  const neuralwattFull = Array.from({ length: 4 }, () => ({
    model: 'neuralwatt/another-model',
    reasoning: 'none',
  }))
  assert.equal(
    route('engineer', neuralwattFull, previous).model,
    'cursor/cursor-grok-4.6-xhigh',
    'when the previous provider is full, the next template candidate applies',
  )
})
}
