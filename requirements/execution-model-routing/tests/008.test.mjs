import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const { createSdkClientPort, sendPrompt } = routing
const promptOptions = (overrides = {}) => ({
  model: undefined,
  agent: undefined,
  directory: undefined,
  metadata: undefined,
  tools: undefined,
    ...overrides,
})

test('WHAT[execution-model-routing-008] EMR_008_sdk_prompt_never_recovers_a_model_from_agent_or_host_inventory', async () => {
  let payload
  const client = {
    session: {
      promptAsync: async (value) => {
        payload = value
        return {}
      },
    },
  }
  const port = createSdkClientPort(client)

  await sendPrompt(port, 'session-2', 'hello', promptOptions({ agent: 'engineer' }))

  assert.equal(payload.model, undefined)
  assert.equal(payload.variant, undefined)
  assert.equal(payload.body.model, undefined)
  assert.equal(payload.body.variant, undefined)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { readFile } = await import("node:fs/promises");
const { default: test } = await import("node:test");

const source = async (relative) => readFile(new URL(`../../../${relative}`, import.meta.url), 'utf8')

test('WHAT[execution-model-routing-008] EMR_008_host_inventory_no_longer_exposes_model_binding_authority', async () => {
  const managed = await source('src/Wanxiangshu/OpenCode/Host/ManagedAgentConfig.fs')
  const port = await source('src/Wanxiangshu/OpenCode/Host/OpenCodePort.fs')
  const wiring = await source('src/Wanxiangshu/OpenCode/Plugin/PluginSessionWiring.fs')

  assert.doesNotMatch(managed, /tryBoundModel|tryOpencodeModel|DuplicatePairModel|liveInventory|Model:\s*string/)
  assert.doesNotMatch(port, /ManagedAgentConfig\.tryBoundModel/)
  assert.doesNotMatch(wiring, /ManagedAgentInventory|tryOpencodeModel|promptModelFor/)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");
const { withExecutablePlugin } = await import("../../verification-system/tests/support/plugin-fixture.mjs");

// execution-model-routing-008: distinct current roles may hold the same physical target.
test('WHAT[execution-model-routing-008] EMR_008_distinct_current_roles_may_hold_the_same_physical_target', async () => {
  const sameTarget = { model: 'provider/shared', reasoning: 'none' }
  const runtime = routing.createRuntime(() => sameTarget)
  for (const role of ['engineer', 'devops', 'manager', 'orchestrator', 'plan']) {
    const acquired = await routing.acquireExecutionAdmission(runtime, role, `msg-${role}`, role, role, null, 'normal')
    assert.equal(acquired.kind, 'Acquired')
    assert.deepEqual(routing.executionAdmissionTarget(runtime, acquired.lease), sameTarget)
  }
  assert.deepEqual(routing.snapshotOccupied(runtime), Array.from({ length: 5 }, () => sameTarget))
  for (const role of ['engineer', 'devops', 'manager', 'orchestrator', 'plan']) {
    routing.releasePhysicalExecution(runtime, role, `msg-${role}`)
  }
})

// execution-model-routing-008: the live plugin ignores both a configured Host
// agent model and an incoming message model; chat.message owns the projection.
test('WHAT[execution-model-routing-008] EMR_008_actual_plugin_ignores_configured_host_agent_model_and_incoming_message_model', async () => {
  await withExecutablePlugin(async (hooks) => {
    const output = { message: {
      id: 'msg-config-precedence', role: 'user', sessionID: 'ses-config-precedence', agent: 'engineer',
      model: { providerID: 'host', modelID: 'override-attempt' },
    }, parts: [] }
    await hooks['chat.message']({ sessionID: 'ses-config-precedence', messageID: 'msg-config-precedence', agent: 'engineer' }, output)
    assert.deepEqual({ ...output.message.model }, { providerID: 'provider', modelID: 'engineer-model', variant: 'none' })
  })
})
}
