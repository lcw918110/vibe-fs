import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const HostBoundarySurface = await import("../../../dist/OpenCode/Host/HostBoundarySurface.js");
const HostMessageProjection = await import("../../../dist/OpenCode/Host/HostMessageProjection.js");

const sanitizeMessage = HostMessageProjection.sanitizeMessage
const sanitizeMessages = HostBoundarySurface.sanitizeMessages

test('WHAT[host-boundary-011] HOST_016_assistant_message_with_only_reasoning_gets_semantically_empty_dot_text', () => {
  const raw = { info: { id: 'asst_1', role: 'assistant' }, parts: [{ type: 'reasoning', text: 'Step-by-step thinking content' }] }
  const result = sanitizeMessage(raw)
  assert.equal(result.parts.at(-1).type, 'text')
  assert.equal(result.parts.at(-1).text, '.')
})
test('WHAT[host-boundary-011] HOST_016_assistant_message_with_thinking_type_gets_semantically_empty_dot_text', () => {
  const result = sanitizeMessage({ info: { role: 'assistant' }, parts: [{ type: 'thinking', thinking: 'Deep reasoning text' }] })
  assert.equal(result.parts.at(-1).text, '.')
})
test('WHAT[host-boundary-011] HOST_016_assistant_message_with_empty_parts_gets_ellipsis_fallback', () => {
  const result = sanitizeMessage({ info: { role: 'assistant' }, parts: [] })
  assert.equal(result.parts.at(-1).text, '...')
})
test('WHAT[host-boundary-011] HOST_016_user_message_with_empty_parts_gets_hash_fallback', () => {
  const result = sanitizeMessage({ info: { role: 'user' }, parts: [] })
  assert.equal(result.parts.at(-1).text, '#')
})
test('WHAT[host-boundary-011] HOST_016_message_with_existing_text_is_untouched', () => {
  const raw = { info: { role: 'assistant' }, parts: [{ type: 'text', text: 'Formal answer' }] }
  assert.deepEqual(sanitizeMessage(raw), raw)
})
test('WHAT[host-boundary-011] HOST_016_assistant_message_with_tool_call_is_untouched', () => {
  const raw = { info: { role: 'assistant' }, parts: [{ type: 'tool', tool: 'auto-injected', callID: 'g1' }] }
  assert.deepEqual(sanitizeMessage(raw), raw)
})
test('WHAT[host-boundary-011] HOST_016_sanitizeMessages_processes_whole_array', () => {
  const raw = [
    { info: { role: 'user' }, parts: [{ type: 'text', text: 'Hi' }] },
    { info: { role: 'assistant' }, parts: [{ type: 'reasoning', text: 'Thinking' }] },
  ]
  const result = sanitizeMessages(raw)
  assert.equal(result[0].parts[0].text, 'Hi')
  assert.equal(result[1].parts.at(-1).text, '.')
})
test('WHAT[host-boundary-011] HOST_016_consecutive_user_messages_get_assistant_dot_inserted_between_them', () => {
  const raw = [
    { info: { role: 'user' }, parts: [{ type: 'text', text: 'First user message' }] },
    { info: { role: 'user' }, parts: [{ type: 'text', text: 'Second user message' }] },
  ]
  const result = sanitizeMessages(raw)
  assert.equal(result.length, 3)
  assert.equal(result[0].parts[0].text, 'First user message')
  assert.equal(result[1].role, 'assistant')
  assert.equal(result[1].info.role, 'assistant')
  assert.equal(result[1].parts[0].type, 'text')
  assert.equal(result[1].parts[0].text, '.')
  assert.equal(result[2].parts[0].text, 'Second user message')
})
test('WHAT[host-boundary-011] HOST_016_three_consecutive_user_messages_get_assistant_dot_between_each_pair', () => {
  const raw = [
    { role: 'user', content: 'Msg 1' },
    { role: 'user', content: 'Msg 2' },
    { role: 'user', content: 'Msg 3' },
  ]
  const result = sanitizeMessages(raw)
  assert.equal(result.length, 5)
  assert.equal(result[0].content, 'Msg 1')
  assert.equal(result[1].role, 'assistant')
  assert.equal(result[1].parts[0].text, '.')
  assert.equal(result[2].content, 'Msg 2')
  assert.equal(result[3].role, 'assistant')
  assert.equal(result[3].parts[0].text, '.')
  assert.equal(result[4].content, 'Msg 3')
})
test('WHAT[host-boundary-011] HOST_016_alternating_messages_remain_untouched_without_extra_assistant', () => {
  const raw = [
    { info: { role: 'user' }, parts: [{ type: 'text', text: 'U1' }] },
    { info: { role: 'assistant' }, parts: [{ type: 'text', text: 'A1' }] },
    { info: { role: 'user' }, parts: [{ type: 'text', text: 'U2' }] },
  ]
  const result = sanitizeMessages(raw)
  assert.equal(result.length, 3)
  assert.equal(result[0].parts[0].text, 'U1')
  assert.equal(result[1].parts[0].text, 'A1')
  assert.equal(result[2].parts[0].text, 'U2')
})
test('WHAT[host-boundary-011] HOST_030_non_array_parts_projects_safely_to_empty_without_throwing', () => {
  const stringParts = sanitizeMessage({ info: { role: 'assistant' }, parts: 'not-an-array' })
  assert.deepEqual(Array.from(stringParts.parts), [{ type: 'text', text: '...' }])

  const objectParts = sanitizeMessage({ info: { role: 'assistant' }, parts: { type: 'text', text: 'x' } })
  assert.deepEqual(Array.from(objectParts.parts), [{ type: 'text', text: '...' }])

  const userStringParts = sanitizeMessage({ info: { role: 'user' }, parts: 'not-an-array' })
  assert.deepEqual(Array.from(userStringParts.parts), [{ type: 'text', text: '#' }])
})
test('WHAT[host-boundary-011] dedicated Advisor provider view never invents assistant dot or a prior answer', () => {
  const historical = [
    {info:{id:'u1',role:'user'},parts:[{type:'text',text:'What evidence changed the estimate?'}]},
    {info:{id:'a1',role:'assistant'},parts:[{type:'reasoning',text:'Only private reasoning, no final answer'}]},
    {info:{id:'u2',role:'user'},parts:[{type:'text',text:'Please provide the missing facts'}]},
    {info:{id:'a2',role:'assistant'},parts:[{type:'text',text:'The missing fact is 314159'}]},
    {info:{id:'u3',role:'user'},parts:[{type:'text',text:'What follows from that?'}]},
  ]
  const original = structuredClone(historical)
  const output={messages:structuredClone(historical)}
  HostMessageProjection.sanitizeAdvisorOutputMessages(output)
  const projected=output.messages
  assert.deepEqual(projected.map(m=>m.info.role), ['user','assistant','user'])
  assert.deepEqual(projected[0].parts.filter(p=>p.type==='text').map(p=>p.text),
    ['What evidence changed the estimate?','\n\n','Please provide the missing facts'])
  assert.equal(projected[1].parts[0].text,'The missing fact is 314159')
  assert.equal(projected.some(m=>m.info.role==='assistant' && m.parts.some(p=>p.text==='.' || p.text==='...')),false)
  assert.deepEqual(historical,original,'only the outgoing request copy is reprojected')
  const ordinary = sanitizeMessages(original)
  assert.equal(ordinary.filter(m=>m.info.role==='assistant' && m.parts.some(p=>p.text==='.')).length,1,
    'ordinary Host 016 behavior remains unchanged')
})

test('WHAT[host-boundary-011] consecutive pure user content in Advisor merges without a fake assistant turn', () => {
  const source = [
    {role:'user',content:'First question'},
    {role:'user',content:'Additional context'},
    {role:'user',content:'Follow-up question'},
  ]
  const output={messages:structuredClone(source)}
  HostMessageProjection.sanitizeAdvisorOutputMessages(output)
  const projected=output.messages
  assert.equal(projected.length,1)
  assert.equal(projected[0].role,'user')
  assert.deepEqual(projected[0].parts.map(part=>part.text),
    ['First question','\n\n','Additional context','\n\n','Follow-up question'])
  assert.equal(projected[0].content,undefined)
  assert.deepEqual(source.map(m=>m.content),['First question','Additional context','Follow-up question'])
})


}

{
const { default: assert } = await import("node:assert/strict");
const { readFileSync } = await import("node:fs");
const { resolve } = await import("node:path");
const { default: test } = await import("node:test");

const root = resolve(import.meta.dirname, '../../..')
const read = (path) => readFileSync(resolve(root, path), 'utf8')


test('WHAT[host-boundary-011] HostMessageProjection owns sanitizeOutputMessages entry point', () => {
  const projection = read('src/Wanxiangshu/OpenCode/Host/HostMessageProjection.fs')
  const pt = read('src/Wanxiangshu/OpenCode/Plugin/PluginTransforms.fs')

  assert.match(projection, /let\s+sanitizeOutputMessages/)
  assert.match(projection, /replaceMessagesInPlace/)
  assert.match(pt, /HostMessageProjection\.sanitizeOutputMessages/)
})
}

