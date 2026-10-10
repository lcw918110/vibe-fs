import assert from 'node:assert/strict'
import test from 'node:test'
import { rolePredicate } from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'

test('WHAT[intra-participant-parallelism-012] the role component of Fission permission admits Engineer and rejects other roles and aliases', () => {
  assert.equal(rolePredicate('fission', 'Engineer'), true)
  for (const role of [
    'Manager', 'DevOps', 'Orchestrator', 'Blogger', 'Bookkeeper', 'Predictor',
    'Coder', 'Inspector', 'Browser', 'Inquiry', 'Reviewer', 'Distiller',
    'engineer-alias', 'ManagerWithWorkRecord', 'DevOpsEngineer', 'CustomSubagent', 'self-claimed-engineer',
  ]) {
    assert.equal(rolePredicate('fission', role), false, role)
  }
})

test.todo('WHAT[intra-participant-parallelism-012] GAP-158: all production visibility and invocation paths use the same office capability decision')
