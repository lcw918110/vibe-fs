import test from 'node:test'
import assert from 'node:assert/strict'
import { ofArray } from '../../../dist/fable_modules/fable-library-js.5.13.0/List.js'
import { SessionMessage, SessionToolPart, SnapshotToolPartState } from '../../../dist/Foundation/HostSessionSnapshot.js'
import { MessagePart } from '../../../dist/Foundation/HostMessage.js'
import { ToolCallIdModule_create, HostToolPartIdModule_create } from '../../../dist/Foundation/Identity.js'
import { filterSnapshot } from '../../../dist/Context/Scope/SnapshotScope.js'

const call = (id, tool) => new MessagePart(2, [id, tool, '{}'])
const returned = (id, text) => new MessagePart(3, [id, text])
const text = (body) => new MessagePart(0, [body])
const msg = (id, role, parts, toolParts = []) => new SessionMessage(
  id, role, undefined, undefined, undefined, undefined, undefined, undefined,
  true, false, undefined, parts, parts.map(() => undefined), toolParts,
)
const physicalResult = (id, output) => new SessionToolPart(
  HostToolPartIdModule_create('part-' + id), ToolCallIdModule_create(id),
  id === 'reset' ? 'resetScope' : 'read', '{}', new SnapshotToolPartState(1, [output]),
)

test('WHAT[context-compression-040] SnapshotScope keeps the result co-located with an earlier reset call', () => {
  const raw = [
    msg('u1', 'user', [text('Task')]),
    msg('a1', 'assistant', [text('work'), call('old','read'), returned('old','old result'), call('r1','resetScope'), returned('r1','first reset')]),
    msg('a2', 'assistant', [call('new','read'), call('r2','resetScope'), returned('r2','second reset')]),
  ]
  const retained = filterSnapshot(ofArray(raw))
  assert.deepEqual(retained.map(m => m.id), ['u1', 'a1', 'a2'])
  assert.deepEqual(retained[1].parts.map(p => p.type), ['text', 'toolCall', 'toolResult'],
    'the surviving assistant prose precedes both halves of the older reset')
  assert.equal(retained[1].parts[0].text, 'work', 'assistant prose of the same message is not dropped')
  assert.deepEqual(retained[1].parts.slice(1).map(p => p.id), ['r1', 'r1'], 'both halves of the older reset must survive')
  assert.deepEqual(retained[2].parts.map(p => p.id), ['r2', 'r2'])
  assert.equal(raw[1].Parts.length, 5, 'the session snapshot remains unchanged')
})

test('WHAT[context-compression-040] SnapshotScope preserves completed reset tool states and removes unrelated old work', () => {
  const raw = [
    msg('u1', 'user', [text('Task')]),
    msg('a1', 'assistant', [call('old', 'read'), call('reset', 'resetScope')], [physicalResult('old', 'read output'), physicalResult('reset', 'Scope reset.')]),
    msg('a2', 'assistant', [call('r2', 'resetScope')]),
  ]
  const kept = filterSnapshot(ofArray(raw))
  assert.equal(kept.length, 3)
  assert.deepEqual(kept[1].parts.map(p => p.id), ['reset','reset'], 'the prior reset result is paired with its call')
  assert.deepEqual(kept[2].parts.map(p => p.id), ['r2'])
})
