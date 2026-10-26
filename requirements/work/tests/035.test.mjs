import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'

const review = (work, id, office, updates) =>
  work.execute(id, {
    type: 'Review',
    office,
    providerRunId: 'run',
    toolCallId: id,
    updates,
  })

const confirm = (work, effect, outcome = 'Sent') => {
  const reply = work.execute(`confirm-${effect.id}-${outcome}`, { type: 'Confirm', effectId: effect.id, outcome })
  assert.equal(reply.status, 'Applied', reply.reason)
}

const closeManager = (workId) => {
  const work = new Work(workId)
  review(work, 'r1', 'Manager', [{ key: 'job', value: 'do' }])
  const relayed = review(work, 'r2', 'Manager', [{ key: 'job', value: null }])
  confirm(work, relayed.result.effect)
  const advanced = review(work, 'r3', 'Manager', [])
  confirm(work, advanced.result.effect)
  const closed = review(work, 'r4', 'Manager', [])
  assert.equal(closed.result.kind, 'Completed')
  confirm(work, closed.result.effect, 'Requested')
  return work
}

test('WHAT[work-035] a completed work restores completed and does not open a tenure', () => {
  const work = closeManager('work-35-done')
  const draft = 'this prose must not choose a milestone'
  const restored = new Work('work-35-done', work.records())
  assert.equal(restored.inspect().completed, true)
  assert.equal(restored.inspect().milestone, '已完成')
  assert.equal(restored.inspect().active, null)
  assert.equal(restored.inspect().milestone, work.inspect().milestone)
  assert.equal(draft.includes('已完成'), false)
  const late = restored.execute('late', {
    type: 'Review', office: 'Manager', providerRunId: 'run', toolCallId: 'late', updates: [],
  })
  assert.equal(late.status, 'Conflict')
  assert.equal(restored.records().length, work.records().length)
})

test('WHAT[work-035] an active work rebinds the same tenure, milestone, and ledger', () => {
  const work = new Work('work-35-active')
  const recorded = review(work, 'r1', 'Plan', [{ key: 'need', value: 'scope' }])
  assert.equal(recorded.result.milestone, '事实与取舍')
  const restored = new Work('work-35-active', work.records())
  assert.deepEqual(restored.inspect(), work.inspect())
  assert.equal(restored.inspect().active.id, work.inspect().active.id)
  assert.equal(restored.inspect().active.milestone, '事实与取舍')
  assert.deepEqual(restored.inspect().obligations, [{ key: 'need', value: 'scope' }])
  assert.equal(restored.recover().next, work.recover().next)
})

test('WHAT[work-035] a missing work stays unopened', () => {
  const missing = new Work('work-35-missing')
  assert.deepEqual(missing.records(), [])
  assert.equal(missing.inspect().office, null)
  assert.equal(missing.inspect().active, null)
  assert.equal(missing.inspect().completed, false)
  assert.equal(missing.recover().next, 'Review')
})

test('WHAT[work-035] the same request with different bytes is refused and the history is unchanged', () => {
  const work = new Work('work-35-bytes')
  review(work, 'r1', 'Plan', [])
  const before = work.records()
  const conflict = work.execute('r1', {
    type: 'Review', office: 'Plan', providerRunId: 'run', toolCallId: 'r1', updates: [{ key: 'need', value: 'other' }],
  })
  assert.equal(conflict.status, 'Conflict')
  assert.equal(conflict.reason, 'RequestIdConflict')
  assert.deepEqual(work.records(), before)
  assert.throws(() => new Work('work-35-bytes', [`${before[0]}\n`]), /canonical/)
})
