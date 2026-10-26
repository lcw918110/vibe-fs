import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'

const review = (work, id, office, updates, extra = {}) =>
  work.execute(id, {
    type: 'Review',
    office,
    providerRunId: 'run',
    toolCallId: id,
    updates,
    ...extra,
  })

test('WHAT[work-002] the first review opens the work and an Open command writes nothing', () => {
  const plan = new Work('w')
  const asked = review(plan, 'r1', 'Plan', [])
  assert.equal(asked.status, 'Applied', asked.reason)
  assert.equal(asked.result.kind, 'Asked')
  assert.equal(asked.result.milestone, '事实与取舍')
  assert.deepEqual(plan.history()[0].events.map((event) => event.type), ['WorkOpened', 'IncumbencyOpened', 'QuestionRequested'])
  const planRecord = plan.records()[0]
  assert.equal(planRecord.includes('snapshotId'), false)
  assert.equal(planRecord.includes('"S1"'), false)
  assert.equal(planRecord.includes('confirmationCount'), false)

  const manager = new Work('m')
  const opened = review(manager, 'r1', 'Manager', [{ key: 'gap', value: 'do' }])
  assert.equal(opened.status, 'Applied', opened.reason)
  assert.equal(opened.result.kind, 'Reviewed')
  assert.equal(opened.result.milestone, '执行')
  assert.equal(manager.records()[0].includes('"S4"'), false)

  const before = plan.records()
  assert.throws(() => plan.execute('open', { type: 'Open', snapshotId: 's' }), /Unknown work command/)
  assert.deepEqual(plan.records(), before)

  const extra = new Work('y')
  assert.throws(
    () => review(extra, 'r1', 'Plan', [], { snapshotId: 's' }),
    /Unknown field: snapshotId/,
  )
  assert.equal(extra.records().length, 0)
})
