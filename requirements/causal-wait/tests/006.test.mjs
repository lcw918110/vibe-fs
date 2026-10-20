import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { assertOpaque } = await import("../../verification-system/tests/support/js-contract.mjs");

const causal = await import('../../../dist/Execution/Session/Wait/Surface.js')
const process = await import('../../../dist/Process/Surface.js')
const owner = (id) => causal.owner('flow', { id })
const external = (id) => causal.externalProducer('capability', { id })
const waitFor = (ownerId, producerId, waitKind = 'capability') =>
  causal.createWait({
    waitKind,
    owner: owner(ownerId),
    subject: { target: producerId },
    producer: external(producerId),
    escapes: [causal.escape('processLifetime')],
    source: 'causal-wait.test',
  })
const deferred = () => {
  let resolve
  let reject
  const promise = new Promise((resolveValue, rejectValue) => {
    resolve = resolveValue
    reject = rejectValue
  })
  return {
    promise,
    resolve,
    reject,
    cancel: () => reject(new Error('Operation Cancelled')),
  }
}
const lastExit = (registry) => {
  const history = causal.snapshot(registry).history
  assert.ok(history.length > 0, 'expected history')
  assert.ok(history.at(-1).exit, 'expected leave exit')
  return history.at(-1).exit
}
const activeCount = (registry) => causal.snapshot(registry).active.length

test('WHAT[causal-wait-006] RED_2_resolve_clears_active_and_records_resolved', async () => {
  const registry = causal.createRegistry()
  const pending = deferred()
  const awaited = causal.awaitTask(registry, waitFor('A', 'X'), pending.promise)

  assert.equal(activeCount(registry), 1, 'visible while pending')
  pending.resolve('ok')
  assert.equal(await awaited, 'ok')
  assert.equal(activeCount(registry), 0)
  assert.equal(lastExit(registry), 'WaitResolved')
})
test('WHAT[causal-wait-006] RED_3_fail_clears_active_and_records_failed', async () => {
  const registry = causal.createRegistry()
  const pending = deferred()
  const awaited = causal.awaitTask(registry, waitFor('A', 'X'), pending.promise)

  pending.reject(new Error('boom'))
  await assert.rejects(() => awaited, /boom/)
  assert.equal(activeCount(registry), 0)
  assert.equal(lastExit(registry), 'WaitFailed')
})
test('WHAT[causal-wait-006] RED_4_cancel_clears_active_and_records_cancelled', async () => {
  const registry = causal.createRegistry()
  const pending = deferred()
  const awaited = causal.awaitTask(registry, waitFor('A', 'X'), pending.promise)

  pending.cancel()
  await assert.rejects(() => awaited)
  assert.equal(activeCount(registry), 0)
  assert.equal(lastExit(registry), 'WaitCancelled')
})
test('WHAT[causal-wait-006] RED_4_cancel_message_also_classifies_as_cancelled', async () => {
  const registry = causal.createRegistry()
  const pending = deferred()
  const awaited = causal.awaitTask(registry, waitFor('A', 'X'), pending.promise)

  pending.reject(new Error('Operation Cancelled'))
  await assert.rejects(() => awaited, /Cancel/)
  assert.equal(lastExit(registry), 'WaitCancelled')
  assert.equal(activeCount(registry), 0)
})
test('WHAT[causal-wait-006] history_capacity_bounds_ring_buffer', () => {
  const registry = causal.createRegistry(2)
  for (let i = 0; i < 3; i += 1) {
    const lease = causal.enter(registry, waitFor('A', `X${i}`))
    causal.markExit(lease, 'WaitResolved')
    causal.dispose(lease)
  }

  const history = causal.snapshot(registry).history
  assert.equal(history.length, 2)
  assert.ok(history.length <= 2)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { default: fs } = await import("node:fs");
const { default: os } = await import("node:os");
const { default: path } = await import("node:path");

const causal = await import('../../../dist/Execution/Session/Wait/Surface.js')
const owner = (id) => causal.owner('flow', { id })
const readDiagnostic = (workspace) =>
  JSON.parse(fs.readFileSync(path.join(workspace, '.Herdchestra', 'diagnostics', 'causal-waits.json'), 'utf8'))
const write = async (descriptor) => {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'escape-taxonomy-'))
  const registry = causal.createRegistry()
  const lease = causal.enter(registry, descriptor)
  await causal.writeSnapshot(workspace, registry)
  return { workspace, lease, registry }
}

test('WHAT[causal-wait-006] CAUSAL_006_wait_escape_has_five_typed_cases', () => {
  const tags = [
    causal.escape('deadlineAt', '2026-01-01T00:00:00Z'),
    causal.escape('cancelledBy', owner('review-attempt')),
    causal.escape('processLifetime'),
    causal.escape('sessionLifetime'),
    causal.escape('openEndedExternal'),
  ].map((value) => value.kind)

  assert.deepEqual(tags, ['deadlineAt', 'cancelledBy', 'processLifetime', 'sessionLifetime', 'openEndedExternal'])
})
test('WHAT[causal-wait-006] CAUSAL_006_escapes_render_distinctly_in_diagnostics', async () => {
  const wait = causal.createWait({
    waitKind: 'escape-taxonomy',
    owner: owner('A'),
    subject: { target: 'X' },
    producer: causal.externalProducer('capability', { id: 'X' }),
    escapes: [
      causal.escape('deadlineAt', '2026-01-01T00:00:00Z'),
      causal.escape('cancelledBy', owner('review-attempt')),
      causal.escape('processLifetime'),
      causal.escape('sessionLifetime'),
      causal.escape('openEndedExternal'),
    ],
    source: 'escape-taxonomy.test',
  })

  const { workspace, lease } = await write(wait)
  try {
    const snap = readDiagnostic(workspace)
    assert.equal(snap.active.length, 1)
    const tags = snap.active[0].escapes.map(({ tag }) => tag).sort()
    assert.deepEqual(tags, ['cancelledBy', 'deadlineAt', 'openEndedExternal', 'processLifetime', 'sessionLifetime'])
  } finally {
    causal.dispose(lease)
    fs.rmSync(workspace, { recursive: true, force: true })
  }
})
test('WHAT[causal-wait-006] CAUSAL_006_deadline_escape_carries_typed_instant', async () => {
  const wait = causal.createWait({
    waitKind: 'deadline-escape',
    owner: owner('A'),
    subject: {},
    producer: causal.externalProducer('capability', { id: 'X' }),
    escapes: [causal.escape('deadlineAt', '2026-01-01T00:00:00Z')],
    source: 'escape-taxonomy.test',
  })

  const { workspace, lease } = await write(wait)
  try {
    const [{ tag: deadlineKind, at: deadlineAt }] = readDiagnostic(workspace).active[0].escapes
    assert.equal(deadlineKind, 'deadlineAt')
    assert.match(deadlineAt, /^2026-01-01T00:00:00(\.\d+)?(Z|\+00:00)$/)
  } finally {
    causal.dispose(lease)
    fs.rmSync(workspace, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { assertOpaque } = await import("../../verification-system/tests/support/js-contract.mjs");

const causal = await import('../../../dist/Execution/Session/Wait/Surface.js')
const owner = (id) => causal.owner('flow', { id })
const descriptor = (id) =>
  causal.createWait({
    waitKind: 'lifecycle-wait',
    owner: owner(id),
    subject: { target: id },
    producer: causal.externalProducer('capability', { id }),
    escapes: [causal.escape('processLifetime')],
    source: 'wait-lifecycle.test',
  })
const lastTransition = (registry) => {
  const history = causal.snapshot(registry).history
  assert.ok(history.length > 0, 'expected history')
  return history.at(-1)
}

test('WHAT[causal-wait-006] CAUSAL_006_dispose_defaults_to_wait_disposed', () => {
  const registry = causal.createRegistry()
  const lease = causal.enter(registry, descriptor('A'))
  assertOpaque(lease, 'wait lease')
  causal.dispose(lease)

  const transition = lastTransition(registry)
  assert.equal(transition.kind, 'Left')
  assert.equal(transition.exit, 'WaitDisposed')
  assert.equal(causal.snapshot(registry).active.length, 0)
})
test('WHAT[causal-wait-006] CAUSAL_006_mark_exit_then_dispose_preserves_exit', () => {
  const registry = causal.createRegistry()
  const lease = causal.enter(registry, descriptor('A'))
  causal.markExit(lease, 'WaitCancelled')
  causal.dispose(lease)
  assert.equal(lastTransition(registry).exit, 'WaitCancelled')
})
test('WHAT[causal-wait-006] CAUSAL_006_repeated_mark_exit_last_one_wins', () => {
  const registry = causal.createRegistry()
  const lease = causal.enter(registry, descriptor('A'))
  causal.markExit(lease, 'WaitResolved')
  causal.markExit(lease, 'WaitFailed')
  causal.dispose(lease)
  assert.equal(lastTransition(registry).exit, 'WaitFailed')
})
test('WHAT[causal-wait-006] CAUSAL_006_dispose_is_idempotent_single_leave', () => {
  const registry = causal.createRegistry()
  const lease = causal.enter(registry, descriptor('A'))
  causal.dispose(lease)
  causal.dispose(lease)

  const ended = causal.snapshot(registry)
  causal.markExit(lease, 'WaitResolved')
  causal.dispose(lease)
  assert.deepEqual(causal.snapshot(registry), ended, 'a late mark cannot alter the already recorded exit')

  const leaves = causal.snapshot(registry).history.filter((transition) => transition.kind === 'Left')
  assert.equal(leaves.length, 1)
  assert.equal(causal.snapshot(registry).active.length, 0)
})
test('WHAT[causal-wait-006] CAUSAL_006_reenter_is_fresh_observation_not_revival', () => {
  const registry = causal.createRegistry()
  const first = causal.enter(registry, descriptor('A'))
  const sequenceAfterEnter = causal.snapshot(registry).sequence
  causal.dispose(first)
  const sequenceAfterLeave = causal.snapshot(registry).sequence
  assert.ok(sequenceAfterLeave > sequenceAfterEnter, 'leave must advance the observation sequence')

  const second = causal.enter(registry, descriptor('A'))
  assert.ok(causal.snapshot(registry).sequence > sequenceAfterLeave)
  assert.equal(causal.snapshot(registry).active.length, 1)
  causal.dispose(second)
  assert.equal(causal.snapshot(registry).active.length, 0)
})
test('WHAT[causal-wait-006] CAUSAL_006_history_default_capacity_is_256', () => {
  assert.equal(causal.historyCapacity(causal.createRegistry()), 256)
})
}

{
  const assert = (await import('node:assert/strict')).default
  const causal = await import('../../../dist/Execution/Session/Wait/Surface.js')
  const temporal = await import('../../../dist/Process/Surface.js')
  const descriptor = causal.createWait({
    waitKind: 'controlled-failing-wait',
    owner: causal.owner('workflow', { id: 'failure' }),
    subject: { target: 'material' },
    producer: causal.externalProducer('signal', { id: 'signal' }),
    escapes: [causal.escape('deadlineAt', '2000-01-01T00:00:00.100Z')],
    source: 'failure-boundary',
  })

  for (const failureAt of ['read', 'signal']) {
    test(`WHAT[causal-wait-006] ${failureAt} failure records failure, cancels the unused deadline and cannot revive the wait`, async () => {
      const registry = causal.createRegistry()
      const timer = temporal.createVirtualTimer()
      const deadline = temporal.timerDelay(timer, 100)
      const fail = () => { throw new Error(`controlled ${failureAt} failure`) }
      try {
        await assert.rejects(causal.untilSignalOrDeadline(registry, descriptor, deadline,
          failureAt === 'read' ? fail : () => null,
          failureAt === 'signal' ? fail : () => new Promise(() => {})), /controlled/)
        const ended = causal.snapshot(registry)
        assert.equal(ended.active.length, 0)
        assert.equal(ended.history.at(-1).exit, 'WaitFailed')
        assert.equal(deadline.cancelCount, 1)
        temporal.timerAdvance(timer, 1000)
        await new Promise(resolve => setImmediate(resolve))
        assert.deepEqual(causal.snapshot(registry), ended)
      } finally {
        temporal.timerDispose(timer)
      }
    })
  }
}
