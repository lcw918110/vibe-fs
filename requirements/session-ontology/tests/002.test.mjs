import assert from 'node:assert/strict'
import test from 'node:test'
import * as association from '../../../dist/Execution/Session/AssociationSurface.js'

test('WHAT[session-ontology-002] an actual companion association exposes one owner and one attachment kind', () => {
  const linked = association.link({ main: 'owner', companion: 'companion' }, association.empty)
  assert.equal(linked.ok, true, linked.message)
  assert.deepEqual(association.classify('companion', linked.value), {
    executionClass: 'InternalLeaf',
    ownership: { kind: 'Attached', owner: 'owner', attachment: 'Companion', transactionId: null },
  })
})

test('WHAT[session-ontology-002] Root and Attached project mutually exclusive ownership shapes', () => {
  assert.deepEqual(association.ownershipRoot, {
    kind: 'Root', owner: null, attachment: null, transactionId: null,
  })
  for (const kind of ['SyncInspector', 'SyncCoder', 'SyncEngineer']) {
    assert.deepEqual(association.ownershipAttached('owner', kind), {
      kind: 'Attached', owner: 'owner', attachment: kind, transactionId: null,
    })
  }
})
