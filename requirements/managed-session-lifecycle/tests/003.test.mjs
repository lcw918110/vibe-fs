import assert from 'node:assert/strict'
import test from 'node:test'
import * as satellites from '../../../dist/OpenCode/Host/SatelliteSurface.js'

test('WHAT[managed-session-lifecycle-003] actual satellite owner reuses an exactly matching supplied journal association', async () => {
  const observed = await satellites.SatelliteSurface_scenario(true, true, false, false)
  assert.equal(observed.ok, true)
  assert.equal(observed.origin, 'Reused')
  assert.equal(observed.child, 'companion-1')
  assert.deepEqual(observed.created, [])
  assert.deepEqual(observed.linked, [['work', 'companion-1', 'companion']])
})

test('WHAT[managed-session-lifecycle-003] conflicting association and failed Host query refuse recovery without creating children', async () => {
  const conflict = await satellites.SatelliteSurface_scenario(true, true, true, false)
  assert.equal(conflict.ok, false)
  assert.match(conflict.error, /Conflicting companion recovery for work: child companion-1 has a different agent or title/)
  assert.deepEqual(conflict.created, [])
  assert.deepEqual(conflict.linked, [])
  const queryError = await satellites.SatelliteSurface_scenario(false, false, false, true)
  assert.equal(queryError.ok, false)
  assert.match(queryError.error, /Cannot recover companion satellite/)
  assert.deepEqual(queryError.created, [])
})

test('WHAT[managed-session-lifecycle-003] no journal association means new child rather than adoption of a matching sibling', async () => {
  const observed = await satellites.SatelliteSurface_scenario(false, true, false, false)
  assert.equal(observed.ok, true)
  assert.equal(observed.origin, 'Created')
  assert.equal(observed.child, 'created-1')
})

test.todo('WHAT[managed-session-lifecycle-003] journal reopened after actual process restart selects one exact child and attaches a new child to the real family root (GAP-133)')
