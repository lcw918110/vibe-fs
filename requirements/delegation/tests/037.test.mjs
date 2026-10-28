import test from 'node:test'
import assert from 'node:assert/strict'
import {withRealFork} from './support/real-fork.mjs'

const steerPart = { type: 'text', text: 'answer', metadata: { wanxiangshu_consultation_steer: true } }
const user = (sid, id, parts) => ({ info: { id, role: 'user', sessionID: sid }, parts })
const assistant = (sid, id, parts, completed = 5) =>
  ({ info: { id, role: 'assistant', sessionID: sid, time: completed == null ? {} : { completed } }, parts })
const tool = (name, callID) => ({ type: 'tool', tool: name, callID, state: { status: 'completed', input: {}, output: 'ok' } })

// Field incident 2026-10-28 00:11:47 UTC (ses_e80299a17ffe…): an Engineer took a
// previous-Scope answer, kept working, then called resetScope. The next step was
// aborted 48 ms after it began and nothing ever resumed it; the Manager's join
// waited forever. Eleven such aborts (each right after resetScope, never resumed)
// appear in opencode.log on 2026-10-27/28.
test('WHAT[delegation-037] a consultation answer the agent already worked past never stops the live loop', async () => {
  await withRealFork('manager-consultation-consumed-037', async ({ hooks, runtime }) => {
    const cases = {
      // Raw history: the turn before the answer is tool-free prose, but the agent
      // has already answered it with further steps.
      'consumed-after-prose': sid => [
        user(sid, 'u', [{ type: 'text', text: 'task' }]),
        assistant(sid, 'a0', [{ type: 'text', text: 'Looking into it.' }]),
        user(sid, 's', [steerPart]),
        assistant(sid, 'a1', [tool('todowrite', 'c1')]),
        assistant(sid, 'a2', [tool('resetScope', 'c2')]),
      ],
      // Busy loop: the turn before the answer called a tool and the agent then
      // committed resetScope. Projecting that reset retires the old tool parts,
      // which must not turn this into a "finished tool-free turn".
      'busy-then-reset': sid => [
        user(sid, 'u', [{ type: 'text', text: 'task' }]),
        assistant(sid, 'a0', [{ type: 'text', text: 'Plan.' }]),
        assistant(sid, 'a1', [tool('todowrite', 'c1')]),
        user(sid, 's', [steerPart]),
        assistant(sid, 'a2', [tool('todowrite', 'c3')]),
        assistant(sid, 'a3', [tool('resetScope', 'c4')]),
      ],
    }
    for (const [name, build] of Object.entries(cases)) {
      const sid = `consult-037-${name}`
      const output = { messages: build(sid) }
      const before = runtime.abortedIds.length
      await assert.rejects(hooks['experimental.chat.messages.transform']({}, output), /accepted-execution-missing/,
        `${name}: the request must proceed to the ordinary stages`)
      assert.deepEqual(runtime.abortedIds.slice(before), [], `${name}: no AbortSession for a live loop`)
    }

    // Unchanged boundary: an answer landing after a finished tool-free turn still stops.
    const sid = 'consult-037-late'
    const output = { messages: [
      user(sid, 'u', [{ type: 'text', text: 'task' }]),
      assistant(sid, 'a', [{ type: 'text', text: 'Finished' }]),
      user(sid, 's', [steerPart]),
    ] }
    const before = runtime.abortedIds.length
    await hooks['experimental.chat.messages.transform']({}, output)
    assert.deepEqual(output.messages, [])
    assert.deepEqual(runtime.abortedIds.slice(before), [sid])
  })
})

// Seam proof on the production transform: the stop is judged once, on the raw
// history, and the projected copy (which hollows out the pre-answer todowrite
// turn after a later resetScope) is never re-judged.
test('WHAT[delegation-037] the late-consultation stop sees raw history once, never the reset projection', async () => {
  const { normalTransform, DeliveryAdjudication } = await import('../../../dist/OpenCode/Plugin/PluginTransforms.js')
  const { clearSessionState } = await import('../../../dist/Context/Scope/Surface.js')
  const sid = 'ses_consult_037_projection'
  clearSessionState(sid)
  const step = (id, name, callID) => ({ info: { id, role: 'assistant', sessionID: sid, time: { created: 2, completed: 5 }, finish: 'tool-calls' },
    parts: [{ type: 'step-start' }, { type: 'reasoning', text: 'think' }, tool(name, callID), { type: 'step-finish' }] })
  // Tail of ses_e80299a17ffe… (2026-10-28 00:11): askPreviousScope → todowrite → answer → todowrite → resetScope.
  const messages = [
    user(sid, 'u0', [{ type: 'text', text: 'assignment' }]),
    step('a-rs1', 'resetScope', 'c1'),
    step('a-aps', 'askPreviousScope', 'c2'),
    step('a-tw2', 'todowrite', 'c3'),
    user(sid, 'answer', [steerPart]),
    step('a-tw3', 'todowrite', 'c4'),
    step('a-rs2', 'resetScope', 'c5'),
  ]
  const seen = []
  const yes = async () => {}
  const caps = {
    BeginPhysicalProviderAttempt: yes, BindSessionStartedAt: async () => null, ApplyRelayProjection: async () => null,
    ApplyTenureIsolation: yes, CaptureXTraceMessages: async () => ({ Current: null, RawMessages: [], Receipt: null }),
    RefreshCompanionXTrace: () => {}, ApplyCompanion: yes, ApplyXWire: yes, FreezeProviderAttemptPlan: yes,
    InjectPairGuideline: yes, SettleAndReplaceDeferredInspections: yes, SanitizeMessages: () => {},
    IsPureChatSession: () => false, AdjudicateDelivery: async () => new DeliveryAdjudication(0, []), StopAfterDelivery: yes,
    StopCompletedContinuation: async (_sid, _physical, out) => {
      const before = out.messages.find(m => m.info?.id === 'a-tw2')
      seen.push(before.parts.some(p => p.type === 'tool'))
      return false
    },
  }
  const out = { messages: [...messages] }
  await normalTransform(caps, sid, {}, out)
  assert.deepEqual(seen, [true], 'judged exactly once, while the pre-answer todowrite part is still present')
  // The hazard the single raw judgment avoids: the projection keeps resetScope but hollows the pre-answer turn.
  const hollow = out.messages.find(m => m.info?.id === 'a-tw2')
  assert.ok(hollow && !hollow.parts.some(p => p.type === 'tool'), 'projection retires the pre-answer todowrite part')
  assert.ok(out.messages.some(m => m.info?.id === 'a-rs2' && m.parts.some(p => p.tool === 'resetScope')), 'resetScope itself is retained')
  clearSessionState(sid)
})
