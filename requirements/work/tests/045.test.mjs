import assert from 'node:assert/strict'
import test from 'node:test'
import { Projection } from 'wanxiangli/projection'
import { Work } from 'wanxiangli/work'

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

test('WHAT[work-045] the barrier keeps the user request and successor history and does not narrate the workspace', () => {
  const messages = [
    message('request', 'user', { tenureId: 'old' }),
    message('summary', 'assistant', { tenureId: 'old' }),
    message('call', 'assistant', { toolCallId: 'review-1', tenureId: 'old' }),
    message('result', 'tool', { resultFor: 'review-1', tenureId: 'old' }),
    message('current', 'assistant', { tenureId: 'next' }),
  ]
  const before = JSON.stringify(messages)
  const kept = projection.tenure(messages, barrier)
  assert.deepEqual(Array.from(kept), [0, 2, 3, 4])
  assert.deepEqual(Array.from(kept, (index) => messages[index].id), ['request', 'call', 'result', 'current'])
  assert.equal(JSON.stringify(messages), before)
  assert.equal(kept.some((index) => messages[index].id === 'summary'), false)
})

test('WHAT[work-045] a relay that null-cleared undone obligations still shows the successor the user request', () => {
  // ses.txt：Manager 记账后立刻用 null 删光义务 → Relayed；后任须仍看见「实施 @指南.txt」与随附指南。
  const work = new Work('work-045-relay')
  const review = (id, updates) =>
    work.execute(id, { type: 'Review', office: 'Manager', providerRunId: 'run', toolCallId: id, updates })
  assert.equal(review('review-0', [{ key: 'implement', value: '实施指南' }]).status, 'Applied')
  const relayed = review('review-1', [{ key: 'implement', value: null }])
  assert.equal(relayed.result.kind, 'Relayed')
  const successor = relayed.result.successorId
  assert.equal(typeof successor, 'string')
  const old = work.inspect().rounds.at(-1).incumbencyId

  const messages = [
    message('root', 'user', { tenureId: old }),
    message('guide', 'user', { tenureId: old }),
    message('record', 'assistant', { toolCallId: 'review-0', tenureId: old }),
    message('recorded', 'tool', { resultFor: 'review-0', tenureId: old }),
    message('clear', 'assistant', { toolCallId: 'review-1', tenureId: old }),
    message('relayed', 'tool', { resultFor: 'review-1', tenureId: old }),
    message('start', 'assistant', { tenureId: successor }),
  ]
  const kept = projection.tenure(messages, { toolCallId: 'review-1', successorTenureId: successor })
  assert.deepEqual(Array.from(kept, (index) => messages[index].id), ['root', 'guide', 'clear', 'relayed', 'start'])
})
