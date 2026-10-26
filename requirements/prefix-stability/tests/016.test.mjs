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
    IsPureChatSession: () => false,
    AdjudicateDelivery: async () => new DeliveryAdjudication(0, []),
    StopAfterDelivery: yes,
  }
  const out = { messages }
  await normalTransform(caps, sessionId, {}, out)
  return out.messages
}

test('WHAT[prefix-stability-016] OpenCode provider transform preserves old completed output bytes after turnBudget decreases', async () => {
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
  assert.match(first[1].parts[0].state.output, /Remaining turnBudget: 6/)
  const read = toolMessage('read-016', 'read', 'new data')
  const second = await outbound(sid, [operator, reset, read])
  assert.equal(JSON.stringify(second[1]), firstRendered,
    'the historical reset result must remain a verbatim prefix, including its old budget suffix')
  assert.match(second[2].parts[0].state.output, /Remaining turnBudget: 5/)
  assert.equal(reset.parts[0].state.output, rawResetText,
    'raw Host history must remain untouched even though the outgoing array changed')
  clearSessionState(sid)
})
const user = (key, timestamp = 1) => ({ kind: 'user', operator: true, key, timestamp, parts: [] })
const assistant = (id, tool) => ({ kind: 'assistant', operator: false, parts: [{ id, tool, terminalField: 'output' }] })
const tail = (plan, index) => plan.hints.find(x => x.kind === 'tool' && x.index === index)?.tail

test('WHAT[prefix-stability-016] repeated provider requests replay each old terminal result verbatim', () => {
  const sid = 'pfx-016-open'
  const operator = user('task', 1), reset = assistant('r1', 'resetScope'), read = assistant('read-1', 'read')
  clearSessionState(sid)
  planRequest(sid, [operator])
  resetScopeState(sid, 6)
  const prior = planRequest(sid, [operator, reset])
  assert.equal(tail(prior, 1), '[Remaining turnBudget: 6]')
  onTurnStart(sid)
  const later = planRequest(sid, [operator, reset, read])
  assert.equal(tail(later, 1), tail(prior, 1), 'the earliest previous result must not change when time advances')
  assert.equal(tail(later, 2), '[Remaining turnBudget: 5]', 'the new result carries the current budget')
  clearSessionState(sid)
})

test('WHAT[prefix-stability-016] new operator steer preserves old result bytes without resetScope reanchor', () => {
  const sid = 'pfx-016-steer'
  const operator = user('task', 1), reset = assistant('r1', 'resetScope')
  clearSessionState(sid)
  planRequest(sid, [operator])
  resetScopeState(sid, 5)
  const previous = planRequest(sid, [operator, reset])
  const next = planRequest(sid, [operator, reset, user('steer', 2)])
  assert.equal(next.initialized, true)
  assert.equal(next.budget, previous.budget)
  assert.equal(next.strong, null)
  assert.equal(tail(next, 1), tail(previous, 1), 'steer is not a cold prefix boundary')
  clearSessionState(sid)
})
