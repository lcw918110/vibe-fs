{
const { default: assert } = await import("node:assert/strict");
const { mkdtemp, readFile, rm } = await import("node:fs/promises");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const { bootstrapAndLoadAt, invokeScheduler } = routing
const templateUrl = new URL('../../../resources/wanxiangshu.mjs', import.meta.url)

const load = async (body) => {
  const root = await mkdtemp(join(tmpdir(), 'wanxiangshu-routing-config-'))
  const path = join(root, 'nested', 'wanxiangshu.mjs')
  try {
    return await bootstrapAndLoadAt(path, body)
  } finally {
    await rm(root, { recursive: true, force: true })
  }
}

test('WHAT[execution-model-routing-020] EMR_020_no_second_pool_loader_loads_pure_role_scheduler', async () => {
  const scheduler = await load(await readFile(templateUrl, 'utf8'))
  assert.equal(typeof scheduler, 'function')
  assert.equal(typeof scheduler.hasTheoreticalCapacity, 'function')

  // Capacity is answered per role
  assert.equal(scheduler.hasTheoreticalCapacity('engineer'), true)
  assert.equal(scheduler.hasTheoreticalCapacity('unknown-role'), false)
})

test('WHAT[execution-model-routing-020] EMR_020_no_separate_configuration_query_required', async () => {
  const scheduler = await load(`export const routingProtocol = 2
export const hasTheoreticalCapacity = (role) => role === 'engineer'
export default function route(role, running, previous) {
  return role === 'engineer' ? { model: 'provider/engineer-model', reasoning: 'none' } : null
}
`)
  assert.equal(typeof scheduler, 'function')
  const target = invokeScheduler(scheduler, 'engineer', [], null)
  assert.deepEqual(target, { model: 'provider/engineer-model', reasoning: 'none' })
})

test('WHAT[execution-model-routing-020] EMR_020_extra_query_export_does_not_open_second_axis', async () => {
  const scheduler = await load(`export const routingProtocol = 2
export const hasTheoreticalCapacity = (role) => role === 'engineer'
export default function route(role, running, previous) {
  return role === 'engineer' ? { model: 'provider/role-model', reasoning: 'none' } : null
}
`)
  assert.equal(typeof scheduler, 'function')
  const target = invokeScheduler(scheduler, 'engineer', [], null)
  assert.deepEqual(target, { model: 'provider/role-model', reasoning: 'none' })
})

test('WHAT[execution-model-routing-020] EMR_020_route_serves_only_role_pools', async () => {
  const scheduler = await load(await readFile(templateUrl, 'utf8'))
  const normal = invokeScheduler(scheduler, 'engineer', [], null)
  assert.equal(normal.model, 'cursor/cursor-grok-4.6-xhigh')
})
}
