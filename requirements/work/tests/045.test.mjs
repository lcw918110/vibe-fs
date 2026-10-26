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

test('WHAT[work-045] the barrier keeps successor history and does not narrate the workspace', () => {
  const messages = [
    message('request', 'user', { tenureId: 'old' }),
    message('summary', 'assistant', { tenureId: 'old' }),
    message('call', 'assistant', { toolCallId: 'review-1', tenureId: 'old' }),
    message('result', 'tool', { resultFor: 'review-1', tenureId: 'old' }),
    message('current', 'assistant', { tenureId: 'next' }),
  ]
  const before = JSON.stringify(messages)
  const kept = projection.tenure(messages, barrier)
  assert.deepEqual(Array.from(kept), [4])
  assert.equal(messages[kept[0]].id, 'current')
  assert.equal(JSON.stringify(messages), before)
  assert.equal(kept.some((index) => messages[index].id === 'request' || messages[index].id === 'summary'), false)
})
