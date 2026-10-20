import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { configure: configureManagedAgents, installDefaultResources } = await import("../../../dist/OpenCode/Host/ManagedAgentConfigSurface.js");
const { toolSpecNames } = await import("../../../dist/OpenCode/Tools/ToolSurface.js");
const { permissions } = await import("../../../dist/Participant/Persona/OfficeCapabilitySurface.js");

const permissionKey = 'sphinx_*'
const ROLES = [
  'Manager',
  'Orchestrator',
  'Engineer',
  'DevOps',
]
const agentName = (role) => `${role.toLowerCase()}`
const buildConfig = () => {
  const agent = {}
  for (const role of ROLES) {
    agent[agentName(role)] = { model: `${role.toLowerCase()}-model` }
  }
  return { agent }
}
installDefaultResources()

test('WHAT[capability-enforcement-007] sphinx_native_surface_is_absent_under_mcp_only', () => {
  // WP-039 守护（MCP-only）：Sphinx 以独立 MCP stdio 服务存在，插件不提供原生
  // sphinx 工具面。以下断言固定三处现状：ToolRegistry 无 sphinx ToolSpec、
  // 能力词表无 Sphinx、provider schema 无 sphinx。
  assert.equal(toolSpecNames().includes('sphinx'), false, 'ToolRegistry must not produce a sphinx ToolSpec')

  const config = buildConfig()
  assert.equal(configureManagedAgents(config).ok, true)

  for (const role of ROLES) {
    const name = agentName(role)
    assert.equal(permissions(name).includes('Sphinx'), false, `${name} capability catalog must not contain the retired Sphinx permission`)
    const permission = config.agent[name].permission
    assert.equal(
      permission[permissionKey],
      undefined,
      `${name} must not install the retired sphinx_* permission`,
    )
    assert.equal(permission.sphinx, undefined, `${name} must not expose any sphinx tool in the provider schema`)
    assert.equal(permission['*'], 'deny', 'retired and unknown tool names remain denied by default')
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { configure: configureManagedAgents, installDefaultResources } = await import("../../../dist/OpenCode/Host/ManagedAgentConfigSurface.js");
const { allRoleLabels } = await import("../../../dist/Foundation/RolesSurface.js");
const { isAllowed } = await import("../../../dist/Participant/Persona/OfficeCapabilitySurface.js");

const ROLES = [
  'Manager',
  'Orchestrator',
  'Engineer',
  'DevOps',
]
const agentName = (role) => `${role.toLowerCase()}`
const buildConfig = () => {
  const agent = {}
  for (const role of ROLES) {
    agent[agentName(role)] = { model: `${role.toLowerCase()}-model` }
  }
  return { agent }
}
installDefaultResources()

test('WHAT[capability-enforcement-007] browser_role_is_revoked_from_canonical_active_roles', () => {
  assert.equal(allRoleLabels.includes('browser'), false, 'Browser role must be deleted from canonical active roles')
})
test('WHAT[capability-enforcement-007] network_and_stealth_browser_mcp_are_denied_for_all_roles', () => {
  const config = buildConfig()
  assert.equal(configureManagedAgents(config).ok, true)

  for (const role of ROLES) {
    const name = agentName(role)
    const permission = config.agent[name].permission
    assert.notEqual(permission['stealth-browser-mcp_*'], 'allow', `${name} must not allow stealth-browser-mcp`)
    assert.equal(isAllowed(name, 'Network'), false, `${name} must not have Network permission`)
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { configure: configureManagedAgents, installDefaultResources } = await import("../../../dist/OpenCode/Host/ManagedAgentConfigSurface.js");

installDefaultResources()

// capability-enforcement-007: a Host-declared external (MCP) tool namespace is
// not an office capability and gets no per-source permission table. The live
// Host config is the only authority for which namespaces exist; the projection
// forwards exactly those, and the runtime gate refuses them (see the
// integration block below).
const MCP_SERVER = 'wanxiang_fixture'
const MCP_PATTERN = `${MCP_SERVER}_*`
const MCP_TOOL = `${MCP_SERVER}_search`
const MCP_SERVERS = { [MCP_SERVER]: { type: 'local', command: ['node', 'fixture.js'], enabled: true } }
const AGENT_NAMES = ['manager', 'orchestrator', 'engineer', 'devops', 'plan', 'bookkeeper']
// Office roles the managed catalog projects onto the Host; Bookkeeper is the hidden leaf (deny-all).
const OFFICE_AGENTS = ['manager', 'orchestrator', 'engineer', 'devops', 'plan']
const CLOSED_AGENTS = ['bookkeeper']

const buildConfig = (extra = {}) => ({
  agent: Object.fromEntries(AGENT_NAMES.map((name) => [name, {}])),
  ...extra,
})

// Host's own agent-permission fallback (`*` -> allow) merged with the plugin's
// map, last match wins — the same model the neighboring tests use.
const wildcardMatch = (input, pattern) =>
  new RegExp('^' + pattern.replace(/[.+^$${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*') + '$', 's').test(input)
const evaluate = (permission, tool) =>
  [
    { permission: '*', action: 'allow' },
    ...Object.entries(permission).map(([key, action]) => ({ permission: key, action })),
  ].reverse().find((rule) => wildcardMatch(tool, rule.permission))?.action ?? 'ask'
const namespaceKeys = (permission) => Object.keys(permission).filter((key) => key.endsWith('_*'))
const withoutNamespaces = (permission) =>
  Object.fromEntries(Object.entries(permission).filter(([key]) => !key.endsWith('_*')))

test('WHAT[capability-enforcement-007] host_declared_mcp_namespace_is_advertised_without_widening_the_catch_all', () => {
  const config = buildConfig({ mcp: MCP_SERVERS })
  assert.equal(configureManagedAgents(config).ok, true)

  for (const name of OFFICE_AGENTS) {
    const permission = config.agent[name].permission
    assert.equal(
      evaluate(permission, MCP_TOOL),
      'allow',
      `${name} must advertise the Host-declared MCP tool (a blanket deny is what hid it)`,
    )
    assert.equal(permission['*'], 'deny', `${name} must keep unknown tool names denied by default`)
    assert.equal(permission[MCP_PATTERN], 'allow', `${name} must forward the Host-declared namespace`)
  }
})

test('WHAT[capability-enforcement-007] mcp_namespace_forwarding_moves_no_office_boundary', () => {
  const withoutMcp = buildConfig()
  const withMcp = buildConfig({ mcp: MCP_SERVERS })
  assert.equal(configureManagedAgents(withoutMcp).ok, true)
  assert.equal(configureManagedAgents(withMcp).ok, true)

  for (const name of AGENT_NAMES) {
    const expected = OFFICE_AGENTS.includes(name) ? [MCP_PATTERN] : []
    assert.deepEqual(
      namespaceKeys(withMcp.agent[name].permission),
      expected,
      `${name} must forward exactly the Host-declared external namespace`,
    )
    assert.deepEqual(
      withoutNamespaces(withMcp.agent[name].permission),
      withoutMcp.agent[name].permission,
      `${name} office projection must be unchanged by an MCP declaration`,
    )
  }

  assert.equal(withMcp.agent.bookkeeper.permission['*'], 'deny', 'Bookkeeper stays deny-all')
  assert.equal(withMcp.agent.devops.permission.fission, 'deny', 'DevOps keeps no fission')
  assert.equal(withMcp.agent.engineer.permission['todowrite-for-orchestrator'], 'deny', 'Engineer keeps its own js surface')
})

test('WHAT[capability-enforcement-007] undeclared_or_disabled_mcp_connections_project_no_namespace', () => {
  const undeclared = buildConfig()
  const disabled = buildConfig({
    mcp: { [MCP_SERVER]: { type: 'local', command: ['node', 'fixture.js'], enabled: false } },
  })
  assert.equal(configureManagedAgents(undeclared).ok, true)
  assert.equal(configureManagedAgents(disabled).ok, true)

  for (const name of OFFICE_AGENTS) {
    const undeclaredPermission = undeclared.agent[name].permission
    assert.equal(evaluate(undeclaredPermission, MCP_TOOL), 'deny', `${name} must deny an MCP tool the Host never declared`)
    assert.equal(evaluate(undeclaredPermission, 'semble_search'), 'deny', `${name} must invent no other MCP namespace`)
    assert.equal('semble_*' in undeclaredPermission, false, `${name} must not create a semble_* entry`)
    assert.equal(
      disabled.agent[name].permission[MCP_PATTERN],
      undefined,
      `${name} must not forward a namespace the Host config disables`,
    )
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { integrationTest } = await import("../../verification-system/tests/support/tier-gate.mjs");
const { acceptAuthorityRoot, withExecutablePlugin } = await import("../../verification-system/tests/support/plugin-fixture.mjs");

const MCP_SERVER = 'wanxiang_fixture'
const MCP_PATTERN = `${MCP_SERVER}_*`
const MCP_TOOL = `${MCP_SERVER}_search`
const DENIAL = /external integration tool|外部集成工具/i

integrationTest('WHAT[capability-enforcement-007] advertised_mcp_tool_fails_closed_at_execution_for_a_managed_session', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const config = {
      agent: { manager: {} },
      mcp: { [MCP_SERVER]: { type: 'local', command: ['node', 'fixture.js'], enabled: true } },
    }
    await hooks.config(config)

    // Wire side: the same process forwarded the Host-declared namespace.
    assert.equal(config.agent.manager.permission[MCP_PATTERN], 'allow', 'manager must advertise the Host-declared namespace')

    // Execution side: a canonical office session is refused before any effect.
    await acceptAuthorityRoot(runtime, 'ses-mcp-owner', 'manager')
    const call = { args: { query: 'deterministic semantic search' } }
    await assert.rejects(
      hooks['tool.execute.before']({ tool: MCP_TOOL, sessionID: 'ses-mcp-owner', callID: 'mcp-call-1' }, call),
      DENIAL,
      'an external MCP tool must fail closed at execution even though it is advertised',
    )
    assert.deepEqual(call.args, { query: 'deterministic semantic search' }, 'the refusal leaves the call untouched')

    // Plugin-owned names are not this gate's business.
    await hooks['tool.execute.before'](
      { tool: 'fork', sessionID: 'ses-mcp-owner', callID: 'owned-call' },
      { args: {} },
    )

    // A Host session with no office authority keeps the Host's own MCP surface.
    await hooks['tool.execute.before'](
      { tool: MCP_TOOL, sessionID: 'ses-unmanaged-host-session', callID: 'mcp-call-2' },
      { args: {} },
    )
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { integrationTest } = await import("../../verification-system/tests/support/tier-gate.mjs");
const { acceptAuthorityRoot, withExecutablePlugin } = await import("../../verification-system/tests/support/plugin-fixture.mjs");

const FIRST_SERVER = 'wanxiang_fixture'
const SECOND_SERVER = 'wanxiang_second'
const FIRST_TOOL = `${FIRST_SERVER}_search`
const FIRST_PATTERN = `${FIRST_SERVER}_*`
const DENIAL = /external integration tool|外部集成工具/i
const serverEntry = () => ({ type: 'local', command: ['node', 'fixture.js'], enabled: true })

integrationTest('WHAT[capability-enforcement-007] a_later_narrower_config_cannot_un_declare_a_live_session_namespace', async () => {
  // A session's permission map outlives the config application that wrote it:
  // a reload can withdraw a server, and HOST-012 shares the module singleton
  // across per-directory plugin instances. The gate must therefore never lose a
  // namespace a map this process wrote still advertises.
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const first = { agent: { manager: {} }, mcp: { [FIRST_SERVER]: serverEntry() } }
    await hooks.config(first)
    assert.equal(first.agent.manager.permission[FIRST_PATTERN], 'allow', 'first application advertises the namespace')

    const second = { agent: { engineer: {} }, mcp: { [SECOND_SERVER]: serverEntry() } }
    await hooks.config(second)
    assert.equal(
      second.agent.engineer.permission[FIRST_PATTERN],
      undefined,
      'a later application must not widen another workspace/config into the withdrawn namespace',
    )
    assert.equal(second.agent.engineer.permission[`${SECOND_SERVER}_*`], 'allow', 'the later application projects its own namespace')

    const third = { agent: { manager: {} } }
    await hooks.config(third)
    assert.equal(third.agent.manager.permission[FIRST_PATTERN], undefined, 'a config without mcp projects no namespace')

    // Sessions projected by the first application are still closed at execution.
    await acceptAuthorityRoot(runtime, 'ses-first-world-owner', 'manager')
    await assert.rejects(
      hooks['tool.execute.before'](
        { tool: FIRST_TOOL, sessionID: 'ses-first-world-owner', callID: 'withdrawn-call' },
        { args: {} },
      ),
      DENIAL,
      'a namespace withdrawn by a later config must still fail closed for a live session',
    )
  })
})
}
