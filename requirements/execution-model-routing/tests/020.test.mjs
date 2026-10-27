{
const { default: assert } = await import("node:assert/strict");
const { mkdtemp, readFile, rm } = await import("node:fs/promises");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const { invokeScheduler } = routing

test('WHAT[execution-model-routing-020] EMR_020_scheduler_serves_only_role_pools', () => {
  const scheduler = (role) => role === 'engineer' ? { model: 'provider/engineer-model', reasoning: 'none' } : null
  scheduler.hasTheoreticalCapacity = (role) => role === 'engineer'

  assert.equal(scheduler.hasTheoreticalCapacity('engineer'), true)
  assert.equal(scheduler.hasTheoreticalCapacity('unknown-role'), false)
  const target = invokeScheduler(scheduler, 'engineer', [], null)
  assert.deepEqual(target, { model: 'provider/engineer-model', reasoning: 'none' })
})
}
