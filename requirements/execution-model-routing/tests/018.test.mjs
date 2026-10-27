import assert from 'node:assert/strict'
import test from 'node:test'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'

test('WHAT[execution-model-routing-018] routing supports new role collection (Engineer, DevOps) and decouples legacy slots', async () => {
  const route = (role) => {
    if (role === 'engineer') return { model: 'provider/engineer-model', reasoning: 'none' }
    if (role === 'devops') return { model: 'provider/devops-model', reasoning: 'none' }
    return null
  }

  // Must route engineer to a valid target
  const engineerTarget = route('engineer', [], null)
  assert.ok(engineerTarget, 'route("engineer") must return a valid model target')
  assert.equal(typeof engineerTarget.model, 'string')

  // Must route devops to a valid target
  const devopsTarget = route('devops', [], null)
  assert.ok(devopsTarget, 'route("devops") must return a valid model target')
  assert.equal(typeof devopsTarget.model, 'string')

  // Deprecated roles must not have routing slots in the template (must return null)
  assert.equal(route('coder', [], null), null, 'coder slot must be decoupled and return null')
  assert.equal(route('inspector', [], null), null, 'inspector slot must be decoupled and return null')
  assert.equal(route('browser', [], null), null, 'browser slot must be decoupled and return null')
  assert.equal(route('inquiry', [], null), null, 'inquiry slot must be decoupled and return null')
  assert.equal(route('distiller', [], null), null, 'distiller slot must be decoupled and return null')

  // Real ModelRoutingSurface runtime execution verification:
  const runtime = routing.createRuntime(route)

  // Active new roles must acquire admission successfully
  const engAcquisition = await routing.acquireExecutionAdmission(
    runtime,
    'ses_eng_1',
    'msg_eng_1',
    'engineer',
    'Ada',
    null,
  )
  assert.equal(engAcquisition.kind, 'Acquired', 'engineer must acquire execution admission')
  const engTarget = routing.executionAdmissionTarget(runtime, engAcquisition.lease)
  assert.ok(engTarget && engTarget.model, 'engineer acquisition must produce valid target')

  const devAcquisition = await routing.acquireExecutionAdmission(
    runtime,
    'ses_dev_1',
    'msg_dev_1',
    'devops',
    'devops',
    null,
  )
  assert.equal(devAcquisition.kind, 'Acquired', 'devops must acquire execution admission')
  const devTarget = routing.executionAdmissionTarget(runtime, devAcquisition.lease)
  assert.ok(devTarget && devTarget.model, 'devops acquisition must produce valid target')

  // Deprecated roles must fail closed and NEVER enter waiting queue (pendingCount === 0)
  const deprecatedRoles = ['coder', 'inspector', 'browser', 'inquiry', 'distiller']
  for (const depRole of deprecatedRoles) {
    await assert.rejects(
      async () => {
        await routing.acquireExecutionAdmission(
          runtime,
          `ses_${depRole}`,
          `msg_${depRole}`,
          depRole,
          'TestAgent',
          null,
        )
      },
      (err) => {
        assert.match(String(err), /deprecated role/)
        return true
      },
      `deprecated role ${depRole} must fail closed on admission acquire`,
    )
    assert.equal(routing.pendingCount(runtime), 0, `deprecated role ${depRole} must not enter pending queue`)
    assert.equal(
      routing.tryLease(runtime, `ses_lease_${depRole}`, `msg_lease_${depRole}`, depRole, 'TestAgent', null),
      null,
      `deprecated role ${depRole} must not acquire lease`,
    )
  }
})
