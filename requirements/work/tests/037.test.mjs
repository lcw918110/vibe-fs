import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'

const review = (work, id, updates) =>
  work.execute(id, {
    type: 'Review',
    office: 'Manager',
    providerRunId: 'run',
    toolCallId: id,
    updates,
  })

const confirm = (work, effect, outcome = 'Sent') => {
  const reply = work.execute(`confirm-${effect.id}-${outcome}`, { type: 'Confirm', effectId: effect.id, outcome })
  assert.equal(reply.status, 'Applied', reply.reason)
}

const complete = (work) => {
  review(work, 'r1', [{ key: 'job', value: 'do' }])
  const relayed = review(work, 'r2', [{ key: 'job', value: null }])
  confirm(work, relayed.result.effect)
  const advanced = review(work, 'r3', [])
  confirm(work, advanced.result.effect)
  const closed = review(work, 'r4', [])
  assert.equal(closed.result.kind, 'Completed')
  confirm(work, closed.result.effect, 'Requested')
  return closed
}

test('WHAT[work-037] only a new operator message reopens a completed work, once', () => {
  const work = new Work('work-37')
  const before = work.records().length
  const early = work.execute('early', { type: 'OperatorMessage', kind: 'Operator', messageId: 'too-soon' })
  assert.equal(early.status, 'Conflict')
  assert.equal(early.reason, 'Operator message is only accepted after completion')
  assert.equal(work.records().length, before)

  complete(work)
  assert.equal(work.inspect().active, null)
  const advisor = work.execute('steer', { type: 'OperatorMessage', kind: 'Advisor', messageId: 'steer-1' })
  const agent = work.execute('agent', { type: 'OperatorMessage', kind: 'Agent', messageId: 'agent-1' })
  assert.equal(advisor.result.kind, 'Ignored')
  assert.equal(agent.result.kind, 'Ignored')
  assert.equal(work.inspect().completed, true)
  const reopened = work.execute('operator', { type: 'OperatorMessage', kind: 'Operator', messageId: 'operator-1' })
  assert.equal(reopened.status, 'Applied', reopened.reason)
  assert.equal(reopened.result.kind, 'Reopened')
  assert.equal(work.inspect().milestone, '执行')
  assert.equal(work.inspect().active.origin, 'Originator')
  assert.notEqual(work.inspect().active.id, work.inspect().rounds.at(-1).incumbencyId)
  const same = work.execute('operator-replay', { type: 'OperatorMessage', kind: 'Operator', messageId: 'operator-1' })
  assert.equal(same.status, 'Observed')
  assert.equal(same.result.kind, 'Reopened')
  const other = work.execute('operator-2', { type: 'OperatorMessage', kind: 'Operator', messageId: 'operator-2' })
  assert.equal(other.status, 'Conflict')
  const openings = work.history().flatMap((frame) => frame.events).filter((event) => event.type === 'IncumbencyOpened' && event.messageId === 'operator-1')
  assert.equal(openings.length, 1)
  assert.equal(work.history().flatMap((frame) => frame.events).some((event) => event.messageId === 'steer-1' || event.messageId === 'agent-1'), false)
  const restored = new Work('work-37', work.records())
  assert.equal(restored.inspect().active.id, work.inspect().active.id)
  assert.equal(restored.inspect().completed, false)
})
