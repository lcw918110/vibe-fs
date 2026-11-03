import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const {
  sessionCreate,
  sessionView,
  sessionSetClosed,
  sessionSetBackend,
  sessionPushPending,
  sessionResolveExit,
  ptyCommandRead,
} = await import('../../../dist/Process/Surface.js')
const command = ptyCommandRead()

test('WHAT[process-execution-009] PTY_SESSION_create_sets_id_and_backend', () => {
  const backend = { pid: 1234 }
  const session = sessionCreate('pty-abc', backend)
  const view = sessionView(session)
  assert.equal(view.ptyId, 'pty-abc')
  assert.equal(view.backend, backend)
})
test('WHAT[process-execution-009] PTY_SESSION_create_defaults_open_empty_and_pending', () => {
  const view = sessionView(sessionCreate('pty-def', null))
  assert.equal(view.backend, null)
  assert.equal(view.closed, false)
  assert.equal(view.pendingCount, 0)
  assert.equal(view.exitPending, true)
})
test('WHAT[process-execution-009] PTY_SESSION_exit_completion_starts_unresolved', () => {
  const session = sessionCreate('pty-ghi', null)
  assert.equal(sessionView(session).exitPending, true)
  sessionResolveExit(session)
  assert.equal(sessionView(session).exitPending, false)
})
test('WHAT[process-execution-009] PTY_SESSION_mutable_state_roundtrips', () => {
  const session = sessionCreate('pty-jkl', null)

  sessionSetClosed(session, true)
  assert.equal(sessionView(session).closed, true)

  const backend = { pid: 42 }
  sessionSetBackend(session, backend)
  assert.equal(sessionView(session).backend, backend)

  sessionPushPending(session, command)
  assert.equal(sessionView(session).pendingCount, 1)
})
}
