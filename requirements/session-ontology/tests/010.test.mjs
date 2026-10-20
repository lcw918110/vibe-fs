import assert from 'node:assert/strict'
import test from 'node:test'
import * as assoc from '../../../dist/Execution/Session/AssociationSurface.js'

const linked = (pairs, start = assoc.empty) =>
  pairs.reduce((state, pair) => {
    const result = assoc.link(pair, state)
    assert.equal(result.ok, true, result.message)
    return result.value
  }, start)

test('WHAT[session-ontology-010] COMPANION_001_unknown_session_is_not_a_companion', () => {
  assert.equal(assoc.isCompanion('ses_unknown', assoc.empty), false)
  assert.equal(assoc.entry('ses_unknown', assoc.empty), null)
})
