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

const confirm = (work, id, effect) =>
  work.execute(id, { type: 'Confirm', effectId: effect.id, outcome: 'Sent' })

test('WHAT[work-006] clearing the ledger relays and an empty cleanup completes', () => {
  const work = new Work('work-006')
  const reviewed = review(work, 'r1', [{ key: 'job', value: 'do' }])
  assert.equal(reviewed.result.kind, 'Reviewed')

  const relayed = review(work, 'r2', [{ key: 'job', value: null }])
  assert.equal(relayed.result.kind, 'Relayed')
  assert.deepEqual(work.history().at(-1).events.map((event) => event.type), [
    'IncumbencyRetired',
    'ContextBarrier',
    'IncumbencyOpened',
    'PromptObliged',
  ])
  assert.equal(confirm(work, 'c-relay', relayed.result.effect).status, 'Applied')

  const advanced = review(work, 'r3', [])
  assert.equal(advanced.result.kind, 'Advanced')
  assert.equal(advanced.result.milestone, '收尾')
  assert.equal(confirm(work, 'c-advance', advanced.result.effect).status, 'Applied')

  const closed = review(work, 'r4', [])
  assert.equal(closed.result.kind, 'Completed')
  assert.deepEqual(work.history().at(-1).events.map((event) => event.type), ['IncumbencyRetired', 'StopObliged'])
  assert.equal(work.history().at(-1).events.some((event) => event.outcome === 'Delivered'), false)
  assert.equal(work.inspect().milestone, '已完成')
  assert.equal(work.inspect().active, null)
})
