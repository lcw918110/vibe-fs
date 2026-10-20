import assert from 'node:assert/strict'
import test from 'node:test'
import * as assoc from '../../../dist/Execution/Session/AssociationSurface.js'

const linked = assoc.link({ main: 'ses_main', companion: 'ses_companion' }, assoc.empty)

assert.equal(linked.ok, true, linked.message)

const state = linked.value

test('WHAT[session-ontology-007] HOST_008_durable_link_derives_work_and_leaf_cells', () => {
  assert.deepEqual(assoc.classify('ses_main', state), {
    executionClass: 'Work',
    ownership: { kind: 'Root', owner: null, attachment: null, transactionId: null },
  })
  assert.deepEqual(assoc.classify('ses_companion', state), {
    executionClass: 'InternalLeaf',
    ownership: { kind: 'Attached', owner: 'ses_main', attachment: 'Companion', transactionId: null },
  })
})
