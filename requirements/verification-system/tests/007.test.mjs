import test from 'node:test'
import assert from 'node:assert/strict'
import { createVirtualClock } from './support/temporal-harness.mjs'

test('WHAT[verification-system-007] explicit virtual time and cancellation replay the same causal trace', async () => {
  async function replay() {
    const clock = createVirtualClock()
    const trace = []
    const cancelled = clock.port.delay(10)
    const due = clock.port.delay(20)
    cancelled.delay().then(() => trace.push(['cancelled-fired', clock.nowMs()]))
    due.delay().then(() => trace.push(['due-fired', clock.nowMs()]))
    cancelled.cancel()
    trace.push(['cancel', clock.nowMs()])
    clock.advance(19)
    await Promise.resolve()
    trace.push(['before-deadline', clock.nowMs()])
    clock.advance(1)
    await due.delay()
    trace.push(['observed', clock.nowMs()])
    clock.port.dispose()
    return trace
  }
  const expected = [['cancel', 0], ['before-deadline', 19], ['due-fired', 20], ['observed', 20]]
  assert.deepEqual(await replay(), expected)
  assert.deepEqual(await replay(), expected)
})

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { runInterleaving, validPermutations } = await import("./support/identity-capacity-interleaving.mjs");

const parentAgents = ['manager', 'devops']
const childAgents = ['engineer', 'plan']
const capacities = [2, 3]
const replayModes = [false, true]
const duplicateDeliveryModes = [false, true]
const restartBoundaries = [null, 'child dispatched', 'parent new prompt']
const families = parentAgents.flatMap((parentAgent) =>
  childAgents.flatMap((childAgent) =>
    capacities.flatMap((capacity) =>
      replayModes.flatMap((replay) =>
        duplicateDeliveryModes.map((duplicateDelivery) => ({
          parentAgent,
          childAgent,
          capacity,
          replay,
          duplicateDelivery,
        })),
      ),
    ),
  ),
).map((family, index) => Object.freeze({
  ...family,
  name: `family-${index}`,
  restartAfter: restartBoundaries[index % restartBoundaries.length],
  schedule: validPermutations[index % validPermutations.length],
}))

test('WHAT[verification-system-007] deterministic families preserve replay, restart, identity, and fence laws', async () => {
  assert.equal(families.length, 32)
  assert.deepEqual(new Set(families.map(({ parentAgent }) => parentAgent)), new Set(parentAgents))
  assert.deepEqual(new Set(families.map(({ childAgent }) => childAgent)), new Set(childAgents))
  assert.deepEqual(new Set(families.map(({ capacity }) => capacity)), new Set(capacities))
  assert.deepEqual(new Set(families.map(({ replay }) => replay)), new Set(replayModes))
  assert.deepEqual(
    new Set(families.map(({ duplicateDelivery }) => duplicateDelivery)),
    new Set(duplicateDeliveryModes),
  )
  assert.deepEqual(new Set(families.map(({ restartAfter }) => restartAfter)), new Set(restartBoundaries))

  const results = []
  for (const { schedule, ...family } of families) {
    results.push(await runInterleaving(schedule, family))
  }

  assert.equal(results.length, families.length)
  assert.ok(results.every(({ providerDispatches }) => providerDispatches === 1))
  assert.ok(results.every(({ parent, child }) => parent.session !== child.session))
  assert.ok(
    results.every(
      ({ parent, child }) =>
        (parent.participantIdentity.participant ?? parent.participantIdentity.selectedAgent) !==
          (child.participantIdentity.participant ?? child.participantIdentity.selectedAgent),
    ),
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { defaultFamily, operations, permutations, prerequisites, runInterleaving, validPermutations } = await import("./support/identity-capacity-interleaving.mjs");


test('WHAT[verification-system-007] executes every valid identity/admission/capacity causal interleaving', async () => {
  assert.deepEqual(operations, [
    'parent accepted',
    'child dispatched',
    'child returns',
    'parent new prompt',
    'child terminal',
    'capacity release',
  ])
  assert.deepEqual(prerequisites, {
    'parent accepted': [],
    'child dispatched': ['parent accepted'],
    'child returns': ['child dispatched'],
    'parent new prompt': ['child dispatched'],
    'child terminal': ['child dispatched'],
    'capacity release': ['child terminal'],
  })
  assert.equal(permutations.length, 720)
  assert.equal(validPermutations.length, 12)

  const observations = []
  for (const schedule of validPermutations) {
    observations.push(await runInterleaving(schedule, defaultFamily))
  }

  assert.equal(observations.length, 12)
  assert.equal(new Set(observations.map(({ schedule }) => schedule.join(' → '))).size, 12)
  assert.ok(observations.every(({ providerDispatches }) => providerDispatches === 1))
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const temporal = await import("../../../dist/Verification/TemporalSurface.js");
const { DeterministicCompletionSource, DeterministicEventQueue, DurableTraceEvents, createDurableWorld, createRecordedProviderPort, fallbackFacts, runTrace } = await import("./support/temporal-harness.mjs");

const SESSION_A = 'ses_a'
const streamA = { kind: 'Session', session: SESSION_A }
const rootAgentFact = () => fallbackFacts.authorityRoot({ session: SESSION_A })
const advanceAgentFact = (run, count) => ({
  family: 'ProviderFailure',
  case: 'FailureRecorded',
  payload: {
    SessionId: SESSION_A,
    LogicalRunId: 'run_L',
    AuthorityRootUserMessageId: 'msg_u1',
    ProviderRun: run,
    ConsecutiveFailureCount: count,
    Reason: 'provider_error',
  },
})
const providerFailureOf = (projection) => projection?.sessions?.[SESSION_A]?.providerFailures

test('WHAT[verification-system-007] deterministic queue enumerates races explicitly', () => {
  const a = ['A1', 'A2']
  const b = ['B1']
  const interleavings = DeterministicEventQueue.interleavings(a, b)
  assert.equal(interleavings.length, 3)
  const serialized = interleavings.map((items) => items.join(',')).sort()
  assert.deepEqual(serialized, ['A1,A2,B1', 'A1,B1,A2', 'B1,A1,A2'].sort())

  const permutations = DeterministicEventQueue.permutations(['A', 'B', 'C'])
  assert.equal(permutations.length, 6)
  for (const permutation of permutations) assert.deepEqual([...permutation].sort(), ['A', 'B', 'C'])
})
test('WHAT[verification-system-007] completion source order is explicit', async () => {
  const source = new DeterministicCompletionSource()
  const firstEntry = source.enqueue()
  const secondEntry = source.enqueue()
  assert.equal(source.pendingCount, 2)
  source.resolveId(secondEntry.id, 'second')
  source.resolveId(firstEntry.id, 'first')
  const [first, second] = await Promise.all([firstEntry.promise, secondEntry.promise])
  assert.equal(first, 'first')
  assert.equal(second, 'second')
  assert.equal(source.pendingCount, 0)
})
test('WHAT[verification-system-007] runTrace advances clock and appends durably', async () => {
  const world = await createDurableWorld({ directory: 'temporal-runtrace', runtime: 'rt_trace', pid: 4242 })

  let fired = 0
  const handle = world.vt.port.delay(50)
  handle.delay().then(() => {
    fired += 1
  })

  const events = [
    DurableTraceEvents.appendAgentFact(streamA, undefined, rootAgentFact()),
    DurableTraceEvents.advanceClock(30),
    DurableTraceEvents.appendAgentFact(streamA, 'run_1', advanceAgentFact('run_1', 1)),
    DurableTraceEvents.advanceClock(20),
  ]
  await runTrace(world, events)
  await handle.delay()
  assert.equal(fired, 1, '50ms timer must fire after two advances totalling 50ms')

  const snapshot = temporal.journalSnapshot(world.journal)
  const providerFailure = providerFailureOf(snapshot)
  assert.ok(providerFailure, 'provider failure must exist after runTrace appends')
  assert.equal(providerFailure.failures, 1)
  world.dispose()
})
test('WHAT[verification-system-007] recorded provider port replays in enqueued order', async () => {
  const port = createRecordedProviderPort()
  port.enqueue({ text: 'first' })
  port.enqueue({ text: 'second' })
  assert.equal(port.pendingCount, 2)
  const first = await port.request({})
  const second = await port.request({})
  assert.deepEqual(first, { text: 'first' })
  assert.deepEqual(second, { text: 'second' })
  assert.equal(port.pendingCount, 0)
})
test('WHAT[verification-system-007] journal release drains accepted append prefix and rejects later admission', async () => {
  const result = await temporal.writerReleaseDrainScenario()
  assert.deepEqual(result, {
    acceptedPrefix: 'Committed',
    afterClose: 'WriterDisposed',
    appendCalls: 2,
    closeBlockedOnAcceptedAppend: true,
    duringClose: 'WriterClosing',
  })
})
test('WHAT[verification-system-007] journal poison preserves the first physical failure and stops storage traffic', async () => {
  const result = await temporal.writerPoisonPreservesFirstFailureScenario()
  assert.deepEqual(result, {
    appendCalls: 2,
    first: 'CommitUnknown:disk exploded',
    second: 'WriterPoisoned:disk exploded',
  })
})
test('WHAT[verification-system-007] reconcile shutdown closes admission and waits for the running pass', async () => {
  const result = await temporal.reconcileSchedulerStopDrainScenario()
  assert.deepEqual(result, {
    blockedOnRunningPass: true,
    drained: true,
    rejectedKickDidNotRun: true,
    snapshotReads: 1,
  })
})
test('WHAT[verification-system-007] poisoned durable substrate rejects new reconcile admission', async () => {
  const result = await temporal.reconcileSchedulerDurableUnavailableScenario()
  assert.deepEqual(result, {
    rejectedWhileFirstPassBlocked: true,
    snapshotReads: 1,
  })
})
test('WHAT[verification-system-007] plugin scope drains reconcile and admitted Host work before disposal', async () => {
  const result = await temporal.pluginScopeStopDrainScenario()
  assert.deepEqual(result, {
    blockedBeforeRelease: true,
    disposed: true,
    lateBackgroundRejected: true,
    lateOwnedRejected: true,
    stillWaitingForReconcile: true,
    stillWaitingForOwnedWork: true,
  })
})
test('WHAT[verification-system-007] plugin scope preserves detached background failure instead of swallowing it', async () => {
  const result = await temporal.pluginScopeBackgroundFailureScenario()
  assert.deepEqual(result, {
    error: 'background exploded',
    lateBackgroundRejected: true,
  })
})
}
