import assert from 'node:assert/strict'
import test from 'node:test'
import { permissions, isAllowed } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'
import { assertJsData } from '../../verification-system/tests/support/js-contract.mjs'

test('WHAT[office-capability-003] editing a returned permission projection cannot change the office authority', () => {
  for (const role of ['manager', 'orchestrator', 'engineer', 'devops']) {
    const expected = [...permissions(role)]
    const returned = permissions(role)
    assertJsData(returned, `permissions(${role})`)
    returned.splice(0, returned.length, 'InventedPermission')
    assert.deepEqual(permissions(role), expected, role)
    assert.equal(isAllowed(role, 'InventedPermission'), false)
  }
})

test('WHAT[office-capability-003] unknown, composite and retired names do not create an office authority', () => {
  for (const role of ['unknown', 'fast-engineer', 'deep-engineer', 'engineer-devops', 'blogger', 'coder', 'inspector', 'browser', 'inquiry', 'distiller']) {
    assert.deepEqual(permissions(role), [], role)
    for (const permission of ['Read', 'Write', 'Exec', 'Fission', 'Fork']) {
      assert.equal(isAllowed(role, permission), false, `${role}/${permission}`)
    }
  }
})
