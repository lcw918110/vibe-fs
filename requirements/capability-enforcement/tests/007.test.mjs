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
  'Blogger',
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
  'Blogger',
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
