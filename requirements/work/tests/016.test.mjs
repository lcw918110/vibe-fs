import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'

const canonical = (value) => {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`
  if (value !== null && typeof value === 'object') {
    return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`
  }
  return JSON.stringify(value)
}

test('WHAT[work-016] an old lifecycle name is not decoded as a new event', () => {
  const work = new Work('work-16')
  const reviewed = work.execute('r1', {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: 'r1',
    updates: [{ key: 'need', value: 'keep' }],
  })
  assert.equal(reviewed.result.kind, 'Reviewed')
  const produced = work.history()[0].events.find((event) => event.type === 'Reviewed')
  assert.equal(produced.type, 'Reviewed')
  assert.equal(work.history()[0].events.some((event) => event.type === 'WorkOpened'), true)
  const restored = new Work('work-16', work.records())
  assert.equal(restored.history()[0].events.find((event) => event.type === 'Reviewed').type, 'Reviewed')

  for (const oldName of ['RoadOpened', 'AssessmentCommitted']) {
    const frame = JSON.parse(work.records()[0])
    const event = frame.events.find((item) => item.type === 'Reviewed')
    event.type = oldName
    assert.throws(() => new Work('work-16', [canonical(frame)]), /Unknown lifecycle event/)
  }
})
