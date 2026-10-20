import assert from 'node:assert/strict'
import test from 'node:test'
import * as assoc from '../../../dist/Execution/Session/AssociationSurface.js'

const linked = (pairs, start = assoc.empty) =>
  pairs.reduce((state, pair) => {
    const result = assoc.link(pair, state)
    assert.equal(result.ok, true, result.message)
    return result.value
  }, start)

test('WHAT[session-ontology-005] HOST_008_companion_cannot_serve_two_work_sessions', () => {
  const result = assoc.link({ main: 'ses_x2', companion: 'ses_y' }, linked([{ main: 'ses_x1', companion: 'ses_y' }]))
  assert.equal(result.ok, false)
  assert.equal(result.error, 'CompanionClaimedByOther')
  assert.match(result.message, /ses_x1/)
})

test('WHAT[session-ontology-005] HOST_008_session_cannot_be_its_own_companion', () => {
  const result = assoc.link({ main: 'ses_x', companion: 'ses_x' }, assoc.empty)
  assert.equal(result.ok, false)
  assert.equal(result.error, 'SelfLink')
})
