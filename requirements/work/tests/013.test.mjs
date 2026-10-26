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

const review = (work, id, office, updates) =>
  work.execute(id, {
    type: 'Review',
    office,
    providerRunId: 'run',
    toolCallId: id,
    updates,
  })

const confirm = (work, effect) => {
  const reply = work.execute(`confirm-${effect.id}`, { type: 'Confirm', effectId: effect.id, outcome: 'Sent' })
  assert.equal(reply.status, 'Applied', reply.reason)
}

test('WHAT[work-013] clearing the ledger writes the relay pair and an advance does not retire', () => {
  const work = new Work('work-13')
  assert.equal(review(work, 'r1', 'Manager', [{ key: 'job', value: 'do' }]).result.kind, 'Reviewed')
  const relayed = review(work, 'r2', 'Manager', [{ key: 'job', value: null }])
  assert.equal(relayed.result.kind, 'Relayed')
  const pair = work.history().at(-1).events
  assert.deepEqual(pair.map((event) => event.type), ['IncumbencyRetired', 'ContextBarrier', 'IncumbencyOpened', 'PromptObliged'])
  assert.equal(pair[0].outcome, 'Relayed')
  assert.equal(pair.some((event) => event.outcome === 'Delivered'), false)
  confirm(work, relayed.result.effect)
  const advanced = review(work, 'r3', 'Manager', [])
  assert.equal(advanced.result.kind, 'Advanced')
  assert.deepEqual(work.history().at(-1).events.map((event) => event.type), ['MilestoneAdvanced', 'PromptObliged'])
  const before = work.records()
  assert.throws(() => work.execute('deliver', { type: 'Deliver', incumbencyId: 'x' }), /Unknown work command/)
  assert.deepEqual(work.records(), before)
})

test('WHAT[work-013] a half pair, an extra field, or a lone opening is not history', () => {
  const work = new Work('work-13-pair')
  review(work, 'r1', 'Plan', [{ key: 'need', value: 'scope' }])
  review(work, 'r2', 'Plan', [{ key: 'need', value: null }])
  const frame = JSON.parse(work.records().at(-1))
  const retirement = frame.events.find((event) => event.type === 'IncumbencyRetired')
  assert.throws(() => new Work('work-13-pair', [canonical({ ...frame, events: [retirement] })]), /Illegal lifecycle transaction/)
  retirement.retirementCursor = 6
  assert.throws(() => new Work('work-13-pair', [canonical(frame)]), /Unknown field: retirementCursor/)
  retirement.outcome = 'Delivered'
  delete retirement.retirementCursor
  assert.throws(() => new Work('work-13-pair', [canonical(frame)]), /Unknown value: Delivered/)
  const opened = JSON.parse(work.records()[0])
  const lone = opened.events.find((event) => event.type === 'IncumbencyOpened')
  assert.throws(() => new Work('work-13-lone', [canonical({ ...opened, events: [lone] })]), /Work is not open/)
})
