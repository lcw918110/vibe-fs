import test from 'node:test'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { permissions } = await import("../../../dist/Participant/Persona/OfficeCapabilitySurface.js");
const { configure: configureManagedAgents, installDefaultResources, validate: validateManagedAgents } = await import("../../../dist/OpenCode/Host/ManagedAgentConfigSurface.js");

installDefaultResources()
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
    agent[agentName(role)] = {
      model: `${role.toLowerCase()}-model`,
    }
  }
  agent.bookkeeper = { model: 'bookkeeper-model' }
  agent.predictor = { model: 'predictor-model' }
  return { agent }
}
const wildcardMatch = (input, pattern) => {
  const normalized = input.replaceAll('\\', '/')
  let escaped = pattern
    .replaceAll('\\', '/')
    .replace(/[.+^$${}()|[\]\\]/g, '\\$&')
    .replace(/\*/g, '.*')
    .replace(/\?/g, '.')
  if (escaped.endsWith(' .*')) escaped = escaped.slice(0, -3) + '( .*)?'
  return new RegExp('^' + escaped + '$', 's').test(normalized)
}
const evaluate = (rules, permission, pattern) =>
  [...rules].reverse().find((r) => wildcardMatch(permission, r.permission) && wildcardMatch(pattern, r.pattern)) ?? {
    action: 'ask',
  }
const rulesOf = (permissionObj) => {
  const rules = []
  for (const key in permissionObj) {
    const value = permissionObj[key]
    if (typeof value === 'string') {
      rules.push({ permission: key, action: value, pattern: '*' })
      continue
    }
    for (const pattern in value) rules.push({ permission: key, pattern, action: value[pattern] })
  }
  return rules
}
const hostDefaults = () => [
  { permission: '*', pattern: '*', action: 'allow' },
  { permission: 'doom_loop', pattern: '*', action: 'ask' },
  { permission: 'external_directory', pattern: '*', action: 'ask' },
  { permission: 'question', pattern: '*', action: 'deny' },
  { permission: 'plan_enter', pattern: '*', action: 'deny' },
  { permission: 'plan_exit', pattern: '*', action: 'deny' },
  { permission: 'read', pattern: '*', action: 'allow' },
  { permission: 'read', pattern: '*.env', action: 'ask' },
  { permission: 'read', pattern: '*.env.*', action: 'ask' },
  { permission: 'read', pattern: '*.env.example', action: 'allow' },
]
const mergedRules = (config, name) => [...hostDefaults(), ...rulesOf(config.agent[name].permission)]
const allowList = (config, name) => {
  const rules = mergedRules(config, name)
  const tools = [
    'bash',
    'bash-honeypot',
    'assume',
    'todowrite',
    'read',
    'write',
    'edit',
    'glob',
    'grep',
    'mv',
    'rm',
    'run',
    'fork',
    'resume',
    'commission',
    'open-terminal',
    'send-terminal',
    'read-terminal',
    'signal-terminal',
    'join',
    'horizon',
    'fission',
    'review',
    'chronicle',
    'fetch',
    'suicide',
    'skill',
  ]
  return tools.filter((tool) => evaluate(rules, tool, '*').action === 'allow')
}
const HOST_UTILITY_ALLOW = ['skill']
const COGNITIVE_UTILITY_ALLOW = ['assume', 'todowrite']
const hostUtilityAllowFor = (role) => (role === 'Blogger' ? [] : HOST_UTILITY_ALLOW)
const cognitiveUtilityAllowFor = (role) => (role === 'Blogger' ? [] : COGNITIVE_UTILITY_ALLOW)
const ROLE_ALLOW = {
  Manager: ['fork', 'resume', 'join', 'horizon', 'suicide', 'review'],
  Orchestrator: ['commission', 'join', 'horizon'],
  Engineer: ['read', 'write', 'edit', 'glob', 'grep', 'mv', 'rm', 'bash-honeypot', 'fetch', 'fission'],
  DevOps: [
    'read',
    'write',
    'edit',
    'glob',
    'grep',
    'mv',
    'rm',
    'run',
    'join',
    'horizon',
    'open-terminal',
    'send-terminal',
    'read-terminal',
    'signal-terminal',
  ],
  Blogger: ['chronicle'],
}

test('WHAT[capability-enforcement-011] AGENT_002_missing_agent_is_projected_on_configure', () => {
  const config = buildConfig()
  delete config.agent.engineer
  const outcome = configureManagedAgents(config)
  assert.equal(outcome.ok, true, outcome.error)
  const entry = config.agent.engineer
  assert.equal(entry.mode, 'primary')
  assert.equal(entry.permission['*'], 'deny')
  assert.ok(typeof entry.prompt === 'string' && entry.prompt.length > 0)
  assert.equal('model' in entry, false)
})
test('WHAT[capability-enforcement-011] AGENT_002_owned_writes_never_touch_the_model_binding', () => {
  const config = buildConfig()
  const before = {}
  for (const name in config.agent) before[name] = config.agent[name].model
  const outcome = configureManagedAgents(config)
  assert.equal(outcome.ok, true, outcome.error)
  for (const name in before) {
    assert.equal(config.agent[name].model, before[name], `model binding of ${name} must be untouched`)
  }
})
test('WHAT[capability-enforcement-011] AGENT_019_external_directory_overrides_host_default_ask', () => {
  const config = buildConfig()
  assert.equal(configureManagedAgents(config).ok, true)

  for (const role of ROLES) {
    const name = agentName(role)
    const rules = mergedRules(config, name)
    const action = evaluate(rules, 'external_directory', '/tmp/outside/*').action
    assert.equal(action, 'allow', `${name} must allow external_directory (got ${action})`)
    assert.equal(
      config.agent[name].permission.external_directory,
      'allow',
      `${name} permission object must set external_directory allow`,
    )
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const { installDefaultResources, validate, configure, configureManager } = await import(
  '../../../dist/OpenCode/Host/ManagedAgentConfigSurface.js'
)
installDefaultResources()
const okOf = (result) => result
const errOf = (result) => result.error
const NAMES = [
  'manager',
  'orchestrator',
  'engineer',
  'devops',
  'blogger',
  'plan',
  'bookkeeper',
  'predictor',
]
function fullConfig() {
  return { agent: Object.fromEntries(NAMES.map((name) => [name, {}])) }
}

test('WHAT[capability-enforcement-011] MACFG_validate_accepts_empty_agent_map_and_projects_full_catalog', () => {
  const empty = okOf(validate({}))
  assert.equal(empty.ok, true, empty.ok ? '' : empty.error)
  assert.equal(empty.bindingNames.length, 6)
  const blankMap = okOf(validate({ agent: {} }))
  assert.equal(blankMap.ok, true, blankMap.ok ? '' : blankMap.error)
})
test('WHAT[capability-enforcement-011] MACFG_validate_accepts_missing_equal_and_arbitrary_model_fields', () => {
  const cfg = fullConfig()
  cfg.agent.manager.model = 'provider/shared'
  cfg.agent.engineer.model = ''
  cfg.agent.devops.model = { anything: 'host-owned-and-ignored' }

  const result = okOf(validate(cfg))
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  assert.equal(result.bindingNames.length, 6, 'bookkeeper and predictor have no active Role binding')
})
test('WHAT[capability-enforcement-011] MACFG_applyOwnedFields_writes_owned_keys_and_never_touches_model', () => {
  const cfg = fullConfig()
  for (const name of NAMES) cfg.agent[name].model = `host/${name}`
  const result = configure(cfg)
  assert.equal(result.ok, true, result.ok ? '' : result.error)

  assert.equal(cfg.compaction.auto, false)
  for (const name of NAMES) {
    const entry = cfg.agent[name]
    assert.ok(entry.mode !== undefined, `${name} must receive owned mode`)
    assert.ok(entry.permission !== undefined, `${name} must receive owned permission`)
    assert.equal(entry.temperature, 1, `${name} must receive forced temperature 1.0`)
    assert.equal(entry.model, `host/${name}`, 'model stays untouched but is never routing truth')
  }
})
test('WHAT[capability-enforcement-011] MACFG_applyOwnedFields_skips_null_config_and_projects_missing_catalog_agents', () => {
  const rejected = configure(null)
  assert.equal(rejected.ok, false)

  const cfg = fullConfig()
  delete cfg.agent.blogger
  const result = configure(cfg)
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  const projected = cfg.agent.blogger
  assert.equal(projected.mode, 'primary')
  assert.equal(projected.hidden, true)
  assert.equal(projected.permission['*'], 'deny')
  assert.equal('model' in projected, false, 'projection never invents a model binding')
})
test('WHAT[capability-enforcement-011] MACFG_applyOwnedFields_honors_chat_max_retries_env', () => {
  const cfg = fullConfig()
  const result = configure(cfg)
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  assert.equal(cfg.experimental.chatMaxRetries, 0)
})
test('WHAT[capability-enforcement-011] MACFG_configureFromHostConfig_returns_role_inventory_without_model_authority', () => {
  const cfg = fullConfig()
  cfg.agent.manager.model = 'provider/shared'
  const result = configure(cfg)
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  assert.equal(cfg.compaction.auto, false)
  assert.equal(cfg.agent.manager.model, 'provider/shared')
})
test('WHAT[capability-enforcement-011] MACFG_configureFromHostConfig_projects_missing_catalog_without_model_authority', () => {
  const cfg = {}
  const result = configure(cfg)
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  assert.equal(result.bindingNames.length, 6)
  for (const name of NAMES) {
    const entry = cfg.agent[name]
    assert.ok(entry.mode !== undefined, `${name} must be projected`)
    assert.ok(entry.permission !== undefined, `${name} must receive owned permission`)
    assert.equal('model' in entry, false, `${name} must not receive a model binding`)
  }
  assert.equal(cfg.agent.blogger.hidden, true)
  assert.equal(cfg.agent.bookkeeper.hidden, true)
  assert.equal(cfg.compaction.auto, false)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { withPlugin } = await import("../../verification-system/tests/support/plugin-fixture.mjs");

const TOOL_NAMES = [
  'fork', 'resume', 'commission', 'join', 'horizon', 'fission',
  'read', 'write', 'edit', 'glob', 'grep', 'mv', 'rm',
  'bash-honeypot', 'assume', 'todowrite',
  'defer',  'publish',
  'run', 'open-terminal', 'send-terminal', 'read-terminal', 'signal-terminal',
  'review', 'chronicle', 'fetch', 'suicide',
]

const ROLE_NAMES = ['orchestrator', 'manager', 'engineer', 'devops', 'blogger']

const COGNITIVE_TOOLS = ['defer',  'publish']

const ALLOWED = {
  orchestrator: ['commission', 'join', 'horizon', 'assume', 'todowrite', ...COGNITIVE_TOOLS],
  manager: ['fork', 'resume', 'join', 'horizon', 'review', 'suicide', 'assume', 'todowrite', ...COGNITIVE_TOOLS],
  engineer: ['fission', 'read', 'write', 'edit', 'glob', 'grep', 'fetch', 'mv', 'rm', 'bash-honeypot', 'assume', 'todowrite', ...COGNITIVE_TOOLS],
  devops: [
    'join', 'horizon', 'read', 'write', 'edit', 'glob', 'grep', 'mv', 'rm', 'run',
    'open-terminal', 'send-terminal', 'read-terminal', 'signal-terminal',
    'assume', 'todowrite', ...COGNITIVE_TOOLS,
  ],
  blogger: ['chronicle'],
}

const fullConfig = () => ({
  agent: Object.fromEntries(
    ROLE_NAMES.map((role) => [role, {}]),
  ),
})

integrationTest('WHAT[capability-enforcement-011] MANAGER_config_projects_owned_permissions_with_default_deny', async () => {
  await withPlugin(async (hooks) => {
    const config = fullConfig()
    hooks.config(config)
    assert.equal(config.compaction.auto, false)
    for (const role of ROLE_NAMES) {
      const permission = config.agent[role].permission
      for (const toolName of TOOL_NAMES) {
        const expected = ALLOWED[role].includes(toolName) ? 'allow' : 'deny'
        const key = toolName
        assert.equal(permission[key], expected, `${role}.${key}`)
      }
      assert.equal(permission.external_directory, 'allow', `${role}.external_directory`)
      assert.equal(config.agent[role].model, undefined)
    }
  })
})
}
