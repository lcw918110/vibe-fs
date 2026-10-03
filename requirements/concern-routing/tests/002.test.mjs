import assert from 'node:assert/strict'
import test from 'node:test'
import * as concern from '../../../dist/Interaction/Concern/Surface.js'
import { withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { admit, context, user, toolBatch, transform, hints } from './support/plugin.mjs'

test('WHAT[concern-routing-002] projection announcement coverage is per recipient, including the owner', () => {
  let state = concern.subscribe('owner', 'generation', 'build', 'build health', concern.empty()).state
  for (const recipient of ['owner', 'peer', 'newcomer']) {
    const prepared = concern.prepare(recipient, state)
    assert.deepEqual(prepared.announcements, [{ id: 'build', concern: 'build health' }])
    const placed = concern.place(recipient, prepared.announcedGenerations, [], state)
    assert.equal(placed.ok, true)
    state = placed.state
    assert.deepEqual(concern.prepare(recipient, state).announcements, [])
  }
})

test('WHAT[concern-routing-002] actual newly eligible peer receives address discovery once and never receives the owner’s message', async () => {
  await withExecutablePlugin(async (hooks, directory, created, runtime) => {
    await admit(runtime, 'discovery-owner', 'engineer', hooks)
    await hooks.tool.subscribe.execute({ id: 'build', concern: 'DISCOVERY-CONCERN' }, context('discovery-owner', 'subscription'))
    await hooks.tool.publish.execute({ id: 'build', message: 'OWNER-ONLY-MESSAGE' }, context('discovery-owner', 'publication'))
    await admit(runtime, 'discovery-peer', 'engineer', hooks)
    const first = await transform(hooks, 'discovery-peer', [user('discovery-peer')])
    assert.match(JSON.stringify(hints(first)), /DISCOVERY-CONCERN/)
    assert.doesNotMatch(JSON.stringify(first), /OWNER-ONLY-MESSAGE|discovery-owner/)
    const next = await transform(hooks, 'discovery-peer', [...first, ...toolBatch('discovery-peer', 'second')])
    assert.ok(hints(next).length > hints(first).length)
    assert.doesNotMatch(JSON.stringify(hints(next).slice(hints(first).length)), /DISCOVERY-CONCERN|OWNER-ONLY-MESSAGE/)
  })
})

test.todo('WHAT[concern-routing-002] all eligible participant kinds receive each live generation exactly once across restart, excluding ineligible roles (GAP-155)')
