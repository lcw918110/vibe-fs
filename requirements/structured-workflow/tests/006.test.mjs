import assert from 'node:assert/strict'
import test from 'node:test'
import * as tr from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'



test('WHAT[structured-workflow-006] the lifecycle finality gate is review: office-admitted for Manager, while retired deliver admits no role', () => {
  assert.equal(tr.admissionAuthority('review'), 'office')
  assert.equal(tr.rolePredicate('review', 'Manager'), true)
  assert.equal(tr.rolePredicate('deliver', 'Manager'), false)
  assert.equal(tr.rolePredicate('deliver', 'Coder'), false)
  assert.equal(tr.rolePredicate('deliver', 'Orchestrator'), false)
  assert.equal(tr.rolePredicate('deliver', 'Inspector'), false)
})

test('WHAT[structured-workflow-006] actual retirement freezes admissions and drains resources before completing its owning workflow', { todo: 'GAP-197: pure tool-registry predicate does not start Manager retirement runtime; actual retirement freeze and resource drain lifecycle is verified in relay-retirement' }, () => {
  // structured-workflow-006 establishes that SuicideTool is an office-gated tool belonging to the owning workflow.
  // The actual lifecycle execution (freeze admissions and drain blockers) requires relay-retirement runtime (GAP-197).
})
