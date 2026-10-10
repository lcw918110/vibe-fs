import assert from 'node:assert/strict'
import test from 'node:test'
import * as surface from '../../../dist/Mission/Planning/Surface.js'

const openWork = (workId) => surface.PlanningSurface.workOpened(workId, '/tmp/plan-test')
const openInc = (workId, incId, stage, cursor) =>
  surface.PlanningSurface.incumbencyOpened(workId, incId, stage, cursor)
const retireInc = (incId, outcome, cursor) =>
  surface.PlanningSurface.incumbencyRetired(incId, outcome, cursor)

const firstStateOf = (events) => {
  let st = surface.PlanningSurface.empty()
  for (const ev of events) {
    const res = surface.PlanningSurface.applyWorkEvent(ev, st)
    assert.equal(res.ok, true, JSON.stringify(res))
    st = res.state
  }
  return st
}

test('WHAT[planning-022] S1 with no retirement carries no previous range', () => {
  const st = firstStateOf([openWork('w1'), openInc('w1', 'inc-1', 'S1', 0n)])
  assert.equal(surface.PlanningSurface.latestRetirementRange(st), undefined)
})

test('WHAT[planning-022] S1 retirement preserves its opening range for the next tenure', () => {
  const st = firstStateOf([
    openWork('w1'),
    openInc('w1', 'inc-1', 'S1', 7n),
    retireInc('inc-1', 'Continue', 25n),
  ])
  assert.deepEqual(surface.PlanningSurface.latestRetirementRange(st), [7n, 25n])
})

test('WHAT[planning-022] tenure assembly consumes PreviousRange into LWR_prev', () => {
  const rawMessages = [
    { id: 'u1', role: 'user', content: 'go' },
    { id: 'a-old', role: 'assistant', content: 'old work', cursor: 9n },
    { id: 'a-new', role: 'assistant', content: 'new work', cursor: 30n },
  ]
  const tenure = {
    workId: 'w1',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    previousRange: [7n, 25n],
    isFreshHandover: false,
  }
  let seen = null
  const result = surface.PlanningSurface.assembleTenureMessages(rawMessages, tenure, (range) => {
    seen = range
    return 'prev record'
  })
  assert.equal(seen.start, 7n)
  assert.equal(seen.end, 25n)
  const ids = result.messages.map((m) => m.id)
  assert.ok(!ids.includes('a-old'))
  assert.ok(ids.includes('a-new'))
  assert.ok(ids.some((id) => String(id).startsWith('lwr-prev-')))
})
