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

test('WHAT[work-040] one open Work has at most one active tenure', () => {
  const work = new Work('work-single-tenure')
  const opened = work.execute('open', {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: 'open',
    updates: [],
  })
  assert.equal(opened.result.kind, 'Asked')
  assert.equal(work.inspect().active.origin, 'Originator')
  const firstId = work.inspect().active.id
  const kept = work.execute('kept', {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: 'kept',
    updates: [{ key: 'need', value: 'one' }],
  })
  assert.equal(kept.status, 'Conflict')
  assert.equal(work.inspect().active.id, firstId)
  const replay = work.execute('open', {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: 'open',
    updates: [],
  })
  assert.equal(replay.status, 'Replayed')
  assert.equal(work.history().filter((frame) => frame.events.some((event) => event.type === 'IncumbencyOpened')).length, 1)

  const second = JSON.parse(work.records()[0])
  second.requestId = 'open-second'
  assert.throws(() => new Work('work-single-tenure', [work.records()[0], canonical(second)]))
  assert.equal(new Work('work-single-tenure', work.records()).inspect().active.id, firstId)
})
