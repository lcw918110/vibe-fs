import assert from 'node:assert/strict'
import test from 'node:test'
import { isAllowed } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'
import * as completedTurn from '../../../dist/Interaction/Repair/CompletedTurnSurface.js'

// WHAT[work-032] keeps unrelated offices unchanged. Manager shares human Ask;
// it must not inherit Plan's DevOps consultation or draft capabilities. Two
// observable faces are asserted here: non-Plan roles never receive the
// Plan-only permission projection, and non-Plan empty stops keep requiring
// interaction repair (the full classification suite lives in 027.test.mjs
// under work-027). Manager's review tool is reached through the
// ReviewAssessment permission and the shared shell of work-038; the
// Plan-backend Review label itself never appears in a non-Plan projection.
// The Baton isolation trigger is gated by ownerRole = Plan inside the
// transform pipeline (PluginTransforms planSessionOf); its live proof
// belongs to the physical host canary tracked by GAP-230, not to this pure
// surface.

test('WHAT[work-032] non-Plan roles never receive the Plan-only tool projection', () => {
  const planOnlyPermissions = ['JsPlan', 'DevOpsConsultation', 'Deliver', 'Review']
  const nonPlanRoles = ['manager', 'engineer', 'devops', 'orchestrator']
  // Each role label must resolve to a real role, so the deny below is a
  // genuine projection fact and not an unknown-role fallback.
  const roleWitness = {
    manager: 'Fork',
    engineer: 'Write',
    devops: 'Exec',
    orchestrator: 'Fork',
  }
  for (const role of nonPlanRoles) {
    assert.equal(isAllowed(role, roleWitness[role]), true, `${role} must resolve as a real role`)
    for (const perm of planOnlyPermissions) {
      assert.equal(isAllowed(role, perm), false, `${role} must be denied ${perm}`)
    }
  }
})

test('WHAT[work-032] non-Plan empty stops keep requiring interaction repair', () => {
  // There is no Plan empty-stop exemption to propagate to another role.
  assert.equal(completedTurn.needsInteractionRepair('engineer', true, 'stop', []), true)
  assert.equal(completedTurn.needsInteractionRepair('manager', true, 'stop', []), true)
})

test('WHAT[work-032] only Plan may ask the human; Manager and other roles are denied ask', () => {
  assert.equal(isAllowed('plan', 'Ask'), true)
  for (const role of ['manager', 'engineer', 'devops', 'orchestrator', 'coder', 'inspector']) {
    assert.equal(isAllowed(role, 'Ask'), false, role)
    assert.equal(isAllowed(role, 'DevOpsConsultation'), false, role)
  }
})
