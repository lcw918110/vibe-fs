import assert from 'node:assert/strict'
import test from 'node:test'
import { permissions } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'
import {
  configure as configureManagedAgents,
  installDefaultResources,
  validate as validateManagedAgents,
} from '../../../dist/OpenCode/Host/ManagedAgentConfigSurface.js'
import { generateRole } from '../../../dist/Repository/Programming/Js/GeneratorSurface.js'

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

const ROLE_ALLOW = {
  Manager: ['fork', 'resume', 'join', 'horizon', 'deliver', 'review', 'todowrite-for-manager'],
  Orchestrator: ['commission', 'join', 'horizon', 'todowrite-for-orchestrator'],
  Engineer: ['bash-honeypot', 'fetch', 'fission', 'todowrite-for-engineer'],
  DevOps: [
    'join',
    'horizon',
    'todowrite-for-devops',
  ],
}

test('WHAT[capability-enforcement-023] devops_host_schema_contains_direct_mutation_tools', () => {
  const config = buildConfig()
  assert.equal(configureManagedAgents(config).ok, true)
  assert.equal(evaluate(mergedRules(config, 'devops'), 'todowrite-for-devops', '*').action, 'allow', 'devops must allow todowrite-for-devops')
  for (const retired of ['read', 'write', 'edit', 'glob', 'grep', 'mv', 'rm']) {
    assert.equal(retired in config.agent.devops.permission, false, `retired ${retired} must not be projected in devops permission`)
    assert.equal(evaluate(mergedRules(config, 'devops'), retired, '*').action, 'deny', `retired ${retired} must be denied by default`)
  }
  const surface = generateRole('devops', 'en')
  assert.ok(surface, 'DevOps must generate a JS programming surface')
  assert.equal(surface.toolName, 'todowrite-for-devops')
  assert.deepEqual(
    surface.capabilities.slice().sort(),
    ['Advisor', 'Exec', 'Glob', 'Grep', 'Horizon', 'Join', 'Move', 'Read', 'Remove', 'Write'],
  )
  const memberNames = surface.members.map((m) => m.memberName)
  for (const member of ['cat', 'ls', 'rg', 'stat', 'where', 'whereAll', 'mkdir', 'write', 'mv', 'rm', 'call', 'join']) {
    assert.ok(memberNames.includes(member), `todowrite-for-devops must expose ${member}`)
  }
  assert.deepEqual(
    allowList(config, 'devops').sort(),
    [...ROLE_ALLOW.DevOps, ...hostUtilityAllowFor('DevOps'), ...cognitiveUtilityAllowFor('DevOps')].sort(),
    'devops allow set must equal current matrix + non-authority utilities',
  )
  for (const retiredRole of ['blogger', 'predictor']) {
    assert.deepEqual(permissions(retiredRole), [], `retired ${retiredRole} permissions must fail closed`)
    assert.equal(generateRole(retiredRole, 'en'), null, `retired ${retiredRole} must fail closed`)
  }
})
