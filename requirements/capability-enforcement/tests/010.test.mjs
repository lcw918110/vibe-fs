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
    'fork',
    'resume',
    'commission',
    'join',
    'horizon',
    'fission',
    'review',
    'fetch',
    'deliver',
    'skill',
    'todowrite-for-manager',
    'todowrite-for-orchestrator',
    'todowrite-for-engineer',
    'todowrite-for-devops',
  ]
  return tools.filter((tool) => evaluate(rules, tool, '*').action === 'allow')
}
const HOST_UTILITY_ALLOW = ['skill']
const COGNITIVE_UTILITY_ALLOW = ['assume']
const hostUtilityAllowFor = (_role) => HOST_UTILITY_ALLOW
const cognitiveUtilityAllowFor = (_role) => COGNITIVE_UTILITY_ALLOW
// Two distinct faces, kept apart:
//   面 A — 宿主注册的工具面（宿主注册表 / `hooks.tool`）：`fork` / `resume` /
//   `commission` / `devops` 四个名字都不再注册。
//   面 B — 宿主权限目录面（`StaticTools.knownToolNames` 及其派生的权限矩阵）：
//   `fork` / `resume` / `commission` 仍留在目录里（ToolSpec 构造器保留，委派线
//   协议仍在），只有 `devops` 离开了权限目录。
const ROLE_ALLOW = {
  Manager: ['fork', 'resume', 'join', 'horizon', 'review', 'todowrite-for-manager'],
  Orchestrator: ['commission', 'join', 'horizon', 'todowrite-for-orchestrator'],
  Engineer: ['bash-honeypot', 'fetch', 'fission', 'todowrite-for-engineer'],
  DevOps: [
    'join',
    'horizon',
    'todowrite-for-devops',
  ],
}

test('WHAT[capability-enforcement-010] AGENT_002_gate_accepts_distinct_models_and_writes_owned_fields', () => {
  const config = buildConfig()
  const outcome = configureManagedAgents(config)
  assert.equal(outcome.ok, true, `gate must accept distinct models: ${outcome.error}`)

  // Every canonical managed agent got the owned mode + permission + prompt.
  for (const role of ROLES) {
    const entry = config.agent[agentName(role)]
    assert.equal(entry.mode, 'primary', `${agentName(role)} must be primary`)
    assert.ok(entry.permission && entry.permission['*'] === 'deny', `${agentName(role)} must deny by default`)
    assert.ok(typeof entry.prompt === 'string' && entry.prompt.length > 0, `${agentName(role)} must carry a prompt`)
  }
})
test('WHAT[capability-enforcement-010] AGENT_007_bash_stays_denied_even_when_the_gate_fails', () => {
  const config = buildConfig()
  config.agent.build = { model: 'some-model' }
  const outcome = configureManagedAgents(config)
  assert.equal(outcome.ok, false, 'legacy agent name must fail the gate')
  assert.match(outcome.error, /Legacy agent name 'build'/)

  for (const role of ROLES) {
    const name = agentName(role)
    const entry = config.agent[name]
    assert.equal(entry.mode, 'primary', `${name} mode must survive a gate error`)
    assert.ok(entry.permission && entry.permission['*'] === 'deny', `${name} must keep "*": deny after a gate error`)
    assert.deepEqual(
      allowList(config, name).sort(),
      [...ROLE_ALLOW[role], ...hostUtilityAllowFor(role), ...cognitiveUtilityAllowFor(role)].sort(),
      `${name} tool set must survive a gate error`,
    )
    assert.ok(!allowList(config, name).includes('bash'), `${name} must never allow bash`)
  }
})
test('WHAT[capability-enforcement-010] AGENT_007_validation_error_is_still_reported', () => {
  const config = buildConfig()
  config.agent.build = { model: 'some-model' }
  const outcome = validateManagedAgents(config)
  assert.equal(outcome.ok, false)
  assert.match(outcome.error, /Legacy agent name 'build'/)
})
test('WHAT[capability-enforcement-010] AGENT_004_legacy_agent_name_fails_validation', () => {
  const config = buildConfig()
  config.agent.build = { model: 'some-model' }
  const outcome = validateManagedAgents(config)
  assert.equal(outcome.ok, false)
  assert.match(outcome.error, /Legacy agent name 'build'/)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { bashHoneypotContract } = await import("../../../dist/OpenCode/Tools/ToolSurface.js");
const { acceptAuthorityRoot, withExecutablePlugin } = await import("../../verification-system/tests/support/plugin-fixture.mjs");


test('WHAT[capability-enforcement-010] BASHHONEY_spec_is_parameterless_and_named_bash_honeypot', () => {
  const contract = bashHoneypotContract()
  assert.equal(contract.name, 'bash-honeypot')
  assert.match(contract.description, /[Hh]oneypot/)
  assert.deepEqual(contract.argumentNames, [])
})
test('WHAT[capability-enforcement-010] BASHHONEY_execute_returns_hard_denial_and_runs_nothing', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    await acceptAuthorityRoot(runtime, 'ses-honey', 'engineer')
    const result = await hooks.tool['bash-honeypot'].execute({}, { sessionID: 'ses-honey', agent: 'engineer' })
    assert.doesNotMatch(result, /\berror\s*=/)
    assert.match(result, /DENIED/)
    assert.match(result, /unauthorized privilege-escalation|未经授权的提权/i)
    assert.match(result, /not permitted to execute bash|不允许执行 bash|不得执行 bash/i)
    assert.match(result, /No command ran|没有运行任何命令|未运行任何命令/i)
    assert.match(result, /DevOps/)
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { acceptAuthorityRoot, grantWorkOwned, withExecutablePlugin, withPlugin } = await import("../../verification-system/tests/support/plugin-fixture.mjs");


test('WHAT[capability-enforcement-010] FORK_delegation_verbs_are_no_longer_host_registered_tools', async () => {
  // Clean-break: the delegation verbs are programming-surface members now, so
  // the Host registry carries no such tool at all. The authority refusal they
  // used to raise belongs to the member call surface, not to a host tool.
  await withPlugin(async (hooks) => {
    for (const tool of ['fork', 'resume', 'commission', 'devops']) {
      assert.equal(hooks.tool[tool], undefined, `${tool} must not be a host-registered tool`)
    }
    // review is not a delegation verb: it is the one lifecycle entry (work-053).
    assert.equal(typeof hooks.tool.review?.execute, 'function', 'review is the host-registered lifecycle entry')
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { rolePredicate } = await import("../../../dist/OpenCode/Tools/ToolRegistrySurface.js");
const { permissions: rolePermissions, isAllowed: surfaceIsAllowed } = await import("../../../dist/Participant/Persona/OfficeCapabilitySurface.js");
const { allRoleLabels } = await import("../../../dist/Foundation/RolesSurface.js");


test('WHAT[capability-enforcement-010] inquiry_rolePredicate_denies_all_tools', () => {
  assert.equal(rolePredicate('inspect', 'inquiry'), false)
  assert.equal(rolePredicate('fission', 'inquiry'), false)
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
  'bookkeeper',
]
function fullConfig() {
  return { agent: Object.fromEntries(NAMES.map((name) => [name, {}])) }
}

test('WHAT[capability-enforcement-010] MACFG_validate_rejects_null_config_and_legacy_agent', () => {
  assert.match(errOf(validate(null)), /Host config object/)
  assert.match(errOf(validate({ agent: { build: {} } })), /build/)
})
test('WHAT[capability-enforcement-010] MACFG_validate_rejects_legacy_agent_present', () => {
  const cfg = fullConfig()
  cfg.agent.build = {}
  assert.match(errOf(validate(cfg)), /build/)
  delete cfg.agent.build
  cfg.agent.coder = {}
  assert.match(errOf(validate(cfg)), /coder/)
})
test('WHAT[capability-enforcement-010] MACFG_configureManager_legacy_agent_is_fatal_after_owned_fields_land', () => {
  const cfg = fullConfig()
  cfg.agent.build = {}

  assert.throws(() => configureManager(cfg), /Legacy agent name 'build'/)
  assert.equal(cfg.compaction.auto, true)
  assert.equal(cfg.agent.manager.permission['*'], 'deny')
  assert.equal(cfg.agent.orchestrator.permission['*'], 'deny')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { withExecutablePlugin, acceptAuthorityRoot } = await import("../../verification-system/tests/support/plugin-fixture.mjs");

integrationTest('WHAT[capability-enforcement-010] AGENT_023_engineer_receives_hard_denial_and_no_shell', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    await acceptAuthorityRoot(runtime, 'engineer-bash-honey', 'engineer')
    assert.ok(hooks.tool['bash-honeypot'], 'bash-honeypot must be registered')

    const result = await hooks.tool['bash-honeypot'].execute(
      {},
      { sessionID: 'engineer-bash-honey', agent: 'engineer' },
    )

    assert.match(result, /DENIED/)
    assert.match(result, /unauthorized privilege-escalation|提权/)
    assert.match(result, /No command ran|未运行任何命令|没有运行任何命令/)
  })
})
integrationTest('WHAT[capability-enforcement-010] AGENT_023_bash_honeypot_is_denied_for_non_engineer_roles', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    await acceptAuthorityRoot(runtime, 'manager-bash-honey', 'manager')
    const result = await hooks.tool['bash-honeypot'].execute(
      {},
      { sessionID: 'manager-bash-honey', agent: 'manager' },
    )
    assert.match(result, /not available to Manager|对 Manager 不可用/)
  })
})
integrationTest('WHAT[capability-enforcement-010] AGENT_023_bash_honeypot_is_denied_when_the_role_is_unresolved', async () => {
  await withExecutablePlugin(async (hooks) => {
    const result = await hooks.tool['bash-honeypot'].execute(
      {},
      { sessionID: 'unresolved-bash-honey', agent: 'engineer' },
    )
    assert.match(result, /authority is established|权威确立/)
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { markerSource, markerToolName } = await import("../../../dist/OpenCode/Host/PairProgrammingThoughtSurface.js");
const { permissions } = await import("../../../dist/Participant/Persona/OfficeCapabilitySurface.js");
const { acceptAuthorityRoot, grantWorkOwned, withExecutablePlugin, withPlugin } = await import("../../verification-system/tests/support/plugin-fixture.mjs");

const TOOL_NAMES = [
  'fork', 'resume', 'commission', 'join', 'horizon', 'fission',
  'bash-honeypot', 'assume',
  'review', 'fetch', 'deliver',
  'todowrite-for-engineer', 'todowrite-for-devops', 'todowrite-for-manager', 'todowrite-for-orchestrator',
]
const PLUGIN_TOOL_NAMES = [
  'todowrite-for-plan',
  'join', 'horizon', 'fission', 'deliver', 'resetScope', 'negotiate', 'askPreviousScope',
  'bash-honeypot', 'assume',
  'todowrite-for-engineer', 'todowrite-for-devops', 'todowrite-for-manager', 'todowrite-for-orchestrator',
]
// Clean-break: the delegation verbs are programming-surface members, so the
// Host registry carries no such tool.
const RETIRED_HOST_DELEGATION_TOOLS = ['fork', 'resume', 'commission', 'devops']
const HOST_OWNED_TOOL_NAMES = ['skill',
]
const ROLE_NAMES = ['orchestrator', 'manager', 'engineer', 'devops']
const COGNITIVE_TOOLS = []
const ALLOWED = {
  orchestrator: ['commission', 'join', 'horizon', 'todowrite-for-orchestrator', 'assume', ...COGNITIVE_TOOLS],
  manager: ['fork', 'resume', 'join', 'horizon', 'review', 'todowrite-for-manager', 'assume', ...COGNITIVE_TOOLS],
  engineer: ['fission', 'fetch', 'bash-honeypot', 'todowrite-for-engineer', 'assume', ...COGNITIVE_TOOLS],
  devops: [
    'join', 'horizon', 'todowrite-for-devops',
    'assume', ...COGNITIVE_TOOLS,
  ],
}
const withSession = (messages, sessionID = 'ses-capability-manager') =>
  messages.map((message, index) => ({
    ...message,
    info: {
      ...(message.info ?? {}),
      id: message.info?.id ?? `msg-${index}`,
      role: message.info?.role ?? message.role ?? 'user',
      sessionID,
    },
  }))
const fullConfig = () => ({
  agent: Object.fromEntries(
    ROLE_NAMES.map((role) => [role, {}]),
  ),
})

integrationTest('WHAT[capability-enforcement-010] MANAGER_plugin_registers_only_plugin_owned_capability_tools', async () => {
  await withPlugin(async (hooks) => {
    assert.equal(hooks.tool.chronicle, undefined, 'chronicle is not a registered tool')
    assert.equal(hooks.tool['todowrite-for-predictor'], undefined, 'todowrite-for-predictor is not a registered tool')
    assert.equal(new Set(PLUGIN_TOOL_NAMES).size, PLUGIN_TOOL_NAMES.length, 'shared tools have one registration')
    assert.deepEqual(Object.keys(hooks.tool).sort(), [...PLUGIN_TOOL_NAMES].sort())
    for (const toolName of PLUGIN_TOOL_NAMES) {
      assert.equal(typeof hooks.tool[toolName]?.execute, 'function', `${toolName} is registered`)
    }
    for (const toolName of HOST_OWNED_TOOL_NAMES) {
      assert.equal(hooks.tool[toolName], undefined, `${toolName} stays Host-owned`)
    }
    for (const toolName of RETIRED_HOST_DELEGATION_TOOLS) {
      assert.equal(hooks.tool[toolName], undefined, `${toolName} is no longer a host tool`)
    }
    const forbidden = ['read', 'write', 'edit', 'glob', 'grep', 'mv', 'rm', 'auto-injected', '-', 'tool', 'bash', 'shell', 'inspect', 'establish-behavior', 'repair-behavior', 'query-shell', 'js-browser', 'js-coder', 'js-inspector']
    for (const toolName of forbidden) assert.equal(hooks.tool[toolName], undefined, `${toolName} must not be an export`)
  })
})
integrationTest('WHAT[capability-enforcement-010] MANAGER_host_schemas_are_present_for_every_declared_argument', async () => {
  await withPlugin(async (hooks) => {
    const expected = {
      'bash-honeypot': [],
      assume: ['assumption'],
    }
    for (const toolName in expected) {
      for (const argument of expected[toolName]) {
        assert.equal(typeof hooks.tool[toolName].args[argument]?.safeParse, 'function', `${toolName}.${argument}`)
      }
    }
  })
})
integrationTest('WHAT[capability-enforcement-010] MANAGER_delegation_verbs_carry_no_host_schema_anymore', async () => {
  // The `calling` enum and the free byname now belong to the programming
  // surface member payload (`this.fork` / `this.commission`), not to a host
  // tool schema: the Host registers no delegation tool to describe.
  await withPlugin(async (hooks) => {
    for (const toolName of RETIRED_HOST_DELEGATION_TOOLS) {
      assert.equal(hooks.tool[toolName], undefined, `${toolName} must not be a host tool`)
    }
  })
})
integrationTest('WHAT[capability-enforcement-010] MANAGER_legacy_agent_configuration_is_rejected_after_owned_projection', async () => {
  await withPlugin(async (hooks) => {
    const config = fullConfig()
    config.agent.build = {}
    assert.throws(() => hooks.config(config), /Legacy agent name 'build'/)
    assert.equal(config.compaction.auto, true)
    assert.equal(config.agent['manager'].permission['*'], 'deny')
  })
})
}
