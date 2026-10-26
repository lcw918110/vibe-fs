import assert from 'node:assert/strict'
import test from 'node:test'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'

const surface = lifecycle.LifecycleSurface ?? lifecycle

test('WHAT[work-017] an old authority advance writes nothing', () => {
  const scout = surface.advance('work-authority-conflict', '', 'not-the-authority', 'probe-next')
  assert.equal(scout.decision, 'Refuse')
  assert.match(scout.detail, /lifecycle sequence is retired/)
  assert.equal(scout.bytes, '')
  assert.equal(scout.names, '')
  assert.equal(String(scout.names).includes('AdvanceCommitted'), false)
  assert.equal(String(scout.names).includes('IncumbencyRetired'), false)
})
