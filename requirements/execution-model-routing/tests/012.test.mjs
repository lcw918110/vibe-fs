import test from 'node:test'


{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const target = { model: 'provider/shared', reasoning: 'none' }
const identity = (overrides = {}) => ({
  sessionId: 'session-a',
  physicalUserMessageId: 'message-a',
  role: 'engineer',
  participant: 'alice',
  target,
  ...overrides,
})
const acquire = async (runtime, exact = identity()) => {
  const outcome = await routing.acquireExecutionAdmission(
    runtime,
    exact.sessionId,
    exact.physicalUserMessageId,
    exact.role,
    exact.participant,
    exact.lenderSessionId ?? null,
  )
  assert.equal(outcome.kind, 'Acquired')
  return outcome.lease
}
const conflict = (outcome) => assert.deepEqual(outcome, { kind: 'Conflict' })

test('WHAT[execution-model-routing-012] capacity lifecycle admits every legal fenced transition', async () => {
  const beforeProvider = routing.createRuntime(() => target)
  const beforeProviderLease = await acquire(beforeProvider)
  assert.equal(routing.executionAdmissionLifecycle(beforeProvider, beforeProviderLease), 'Pending')
  assert.deepEqual(
    routing.releaseExecutionAdmissionBeforeProvider(beforeProvider, beforeProviderLease, identity()),
    { kind: 'Applied' },
  )
  assert.equal(routing.executionAdmissionLifecycle(beforeProvider, beforeProviderLease), 'Released')

  const provider = routing.createRuntime(() => target)
  const providerLease = await acquire(provider)
  assert.deepEqual(routing.commitExecutionAdmission(provider, providerLease, identity()), { kind: 'Applied' })
  assert.equal(routing.executionAdmissionLifecycle(provider, providerLease), 'Committed')
  routing.releasePhysicalExecution(provider, identity().sessionId, identity().physicalUserMessageId)
  assert.equal(routing.executionAdmissionLifecycle(provider, providerLease), 'Released')
})
test('WHAT[execution-model-routing-012] capacity lifecycle rejects every illegal edge and opposite terminal', async () => {
  const committedRuntime = routing.createRuntime(() => target)
  const committed = await acquire(committedRuntime)
  routing.commitExecutionAdmission(committedRuntime, committed, identity())
  conflict(routing.releaseExecutionAdmissionBeforeProvider(committedRuntime, committed, identity()))

  const releasedRuntime = routing.createRuntime(() => target)
  const released = await acquire(releasedRuntime)
  routing.releaseExecutionAdmissionBeforeProvider(releasedRuntime, released, identity())
  conflict(routing.commitExecutionAdmission(releasedRuntime, released, identity()))
  assert.deepEqual(
    routing.releaseExecutionAdmissionBeforeProvider(releasedRuntime, released, identity()),
    { kind: 'AlreadyApplied' },
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const target = (model = 'provider/shared', reasoning = 'none') => ({ model, reasoning })
const identity = (overrides = {}) => ({
  sessionId: 'session-a',
  physicalUserMessageId: 'message-a',
  role: 'engineer',
  participant: 'alice',
  target: target(),
  ...overrides,
})
const acquire = async (runtime, exact = identity()) => {
  const outcome = await routing.acquireExecutionAdmission(
    runtime,
    exact.sessionId,
    exact.physicalUserMessageId,
    exact.role,
    exact.participant,
    exact.lenderSessionId ?? null,
  )
  assert.equal(outcome.kind, 'Acquired')
  return outcome.lease
}
const conflict = (outcome) => assert.deepEqual(outcome, { kind: 'Conflict' })

test('WHAT[execution-model-routing-012] admission lease is opaque and projects only its frozen target', async () => {
  const runtime = routing.createRuntime(() => target())
  const lease = await acquire(runtime)

  assert.deepEqual(routing.executionAdmissionTarget(runtime, lease), target())
  assert.equal(await acquire(runtime), lease)
  routing.commitExecutionAdmission(runtime, lease, identity())
  assert.equal(routing.tryReadExecution(runtime, identity().sessionId, identity().physicalUserMessageId), lease)
  assert.throws(
    () => JSON.stringify(lease),
    /ExecutionAdmissionToken is process-local and cannot be serialized/,
    'the live process capability must refuse native JSON serialization',
  )
  assert.deepEqual(routing.commitExecutionAdmission(runtime, {}, identity()), { kind: 'StaleFence' })
})
test('WHAT[execution-model-routing-012] admission lease permits one terminal transition and idempotent duplicate', async () => {
  const runtime = routing.createRuntime(() => target())
  const committed = await acquire(runtime)

  assert.deepEqual(routing.commitExecutionAdmission(runtime, committed, identity()), { kind: 'Applied' })
  assert.deepEqual(routing.commitExecutionAdmission(runtime, committed, identity()), { kind: 'AlreadyApplied' })
  conflict(routing.releaseExecutionAdmissionBeforeProvider(runtime, committed, identity()))

  const releasedIdentity = identity({ sessionId: 'session-b', physicalUserMessageId: 'message-b' })
  const released = await acquire(runtime, releasedIdentity)
  assert.deepEqual(
    routing.releaseExecutionAdmissionBeforeProvider(runtime, released, releasedIdentity),
    { kind: 'Applied' },
  )
  assert.deepEqual(
    routing.releaseExecutionAdmissionBeforeProvider(runtime, released, releasedIdentity),
    { kind: 'AlreadyApplied' },
  )
  conflict(routing.commitExecutionAdmission(runtime, released, releasedIdentity))
})
test('WHAT[execution-model-routing-012] rejects commit with the wrong role', async () => {
  const runtime = routing.createRuntime(() => target())
  const lease = await acquire(runtime)

  conflict(routing.commitExecutionAdmission(runtime, lease, identity({ role: 'devops' })))
  assert.equal(routing.snapshotOccupied(runtime).length, 1, 'wrong role cannot settle capacity')
  assert.deepEqual(routing.commitExecutionAdmission(runtime, lease, identity()), { kind: 'Applied' })
})
test('WHAT[execution-model-routing-012] rejects commit with the wrong participant', async () => {
  const runtime = routing.createRuntime(() => target())
  const lease = await acquire(runtime)

  conflict(routing.commitExecutionAdmission(runtime, lease, identity({ participant: 'bob' })))
  assert.equal(routing.snapshotOccupied(runtime).length, 1, 'wrong participant cannot settle capacity')
  assert.deepEqual(routing.commitExecutionAdmission(runtime, lease, identity()), { kind: 'Applied' })
})
test('WHAT[execution-model-routing-012] rejects release from another physical message and every wrong exact identity field', async () => {
  const runtime = routing.createRuntime(() => target())
  const lease = await acquire(runtime)

  for (const change of [
    { sessionId: 'other-session' },
    { physicalUserMessageId: 'other-message' },
    { role: 'devops' },
    { participant: 'bob' },
    { target: { model: 'provider/other', reasoning: 'none' } },
  ]) {
    conflict(routing.releaseExecutionAdmissionBeforeProvider(runtime, lease, identity(change)))
  }

  assert.equal(routing.snapshotOccupied(runtime).length, 1, 'wrong identity cannot release capacity')
})
test('WHAT[execution-model-routing-012] fabricated fences and foreign custody fail closed at every settlement API', async () => {
  const runtime = routing.createRuntime(() => target())
  const lease = await acquire(runtime)
  assert.deepEqual(routing.commitExecutionAdmission(runtime, lease, identity()), { kind: 'Applied' })

  // A fabricated fence never carries the exact capacity identity: every
  // settlement API must fail closed with StaleFence, not mutate the ledger.
  const fabricated = [null, undefined, {}, 'fence-string', 42, { fence: 'x' }, { sessionId: 'session-a' }]
  for (const fake of fabricated) {
    assert.deepEqual(
      routing.commitExecutionAdmission(runtime, fake, identity()),
      { kind: 'StaleFence' },
      `commit must reject fabricated fence ${JSON.stringify(fake) ?? String(fake)}`,
    )
    assert.deepEqual(
      routing.releaseExecutionAdmissionBeforeProvider(runtime, fake, identity()),
      { kind: 'StaleFence' },
      `release must reject fabricated fence ${JSON.stringify(fake) ?? String(fake)}`,
    )
  }

  // Direct foreign custody construction: a shared-custody token minted
  // outside the owner (plain object, null, primitive) is equally fenced out.
  for (const fake of [null, {}, { token: 'x' }, 'str']) {
    assert.deepEqual(
      routing.commitSharedExecutionAdmission(fake, identity()),
      { kind: 'StaleFence' },
      `shared commit must reject foreign custody ${JSON.stringify(fake) ?? String(fake)}`,
    )
    assert.deepEqual(
      routing.releaseSharedExecutionAdmissionBeforeProvider(fake, identity()),
      { kind: 'StaleFence' },
      `shared release must reject foreign custody ${JSON.stringify(fake) ?? String(fake)}`,
    )
  }

  // The real lease stays settled exactly once; no fabricated call disturbed it.
  assert.deepEqual(routing.commitExecutionAdmission(runtime, lease, identity()), { kind: 'AlreadyApplied' })
})

test.todo('WHAT[execution-model-routing-012] compiler rejects fabricated exact opaque capacity fences and direct foreign custody construction (GAP-128: F# type-level opacity proof pending — the runtime fail-closed test above is retained but does not prove compile-time rejection)')
}
