import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'

const review = (work, id, office, updates, extra = {}) =>
  work.execute(id, {
    type: 'Review',
    office,
    providerRunId: extra.providerRunId ?? 'run',
    toolCallId: extra.toolCallId ?? id,
    updates,
  })

const confirm = (work, effect) => {
  const reply = work.execute(`confirm-${effect.id}`, { type: 'Confirm', effectId: effect.id, outcome: 'Sent' })
  assert.equal(reply.status, 'Applied', reply.reason)
}

const retire = (office, workId) => {
  const work = new Work(workId)
  const recorded = review(work, 'r1', office, [{ key: 'job', value: 'do' }])
  assert.equal(recorded.status, 'Applied')
  const relayed = review(work, 'r2', office, [{ key: 'job', value: null }], { toolCallId: 'call-retire', providerRunId: 'run-retire' })
  assert.equal(relayed.result.kind, 'Relayed')
  return { work, relayed }
}

test('WHAT[work-007] the same request replays and a different body conflicts', () => {
  const work = new Work('work-007')
  const command = { type: 'Review', office: 'Plan', providerRunId: 'run', toolCallId: 'call-1', updates: [] }
  const first = work.execute('review-1', command)
  assert.equal(first.result.kind, 'Asked')
  const replay = work.execute('review-1', command)
  assert.equal(replay.status, 'Replayed')
  assert.equal(replay.record, null)
  assert.deepEqual(replay.result, first.result)
  assert.equal(work.records().length, 1)
  const conflict = work.execute('review-1', { ...command, updates: [{ key: 'gap', value: 'fix' }] })
  assert.equal(conflict.status, 'Conflict')
  assert.equal(conflict.reason, 'RequestIdConflict')
  assert.equal(work.records().length, 1)
})

test('WHAT[work-007] the same tool call on another request returns the original result', () => {
  const work = new Work('work-007-call')
  const command = { type: 'Review', office: 'Manager', providerRunId: 'run-1', toolCallId: 'call-1', updates: [{ key: 'gap', value: 'fix' }] }
  const first = work.execute('review-1', command)
  assert.equal(first.result.kind, 'Reviewed')
  const again = work.execute('review-2', { ...command, providerRunId: 'run-2' })
  assert.equal(again.status, 'Observed')
  assert.deepEqual(again.result, first.result)
  assert.equal(work.records().length, 1)
  const replay = work.execute('review-2', { ...command, providerRunId: 'run-2' })
  assert.equal(replay.status, 'Replayed')
  assert.equal(work.records().length, 1)
})

test('WHAT[work-007] retirement identity follows the work and the fence', () => {
  const plan = retire('Plan', 'work-007-plan')
  const manager = retire('Manager', 'work-007-manager')
  const planId = plan.work.history().at(-1).events.find((event) => event.type === 'IncumbencyRetired').retirementId
  const managerId = manager.work.history().at(-1).events.find((event) => event.type === 'IncumbencyRetired').retirementId
  assert.equal(typeof planId, 'string')
  assert.notEqual(planId, managerId)
  confirm(plan.work, plan.relayed.result.effect)
  const replay = new Work('work-007-plan', plan.work.records())
  const again = replay.execute('r2', {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run-retire',
    toolCallId: 'call-retire',
    updates: [{ key: 'job', value: null }],
  })
  assert.equal(again.status, 'Replayed')
  assert.equal(replay.history().filter((frame) => frame.events.some((event) => event.type === 'IncumbencyRetired')).length, 1)
})
