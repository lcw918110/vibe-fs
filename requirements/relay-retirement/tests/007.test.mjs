import assert from 'node:assert/strict'
import test from 'node:test'
import * as relay from '../../../dist/Mission/Relay/Surface.js'

const openAssessed = (scores) => {
  const opened = relay.openIncumbency(relay.empty(), 'road-1', 'inc-1', 'snapshot-1', 'authority-1')
  assert.equal(opened.ok, true)
  const assessed = relay.assess(
    opened.state,
    'road-1',
    'inc-1',
    'assessment-1',
    'snapshot-1',
    'authority-1',
    ...scores,
  )
  assert.equal(assessed.ok, true)
  return assessed.state
}

test('WHAT[relay-retirement-007] Continue retirement commits a closed Continue outcome with cut binding', () => {
  const fresh = relay.openIncumbency(relay.empty(), 'road-1', 'inc-1', 'snapshot-1', 'authority-1')
  assert.equal(fresh.ok, true)
  assert.deepEqual(
    relay.retireContinue(fresh.state, 'road-1', 'inc-1', 'ret-0', 'run-0', 'tool-0', 'snapshot-1'),
    { ok: false, error: 'AssessmentRequired' },
  )

  const state = openAssessed(['REVISE', 'PERFECT', 'PERFECT', 'PERFECT', 'PERFECT', 'PERFECT', 'PERFECT', 'PERFECT'])
  const retired = relay.retireContinue(state, 'road-1', 'inc-1', 'ret-1', 'run-1', 'tool-1', 'snapshot-1')
  assert.equal(retired.ok, true)
  assert.deepEqual(relay.retirement(retired.state, 'road-1'), {
    retirementId: 'ret-1',
    incumbentId: 'inc-1',
    outcome: 'Continue',
    certificateId: null,
    providerRunId: 'run-1',
    toolCallId: 'tool-1',
    snapshotId: 'snapshot-1',
    authorityRevision: 'authority-1',
  })
  assert.equal(relay.view(retired.state, 'road-1').activeIncumbency, null)
})

test('WHAT[relay-retirement-007] Accepted retirement commits a closed Accepted outcome with certificate binding', () => {
  const state = openAssessed(Array(8).fill('PERFECT'))
  const retired = relay.retireAccepted(
    state,
    'road-1',
    'inc-1',
    'ret-1',
    'run-1',
    'tool-1',
    'certificate:assessment-1',
    'snapshot-1',
  )
  assert.equal(retired.ok, true)
  assert.deepEqual(relay.retirement(retired.state, 'road-1'), {
    retirementId: 'ret-1',
    incumbentId: 'inc-1',
    outcome: 'Accepted',
    certificateId: 'certificate:assessment-1',
    providerRunId: 'run-1',
    toolCallId: 'tool-1',
    snapshotId: 'snapshot-1',
    authorityRevision: 'authority-1',
  })
  assert.equal(relay.view(retired.state, 'road-1').activeIncumbency, null)
})

test('WHAT[relay-retirement-007] Accepted with a stale different snapshot fails', () => {
  const state = openAssessed(Array(8).fill('PERFECT'))
  const stale = relay.retireAccepted(
    state,
    'road-1',
    'inc-1',
    'ret-stale',
    'run-1',
    'tool-1',
    'certificate:assessment-1',
    'snapshot-2',
  )
  assert.deepEqual(stale, { ok: false, error: 'RetirementSnapshotStale' })
})

test('WHAT[relay-retirement-007] cleanup-blocked fold accepts a subsequent exact certificate retirement transaction', () => {
  const state = openAssessed(Array(8).fill('PERFECT'))
  const blocked = relay.blockCleanup(state, 'road-1', 'inc-1', 'blocker-digest-1')
  assert.equal(blocked.ok, true)
  assert.equal(relay.view(blocked.state, 'road-1').phase, 'RetirementCleanupBlocked')

  const retired = relay.retireAccepted(
    blocked.state,
    'road-1',
    'inc-1',
    'ret-1',
    'run-1',
    'tool-1',
    'certificate:assessment-1',
    'snapshot-1',
  )
  assert.equal(retired.ok, true)
  assert.equal(relay.retirement(retired.state, 'road-1').outcome, 'Accepted')
})

test('WHAT[relay-retirement-007] real durable retirement is atomic across crash points and cleanup retry', {todo: 'GAP-197: pure fold transition does not persist a transaction or clear actual resource blockers'})

test('WHAT[relay-retirement-007] Accepted road cannot reopen new incumbency while certificate is valid', () => {
  const opened = relay.openIncumbency(relay.empty(), 'road-1', 'inc-1', 'snapshot-1', 'authority-1')
  assert.equal(opened.ok, true)
  const assessed = relay.assess(
    opened.state,
    'road-1',
    'inc-1',
    'assessment-1',
    'snapshot-1',
    'authority-1',
    ...Array(8).fill('PERFECT'),
  )
  assert.equal(assessed.ok, true)

  const retired = relay.retireAccepted(
    assessed.state,
    'road-1',
    'inc-1',
    'ret-accepted-1',
    'run-1',
    'tool-1',
    'certificate:assessment-1',
    'snapshot-1',
  )
  assert.equal(retired.ok, true)

  const reopened = relay.openIncumbency(retired.state, 'road-1', 'inc-2', 'snapshot-1', 'authority-1')
  assert.deepEqual(reopened, { ok: false, error: 'RoadAlreadyAccepted' })

  const invalidated = relay.invalidateCertificate(retired.state, 'road-1', 'NewHumanInputAdvancesPhase')
  assert.equal(invalidated.ok, true)
  const reopenedAfterInvalidate = relay.openIncumbency(invalidated.state, 'road-1', 'inc-2', 'snapshot-1', 'authority-1')
  assert.equal(reopenedAfterInvalidate.ok, true)
  assert.equal(relay.view(reopenedAfterInvalidate.state, 'road-1').activeIncumbency, 'inc-2')
})

test.todo('WHAT[relay-retirement-007] retirement ends incumbent obligations without rewriting independent context-compression history')

test('WHAT[relay-retirement-007] Accepted road cannot reopen new incumbency while certificate is valid', () => {
  const opened = relay.openIncumbency(relay.empty(), 'road-1', 'inc-1', 'snapshot-1', 'authority-1')
  assert.equal(opened.ok, true)
  const assessed = relay.assess(
    opened.state,
    'road-1',
    'inc-1',
    'assessment-1',
    'snapshot-1',
    'authority-1',
    ...Array(8).fill('PERFECT'),
  )
  assert.equal(assessed.ok, true)

  const retired = relay.retireAccepted(
    assessed.state,
    'road-1',
    'inc-1',
    'ret-accepted-1',
    'run-1',
    'tool-1',
    'certificate:assessment-1',
    'snapshot-1',
  )
  assert.equal(retired.ok, true)

  // Direct attempt to reopen incumbency while certificate is still valid fails with RoadAlreadyAccepted
  const reopened = relay.openIncumbency(retired.state, 'road-1', 'inc-2', 'snapshot-1', 'authority-1')
  assert.deepEqual(reopened, { ok: false, error: 'RoadAlreadyAccepted' })

  // After explicit invalidation of the certificate, reopen succeeds
  const invalidated = relay.invalidateCertificate(retired.state, 'road-1', 'NewHumanInputAdvancesPhase')
  assert.equal(invalidated.ok, true)
  const reopenedAfterInvalidate = relay.openIncumbency(invalidated.state, 'road-1', 'inc-2', 'snapshot-1', 'authority-1')
  assert.equal(reopenedAfterInvalidate.ok, true)
  assert.equal(relay.view(reopenedAfterInvalidate.state, 'road-1').activeIncumbency, 'inc-2')
})
