import assert from 'node:assert/strict'
import test from 'node:test'
import * as association from '../../../dist/Execution/Session/AssociationSurface.js'

test('WHAT[session-ontology-004] a linked Companion is an owned leaf with no Companion of its own', () => {
  const linked = association.link({ main: 'work', companion: 'companion' }, association.empty)
  assert.equal(linked.ok, true, linked.message)
  assert.deepEqual(association.classify('companion', linked.value), {
    executionClass: 'InternalLeaf',
    ownership: { kind: 'Attached', owner: 'work', attachment: 'Companion', transactionId: null },
  })
})
