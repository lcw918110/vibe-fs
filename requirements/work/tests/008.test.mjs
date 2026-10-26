import assert from 'node:assert/strict'
import test from 'node:test'
import { Projection } from 'wanxiangli/projection'

const projection = Projection()
const barrier = { toolCallId: 'review-1', successorTenureId: 'next' }
const message = (id, role, extra = {}) => ({
  id,
  role,
  synthetic: false,
  semantic: true,
  cursor: null,
  toolCallId: null,
  resultFor: null,
  tenureId: null,
  ...extra,
})

test('WHAT[work-008] the successor view hides the triggering review and everything before it', () => {
  const messages = [
    message('u-old', 'user', { tenureId: 'old' }),
    message('a-old', 'assistant', { tenureId: 'old' }),
    message('t-old', 'tool', { tenureId: 'old' }),
    message('call', 'assistant', { toolCallId: 'review-1', tenureId: 'old' }),
    message('result', 'tool', { resultFor: 'review-1', tenureId: 'old' }),
    message('a-new', 'assistant', { tenureId: 'next' }),
  ]
  const before = JSON.stringify(messages)
  const kept = projection.tenure(messages, barrier)
  assert.deepEqual(Array.from(kept), [5])
  assert.equal(JSON.stringify(messages), before)
  assert.equal(kept.some((index) => messages[index].role === 'user'), false)
  assert.equal(kept.some((index) => messages[index].toolCallId === 'review-1' || messages[index].resultFor === 'review-1'), false)
  assert.equal(messages.some((row) => row.role === 'synthetic_predecessor'), false)
})
