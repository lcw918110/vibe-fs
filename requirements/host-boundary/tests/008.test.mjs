import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const SessionSnapshotSurface = await import("../../../dist/OpenCode/Host/SessionSnapshotSurface.js");
const ProviderRunBindingSurface = await import("../../../dist/OpenCode/Host/ProviderRunBindingSurface.js");

const projectMessages = SessionSnapshotSurface.projectMessages
const bindableRun = ProviderRunBindingSurface.bindableRun
const observeSequence = ProviderRunBindingSurface.observeSequence
const msg = ({ id, role, parentID, created = 1, completed = false, summary = false } = {}) => ({
  id, role, parentID,
  time: { created, ...(completed ? { completed: true } : {}) },
  summary,
})

test('WHAT[host-boundary-008] host-boundary-008 the bindable run is the unsealed assistant child of the physical user message', () => {
  const physical = 'msg_user_1'
  const messages = projectMessages([
    msg({ id: physical, role: 'user' }),
    msg({ id: 'asst_bindable', role: 'assistant', parentID: physical, completed: false }),
  ])
  const result = bindableRun(physical, messages)
  assert.equal(result.ok, true)
  assert.equal(result.id, 'asst_bindable')
})
test('WHAT[host-boundary-008] host-boundary-008 no bindable run means no ToolContext messageID to treat as the sealed run', () => {
  const physical = 'msg_user_1'
  const messages = projectMessages([msg({ id: physical, role: 'user' })])
  const result = bindableRun(physical, messages)
  assert.equal(result.ok, false)
  assert.equal(result.error, 'NoBindableRun')
})
test('WHAT[host-boundary-008] host-boundary-008 duplicate bindable runs fail closed', () => {
  const physical = 'msg_user_1'
  const messages = projectMessages([
    msg({ id: 'asst_1', role: 'assistant', parentID: physical }),
    msg({ id: 'asst_2', role: 'assistant', parentID: physical }),
  ])
  const result = bindableRun(physical, messages)
  assert.equal(result.ok, false)
  assert.equal(result.error, 'AmbiguousRun')
  assert.equal(result.count, 2)
})
test('WHAT[host-boundary-008] host-boundary-008 projection lag may catch up to the unique bindable run', () => {
  const physical = 'msg_user_1'
  const result = observeSequence(physical, [
    projectMessages([msg({ id: physical, role: 'user' })]),
    projectMessages([
      msg({ id: physical, role: 'user' }),
      msg({ id: 'asst_after_projection', role: 'assistant', parentID: physical }),
    ]),
  ])

  assert.deepEqual(result, {
    ok: true,
    id: 'asst_after_projection',
    reads: 2,
  })
})
test('WHAT[host-boundary-008] host-boundary-008 ambiguity is not retried as projector lag', () => {
  const physical = 'msg_user_1'
  const result = observeSequence(physical, [
    projectMessages([
      msg({ id: 'asst_1', role: 'assistant', parentID: physical }),
      msg({ id: 'asst_2', role: 'assistant', parentID: physical }),
    ]),
    projectMessages([
      msg({ id: physical, role: 'user' }),
      msg({ id: 'asst_later', role: 'assistant', parentID: physical }),
    ]),
  ])

  assert.deepEqual(result, {
    ok: false,
    error: 'AmbiguousRun',
    count: 2,
    reads: 1,
  })
})
test('WHAT[host-boundary-008] host-boundary-008 not-latest rejection is not retried as projector lag', () => {
  const physical = 'msg_user_1'
  const result = observeSequence(physical, [
    projectMessages([
      msg({ id: 'asst_1', role: 'assistant', parentID: physical }),
      msg({ id: 'asst_9', role: 'assistant', parentID: 'msg_other', completed: true }),
    ]),
    projectMessages([
      msg({ id: physical, role: 'user' }),
      msg({ id: 'asst_later', role: 'assistant', parentID: physical }),
    ]),
  ])

  assert.deepEqual(result, {
    ok: false,
    error: 'NotLatestRun',
    reads: 1,
  })
})
test('WHAT[host-boundary-008] host-boundary-008 latest run follows Host creation time rather than lexical ID or list order', () => {
  const physical = 'msg_user_1'
  const olderCandidate = msg({ id: 'zzz-older', role: 'assistant', parentID: physical, created: 10 })
  const newerAssistant = msg({ id: 'aaa-newer', role: 'assistant', parentID: 'msg_other', created: 20 })

  for (const messages of [
    [olderCandidate, newerAssistant],
    [newerAssistant, olderCandidate],
  ]) {
    assert.deepEqual(bindableRun(physical, projectMessages(messages)), {
      ok: false,
      error: 'NotLatestRun',
    })
  }
})
test('WHAT[host-boundary-008] host-boundary-008 invalid Host creation sequence fails closed', () => {
  const physical = 'msg_user_1'

  for (const created of [null, '20', Number.NaN, Number.POSITIVE_INFINITY]) {
    assert.deepEqual(
      bindableRun(physical, projectMessages([
        msg({ id: 'asst-invalid-sequence', role: 'assistant', parentID: physical, created }),
      ])),
      { ok: false, error: 'InsufficientSequence' },
    )
  }
})
test('WHAT[host-boundary-008] host-boundary-008 compaction is not retried as projector lag', () => {
  const physical = 'msg_user_1'
  const result = observeSequence(physical, [
    projectMessages([
      msg({ id: 'asst_compact', role: 'assistant', parentID: physical, summary: true }),
    ]),
    projectMessages([
      msg({ id: physical, role: 'user' }),
      msg({ id: 'asst_later', role: 'assistant', parentID: physical }),
    ]),
  ])

  assert.deepEqual(result, {
    ok: false,
    error: 'NoBindableRun',
    reads: 1,
  })
})
test('WHAT[host-boundary-008] host-boundary-008 projection catch-up is bounded by the production read budget', () => {
  const physical = 'msg_user_1'
  const missing = projectMessages([msg({ id: physical, role: 'user' })])
  const result = observeSequence(physical, Array.from({ length: 8 }, () => missing))

  assert.deepEqual(result, {
    ok: false,
    error: 'NoBindableRun',
    reads: 6,
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const surface = await import('../../../dist/OpenCode/Host/MessageVisibilitySurface.js')
const fakeTimer = () => {
  const deadlines = []
  const port = {
    Delay(ms) {
      let release
      const promise = new Promise((resolve) => {
        release = () => {
          if (!handle.cancelled) resolve()
        }
      })
      const handle = { ms, cancelled: false, Delay: promise, Cancel() { this.cancelled = true }, release }
      deadlines.push(handle)
      return handle
    },
    Dispose() {},
  }
  return { port, deadlines }
}

test('WHAT[host-boundary-008] message_visibility_signal_wakes_waiter_and_cancels_deadline', async () => {
  const timer = fakeTimer()
  const hub = surface.create(timer.port)

  const wait = surface.awaitChange(hub, 'session-a', 10)
  assert.equal(surface.pendingCount(hub, 'session-a'), 1)

  surface.notify(hub, 'session-a')
  await wait

  assert.equal(timer.deadlines[0].cancelled, true, 'event fast path must cancel the deadline backstop')
  assert.equal(surface.pendingCount(hub, 'session-a'), 0, 'settled waiter must leave the registry')
})
test('WHAT[host-boundary-008] deadline_backstop_resolves_when_no_signal_arrives', async () => {
  const timer = fakeTimer()
  const hub = surface.create(timer.port)

  const wait = surface.awaitChange(hub, 'session-b', 10)
  timer.deadlines[0].release()
  await wait

  assert.equal(surface.pendingCount(hub, 'session-b'), 0, 'deadline-settled waiter must leave the registry')
})
test('WHAT[host-boundary-008] foreign_session_signal_never_wakes_waiter', async () => {
  const timer = fakeTimer()
  const hub = surface.create(timer.port)

  let woke = false
  const wait = surface.awaitChange(hub, 'session-c', 10).then(() => { woke = true })

  surface.notify(hub, 'session-other')
  await Promise.resolve()
  assert.equal(woke, false, 'a foreign session signal must not resolve the waiter')
  assert.equal(surface.pendingCount(hub, 'session-c'), 1)

  timer.deadlines[0].release()
  await wait
  assert.equal(woke, true)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const XWireSurface = await import("../../../dist/Context/Prefix/XWireSurface.js");

const baseProjection = {
  messages: [
    { role: 'user', parts: [{ kind: 'text', text: 'hello' }] },
    { role: 'assistant', parts: [{ kind: 'text', text: 'answer' }] },
  ],
}
const acceptedRetryInput = (overrides = {}) => ({
  journal: true,
  sessionId: 'ses_x',
  acceptedRetry: true,
  physicalUser: 'user-1',
  acceptedPhysicalUser: 'user-1',
  currentProjection: baseProjection,
  ...overrides,
})

test('WHAT[host-boundary-008] XWIRE_pre_inference_retry_does_not_require_a_public_session_snapshot', () => {
  const result = XWireSurface.transform(acceptedRetryInput())
  assert.equal(result.ok, true)
  assert.equal(result.noop, false)
  assert.equal(result.consumed, true)
})
}
