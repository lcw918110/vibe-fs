import test from 'node:test'
import assert from 'node:assert/strict'
import { normalTransform, DeliveryAdjudication } from '../../../dist/OpenCode/Plugin/PluginTransforms.js'
import { resetScopeState, clearSessionState } from '../../../dist/Context/Scope/Surface.js'

// [PRODUCT-RETIRED estimate] host/scope-steer/missing-estimate now asks for a retention budget.
const MISSING_ESTIMATE = /no retention budget yet[\s\S]*newScopeRetentionBudget/
const user = (id, text, created) => ({ info: { id, role: 'user', time: { created } }, parts: [{ type: 'text', text }] })
const assistant = (id, ...parts) => ({ info: { id, role: 'assistant', time: { created: 2 } }, parts })
const tool = (callID, name, output, status = 'completed') => ({ type: 'tool', callID, tool: name, state: { status, input: {}, ...(status === 'error' ? { error: output } : { output }) } })
const toolText = (msg) => msg.parts[0].state.output ?? msg.parts[0].state.error

function safeCaps() {
  const yes = async () => {}
  return {
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
}

async function outbound(sessionId, messages) {
  const out = { messages }
  await normalTransform(safeCaps(), sessionId, {}, out)
  return out.messages
}

test('WHAT[context-compression-039] OpenCode sends a scoped request copy that asks for the first estimate without changing the Host history', async () => {
  const sid = 'ses_scope_039_initial'
  clearSessionState(sid)
  const raw = [user('u1', 'read this file', 1), assistant('a1', tool('c1', 'read', 'file body'))]
  const original = [...raw]
  const copy = await outbound(sid, raw)
  assert.equal(copy, raw, 'OpenCode requires the provider request array to be edited in place')
  assert.equal(copy[0].parts[0].text, 'read this file')
  assert.equal(copy[1].parts[0].state.output, 'file body', 'a completed result keeps its exact bytes')
  assert.equal(copy[2].steering, true)
  assert.equal(copy[2].strong, true)
  assert.match(copy[2].parts[0].text, MISSING_ESTIMATE)
  assert.equal(original[0].parts[0].text, 'read this file')
  assert.equal(toolText(original[1]), 'file body')
  clearSessionState(sid)
})

test('WHAT[context-compression-039] OpenCode applies a reset, preserves only its call and later work, and adds no countdown suffix', async () => {
  const sid = 'ses_scope_039_reset'
  clearSessionState(sid)
  const operator = user('u1', 'scope initial', 100)
  await outbound(sid, [operator])
  resetScopeState(sid, 4)
  const raw = [operator, assistant('a1', tool('old', 'read', 'old')), assistant('a2', tool('reset', 'resetScope', 'Scope reset')), assistant('a3', tool('next', 'read', 'new'))]
  const original = [...raw]
  const copy = await outbound(sid, raw)
  assert.equal(copy.length, 4, 'operator, reset and later work survive; the pre-reset result is dropped')
  assert.equal(copy[1].parts[0].tool, 'resetScope')
  assert.equal(copy[1].parts[0].state.output, 'Scope reset', 'the completed reset result keeps its exact bytes')
  assert.equal(copy[2].parts[0].state.output, 'new')
  assert.match(copy[3].parts[0].text, MISSING_ESTIMATE, 'an unproven progress state asks for a real estimate')
  assert.equal(original[3].parts[0].state.output, 'new', 'the original result object is not edited')
  clearSessionState(sid)
})
