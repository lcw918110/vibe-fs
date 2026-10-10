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

test('WHAT[capability-enforcement-006] HOST_skill_is_a_host_utility_for_interactive_roles_only', () => {
  const config = buildConfig()
  assert.equal(configureManagedAgents(config).ok, true)
  for (const role of ROLES) {
    const expected = role === 'Blogger' ? 'deny' : 'allow'
    assert.equal(
      evaluate(mergedRules(config, agentName(role)), 'skill', '*').action,
      expected,
      `${agentName(role)} skill permission`,
    )
  }
})
test('WHAT[capability-enforcement-006] ASSUME_is_a_non_authority_utility_for_interactive_roles_only', () => {
  const config = buildConfig()
  assert.equal(configureManagedAgents(config).ok, true)
  for (const role of ROLES) {
    const expected = role === 'Blogger' ? 'deny' : 'allow'
    assert.equal(
      evaluate(mergedRules(config, agentName(role)), 'assume', '*').action,
      expected,
      `${agentName(role)} assume permission`,
    )
  }
})
test('WHAT[capability-enforcement-006] TODOWRITE_has_the_same_non_authority_role_boundary_as_assume', () => {
  const config = buildConfig()
  assert.equal(configureManagedAgents(config).ok, true)
  for (const role of ROLES) {
    const expected = role === 'Blogger' ? 'deny' : 'allow'
    assert.equal(
      evaluate(mergedRules(config, agentName(role)), 'todowrite', '*').action,
      expected,
      `${agentName(role)} todowrite permission`,
    )
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { markerSource, markerToolName } = await import("../../../dist/OpenCode/Host/PairProgrammingThoughtSurface.js");
const { rolePredicate } = await import("../../../dist/OpenCode/Tools/ToolRegistrySurface.js");
const { withExecutablePlugin, withPlugin } = await import("../../verification-system/tests/support/plugin-fixture.mjs");

const withSession = (messages, sessionID = 'ses-auto-injected') =>
  messages.map((message, index) => ({
    ...message,
    info: {
      ...(message.info ?? {}),
      id: message.info?.id ?? `msg-${index}`,
      role: message.info?.role ?? message.role ?? 'user',
      sessionID,
    },
  }))
const admitManagedRoot = async (hooks, runtime, sessionID = 'ses-auto-injected') => {
  const output = {
    message: {
      id: `root-${sessionID}`,
      role: 'user',
      sessionID,
      agent: 'engineer',
      model: { providerID: 'host', modelID: 'placeholder' },
    },
    parts: [],
  }
  await hooks['chat.message']({ sessionID, agent: 'engineer' }, output)
  runtime.pushHostMessage(sessionID, { info: output.message, parts: output.parts })
  runtime.pushHostMessage(sessionID, {
    info: { id: `provider-${sessionID}`, sessionID, parentID: output.message.id,
      role: 'assistant', agent: 'engineer', providerID: 'provider', modelID: 'engineer-model',
      time: { created: 2 } },
    parts: [],
  })
}

test('WHAT[capability-enforcement-006] AUTOINJ_skill_wire_stays_host_owned_and_is_not_plugin_registered', async () => {
  assert.equal(markerToolName, 'skill')
  assert.equal(rolePredicate('skill', 'engineer'), false, 'Host-owned skill is not a plugin role tool')
  assert.equal(rolePredicate('skill', 'manager'), false)
  assert.equal(rolePredicate('skill', 'blogger'), false)

  await withPlugin(async (hooks) => {
    assert.equal(hooks.tool['auto-injected'], undefined, 'legacy auto-injected must not be in hooks.tool')
    assert.equal(hooks.tool.skill, undefined, 'skill remains Host-owned rather than plugin-registered')
  })
})
test('WHAT[capability-enforcement-006] AUTOINJ_active_empty_skill_call_is_denied_without_touching_real_skill_names', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    await admitManagedRoot(hooks, runtime)
    const transformed = {
      messages: withSession([
        {
          role: 'assistant',
          info: { id: 'asst-empty-skill' },
          parts: [{
            type: 'tool',
            tool: 'skill',
            callID: 'call-empty',
            state: { status: 'error', input: { name: '' }, error: 'Skill not found' },
          }],
        },
        {
          role: 'assistant',
          info: { id: 'asst-real-skill' },
          parts: [{
            type: 'tool',
            tool: 'skill',
            callID: 'call-real',
            state: { status: 'completed', input: { name: 'pdfs' }, output: 'real skill output' },
          }],
        },
        {
          role: 'user',
          info: { id: 'root-ses-auto-injected' },
          parts: [{ type: 'text', text: 'hello' }],
        },
      ]),
    }
    await hooks['experimental.chat.messages.transform']({}, transformed)
    const rewritten = transformed.messages.find((message) => message.info?.id === 'asst-empty-skill')
    assert.ok(rewritten)
    const part = rewritten.parts[0]
    assert.equal(part.state.status, 'completed', 'empty-name skill failure must be rewritten to completed')
    assert.equal(part.state.error, undefined, 'error field must be cleared')
    assert.match(part.state.output, /DENIED|禁止/, 'result must contain denial text')
    assert.match(part.state.output, /skill/, 'denial must identify the reserved empty-name skill load')

    const real = transformed.messages.find((message) => message.info?.id === 'asst-real-skill')
    assert.ok(real)
    assert.deepEqual(real.parts[0].state.input, { name: 'pdfs' })
    assert.equal(real.parts[0].state.output, 'real skill output')
  })
})
test('WHAT[capability-enforcement-006] AUTOINJ_tryInject_rewrites_active_call_without_synthetic_injection', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    await admitManagedRoot(hooks, runtime)
    const transformed = {
      messages: withSession([
        {
          role: 'assistant',
          info: { id: 'asst-1' },
          parts: [
            {
              type: 'tool',
              tool: 'skill',
              callID: 'call-active',
              state: { status: 'error', input: { name: '' }, error: 'Skill not found' },
            },
          ],
        },
        {
          role: 'user',
          info: { id: 'root-ses-auto-injected' },
          parts: [{ type: 'text', text: 'hello' }],
        },
      ]),
    }

    await hooks['experimental.chat.messages.transform']({}, transformed)
    const rewrittenActive = transformed.messages.find((message) => message.info?.id === 'asst-1')
    assert.ok(rewrittenActive)
    assert.equal(rewrittenActive.parts[0].state.status, 'completed')
    assert.match(rewrittenActive.parts[0].state.output, /DENIED/)

    const synthetic = transformed.messages.find(
      (message) => message.info?.source === markerSource,
    )
    assert.equal(synthetic, undefined, 'zero-synthetic mode must not inject a synthetic skill row')
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { installDefaultResources } = await import("../../../dist/OpenCode/Host/ManagedAgentConfigSurface.js");
const { chronicleContract } = await import("../../../dist/OpenCode/Tools/ToolSurface.js");

installDefaultResources()

test('WHAT[capability-enforcement-006] CHRONICLE_spec_exposes_identity_and_argument_surface', () => {
  const contract = chronicleContract()
  assert.equal(contract.name, 'chronicle')
  assert.deepEqual(contract.argumentNames, ['charge', 'occurrence', 'settlement', 'consequence', 'evidence', 'tip'])
  assert.equal(contract.tipCount, 120)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { rolePredicate } = await import("../../../dist/OpenCode/Tools/ToolRegistrySurface.js");
const { permissions: rolePermissions, isAllowed: surfaceIsAllowed } = await import("../../../dist/Participant/Persona/OfficeCapabilitySurface.js");
const { allRoleLabels } = await import("../../../dist/Foundation/RolesSurface.js");


test('WHAT[capability-enforcement-006] inquiry_role_is_revoked_and_permissions_fail_closed', () => {
  assert.equal(allRoleLabels.includes('inquiry'), false, 'Inquiry must not be in canonical active roles')
  const allowed = rolePermissions('inquiry')
  assert.deepEqual(allowed, [], 'inquiry permissions must fail closed to empty set')
})
test('WHAT[capability-enforcement-006] inquiry_isAllowed_denies_all_tools', () => {
  assert.equal(surfaceIsAllowed('inquiry', 'Inspect'), false)
  assert.equal(surfaceIsAllowed('inquiry', 'Fission'), false)
  assert.equal(surfaceIsAllowed('inquiry', 'Read'), false)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { admissionAuthority, privateAttachmentAdmits, rolePredicate } = await import("../../../dist/OpenCode/Tools/ToolRegistrySurface.js");
const { allRoleLabels } = await import("../../../dist/Foundation/RolesSurface.js");

const OFFICE_TOOLS = ['fetch', 'review', 'join', 'chronicle', 'run', 'fork', 'resume']

test('WHAT[capability-enforcement-006] internal_leaf_tool_declares_attachment_authority_not_a_public_office', () => {
  assert.equal(admissionAuthority('js-bookkeeper'), 'private-attachment')
  for (const tool of OFFICE_TOOLS) {
    assert.equal(admissionAuthority(tool), 'office', `${tool} is an office tool`)
  }
  assert.equal(admissionAuthority('no-such-tool'), 'unknown')
})
test('WHAT[capability-enforcement-006] internal_leaf_tool_is_invisible_to_every_public_office_role', () => {
  assert.ok(allRoleLabels.length > 0)
  for (const role of allRoleLabels) {
    assert.equal(rolePredicate('js-bookkeeper', role), false, `js-bookkeeper must stay invisible to ${role}`)
  }
})
test('WHAT[capability-enforcement-006] attachment_authority_is_fail_closed_without_an_attached_transaction', () => {
  assert.equal(privateAttachmentAdmits('js-bookkeeper', 'ses-never-attached'), false)
  assert.equal(privateAttachmentAdmits('js-bookkeeper', ''), false)
})
test('WHAT[capability-enforcement-006] an_office_tool_can_never_be_admitted_through_the_attachment_path', () => {
  for (const tool of OFFICE_TOOLS) {
    assert.equal(privateAttachmentAdmits(tool, 'ses-any'), false, `${tool} must not take the attachment path`)
  }
  assert.equal(privateAttachmentAdmits('no-such-tool', 'ses-any'), false)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { markerSource } = await import("../../../dist/OpenCode/Host/PairProgrammingThoughtSurface.js");
const { withExecutablePlugin, acceptAuthorityRoot } = await import("../../verification-system/tests/support/plugin-fixture.mjs");

const withSession = (messages, sessionID = 'engineer-auto-injected') =>
  messages.map((message, index) => ({
    ...message,
    info: {
      ...(message.info ?? {}),
      id: message.info?.id ?? `msg-${index}`,
      role: message.info?.role ?? message.role ?? 'user',
      sessionID,
    },
  }))

integrationTest('WHAT[capability-enforcement-006] HOST_013_skill_stays_host_owned_and_legacy_marker_is_not_plugin_registered', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    await hooks['chat.message']({ sessionID: 'engineer-auto-injected', agent: 'engineer' }, {
      message: {
        id: 'root-engineer-auto-injected', role: 'user', sessionID: 'engineer-auto-injected',
        agent: 'engineer', model: { providerID: 'host', modelID: 'placeholder' },
      },
      parts: [],
    })
    assert.equal(hooks.tool['auto-injected'], undefined, 'legacy auto-injected must not be in hooks.tool')
    assert.equal(hooks.tool.skill, undefined, 'skill remains Host-owned rather than plugin-registered')

    runtime.pushHostMessage('engineer-auto-injected', {
      info: { id: 'provider-engineer-auto-injected', sessionID: 'engineer-auto-injected',
        parentID: 'root-engineer-auto-injected', role: 'assistant', agent: 'engineer',
        providerID: 'provider', modelID: 'engineer-model', time: { created: 3 } },
      parts: [],
    })

    const transformed = {
      messages: withSession([
        { role: 'user', info: { id: 'root-engineer-auto-injected' }, parts: [{ type: 'text', text: 'start' }] },
        { role: 'assistant', info: { id: 'c1' }, parts: [{ type: 'tool', tool: 'read', callID: 't1', state: { status: 'pending', input: {}, time: { start: 0 } } }] },
        { role: 'assistant', info: { id: 'r1' }, parts: [{ type: 'tool', tool: 'read', callID: 't1', state: { status: 'completed', input: {}, output: 'ok1', time: { start: 0, end: 0 } } }] },
      ]),
    }
    await hooks['experimental.chat.messages.transform']({}, transformed)
    const synthetic = transformed.messages.find((message) => message.info?.source === markerSource)
    assert.equal(synthetic, undefined, 'zero-synthetic mode must not inject a synthetic skill row')
    const terminal = transformed.messages.find((message) => message.info?.id === 'r1')
    assert.ok(terminal, 'terminal real tool result survives the transform')
    const output = terminal.parts?.[0]?.state?.output ?? ''
    assert.ok(output.startsWith('ok1\0\uFEFF'), 'guidance travels as NUL+BOM suffix on the terminal real tool result')
    assert.match(output, /#/, 'suffix carries guidance bytes')
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
  'read', 'write', 'edit', 'glob', 'grep', 'mv', 'rm',
  'bash-honeypot', 'assume', 'todowrite',
  'defer',  'publish',
  'run', 'open-terminal', 'send-terminal', 'read-terminal', 'signal-terminal',
  'review', 'chronicle', 'fetch', 'suicide',
]

const HOST_OWNED_TOOL_NAMES = [ 'read', 'write', 'edit', 'glob', 'grep', 'skill', 'todowrite',
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

integrationTest('WHAT[capability-enforcement-006] MANAGER_pair_guidance_rides_cursor_suffix_without_synthetic_skill_row', async () => {
  assert.equal(markerToolName, 'skill')
  assert.equal(typeof markerSource, 'string')
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    await hooks['chat.message']({ sessionID: 'ses-capability-manager', agent: 'manager' }, {
      message: {
        id: 'root-ses-capability-manager', role: 'user', sessionID: 'ses-capability-manager',
        agent: 'manager', model: { providerID: 'host', modelID: 'placeholder' },
      },
      parts: [],
    })
    assert.equal(hooks.tool.skill, undefined, 'skill remains Host-owned')
    assert.equal(hooks.tool['auto-injected'], undefined, 'legacy auto-injected must not be plugin-registered')
    runtime.pushHostMessage('ses-capability-manager', {
      info: { id: 'provider-capability-manager', sessionID: 'ses-capability-manager',
        parentID: 'root-ses-capability-manager', role: 'assistant', agent: 'manager',
        providerID: 'provider', modelID: 'manager-model', time: { created: 3 } },
      parts: [],
    })
    const transformed = {
      messages: withSession([
        { role: 'user', info: { id: 'root-ses-capability-manager' }, parts: [{ type: 'text', text: 'start' }] },
        { role: 'assistant', info: { id: 'c1' }, parts: [{ type: 'tool', tool: 'read', callID: 't1', state: { status: 'pending', input: {}, time: { start: 0 } } }] },
        { role: 'assistant', info: { id: 'r1' }, parts: [{ type: 'tool', tool: 'read', callID: 't1', state: { status: 'completed', input: {}, output: 'ok1', time: { start: 0, end: 0 } } }] },
      ]),
    }
    await hooks['experimental.chat.messages.transform']({}, transformed)
    const synthetic = transformed.messages.find((message) => message.info?.source === markerSource)
    assert.equal(synthetic, undefined, 'zero-synthetic mode must not inject a synthetic skill row')
    const terminal = transformed.messages.find((message) => message.info?.id === 'r1')
    assert.ok(terminal, 'terminal real tool result survives the transform')
    const output = terminal.parts?.[0]?.state?.output ?? ''
    assert.ok(output.startsWith('ok1\0\uFEFF'), 'guidance travels as NUL+BOM suffix on the terminal real tool result')
    assert.match(output, /#/, 'suffix carries guidance bytes')
    assert.equal(hooks.tool[markerToolName], undefined, 'cursor suffix borrows no plugin-registered skill name')
  })
})
}

{
  const assert = (await import('node:assert/strict')).default
  const managedAgentConfig = await import('../../../dist/OpenCode/Host/ManagedAgentConfigSurface.js')

  test('WHAT[capability-enforcement-006] internal repository search is not installed as a Host MCP tool or role permission', () => {
    managedAgentConfig.installDefaultResources()
    const roles = ['Manager', 'Orchestrator', 'Engineer', 'DevOps', 'Blogger', 'Bookkeeper']
    const config = { agent: Object.fromEntries(roles.map((role) => [role.toLowerCase(), { model: `${role.toLowerCase()}-model` }])) }
    assert.equal(managedAgentConfig.configure(config).ok, true)
    assert.equal(config.mcp?.semble, undefined)
    assert.equal(config.mcp?.['stealth-browser-mcp'], undefined)
    for (const role of roles) {
      const permission = config.agent[role.toLowerCase()].permission
      for (const tool of ['semble', 'semble_*', 'semble_search']) assert.equal(permission[tool], undefined, `${role}.${tool}`)
    }
  })
}
