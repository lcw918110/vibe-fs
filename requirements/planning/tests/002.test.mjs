import assert from 'node:assert/strict'
import test from 'node:test'
import { allPublicRoleLabels, allRoleLabels } from '../../../dist/Foundation/RolesSurface.js'
import { isAllowed, permissions } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'

test('WHAT[planning-002] Role catalog contains Plan and wire label is plan', () => {
  // Roles.all must contain Role.Plan with wire label 'plan'
  assert.ok(allRoleLabels.includes('plan'), 'Roles.all must contain Role.Plan with wire label "plan"')
  assert.ok(allPublicRoleLabels.includes('plan'), 'allPublicRoleLabels must contain "plan"')
})

test('WHAT[planning-002] Plan role permission projection permits js-plan, ask, resume, handoff, deliver and denies repository and execution actions', () => {
  const allowed = permissions('plan')
  assert.ok(Array.isArray(allowed), 'permissions("plan") must return an array')

  // Expected permitted tools / capabilities for Plan role: js-plan, ask, resume, handoff, deliver
  const expectedPermissions = ['Ask', 'Deliver', 'Handoff', 'JsPlan', 'Resume']
  assert.deepEqual(
    [...allowed].sort(),
    expectedPermissions.sort(),
    'Plan role must have exactly the 5 allowed permissions: ask, deliver, handoff, js-plan, resume',
  )

  // Must strictly allow the 5 designated capabilities
  for (const perm of expectedPermissions) {
    assert.equal(isAllowed('plan', perm), true, `Plan must be allowed ${perm}`)
  }

  // Must strictly deny repository, execution and orchestration tools
  const forbiddenPermissions = [
    'Read',
    'Write',
    'Edit',
    'Glob',
    'Grep',
    'Move',
    'Remove',
    'Exec',
    'Pty',
    'Fork',
    'Fission',
    'ReviewAssessment',
    'Chronicle',
    'Fetch',
    'Finality',
    'BashHoneypot',
  ]
  for (const perm of forbiddenPermissions) {
    assert.equal(isAllowed('plan', perm), false, `Plan must be denied ${perm}`)
  }
})
