import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { readFile } = await import("node:fs/promises");
const { default: test } = await import("node:test");

const templateUrl = new URL('../../../resources/wanxiangshu.mjs', import.meta.url)
const MANAGED = [
  'engineer',
  'manager',
  'orchestrator',
  'devops',
]

test('WHAT[execution-model-routing-001] EMR_001_recommended_resource_is_directly_executable_and_uses_full_model_selectors', async () => {
  const source = await readFile(templateUrl, 'utf8')
  assert.match(source, /export default function route/)
  const { default: scheduler } = await import(`${templateUrl.href}?test=${Date.now()}`)
  const { invokeScheduler } = await import('../../../dist/OpenCode/Host/ModelRoutingSurface.js')
  const route = (role, running, previous = null) => invokeScheduler(scheduler, role, running, previous)

  for (const role of MANAGED) {
    const selected = route(role, [])
    assert.ok(selected, `${role} must be schedulable from the empty recommended template`)
    assert.match(selected.model, /^[^/\s]+\/.+$/, `${role} must use provider/model, never a bare model id`)
    assert.ok(selected.reasoning.length > 0)
  }

  for (const retired of ['blogger', 'predictor', 'chronicle']) {
    const selected = route(retired, [])
    assert.equal(selected, null, `${retired} must fail closed (return null)`)
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { mkdtemp, readFile, rm, writeFile } = await import("node:fs/promises");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const { bootstrapAndLoadAt, invokeScheduler } = routing
const template = `export const routingProtocol = 2
export default function route(role, running) {
  if (role !== 'coder') return null
  return running.length === 0
    ? { model: 'provider/coder-model', reasoning: 'none' }
    : null
}\n`
const withTemp = async (run) => {
  const root = await mkdtemp(join(tmpdir(), 'wanxiangshu-routing-'))
  try {
    await run(join(root, 'nested', 'wanxiangshu.mjs'))
  } finally {
    await rm(root, { recursive: true, force: true })
  }
}

test('WHAT[execution-model-routing-001] EMR_001_missing_scheduler_is_created_once_then_loaded_from_disk', async () => {
  await withTemp(async (path) => {
    const scheduler = await bootstrapAndLoadAt(path, template)
    assert.equal(await readFile(path, 'utf8'), template)
    const selected = invokeScheduler(scheduler, 'coder', [])
    assert.equal(selected.model, 'provider/coder-model')
    assert.equal(selected.reasoning, 'none')
  })
})
test('WHAT[execution-model-routing-001] EMR_001_existing_scheduler_is_never_overwritten', async () => {
  await withTemp(async (path) => {
    // A user file from protocol 2: the loader must load it as-is and never
    // overwrite it with the recommended template.
    const existing = `export const routingProtocol = 2\nexport default () => ({ model: 'provider/user-choice', reasoning: 'high' })\n`
    await import('node:fs/promises').then(({ mkdir }) => mkdir(join(path, '..'), { recursive: true }))
    await writeFile(path, existing, 'utf8')

    const scheduler = await bootstrapAndLoadAt(path, template)
    assert.equal(await readFile(path, 'utf8'), existing)
    assert.equal(invokeScheduler(scheduler, 'coder', []).model, 'provider/user-choice')
  })
})
test('WHAT[execution-model-routing-001] EMR_001_concurrent_bootstrap_keeps_one_atomic_winner_without_merge', async () => {
  await withTemp(async (path) => {
    const [left, right] = await Promise.all([
      bootstrapAndLoadAt(path, template),
      bootstrapAndLoadAt(path, template),
    ])

    assert.equal(await readFile(path, 'utf8'), template)
    assert.equal(invokeScheduler(left, 'coder', []).model, 'provider/coder-model')
    assert.equal(invokeScheduler(right, 'coder', []).model, 'provider/coder-model')
  })
})
}
