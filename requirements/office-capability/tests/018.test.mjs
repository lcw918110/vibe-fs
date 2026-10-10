import assert from 'node:assert/strict'
import test from 'node:test'
import { isAllowed, managerForkableOffices } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'

test('WHAT[office-capability-018] Sphinx and historical Inquiry have no public office permissions', () => {
  assert.equal(managerForkableOffices().includes('sphinx'), false)
  for (const role of ['sphinx', 'inquiry']) {
    for (const permission of ['Read', 'Write', 'Exec', 'Fission']) {
      assert.equal(isAllowed(role, permission), false, `${role}/${permission}`)
    }
  }
})

test('WHAT[office-capability-018] the standard Engineer entitlement includes source work and fission but no execution or DevOps dispatch', () => {
  for (const permission of ['Read', 'Write', 'Edit', 'Fission']) {
    assert.equal(isAllowed('engineer', permission), true, permission)
  }
  for (const permission of ['Exec', 'Pty', 'Fork', 'Resume']) {
    assert.equal(isAllowed('engineer', permission), false, permission)
  }
})

test.todo('WHAT[office-capability-018] actual Sphinx workflow must bind standard Engineer authority and retain ordinary worktree and Fission admission; a role matrix does not prove workflow wiring')
