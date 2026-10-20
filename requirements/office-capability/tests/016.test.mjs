import assert from 'node:assert/strict'
import test from 'node:test'
import { isAllowed } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'

test('WHAT[office-capability-016] Engineer owns local source work and exclusive Fission without real execution', () => {
  for (const permission of ['Read', 'Write', 'Edit', 'Glob', 'Grep', 'Move', 'Remove', 'Fission']) {
    assert.equal(isAllowed('engineer', permission), true, permission)
  }
  for (const permission of ['Exec', 'Pty', 'Fork', 'Resume']) {
    assert.equal(isAllowed('engineer', permission), false, permission)
  }
  for (const role of ['manager', 'orchestrator', 'devops', 'bookkeeper', 'plan', 'blogger', 'predictor', 'coder', 'inspector']) {
    assert.equal(isAllowed(role, 'Fission'), false, role)
  }
})
