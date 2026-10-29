import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'

const bindModel = (work, id, target) =>
  work.execute(id, { type: 'BindDevOps', trigger: 'Ask', devopsId: 'dev-1', target })

test('WHAT[work-017] the first DevOps binding wins and an Advance command writes nothing', () => {
  const work = new Work('work-17')
  const opened = work.execute('r1', {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: 'r1',
    updates: [{ key: 'need', value: 'bind' }],
  })
  assert.equal(opened.result.kind, 'Reviewed')
  assert.equal(opened.result.milestone, '事实与取舍')
  const before = work.records()
  assert.throws(
    () => work.execute('adv', { type: 'Advance', incumbencyId: opened.result.incumbencyId, expected: 'a', next: 'b' }),
    /Unknown work command/,
  )
  assert.deepEqual(work.records(), before)

  const bound = bindModel(work, 'bind-1', 'model-a')
  assert.equal(bound.status, 'Applied', bound.reason)
  assert.equal(bound.result.kind, 'DevOpsBound')
  assert.equal(work.inspect().devopsId, 'dev-1')
  assert.equal(work.inspect().devopsTarget, 'model-a')
  assert.equal(work.inspect().milestone, '事实与取舍')
  assert.equal(work.history().filter((frame) => frame.events.some((event) => event.type === 'DevOpsBound')).length, 1)
  assert.equal(work.history().some((frame) => frame.events.some((event) => event.type === 'IncumbencyRetired')), false)

  const same = bindModel(work, 'bind-2', 'model-a')
  assert.equal(same.status, 'Observed')
  assert.equal(same.result.kind, 'DevOpsBound')
  const conflict = bindModel(work, 'bind-3', 'model-b')
  assert.equal(conflict.status, 'Conflict')
  assert.equal(conflict.reason, 'DevOps binding conflict')
  assert.equal(work.history().filter((frame) => frame.events.some((event) => event.type === 'DevOpsBound')).length, 1)
  assert.equal(work.inspect().devopsTarget, 'model-a')
})
