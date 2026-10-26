import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'

const review = (workId, updates) =>
  new Work(workId).execute('r1', {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: 'call-1',
    updates,
  })

test('WHAT[work-043] a nonempty review is Reviewed and an empty review is Asked', () => {
  const gaps = review('work-043-gaps', [{ key: 'criteria', value: 'plan' }])
  assert.equal(gaps.status, 'Applied')
  assert.equal(gaps.result.kind, 'Reviewed')
  assert.equal(gaps.result.milestone, '事实与取舍')
  assert.notEqual(gaps.result.kind, 'Promoted')
  assert.notEqual(gaps.result.kind, 'Stayed')

  const empty = review('work-043-empty', [])
  assert.equal(empty.status, 'Applied')
  assert.equal(empty.result.kind, 'Asked')
  assert.equal(empty.result.effect.text, '我需要干什么？')
  assert.notEqual(empty.result.kind, 'Promoted')
  assert.notEqual(empty.result.kind, 'Stayed')
})
