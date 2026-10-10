import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { permissions } = await import("../../../dist/Participant/Persona/OfficeCapabilitySurface.js");
const { configure: configureManagedAgents, installDefaultResources, validate: validateManagedAgents } = await import("../../../dist/OpenCode/Host/ManagedAgentConfigSurface.js");
const { reviewToolPermissions } = await import("../../../dist/OpenCode/Tools/ToolSurface.js");

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
  // permission 的键是 Host schema 投影的真实来源，静态表覆盖 hostDefaults 提供但 permission 未列的项（如 bash/skill 等宿主默认项）；
  // 只读键会漏掉宿主默认，只留静态表会漏新增工具，并集才是"全部可能被 schema 放行的候选"这一语义。
  // 注意：'*' 与 'external_directory' 属于 Host 配置的兜底通配与路径边界元权限（CAPABILITY-ENFORCEMENT-011），不作为业务工具放行候选。
  const permissionToolKeys = Object.keys(config.agent[name]?.permission ?? {}).filter(
    (key) => key !== '*' && key !== 'external_directory',
  )
  const staticTools = [
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
  const candidateTools = [...new Set([...staticTools, ...permissionToolKeys])]
  return candidateTools.filter((tool) => evaluate(rules, tool, '*').action === 'allow')
}
const HOST_UTILITY_ALLOW = ['skill']
const COGNITIVE_UTILITY_ALLOW = [
  'assume',
  'todowrite',
  'defer',
  
  'publish',
]
const hostUtilityAllowFor = (role) => (role === 'Blogger' ? [] : HOST_UTILITY_ALLOW)
const cognitiveUtilityAllowFor = (role) => (role === 'Blogger' ? [] : COGNITIVE_UTILITY_ALLOW)
const ROLE_ALLOW = {
  Manager: [
    'fork',
    'resume',
    'join',
    'horizon',
    'suicide',
    'review',
    'js-manager',
  ],
  Orchestrator: ['commission', 'join', 'horizon'],
  Engineer: ['read', 'write', 'edit', 'glob', 'grep', 'mv', 'rm', 'bash-honeypot', 'fetch', 'fission', 'js-engineer'],
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
    'js-devops',
  ],
  Blogger: ['chronicle'],
}

test('WHAT[capability-enforcement-002] AGENT_006_role_tool_matrix_reaches_the_host_schema', () => {
  const config = buildConfig()
  const outcome = configureManagedAgents(config)
  assert.equal(outcome.ok, true, outcome.error)

  for (const role of ROLES) {
    const name = agentName(role)
    const allowed = allowList(config, name).sort()
    assert.deepEqual(
      allowed,
      [...ROLE_ALLOW[role], ...hostUtilityAllowFor(role), ...cognitiveUtilityAllowFor(role)].sort(),
      `${name} allow set must equal AGENT-006 matrix + non-authority utilities`,
    )
  }
})
test('WHAT[capability-enforcement-002] office_capability_permissions_agree_with_the_host_schema_matrix', () => {
  const permissionOf = (toolName) =>
    ({
      fork: 'Fork',
      resume: 'Resume',
      commission: 'Fork',
      'open-terminal': 'Pty',
      'send-terminal': 'Pty',
      'read-terminal': 'Pty',
      'signal-terminal': 'Pty',
      join: 'Join',
      horizon: 'Horizon',
      fission: 'Fission',
      read: 'Read',
      write: 'Write',
      edit: 'Edit',
      glob: 'Glob',
      grep: 'Grep',
      mv: 'Move',
      rm: 'Remove',
      'bash-honeypot': 'BashHoneypot',
      run: 'Exec',
      review: 'ReviewAssessment',
      chronicle: 'Chronicle',
      fetch: 'Fetch',
      suicide: 'Finality',
    })[toolName]

  const permissionsForTool = (toolName) => {
    // 评审专用工具经 owner 目录（ToolSurface.reviewToolPermissions）映射为领域权限，
    // 无评审契约的工具名称没有权限映射。
    const reviewPermissions = reviewToolPermissions(toolName)
    if (reviewPermissions.length > 0) return reviewPermissions
    const single = permissionOf(toolName)
    return single ? [single] : []
  }

  for (const role of ROLES) {
    const fromRoles = permissions(role.toLowerCase())
    const config = buildConfig()
    configureManagedAgents(config)
    const nonDomainUtilities = [...hostUtilityAllowFor(role), ...cognitiveUtilityAllowFor(role)]
    const fromSchema = [...new Set(allowList(config, agentName(role)).filter((tool) => !nonDomainUtilities.includes(tool)).flatMap(permissionsForTool))].sort()
    assert.deepEqual(fromSchema, fromRoles, `${role}: domain permissions must equal the Host schema allow list`)
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { assertJsData } = await import("../../verification-system/tests/support/js-contract.mjs");

const { allRoleLabels, allPublicRoleLabels, allInternalRoleLabels } =
  await import('../../../dist/Foundation/RolesSurface.js')
const { nameOf: managedAgentName } = await import('../../../dist/Participant/Persona/Surface.js')

test('WHAT[capability-enforcement-002] P7_SURFACE_role_labels_are_js_native_strings', () => {
  assertJsData(allRoleLabels, 'allRoleLabels')
  assert.equal(allRoleLabels.length, 6, 'exactly six canonical roles')
  assert.deepEqual(
    allRoleLabels,
    ['blogger', 'devops', 'engineer', 'manager', 'orchestrator', 'plan']
      .sort(),
  )
})
test('WHAT[capability-enforcement-002] P7_SURFACE_public_internal_partition_and_managed_agent_name_are_js_native', () => {
  assertJsData(allPublicRoleLabels, 'allPublicRoleLabels')
  assertJsData(allInternalRoleLabels, 'allInternalRoleLabels')
  assert.deepEqual(allPublicRoleLabels, ['devops', 'engineer', 'manager', 'orchestrator', 'plan'])
  assert.deepEqual(allInternalRoleLabels, ['blogger'])
  assert.equal(allPublicRoleLabels.length + allInternalRoleLabels.length, allRoleLabels.length)
  assert.equal(managedAgentName('deep', 'blogger'), 'blogger')
  assert.equal(managedAgentName('engineer', 'engineer'), 'engineer')
  assert.equal(managedAgentName('fast', 'not-a-role'), '', 'unknown role fails closed to empty name')
  assert.equal(managedAgentName('not-a-tier', 'engineer'), 'engineer', 'canonical role resolves without a tier')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { rolePredicate } = await import("../../../dist/OpenCode/Tools/ToolRegistrySurface.js");


test('WHAT[capability-enforcement-002] TOOLSPEC_delegation_tools_have_owner_defined_admission', () => {
  // fork & resume: Manager only
  assert.equal(rolePredicate('fork', 'manager'), true)
  assert.equal(rolePredicate('fork', 'engineer'), false)
  assert.equal(rolePredicate('fork', 'orchestrator'), false)
  assert.equal(rolePredicate('fork', 'devops'), false)
  assert.equal(rolePredicate('resume', 'manager'), true)
  assert.equal(rolePredicate('resume', 'engineer'), false)
  assert.equal(rolePredicate('resume', 'orchestrator'), false)
  assert.equal(rolePredicate('resume', 'devops'), false)

  // commission: Orchestrator only
  assert.equal(rolePredicate('commission', 'orchestrator'), true)
  assert.equal(rolePredicate('commission', 'manager'), false)
  assert.equal(rolePredicate('commission', 'engineer'), false)

  // join & horizon: Join/Horizon permissions
  assert.equal(rolePredicate('join', 'manager'), true)
  assert.equal(rolePredicate('join', 'orchestrator'), true)
  assert.equal(rolePredicate('join', 'devops'), true)
  assert.equal(rolePredicate('join', 'engineer'), false)

  assert.equal(rolePredicate('horizon', 'manager'), true)
  assert.equal(rolePredicate('horizon', 'orchestrator'), true)
  assert.equal(rolePredicate('horizon', 'devops'), true)
  assert.equal(rolePredicate('horizon', 'engineer'), false)
})
test('WHAT[capability-enforcement-002] TOOLSPEC_engineer_and_devops_tools_have_owner_defined_admission', () => {
  // bash-honeypot: Engineer only
  assert.equal(rolePredicate('bash-honeypot', 'engineer'), true)
  assert.equal(rolePredicate('bash-honeypot', 'devops'), false)
  assert.equal(rolePredicate('bash-honeypot', 'manager'), false)

  // mv & rm: Engineer and DevOps (Move / Remove permission)
  assert.equal(rolePredicate('mv', 'engineer'), true)
  assert.equal(rolePredicate('mv', 'devops'), true)
  assert.equal(rolePredicate('mv', 'manager'), false)
  assert.equal(rolePredicate('rm', 'engineer'), true)
  assert.equal(rolePredicate('rm', 'devops'), true)
  assert.equal(rolePredicate('rm', 'manager'), false)

  // pty tools: DevOps only
  assert.equal(rolePredicate('open-terminal', 'devops'), true)
  assert.equal(rolePredicate('open-terminal', 'engineer'), false)
  assert.equal(rolePredicate('send-terminal', 'devops'), true)
  assert.equal(rolePredicate('read-terminal', 'devops'), true)
  assert.equal(rolePredicate('signal-terminal', 'devops'), true)

  // run: DevOps only
  assert.equal(rolePredicate('run', 'devops'), true)
  assert.equal(rolePredicate('run', 'engineer'), false)
  assert.equal(rolePredicate('run', 'manager'), false)
})
test('WHAT[capability-enforcement-002] TOOLSPEC_plugin_owned_cognitive_utility_tools_admission', () => {
  // Native todowrite has no plugin ToolSpec; its role boundary is asserted from
  // the Host permission matrix in capability-enforcement-006.
  for (const tool of ['assume', 'defer',  'publish']) {
    assert.equal(rolePredicate(tool, 'engineer'), true, `${tool} should be allowed for engineer`)
    assert.equal(rolePredicate(tool, 'devops'), true, `${tool} should be allowed for devops`)
    assert.equal(rolePredicate(tool, 'manager'), true, `${tool} should be allowed for manager`)
    assert.equal(rolePredicate(tool, 'blogger'), false, `${tool} should be denied for blogger`)
  }
})
}
