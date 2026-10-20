import assert from 'node:assert/strict'
import test from 'node:test'
import * as xwire from '../../../dist/Context/Prefix/XWireSurface.js'

test('WHAT[context-compression-017] physical user messages survive suppression as the exact Host object while unaddressed and unidentified messages are preserved', () => {
  const raw = [
    { info: { id: 'user-1', role: 'user' }, parts: [{ type: 'text', text: 'first request' }] },
    { info: { id: 'assistant-1', role: 'assistant' }, parts: [{ type: 'text', text: 'old answer 1' }] },
    { info: { id: 'user-2', role: 'user' }, parts: [{ type: 'text', text: 'second request' }] },
    { info: { id: 'assistant-2', role: 'assistant' }, parts: [{ type: 'text', text: 'old answer 2' }] },
    { info: { id: 'user-3', role: 'user' }, parts: [{ type: 'text', text: 'third request' }] },
    { parts: [{ type: 'text', text: 'unidentified message' }] },
    { info: { id: 'tail', role: 'assistant' }, parts: [{ type: 'text', text: 'live tail' }] },
  ]

  const projected = xwire.suppressHostMessagesByIds(raw, ['assistant-1', 'assistant-2'])

  assert.deepEqual(
    projected.map((message) => message.info?.id),
    ['user-1', 'user-2', 'user-3', undefined, 'tail'],
  )
  assert.equal(projected[0], raw[0])
  assert.equal(projected[1], raw[2])
  assert.equal(projected[2], raw[4])
  assert.equal(projected[3], raw[5])
  assert.equal(projected[4], raw[6])
})

