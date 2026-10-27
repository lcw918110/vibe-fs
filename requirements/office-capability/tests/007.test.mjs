import assert from 'node:assert/strict'
import test from 'node:test'
import { isAllowed, managerForkableOffices } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'

test('WHAT[office-capability-007] Manager entitlement includes review reading and delegation but excludes mutation execution and fission', () => {
  assert.deepEqual(managerForkableOffices(), ['engineer'])
  for (const permission of ['Resume', 'Read', 'Glob', 'Grep']) {
    assert.equal(isAllowed('manager', permission), true, permission)
  }
  for (const permission of ['Write', 'Edit', 'Move', 'Remove', 'Exec', 'Pty', 'Fission']) {
    assert.equal(isAllowed('manager', permission), false, permission)
  }
})

test('WHAT[office-capability-007] an existing DevOps model target accepts the same target and rejects drift', async () => {
  const routing = await import('../../../dist/OpenCode/Host/ModelRoutingSurface.js')
  await routing.initialize()
  const route = () => ({ model: 'host/fixed-model', reasoning: 'none' })
  const runtime = routing.createRuntime(route)
  const road = 'office-road'
  const target = { model: 'host/fixed-model', reasoning: 'none' }
  routing.seedDevOpsModelTarget(runtime, road, 'host/fixed-model:none')
  assert.deepEqual(routing.boundDevopsTarget(runtime, road), target)
  assert.throws(
    () => routing.seedDevOpsModelTarget(runtime, road, 'host/other-model:none'),
    /durable DevOps model binding is immutable/,
  )
  assert.deepEqual(routing.boundDevopsTarget(runtime, road), target)
})
test.todo('WHAT[office-capability-007] actual Manager review reading and native-tool refusal require the capability-enforcement-025 runtime entry; entitlement alone does not prove the current window')
