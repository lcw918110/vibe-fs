import assert from 'node:assert/strict'
import test from 'node:test'
import { permissions } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'
import { plan } from '../../../dist/Participant/Provider/Attempt/PlannerSurface.js'

test('WHAT[capability-enforcement-003] request projections retain their kind and never grant permissions outside the office', () => {
  for (const [role, kind] of [
    ['engineer', 'work-main'], ['devops', 'work-main'], ['manager', 'work-main'],
    ['engineer', 'interaction-repair'],
  ]) {
    const planned = plan({ role, kind })
    assert.equal(planned.ok, true, planned.error)
    assert.equal(planned.requestKind, kind)
    const authority = new Set(permissions(role))
    for (const permission of planned.toolCapabilities) {
      assert.equal(authority.has(permission), true, `${role}/${kind} cannot acquire ${permission}`)
    }
  }
  for (const [role, kind] of [
    ['engineer', 'strength-replica'], ['devops', 'strength-replica'], ['blogger', 'blogger-main'],
  ]) {
    const planned = plan({ role, kind })
    assert.equal(planned.ok, false, `retired request kind or role '${role}/${kind}' must fail closed`)
  }
})
