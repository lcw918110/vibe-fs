import assert from 'node:assert/strict'
import test from 'node:test'
import { projectSuccessor } from './support/contract.mjs'

test('WHAT[work-045] projection keeps physical history and does not narrate the workspace', () => {
  const messages = [
    { role: 'user', firstSeen: 1, text: 'the request' },
    { role: 'assistant', firstSeen: 2, text: 'predecessor summary of the workspace' },
    { role: 'assistant', firstSeen: 9, text: 'current observation' },
  ]
  const projected = projectSuccessor(messages, 4, false)
  assert.equal(projected.ok, true)
  assert.equal(projected.projected.some((message) => message.text === 'predecessor summary of the workspace'), true)
  assert.equal(projected.projected.some((message) => message.text === 'the request'), true)
  assert.equal(projected.projected.some((message) => message.text === 'current observation'), true)
  assert.equal(projected.projected.some((message) => String(message.text).includes('workspace narrative')), false)
})
