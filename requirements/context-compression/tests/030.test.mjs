import assert from 'node:assert/strict'
import test from 'node:test'
import * as xwire from '../../../dist/Context/Prefix/XWireSurface.js'

test('WHAT[context-compression-030] every covered assume call and matching result survives suppression unchanged', () => {
  const raw = [
    {
      info: { id: 'assume-call', role: 'assistant' },
      parts: [{ type: 'tool-call', tool: 'assume', callID: 'assume-1', args: { assumption: 'Use the monotone cutoff.' } }],
    },
    {
      info: { id: 'assume-result', role: 'tool' },
      parts: [{ type: 'tool-result', callID: 'assume-1', result: 'Committed.' }],
    },
    { info: { id: 'ordinary-covered', role: 'assistant' }, parts: [{ type: 'text', text: 'old prose' }] },
    { info: { id: 'live-user', role: 'user' }, parts: [{ type: 'text', text: 'continue' }] },
  ]

  const projected = xwire.suppressHostMessagesByIds(raw, ['ordinary-covered'])

  assert.deepEqual(projected.map((message) => message.info.id), ['assume-call', 'assume-result', 'live-user'])
  assert.equal(projected[0], raw[0], 'assume call must remain the exact original Host object')
  assert.equal(projected[1], raw[1], 'assume result must remain the exact original Host object')
  assert.equal(projected[2], raw[3], 'unaddressed live user must remain the exact original Host object')
})
