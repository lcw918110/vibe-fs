import assert from 'node:assert/strict'
import test from 'node:test'
import path from 'node:path'
import { readCompileShardInventory } from '../../../scripts/lib/compile-shards.mjs'
import { buildSubsystemInventory } from '../../../scripts/checks/subsystems.mjs'

const ROOT = path.resolve(import.meta.dirname, '../../..')

test('WHAT[process-execution-012] request vocabulary is compiled separately from process and PTY runtime', { todo: 'GAP-091: current process-processrequest project compiles all these lifetimes together' }, () => {
  const inventory = buildSubsystemInventory({ compileInventory: readCompileShardInventory({ repositoryRoot: ROOT }) })
  assert.equal(inventory.ok, true, inventory.violations.join('\n'))
  const owners = relative => {
    const file = path.join(ROOT, 'src/Wanxiangshu', relative)
    return [...inventory.projects.values()].filter(project => project.implementationFiles.includes(file))
  }
  const [request] = owners('Process/ProcessRequest.fs')
  assert.ok(request)
  for (const relative of ['Process/PtySession.fs', 'Process/Spool.fs']) {
    const [runtime] = owners(relative)
    assert.ok(runtime)
    assert.notEqual(runtime.shard, request.shard, `${relative} must not share the request vocabulary compilation unit`)
  }
})

test.todo('WHAT[process-execution-012] actual consumers require injected process capabilities and cannot reach adapters through pure contracts')
