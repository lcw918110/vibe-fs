import assert from 'node:assert/strict'
import test from 'node:test'
import { allPublicRoleLabels, allRoleLabels } from '../../../dist/Foundation/RolesSurface.js'
import { isAllowed, permissions } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'

test('WHAT[work-021] Role catalog contains Plan and wire label is plan', () => {
  // Roles.all must contain Role.Plan with wire label 'plan'
  assert.ok(allRoleLabels.includes('plan'), 'Roles.all must contain Role.Plan with wire label "plan"')
  assert.ok(allPublicRoleLabels.includes('plan'), 'allPublicRoleLabels must contain "plan"')
})

test('WHAT[work-021] Plan separates human ask from devops investigation and denies direct execution', () => {
  const allowed = permissions('plan')
  assert.ok(Array.isArray(allowed), 'permissions("plan") must return an array')

  // WHAT[work-021]: ask asks the human; devops delegates an investigation.
  // resume and handoff are no longer part of the plan surface.
  const expectedPermissions = ['Ask', 'DevOpsConsultation', 'Deliver', 'JsPlan', 'Review']
  assert.deepEqual(
    [...allowed].sort(),
    expectedPermissions.sort(),
    'Plan must expose distinct human-question and DevOps capabilities',
  )

  // Each designated capability is actually allowed.
  for (const perm of expectedPermissions) {
    assert.equal(isAllowed('plan', perm), true, `Plan must be allowed ${perm}`)
  }

  // Retired plan tools must be strictly denied
  for (const perm of ['Handoff', 'Resume']) {
    assert.equal(isAllowed('plan', perm), false, `Plan must be denied retired tool ${perm}`)
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
    'Fetch',
    'Finality',
    'BashHoneypot',
  ]
  for (const perm of forbiddenPermissions) {
    assert.equal(isAllowed('plan', perm), false, `Plan must be denied ${perm}`)
  }
})
