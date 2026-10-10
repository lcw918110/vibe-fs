import assert from 'node:assert/strict'
import test from 'node:test'
import * as planning from '../../../dist/Mission/Planning/Surface.js'

test('WHAT[planning-003] five durable events drive plan lifecycle with idempotent replay', () => {
  const empty = planning.empty()

  // 1. PlanWorkOpened
  const open1 = planning.applyWorkEvent(planning.workOpened('work-1', '/repo/root'), empty)
  assert.equal(open1.ok, true)
  assert.equal(open1.state.Root, '/repo/root')

  // Replay PlanWorkOpened identically -> idempotent
  const openReplay = planning.applyWorkEvent(planning.workOpened('work-1', '/repo/root'), open1.state)
  assert.equal(openReplay.ok, true)
  assert.deepEqual(openReplay.state, open1.state)

  // 2. PlanDevOpsBound
  const bound1 = planning.applyWorkEvent(planning.devOpsBound('work-1', 'devops-1', 'gpt-4o'), open1.state)
  assert.equal(bound1.ok, true)

  // Replay PlanDevOpsBound identically -> idempotent
  const boundReplay = planning.applyWorkEvent(planning.devOpsBound('work-1', 'devops-1', 'gpt-4o'), bound1.state)
  assert.equal(boundReplay.ok, true)
  assert.deepEqual(boundReplay.state, bound1.state)

  // 3. PlanIncumbencyOpened (S1)
  const inc1 = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-1', 'S1', 0n),
    bound1.state,
  )
  assert.equal(inc1.ok, true)
  assert.equal(planning.activeStage(inc1.state), 'S1')
  assert.equal(planning.retiredCount(inc1.state), 0)

  // Replay PlanIncumbencyOpened identically -> idempotent
  const inc1Replay = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-1', 'S1', 0n),
    inc1.state,
  )
  assert.equal(inc1Replay.ok, true)
  assert.deepEqual(inc1Replay.state, inc1.state)

  // 4. PlanIncumbencyRetired (Continue)
  const ret1 = planning.applyWorkEvent(
    planning.incumbencyRetired('inc-1', 'Continue', 10n),
    inc1.state,
  )
  assert.equal(ret1.ok, true)
  assert.equal(planning.activeStage(ret1.state), undefined)
  assert.equal(planning.retiredCount(ret1.state), 1)

  // Replay PlanIncumbencyRetired identically -> idempotent
  const ret1Replay = planning.applyWorkEvent(
    planning.incumbencyRetired('inc-1', 'Continue', 10n),
    ret1.state,
  )
  assert.equal(ret1Replay.ok, true)
  assert.deepEqual(ret1Replay.state, ret1.state)

  // 5. Open S2 then Retire with Delivered
  const inc2 = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-2', 'S2', 11n),
    ret1.state,
  )
  assert.equal(inc2.ok, true)

  const ret2 = planning.applyWorkEvent(
    planning.incumbencyRetired('inc-2', 'Delivered', 20n),
    inc2.state,
  )
  assert.equal(ret2.ok, true)
  assert.equal(planning.retiredCount(ret2.state), 2)

  // 6. PlanDelivered
  const del = planning.applyWorkEvent(
    planning.delivered('inc-2', 'work-1', 'sha256-abc123', 'root/plan/work-1/plan.md'),
    ret2.state,
  )
  assert.equal(del.ok, true)
  assert.equal(planning.isDelivered(del.state), true)
  assert.equal(planning.deliveredDigest(del.state), 'sha256-abc123')

  // Replay PlanDelivered identically -> idempotent
  const delReplay = planning.applyWorkEvent(
    planning.delivered('inc-2', 'work-1', 'sha256-abc123', 'root/plan/work-1/plan.md'),
    del.state,
  )
  assert.equal(delReplay.ok, true)
  assert.deepEqual(delReplay.state, del.state)
})

test('WHAT[planning-003] conflicting payloads and illegal transitions fail-closed', () => {
  const empty = planning.empty()

  // Conflicting root on work opened
  const open1 = planning.applyWorkEvent(planning.workOpened('work-1', '/repo/root1'), empty)
  assert.equal(open1.ok, true)
  const openConflict = planning.applyWorkEvent(planning.workOpened('work-1', '/repo/root2'), open1.state)
  assert.equal(openConflict.ok, false)

  // Conflicting devops binding
  const bound1 = planning.applyWorkEvent(planning.devOpsBound('work-1', 'devops-1', null), open1.state)
  assert.equal(bound1.ok, true)
  const boundConflict = planning.applyWorkEvent(planning.devOpsBound('work-1', 'devops-2', null), bound1.state)
  assert.equal(boundConflict.ok, false)

  // Multiple active incumbencies forbidden
  const inc1 = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-1', 'S1', 0n),
    bound1.state,
  )
  assert.equal(inc1.ok, true)
  const secondActive = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-2', 'S1', 0n),
    inc1.state,
  )
  assert.equal(secondActive.ok, false, 'cannot open second active incumbency')

  // Cannot revive already retired incumbency
  const ret1 = planning.applyWorkEvent(
    planning.incumbencyRetired('inc-1', 'Continue', 10n),
    inc1.state,
  )
  assert.equal(ret1.ok, true)
  const reviveRetired = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-1', 'S2', 11n),
    ret1.state,
  )
  assert.equal(reviveRetired.ok, false, 'cannot revive retired incumbency')

  // Cannot deliver while active incumbency is still open
  const inc2 = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-2', 'S2', 11n),
    ret1.state,
  )
  const deliverWhileActive = planning.applyWorkEvent(
    planning.delivered('inc-2', 'work-1', 'sha256-abc', 'path'),
    inc2.state,
  )
  assert.equal(deliverWhileActive.ok, false, 'cannot deliver while active')

  // Cannot deliver when retirement outcome was Continue
  const retContinue = planning.applyWorkEvent(
    planning.incumbencyRetired('inc-2', 'Continue', 20n),
    inc2.state,
  )
  const deliverOnContinue = planning.applyWorkEvent(
    planning.delivered('inc-2', 'work-1', 'sha256-abc', 'path'),
    retContinue.state,
  )
  assert.equal(deliverOnContinue.ok, false, 'cannot deliver when retired with Continue')
})
