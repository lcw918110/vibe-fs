import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'

const review = (work, id, office, updates, extra = {}) =>
  work.execute(id, {
    type: 'Review',
    office,
    providerRunId: 'run',
    toolCallId: extra.toolCallId ?? id,
    updates,
    ...(extra.blockers ? { blockers: extra.blockers } : {}),
  })

const confirm = (work, effect, outcome = 'Sent') => {
  const reply = work.execute(`confirm-${effect.id}-${outcome}`, { type: 'Confirm', effectId: effect.id, outcome })
  assert.equal(reply.status, 'Applied', reply.reason)
}

const reachReady = (work) => {
  for (const next of ['理解与路径', '流程与伪代码', '待执行']) {
    assert.equal(review(work, `fill-${next}`, 'Plan', [{ key: 'item', value: next }]).result.kind, 'Reviewed')
    const relayed = review(work, `clear-${next}`, 'Plan', [{ key: 'item', value: null }])
    assert.equal(relayed.result.kind, 'Relayed')
    confirm(work, relayed.result.effect)
    const advanced = review(work, `adv-${next}`, 'Plan', [])
    assert.equal(advanced.result.kind, 'Advanced')
    assert.equal(advanced.result.milestone, next)
    confirm(work, advanced.result.effect)
  }
}

test('WHAT[work-014] one ready review launches once and a replay does not launch again', () => {
  const work = new Work('work-14-ready')
  reachReady(work)
  const command = { type: 'Review', office: 'Plan', providerRunId: 'run', toolCallId: 'once', updates: [] }
  const launched = work.execute('once', command)
  assert.equal(launched.result.kind, 'Launched')
  assert.equal(work.inspect().milestone, '待执行')
  assert.equal(work.inspect().launches.length, 1)
  assert.equal(work.execute('once', command).status, 'Replayed')
  assert.equal(work.inspect().launches.length, 1)
  for (const toolCallId of ['a', 'b', 'c']) {
    const next = review(work, toolCallId, 'Plan', [], { toolCallId })
    assert.equal(next.result.kind, 'Launched')
  }
  assert.equal(work.inspect().launches.length, 4)
  assert.equal(work.inspect().milestone, '待执行')
  assert.equal(work.history().some((frame) => frame.events.some((event) => event.type === 'IncumbencyRetired' && event.outcome === 'Completed')), false)
})

test('WHAT[work-014] cleanup blocks without retiring and an empty cleanup completes once', () => {
  const work = new Work('work-14-close')
  assert.equal(review(work, 'r1', 'Manager', [{ key: 'job', value: 'do' }]).result.kind, 'Reviewed')
  const relayed = review(work, 'r2', 'Manager', [{ key: 'job', value: null }])
  confirm(work, relayed.result.effect)
  const advanced = review(work, 'r3', 'Manager', [])
  assert.equal(advanced.result.milestone, '收尾')
  confirm(work, advanced.result.effect)
  const before = work.records().length
  const blocked = review(work, 'blocked', 'Manager', [], { blockers: ['lock'] })
  assert.equal(blocked.status, 'Observed')
  assert.equal(blocked.result.kind, 'Blocked')
  assert.equal(work.records().length, before)
  const closed = review(work, 'close', 'Manager', [])
  assert.equal(closed.result.kind, 'Completed')
  assert.equal(work.history().at(-1).events.some((event) => event.type === 'StopObliged'), true)
  confirm(work, closed.result.effect, 'Requested')
  assert.equal(work.execute('close', {
    type: 'Review', office: 'Manager', providerRunId: 'run', toolCallId: 'close', updates: [],
  }).status, 'Replayed')
  assert.equal(work.history().filter((frame) => frame.events.some((event) => event.type === 'StopObliged')).length, 1)
})
