import assert from 'node:assert/strict'
import test from 'node:test'
import { projectSuccessor } from './support/contract.mjs'

test('WHAT[work-008] a successor retains prior assistant and tool history and never injects a synthetic predecessor record', () => {
  const messages = [
    { role: 'user', firstSeen: 1 },
    { role: 'assistant', firstSeen: 2 },
    { role: 'tool', firstSeen: 3 },
    { role: 'assistant', firstSeen: 9 },
  ]
  const successor = projectSuccessor(messages, 4, false)
  assert.equal(successor.ok, true)
  assert.equal(successor.projected.filter((message) => message.role === 'synthetic_predecessor').length, 0)
  assert.equal(successor.projected.some((message) => message.role === 'assistant' && message.firstSeen <= 4), true)
  assert.equal(successor.projected.some((message) => message.role === 'tool'), true)
  assert.equal(successor.retainedPriorAssistant, true)
  const first = projectSuccessor(messages, 0, true)
  assert.equal(first.projected.some((message) => message.role === 'synthetic_predecessor'), false)
})

// The fail-closed stable-identity rule belongs to the tenure assembly that
// actually materializes a request. WHAT[work-034] owns that boundary; the
// Manager relay no longer assembles or strips, so it has nothing to refuse.
