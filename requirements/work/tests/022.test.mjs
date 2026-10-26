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

const review = (work, id, office, updates, extra = {}) =>
  work.execute(id, {
    type: 'Review',
    office,
    providerRunId: 'run',
    toolCallId: extra.toolCallId ?? id,
    updates,
  })

const confirm = (work, effect, outcome = 'Sent') => {
  const reply = work.execute(`confirm-${effect.id}`, { type: 'Confirm', effectId: effect.id, outcome })
  assert.equal(reply.status, 'Applied', reply.reason)
}

const types = (work) => work.history().at(-1).events.map((event) => event.type)

test('WHAT[work-022] rotation facts are the published events and an exact replay writes nothing', () => {
  const plan = new Work('work-22')
  const asked = review(plan, 'ask', 'Plan', [])
  assert.equal(asked.result.kind, 'Asked')
  assert.deepEqual(types(plan), ['WorkOpened', 'IncumbencyOpened', 'QuestionRequested'])
  confirm(plan, asked.result.effect, 'Parked')
  assert.equal(plan.execute('answer', { type: 'OperatorAnswer', messageId: 'the-goal' }).status, 'Applied')
  const recorded = review(plan, 'review', 'Plan', [{ key: 'tradeoff', value: 'keep' }])
  assert.equal(recorded.result.kind, 'Reviewed')
  assert.deepEqual(types(plan), ['Reviewed'])
  const kept = review(plan, 'empty', 'Plan', [])
  assert.equal(kept.result.kind, 'Reviewed')
  const relayed = review(plan, 'relay', 'Plan', [{ key: 'tradeoff', value: null }])
  assert.equal(relayed.result.kind, 'Relayed')
  assert.deepEqual(types(plan), ['IncumbencyRetired', 'ContextBarrier', 'IncumbencyOpened', 'PromptObliged'])
  const relayReplay = plan.execute('relay', {
    type: 'Review', office: 'Plan', providerRunId: 'run', toolCallId: 'relay', updates: [{ key: 'tradeoff', value: null }],
  })
  assert.equal(relayReplay.status, 'Replayed')
  assert.equal(plan.history().filter((frame) => frame.events.some((event) => event.type === 'ContextBarrier')).length, 1)
  confirm(plan, relayed.result.effect)
  const advanced = review(plan, 'advance', 'Plan', [])
  assert.equal(advanced.result.kind, 'Advanced')
  assert.equal(advanced.result.milestone, '理解与路径')
  assert.deepEqual(types(plan), ['MilestoneAdvanced', 'PromptObliged'])
  assert.equal(plan.inspect().active.id, relayed.result.successorId)
  confirm(plan, advanced.result.effect)

  const ready = new Work('work-22-ready')
  for (const next of ['理解与路径', '流程与伪代码', '待执行']) {
    assert.equal(review(ready, `fill-${next}`, 'Plan', [{ key: 'item', value: next }]).result.kind, 'Reviewed')
    const stepRelay = review(ready, `clear-${next}`, 'Plan', [{ key: 'item', value: null }])
    assert.equal(stepRelay.result.kind, 'Relayed')
    confirm(ready, stepRelay.result.effect)
    const step = review(ready, `adv-${next}`, 'Plan', [])
    assert.equal(step.result.milestone, next)
    confirm(ready, step.result.effect)
  }
  const launched = review(ready, 'launch', 'Plan', [])
  assert.equal(launched.result.kind, 'Launched')
  assert.deepEqual(types(ready), ['DeliveryRequested'])
  assert.equal(ready.inspect().milestone, '待执行')

  const manager = new Work('work-22-manager')
  review(manager, 'r1', 'Manager', [{ key: 'job', value: 'do' }])
  const handoff = review(manager, 'r2', 'Manager', [{ key: 'job', value: null }])
  confirm(manager, handoff.result.effect)
  const cleanup = review(manager, 'r3', 'Manager', [])
  confirm(manager, cleanup.result.effect)
  const closed = review(manager, 'r4', 'Manager', [])
  assert.equal(closed.result.kind, 'Completed')
  assert.deepEqual(types(manager), ['IncumbencyRetired', 'StopObliged'])
  confirm(manager, closed.result.effect, 'Requested')
  assert.equal(manager.execute('steer', { type: 'OperatorMessage', kind: 'Advisor', messageId: 'steer-1' }).result.kind, 'Ignored')
  const reopened = manager.execute('operator', { type: 'OperatorMessage', kind: 'Operator', messageId: 'operator-1' })
  assert.equal(reopened.result.kind, 'Reopened')
  assert.deepEqual(types(manager), ['IncumbencyOpened'])
  assert.equal(manager.inspect().milestone, '执行')
  assert.equal(manager.inspect().active.origin, 'Originator')
  const sameMessage = manager.execute('operator-again', { type: 'OperatorMessage', kind: 'Operator', messageId: 'operator-1' })
  assert.equal(sameMessage.status, 'Observed')
  assert.equal(manager.history().flatMap((frame) => frame.events).filter((event) => event.type === 'IncumbencyOpened' && event.messageId === 'operator-1').length, 1)
})

test('WHAT[work-022] a conflicting payload and a half pair write nothing', () => {
  const work = new Work('work-22-conflict')
  const opened = review(work, 'r1', 'Plan', [{ key: 'need', value: 'one' }])
  assert.equal(opened.status, 'Applied')
  const conflict = work.execute('r1', {
    type: 'Review', office: 'Plan', providerRunId: 'run', toolCallId: 'r1', updates: [{ key: 'need', value: 'two' }],
  })
  assert.equal(conflict.status, 'Conflict')
  assert.equal(conflict.reason, 'RequestIdConflict')
  assert.equal(work.records().length, 1)
  const late = work.execute('late', { type: 'Review', office: 'Manager', providerRunId: 'run', toolCallId: 'late', updates: [] })
  assert.equal(late.status, 'Conflict')
  assert.equal(late.reason, 'Office is fixed')
  const frame = JSON.parse(work.records()[0])
  frame.events = [frame.events.find((event) => event.type === 'IncumbencyRetired') ?? frame.events[0]]
  if (frame.events[0].type !== 'IncumbencyRetired') {
    const relay = review(work, 'r2', 'Plan', [{ key: 'need', value: null }])
    assert.equal(relay.result.kind, 'Relayed')
    const relayFrame = JSON.parse(work.records().at(-1))
    assert.throws(
      () => new Work('work-22-conflict', [canonical({ ...relayFrame, events: [relayFrame.events[0]] })]),
      /Illegal lifecycle transaction/,
    )
  }
})
