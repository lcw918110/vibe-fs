import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'

test('WHAT[work-041] a retired tenure cannot bind a second DevOps', () => {
  const work = new Work('work-041')
  const reviewed = work.execute('r1', {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: 'r1',
    updates: [{ key: 'gap', value: 'fix' }],
  })
  assert.equal(reviewed.result.kind, 'Reviewed')
  const predecessor = work.inspect().active.id
  const bound = work.execute('bind', { type: 'BindDevOps', trigger: 'Ask', devopsId: 'd', target: 'model-a' })
  assert.equal(bound.result.kind, 'DevOpsBound')
  const relayed = work.execute('r2', {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: 'r2',
    updates: [{ key: 'gap', value: null }],
  })
  assert.equal(relayed.result.kind, 'Relayed')
  assert.equal(work.execute('c-relay', { type: 'Confirm', effectId: relayed.result.effect.id, outcome: 'Sent' }).status, 'Applied')
  assert.equal(work.inspect().active.id, relayed.result.successorId)
  assert.notEqual(work.inspect().active.id, predecessor)
  const second = work.execute('bind-2', { type: 'BindDevOps', trigger: 'Ask', devopsId: 'other', target: 'model-b' })
  assert.equal(second.status, 'Conflict')
  assert.equal(work.inspect().devopsId, 'd')
  assert.equal(work.inspect().devopsTarget, 'model-a')
})
