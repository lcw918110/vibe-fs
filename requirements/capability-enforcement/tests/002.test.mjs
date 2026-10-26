import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { permissions } = await import("../../../dist/Participant/Persona/OfficeCapabilitySurface.js");
const { configure: configureManagedAgents, installDefaultResources, validate: validateManagedAgents } = await import("../../../dist/OpenCode/Host/ManagedAgentConfigSurface.js");
const { reviewToolPermissions } = await import("../../../dist/OpenCode/Tools/ToolSurface.js");
const { generateRole } = await import("../../../dist/Repository/Programming/Js/GeneratorSurface.js");

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
  ]
  const candidateTools = [...new Set([...staticTools, ...permissionToolKeys])]
  return candidateTools.filter((tool) => evaluate(rules, tool, '*').action === 'allow')
}
const HOST_UTILITY_ALLOW = ['skill']
const COGNITIVE_UTILITY_ALLOW = [
  'assume',
]
const hostUtilityAllowFor = (_role) => HOST_UTILITY_ALLOW
const cognitiveUtilityAllowFor = (_role) => COGNITIVE_UTILITY_ALLOW
// Two distinct faces, kept apart:
//   面 A — 宿主注册的工具面（宿主注册表 / `hooks.tool`）：`fork` / `resume` /
//   `commission` / `devops` 四个名字都不再注册，宿主不再暴露它们。
//   面 B — 宿主权限目录面（`StaticTools.knownToolNames` 及其派生的权限矩阵）：
//   `fork` / `resume` / `commission` 仍留在目录里（它们的 ToolSpec 构造器保留，
//   委派线协议仍在），因此权限对象仍为它们出条目：Manager 持有 fork / resume，
//   Orchestrator 持有 commission。只有 `devops` 离开了权限目录。
const ROLE_ALLOW = {
  Manager: [
    'negotiate',
    'resetScope',
    'askPreviousScope',
    'fork',
    'resume',
    'join',
    'horizon',
    'review',
    'todowrite-for-manager',
  ],
  Orchestrator: ['negotiate', 'resetScope', 'askPreviousScope', 'commission', 'join', 'horizon', 'todowrite-for-orchestrator'],
  Engineer: ['negotiate', 'resetScope', 'askPreviousScope', 'bash-honeypot', 'fetch', 'fission', 'todowrite-for-engineer'],
  DevOps: [
    'negotiate',
    'resetScope',
    'askPreviousScope',
    'join',
    'horizon',
    'todowrite-for-devops',
  ],
}

test('WHAT[capability-enforcement-002] AGENT_006_role_tool_matrix_reaches_the_host_schema', () => {
  const config = buildConfig()
  const outcome = configureManagedAgents(config)
  assert.equal(outcome.ok, true, outcome.error)

  for (const role of ROLES) {
    const name = agentName(role)
    const allowed = allowList(config, name).sort()
    // 面 A / 面 B 在 `devops` 上重合：它的 ToolSpec 已整体删除，宿主注册表与
    // 权限目录都不再持有这个名字，所以任何角色都不得放行它。
    assert.ok(!allowed.includes('devops'), `${name} must not expose devops as a host tool`)
    assert.deepEqual(
      allowed,
      [...ROLE_ALLOW[role], ...hostUtilityAllowFor(role), ...cognitiveUtilityAllowFor(role)].sort(),
      `${name} allow set must equal AGENT-006 matrix + non-authority utilities`,
    )
  }
})
test('WHAT[capability-enforcement-002] office_capability_permissions_agree_with_the_host_schema_matrix', () => {
  // Edit remains an office permission but projects no separate JS capability
  // (string replacement uses Read + Write; Grep projects rg).
  // Finality names no host tool any more: the review entry closes cleanup
  // (OfficeMatrix: 收尾由 review 结案), so Finality only gates that close.
  const DIRECT_PERMISSION_ONLY = new Set(['Edit', 'Ask', 'JsPlan', 'Finality'])
  const permissionOf = (toolName) =>
    ({
      resetScope: 'ResetScope',
      askPreviousScope: 'ResetScope',
      fork: 'Fork',
      resume: 'Resume',
      commission: 'Fork',
      join: 'Join',
      horizon: 'Horizon',
      fission: 'Fission',
      'bash-honeypot': 'BashHoneypot',
      review: 'ReviewAssessment',
      fetch: 'Fetch',
    })[toolName]

  const permissionsForTool = (toolName) => {
    // 评审专用工具经 owner 目录（ToolSurface.reviewToolPermissions）映射为领域权限，
    // 无评审契约的工具名称没有权限映射。
    const reviewPermissions = reviewToolPermissions(toolName)
    // The generated surface's members are part of what the Host schema allows
    // for that tool, so a member whose only carrier is the surface (e.g.
    // `advisor`) must be counted beside any review-contract permissions.
    // The delegation capability reaches the surface as a member (`fork`,
    // `resume`, `commission`, `devops`), never as a host tool; the office
    // vocabulary behind a member is the permission label it was granted by.
    const OFFICE_LABEL_OF_SURFACE_CAPABILITY = {
      Fork: 'Fork',
      Resume: 'Resume',
      Commission: 'Fork',
      DevOps: 'DevOpsConsultation',
      Review: 'ReviewAssessment',
    }
    const officeLabel = (capability) =>
      OFFICE_LABEL_OF_SURFACE_CAPABILITY[capability] ?? capability
    const generated = toolName.startsWith('todowrite-for-')
      ? generateRole(toolName.slice('todowrite-for-'.length), 'en')
      : null

    if (generated) return [...new Set([...reviewPermissions, ...generated.capabilities.map(officeLabel)])]
    if (reviewPermissions.length > 0) return reviewPermissions
    const single = permissionOf(toolName)
    return single ? [single] : []
  }

  for (const role of ROLES) {
    const fromRoles = permissions(role.toLowerCase()).filter((p) => !DIRECT_PERMISSION_ONLY.has(p))
    const config = buildConfig()
    configureManagedAgents(config)
    const nonDomainUtilities = [...hostUtilityAllowFor(role), ...cognitiveUtilityAllowFor(role)]
    const fromSchema = [...new Set(allowList(config, agentName(role)).filter((tool) => !nonDomainUtilities.includes(tool)).flatMap(permissionsForTool))].sort()
    assert.deepEqual(fromSchema, fromRoles, `${role}: domain permissions must equal the Host schema allow list`)
  }

  for (const retiredRole of ['blogger', 'predictor']) {
    assert.deepEqual(permissions(retiredRole), [], `retired ${retiredRole} permissions must fail closed`)
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
    ['advisor', 'devops', 'engineer', 'manager', 'orchestrator', 'plan']
      .sort(),
  )
})
test('WHAT[capability-enforcement-002] P7_SURFACE_public_internal_partition_and_managed_agent_name_are_js_native', () => {
  assertJsData(allPublicRoleLabels, 'allPublicRoleLabels')
  assertJsData(allInternalRoleLabels, 'allInternalRoleLabels')
  assert.deepEqual(allPublicRoleLabels, ['devops', 'engineer', 'manager', 'orchestrator', 'plan'])
  assert.deepEqual(allInternalRoleLabels, ['advisor'])
  assert.equal(allPublicRoleLabels.length + allInternalRoleLabels.length, allRoleLabels.length)
  assert.equal(managedAgentName('deep', 'blogger'), '', 'retired blogger fails closed to empty name')
  assert.equal(managedAgentName('engineer', 'engineer'), 'engineer')
  assert.equal(managedAgentName('fast', 'not-a-role'), '', 'unknown role fails closed to empty name')
  assert.equal(managedAgentName('not-a-tier', 'engineer'), 'engineer', 'canonical role resolves without a tier')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { rolePredicate, admissionAuthority } = await import("../../../dist/OpenCode/Tools/ToolRegistrySurface.js");
const { generateRole } = await import("../../../dist/Repository/Programming/Js/GeneratorSurface.js");


test('WHAT[capability-enforcement-002] TOOLSPEC_delegation_verbs_are_no_longer_host_tools', async () => {
  const { toolSpecNames } = await import("../../../dist/OpenCode/Tools/ToolSurface.js");
  const hostToolNames = toolSpecNames()
  // Clean-break: the delegation verbs live exclusively as programming-surface
  // members, so the Host registers no `fork` / `resume` / `commission` / `devops` tool.
  for (const tool of ['fork', 'resume', 'commission', 'devops']) {
    assert.equal(hostToolNames.includes(tool), false, `${tool} must not be a registered host tool`)
  }
  // devops is completely retired and admits no role
  for (const role of ['manager', 'orchestrator', 'engineer', 'devops']) {
    assert.equal(rolePredicate('devops', role), false, `devops must not be admitted for ${role}`)
  }
  for (const role of ['orchestrator', 'engineer', 'devops']) {
    assert.equal(rolePredicate('fork', role), false, `fork must not be admitted for ${role}`)
    assert.equal(rolePredicate('resume', role), false, `resume must not be admitted for ${role}`)
  }
  for (const role of ['manager', 'engineer', 'devops']) {
    assert.equal(rolePredicate('commission', role), false, `commission must not be admitted for ${role}`)
  }
})
test('WHAT[capability-enforcement-002] TOOLSPEC_delegation_member_names_stay_on_the_programming_surface', () => {
  const memberNames = (role) => generateRole(role, 'en').members.map((member) => member.memberName)
  assert.ok(memberNames('manager').includes('fork'), 'manager keeps the fork member')
  assert.ok(memberNames('manager').includes('resume'), 'manager keeps the resume member')
  assert.ok(memberNames('orchestrator').includes('commission'), 'orchestrator keeps the commission member')
  assert.ok(memberNames('plan').includes('devops'), 'plan keeps the devops member')
})
test('WHAT[capability-enforcement-002] TOOLSPEC_lifecycle_tools_have_owner_defined_admission', () => {

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

  // todowrite-for-engineer: Engineer only (carries cat/ls/stat/where/whereAll/mkdir/write/mv/rm/fission)
  assert.equal(rolePredicate('todowrite-for-engineer', 'engineer'), true)
  assert.equal(rolePredicate('todowrite-for-engineer', 'devops'), false)
  assert.equal(rolePredicate('todowrite-for-engineer', 'manager'), false)

  // todowrite-for-devops: DevOps only (carries cat/ls/stat/where/whereAll/mkdir/write/mv/rm + this.call via Exec; direct file/exec/pty tools are retired)
  assert.equal(rolePredicate('todowrite-for-devops', 'devops'), true)
  assert.equal(rolePredicate('todowrite-for-devops', 'engineer'), false)
  assert.equal(rolePredicate('todowrite-for-devops', 'manager'), false)
  for (const retired of ['read', 'write', 'edit', 'glob', 'grep', 'mv', 'rm', 'run', 'open-terminal', 'send-terminal', 'read-terminal', 'signal-terminal']) {
    assert.equal(rolePredicate(retired, 'engineer'), false, `retired ${retired} must be denied for engineer`)
    assert.equal(rolePredicate(retired, 'devops'), false, `retired ${retired} must be denied`)
  }
})
test('WHAT[capability-enforcement-002] TOOLSPEC_plugin_owned_cognitive_utility_tools_admission', () => {
  // Native todowrite has no plugin ToolSpec; its role boundary is asserted from
  // the Host permission matrix in capability-enforcement-006.
  for (const tool of ['assume']) {
    assert.equal(rolePredicate(tool, 'engineer'), true, `${tool} should be allowed for engineer`)
    assert.equal(rolePredicate(tool, 'devops'), true, `${tool} should be allowed for devops`)
    assert.equal(rolePredicate(tool, 'manager'), true, `${tool} should be allowed for manager`)
    assert.equal(rolePredicate(tool, 'blogger'), false, `${tool} should fail closed for retired blogger`)
    assert.equal(rolePredicate(tool, 'predictor'), false, `${tool} should fail closed for retired predictor`)
  }
})
test('WHAT[capability-enforcement-002] TOOLSPEC_negotiation_and_reset_admission_excludes_the_tool_less_advisor_seat', () => {
  // The dedicated Advisor seat is tool-less (`tools:false`): its Host
  // AgentConfig, Provider tool map and execution admission all refuse every
  // tool, including the protocol-only `negotiate` and `resetScope`.
  assert.equal(admissionAuthority('resetScope'), 'office', 'resetScope must have an office-role admission')
  for (const role of ['manager', 'orchestrator', 'engineer', 'devops', 'plan']) {
    assert.equal(rolePredicate('resetScope', role), true, `resetScope must be admitted for ${role}`)
  }
  assert.equal(rolePredicate('resetScope', 'advisor'), false, 'the tool-less Advisor seat must not be admitted to resetScope')
  assert.equal(admissionAuthority('negotiate'), 'office')
  for (const role of ['manager', 'orchestrator', 'engineer', 'devops', 'plan']) {
    assert.equal(rolePredicate('negotiate', role), true, `the protocol-only negotiation tool is admitted for ${role}`)
  }
  assert.equal(rolePredicate('negotiate', 'advisor'), false, 'the tool-less Advisor seat must not be admitted to negotiate')
  assert.equal(rolePredicate('negotiate', 'unknown'), false, 'unknown callers cannot negotiate')
  assert.equal(rolePredicate('resetScope', 'unknown'), false, 'resetScope must fail closed for unknown role')
})
}
