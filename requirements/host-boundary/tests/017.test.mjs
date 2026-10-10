import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const EventsSurface = await import("../../../dist/OpenCode/Host/EventsSurface.js");

const notify = (port, sessionId, outcome) => EventsSurface.notify(port, sessionId, outcome.kind, outcome.providerRun ?? '', outcome.error ?? outcome.value ?? '')
const completed = (providerRun = '') => ({ kind: 'Completed', providerRun })
const failed = (error) => ({ kind: 'Failed', error })
const aborted = (reason) => ({ kind: 'Aborted', error: reason })

test('WHAT[host-boundary-017] HOST_CTX_notifyCompleted_rejects_unknown_or_blank_roles', () => {
  const port = EventsSurface.create()
  const received = []
  EventsSurface.subscribe(port, (_, outcome) => received.push(outcome.kind))

  assert.equal(EventsSurface.notifyCompleted(port, 'ses_role', 'done', 'done', 'coder'), true)
  assert.equal(EventsSurface.notifyCompleted(port, 'ses_role', 'ignored', 'ignored', 'reviewer'), false)
  assert.equal(EventsSurface.notifyCompleted(port, 'ses_role', 'ignored', 'ignored', 'unknown-role'), false)
  assert.equal(EventsSurface.notifyCompleted(port, 'ses_role', 'ignored', 'ignored', ''), false)
  assert.equal(EventsSurface.notifyCompleted(port, 'ses_role', 'ignored', 'ignored', null), false)
  assert.deepEqual(received, ['Completed'])
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const HostSessionContext = await import("../../../dist/OpenCode/Host/HostSessionContextSurface.js");

const roleOf = HostSessionContext.roleOf
const read = HostSessionContext.read
const labelOf = (role) => role ?? undefined

test('WHAT[host-boundary-017] HOST_CTX_roleOf_rejects_absent_and_blank_agents', () => {
  assert.equal(roleOf(null), undefined)
  assert.equal(roleOf(undefined), undefined)
  assert.equal(roleOf(''), undefined)
  assert.equal(roleOf('  '), undefined)
})
test('WHAT[host-boundary-017] HOST_CTX_roleOf_resolves_managed_identity_and_rejects_aliases', () => {
  assert.equal(labelOf(roleOf('coder')), 'coder')
  assert.equal(labelOf(roleOf('inspector')), 'inspector')
  assert.equal(labelOf(roleOf('plan')), 'plan')
  assert.equal(roleOf('build'), undefined)
  assert.equal(roleOf('reviewer'), undefined)
})
test('WHAT[host-boundary-017] HOST_CTX_read_tolerates_null_and_shapeless_events', () => {
  assert.deepEqual(read(null), ['', undefined])
  assert.deepEqual(read(undefined), ['', undefined])
  assert.deepEqual(read({}), ['', undefined])
})
test('WHAT[host-boundary-017] HOST_CTX_read_prefers_properties_session_id', () => {
  const raw = { event: { properties: { sessionID: 'ses_props' }, sessionID: 'ses_event' } }
  assert.deepEqual(read(raw), ['ses_props', undefined])
})
test('WHAT[host-boundary-017] HOST_CTX_read_accepts_a_bare_message_without_event_wrapper', () => {
  assert.deepEqual(read({ sessionID: 'ses_bare' }), ['ses_bare', undefined])
  assert.deepEqual(read({ properties: { sessionID: 'ses_top' } }), ['ses_top', undefined])
})
test('WHAT[host-boundary-017] HOST_CTX_read_extracts_the_agent_only_from_properties_info', () => {
  assert.deepEqual(read({ event: { properties: { sessionID: 'ses_a', info: { agent: 'manager' } } } }), ['ses_a', 'manager'])
  assert.deepEqual(read({ event: { sessionID: 'ses_a', agent: 'manager' } }), ['ses_a', undefined])
})
}
