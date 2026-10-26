import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { Work } from 'wanxiangli/work'
import * as host from '../../../dist/Mission/Lifecycle/DurableWorkHost.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'

const surface = lifecycle.LifecycleSurface ?? lifecycle
const durable = host.DurableWorkHost ?? host

const review = (work, id, office, updates) =>
  work.execute(id, {
    type: 'Review',
    office,
    providerRunId: 'run',
    toolCallId: id,
    updates,
  })

const confirm = (work, effect, outcome) => {
  const reply = work.execute(`confirm-${effect.id}-${outcome}`, { type: 'Confirm', effectId: effect.id, outcome })
  assert.equal(reply.status, 'Applied', reply.reason)
  return reply.result
}

test('WHAT[work-003] review asks, records, relays, and advances without Promoted', () => {
  const work = new Work('work-003')
  const asked = review(work, 'r1', 'Plan', [])
  assert.equal(asked.result.kind, 'Asked')
  assert.equal(asked.result.effect.text, '我需要干什么？')
  assert.equal(work.history()[0].events.some((event) => event.type === 'Reviewed'), false)
  confirm(work, asked.result.effect, 'Parked')
  const answered = work.execute('answer-goal', { type: 'OperatorAnswer', messageId: 'the-goal' })
  assert.equal(answered.status, 'Applied', answered.reason)

  const recorded = review(work, 'r2', 'Plan', [{ key: 'need', value: 'a plan' }])
  assert.equal(recorded.result.kind, 'Reviewed')
  assert.equal(recorded.result.milestone, '事实与取舍')
  assert.notEqual(recorded.result.kind, 'Promoted')
  assert.notEqual(recorded.result.kind, 'Stayed')

  const relayed = review(work, 'r3', 'Plan', [{ key: 'need', value: null }])
  assert.equal(relayed.result.kind, 'Relayed')
  confirm(work, relayed.result.effect, 'Sent')
  const advanced = review(work, 'r4', 'Plan', [])
  assert.equal(advanced.result.kind, 'Advanced')
  assert.equal(advanced.result.milestone, '理解与路径')
  assert.equal(work.history().at(-1).events.some((event) => event.type === 'IncumbencyOpened'), false)
})

test('WHAT[work-003] a completed manager closes authority without an S sequence', () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-work-003-'))
  const step = (id, updates, extra) =>
    surface.submitReview(root, 'manager-003', id, 'Manager', 'run', id, updates, extra?.blockers ?? null, '')
  try {
    const recorded = step('r1', [{ key: 'job', value: 'do' }])
    assert.equal(recorded.kind, 'Reviewed')
    const relayed = step('r2', [{ key: 'job', value: null }])
    assert.equal(relayed.kind, 'Relayed')
    const advanced = step('r3', [])
    assert.equal(advanced.kind, 'Advanced')
    assert.equal(advanced.milestone, '收尾')
    const closed = step('r4', [])
    assert.equal(closed.kind, 'Completed')
    assert.equal(closed.milestone, '已完成')

    const view = surface.inspected(root, 'manager-003')
    assert.equal(view.Stage, '已完成')
    assert.equal(view.Delivered, true)
    assert.equal(Array.from(view.Sequence ?? []).join(','), '')
    assert.equal(view.ActiveId ?? null, null)
    const retirement = Array.from(view.Retirements ?? []).find((item) => item.Outcome === 'Completed')
    assert.equal(typeof retirement.RetirementId, 'string')
    assert.notEqual(retirement.RetirementId, '')
    assert.equal(durable.closesAuthority(view), true)
    assert.equal(durable.completedClosureId(view), retirement.RetirementId)

    const open = surface.submitReview(root, 'open-003', 'o1', 'Plan', 'run', 'o1', [{ key: 'need', value: 'stay' }], null, '')
    assert.equal(open.kind, 'Reviewed')
    const openView = surface.inspected(root, 'open-003')
    assert.equal(durable.closesAuthority(openView), false)
    assert.equal(durable.completedClosureId(openView) ?? null, null)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
