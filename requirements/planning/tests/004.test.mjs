import assert from 'node:assert/strict'
import test from 'node:test'
import * as planning from '../../../dist/Mission/Planning/Surface.js'

test('WHAT[planning-004] three-stage state machine enforces S1 start and strictly sequential progression', () => {
  const empty = planning.empty()
  const open = planning.applyWorkEvent(planning.workOpened('work-1', '/root'), empty)
  assert.equal(open.ok, true)

  // Initial incumbency cannot start at S2 or S3
  const badStartS2 = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-1', 'S2', 0n),
    open.state,
  )
  assert.equal(badStartS2.ok, false, 'initial incumbency cannot start at S2')

  const badStartS3 = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-1', 'S3', 0n),
    open.state,
  )
  assert.equal(badStartS3.ok, false, 'initial incumbency cannot start at S3')

  // Initial incumbency starts at S1
  const inc1 = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-1', 'S1', 0n),
    open.state,
  )
  assert.equal(inc1.ok, true)
  assert.equal(planning.activeStage(inc1.state), 'S1')

  // Retire S1
  const ret1 = planning.applyWorkEvent(
    planning.incumbencyRetired('inc-1', 'Continue', 10n),
    inc1.state,
  )
  assert.equal(ret1.ok, true)

  // Stage cannot skip from S1 to S3
  const skipToS3 = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-2', 'S3', 11n),
    ret1.state,
  )
  assert.equal(skipToS3.ok, false, 'cannot skip stage S2 directly to S3')

  // Stage cannot repeat S1
  const repeatS1 = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-2', 'S1', 11n),
    ret1.state,
  )
  assert.equal(repeatS1.ok, false, 'cannot repeat stage S1')

  // Succession to S2 is valid
  const inc2 = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-2', 'S2', 11n),
    ret1.state,
  )
  assert.equal(inc2.ok, true)
  assert.equal(planning.activeStage(inc2.state), 'S2')

  // Retire S2
  const ret2 = planning.applyWorkEvent(
    planning.incumbencyRetired('inc-2', 'Continue', 20n),
    inc2.state,
  )
  assert.equal(ret2.ok, true)

  // Stage cannot regress from S2 back to S1
  const regressToS1 = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-3', 'S1', 21n),
    ret2.state,
  )
  assert.equal(regressToS1.ok, false, 'stage cannot regress backwards')

  // Succession to S3 is valid
  const inc3 = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-3', 'S3', 21n),
    ret2.state,
  )
  assert.equal(inc3.ok, true)
  assert.equal(planning.activeStage(inc3.state), 'S3')

  // Retire S3
  const ret3 = planning.applyWorkEvent(
    planning.incumbencyRetired('inc-3', 'Delivered', 30n),
    inc3.state,
  )
  assert.equal(ret3.ok, true)

  // S3 has no successor stage
  const pastS3 = planning.applyWorkEvent(
    planning.incumbencyOpened('work-1', 'inc-4', 'S3', 31n),
    ret3.state,
  )
  assert.equal(pastS3.ok, false, 'S3 has no further successor stage')
})
