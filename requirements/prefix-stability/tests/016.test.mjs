import assert from 'node:assert/strict'
import test from 'node:test'
import { planRequest, resetScopeState, onTurnStart, clearSessionState } from '../../../dist/Context/Scope/Surface.js'
import { normalTransform, DeliveryAdjudication } from '../../../dist/OpenCode/Plugin/PluginTransforms.js'

async function outbound(sessionId, messages) {
  const yes = async () => {}
  const caps = {
    BeginPhysicalProviderAttempt: yes,
    StopCompletedContinuation: async () => false,
    BindSessionStartedAt: async () => null,
    ApplyRelayProjection: async () => null,
    ApplyTenureIsolation: yes,
    CaptureXTraceMessages: async () => ({ Current: null, RawMessages: [], Receipt: null }),
    RefreshCompanionXTrace: () => {},
    ApplyCompanion: yes,
    ApplyXWire: yes,
    FreezeProviderAttemptPlan: yes,
    InjectPairGuideline: yes,
    SettleAndReplaceDeferredInspections: yes,
    SanitizeMessages: () => {},
    AdjudicateDelivery: async () => new DeliveryAdjudication(0, []),
    StopAfterDelivery: yes,
  }
  const out = { messages }
  await normalTransform(caps, sessionId, {}, out)
  return out.messages
}

test('WHAT[prefix-stability-016] later rounds never rewrite bytes already sent for earlier tool results', async () => {
  const sid = 'ses_scope_016_cache'
  const toolMessage = (id, name, output) => ({
    info: { id: 'asst-' + id, role: 'assistant', time: { created: 2 } },
    parts: [{ type: 'tool', callID: id, tool: name, state: { status: 'completed', input: {}, output } }],
  })
  clearSessionState(sid)
  const operator = { info: { id: 'user-016', role: 'user', time: { created: 100 } }, parts: [{ type: 'text', text: 'do the work' }] }
  await outbound(sid, [operator])
  resetScopeState(sid, 7)
  const reset = toolMessage('reset-016', 'resetScope', 'Scope reset')
  const rawResetText = reset.parts[0].state.output
  const first = await outbound(sid, [operator, reset])
  const firstRendered = JSON.stringify(first[1])
  assert.equal(first[1].parts[0].state.output, rawResetText, 'no countdown suffix is appended')
  const read = toolMessage('read-016', 'read', 'new data')
  const second = await outbound(sid, [operator, reset, read])
  assert.equal(JSON.stringify(second[1]), firstRendered,
    'the historical reset result must stay a verbatim prefix across requests')
  assert.equal(second[2].parts[0].state.output, 'new data')
  assert.equal(reset.parts[0].state.output, rawResetText,
    'raw Host history must remain untouched even though the outgoing array changed')
  clearSessionState(sid)
})
const user = (key, timestamp = 1) => ({ kind: 'user', operator: true, key, timestamp, parts: [] })
const assistant = (id, tool) => ({ kind: 'assistant', operator: false, parts: [{ id, tool, terminalField: 'output' }] })

test('WHAT[prefix-stability-016] repeated provider requests plan the same retention for old terminal results', () => {
  const sid = 'pfx-016-open'
  const operator = user('task', 1), reset = assistant('r1', 'resetScope'), read = assistant('read-1', 'read')
  clearSessionState(sid)
  planRequest(sid, [operator])
  resetScopeState(sid, 6)
  const prior = planRequest(sid, [operator, reset])
  onTurnStart(sid)
  const later = planRequest(sid, [operator, reset, read])
  assert.deepEqual(prior.hints, [], 'no countdown tail is planned for any tool result')
  assert.deepEqual(later.hints, [], 'a later request plans no tail either')
  assert.deepEqual(later.slots.map(s => s.index).slice(0, prior.slots.length),
    prior.slots.map(s => s.index), 'the retained prefix keeps its positions')
  assert.equal(later.slots.length, prior.slots.length + 1, 'the new terminal result is appended, not merged')
  clearSessionState(sid)
})

test('WHAT[prefix-stability-016] new operator steer is not a cold prefix boundary', () => {
  const sid = 'pfx-016-steer'
  const operator = user('task', 1), reset = assistant('r1', 'resetScope')
  clearSessionState(sid)
  planRequest(sid, [operator])
  resetScopeState(sid, 5)
  const previous = planRequest(sid, [operator, reset])
  const next = planRequest(sid, [operator, reset, user('steer', 2)])
  assert.equal(next.initialized, true)
  assert.equal(next.strong, null)
  assert.deepEqual(next.hints, [])
  assert.deepEqual(next.slots.slice(0, previous.slots.length).map(s => s.index),
    previous.slots.map(s => s.index), 'the earlier plan survives a new operator message')
  clearSessionState(sid)
})
