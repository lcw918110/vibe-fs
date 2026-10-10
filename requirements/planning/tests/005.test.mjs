import assert from 'node:assert/strict'
import test from 'node:test'
import * as planning from '../../../dist/Mission/Planning/Surface.js'

test('WHAT[planning-005] action gate permits handoff only in S1, handoff and deliver in S2, deliver only in S3', () => {
  assert.ok(typeof planning.isActionAllowed === 'function', 'gate.isActionAllowed must be an exported function')

  // In S1: only handoff is allowed; deliver is strictly rejected
  assert.equal(planning.isActionAllowed({ stage: 'S1', action: 'handoff', hasDelivered: false, hasBlocker: false }), true)
  assert.equal(planning.isActionAllowed({ stage: 'S1', action: 'deliver', hasDelivered: false, hasBlocker: false }), false)

  // In S2: both handoff and deliver are allowed
  assert.equal(planning.isActionAllowed({ stage: 'S2', action: 'handoff', hasDelivered: false, hasBlocker: false }), true)
  assert.equal(planning.isActionAllowed({ stage: 'S2', action: 'deliver', hasDelivered: false, hasBlocker: false }), true)

  // In S3: deliver is allowed; handoff is strictly rejected
  assert.equal(planning.isActionAllowed({ stage: 'S3', action: 'handoff', hasDelivered: false, hasBlocker: false }), false)
  assert.equal(planning.isActionAllowed({ stage: 'S3', action: 'deliver', hasDelivered: false, hasBlocker: false }), true)

  // Blocker presence rejects both handoff and deliver
  assert.equal(planning.isActionAllowed({ stage: 'S1', action: 'handoff', hasDelivered: false, hasBlocker: true }), false)
  assert.equal(planning.isActionAllowed({ stage: 'S2', action: 'handoff', hasDelivered: false, hasBlocker: true }), false)
  assert.equal(planning.isActionAllowed({ stage: 'S2', action: 'deliver', hasDelivered: false, hasBlocker: true }), false)
  assert.equal(planning.isActionAllowed({ stage: 'S3', action: 'deliver', hasDelivered: false, hasBlocker: true }), false)

  // After Delivered: all mutation actions (handoff, deliver, ask, resume) rejected; only js-plan.read permitted
  assert.equal(planning.isActionAllowed({ stage: 'S3', action: 'deliver', hasDelivered: true, hasBlocker: false }), false)
  assert.equal(planning.isActionAllowed({ stage: 'S3', action: 'handoff', hasDelivered: true, hasBlocker: false }), false)
  assert.equal(planning.isActionAllowed({ stage: 'S3', action: 'ask', hasDelivered: true, hasBlocker: false }), false)
  assert.equal(planning.isActionAllowed({ stage: 'S3', action: 'resume', hasDelivered: true, hasBlocker: false }), false)
  assert.equal(planning.isActionAllowed({ stage: 'S3', action: 'read', hasDelivered: true, hasBlocker: false }), true)
  assert.equal(planning.isActionAllowed({ stage: 'S3', action: 'js-plan.read', hasDelivered: true, hasBlocker: false }), true)
})

test('WHAT[planning-005] decideAction provides typed rejections for disallowed actions', () => {
  // S1 deliver -> DeliverForbiddenInS1
  const s1Deliver = planning.decideAction('S1', 'deliver', false, false)
  assert.equal(s1Deliver.ok, false)
  assert.equal(s1Deliver.rejection, 'DeliverForbiddenInS1')

  // S3 handoff -> HandoffForbiddenInS3
  const s3Handoff = planning.decideAction('S3', 'handoff', false, false)
  assert.equal(s3Handoff.ok, false)
  assert.equal(s3Handoff.rejection, 'HandoffForbiddenInS3')

  // Delivered mutation -> AlreadyDelivered
  const postDeliveredAsk = planning.decideAction('S3', 'ask', true, false)
  assert.equal(postDeliveredAsk.ok, false)
  assert.equal(postDeliveredAsk.rejection, 'AlreadyDelivered')

  // Blocker -> BlockedByLiveResources
  const blockedHandoff = planning.decideAction('S1', 'handoff', false, true)
  assert.equal(blockedHandoff.ok, false)
  assert.equal(blockedHandoff.rejection, 'BlockedByLiveResources')
})

test('WHAT[planning-005] allowedPermissions maps to ToolPermission set for each stage', () => {
  // S1 without blocker: JsPlan, Ask, Resume, Handoff
  const s1Perms = planning.allowedPermissions('S1', false, false)
  assert.deepEqual(s1Perms, ['Ask', 'Handoff', 'JsPlan', 'Resume'])

  // S2 without blocker: JsPlan, Ask, Resume, Handoff, Deliver
  const s2Perms = planning.allowedPermissions('S2', false, false)
  assert.deepEqual(s2Perms, ['Ask', 'Deliver', 'Handoff', 'JsPlan', 'Resume'])

  // S3 without blocker: JsPlan, Ask, Resume, Deliver
  const s3Perms = planning.allowedPermissions('S3', false, false)
  assert.deepEqual(s3Perms, ['Ask', 'Deliver', 'JsPlan', 'Resume'])

  // Delivered: only JsPlan
  const deliveredPerms = planning.allowedPermissions('S3', true, false)
  assert.deepEqual(deliveredPerms, ['JsPlan'])
})
