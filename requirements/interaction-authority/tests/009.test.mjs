import assert from 'node:assert/strict'
import test from 'node:test'
import * as intent from '../../../dist/Interaction/Admission/ChatAdmissionIntentSurface.js'
import * as authority from '../../../dist/Interaction/Authority/RuntimeSurface.js'

const hash = (value) => `H(${value})`

const personas = {
  engineer: 'Engineer',
  coder: 'Coder',
  manager: 'Lead',
  reviewer: 'Investigator',
  inspector: 'Investigator',
}

const rootSelection = (agent) => {
  const role = agent === 'predictor' ? 'inspector' : agent
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant: agent,
      role,
      selectedTier: 'deep',
      persona: personas[agent] ?? 'Unknown',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
}

const inheritedSelection = (agent, physical) => {
  const owner = authority.createAuthorityRoot(
    hash,
    'rt_owner',
    'ses_owner',
    'HumanRoot',
    `owner_${physical}`,
    rootSelection('manager'),
  )
  assert.equal(owner.ok, true, owner.error)
  const inherited = authority.issueInheritedIdentitySeed(agent, owner.value)
  assert.equal(inherited.ok, true, inherited.error)
  return inherited.value
}

const rootFor = (agent = 'engineer', physical = 'msg_u1', kind = 'HumanRoot') => {
  const seed = kind === 'AgentOwnerRoot' ? inheritedSelection(agent, physical) : rootSelection(agent)
  const result = authority.createAuthorityRoot(hash, 'rt_1', 'ses_a', kind, physical, seed)
  assert.equal(result.ok, true, result.error)
  return result.value
}

const profile = (value) => ({
  session: value.session,
  logicalRun: value.logicalRun,
  authorityRoot: value.authorityRoot,
  authorityKind: value.authorityKind,
  participant: value.participantIdentity.participant,
  role: value.participantIdentity.role,
})

const register = (root) => authority.registerAuthority(root, authority.empty)

test.todo('WHAT[interaction-authority-009] actual ingress resolves an omitted participant from active or historical durable Profile while malformed carriers and unprovable identity remain rejected (GAP-122)')

test('WHAT[interaction-authority-009] IA_009_pure_resolution_never_infers_human_root', () => {
  const root = rootFor('engineer', 'msg_u1', 'HumanRoot')
  const state = register(root)
  assert.equal(state.activeLogicalRun.authorityKind, 'HumanRoot')
  assert.equal(authority.resolveKnownOrigin('msg_new', 'pk_any', false, state), 'UnknownOrigin')
})

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const intent = await import("../../../dist/Interaction/Admission/ChatAdmissionIntentSurface.js");

const message = (overrides = {}) => ({
  sessionId: 'ses-chat',
  physicalUserMessageId: 'msg-chat',
  explicitAgent: null,
  promptKey: null,
  hostCompaction: false,
  hostSynthetic: false,
  ...overrides,
})
const snapshot = (overrides = {}) => ({
  available: true,
  activeParticipant: null,
  activeKind: null,
  claims: [],
  acceptedContinuations: [],
  ...overrides,
})
const decide = (decoded, durable = snapshot()) => intent.resolve(decoded, durable)

test('WHAT[interaction-authority-009] explicit agent cannot infer HumanRoot while active', () => {
  assert.deepEqual(
    decide(
      message({ explicitAgent: 'manager' }),
      snapshot({ activeParticipant: 'engineer', activeKind: 'HumanRoot' }),
    ),
    { case: 'Reject', reason: 'UnknownOriginWhileActive' },
  )
})
test('WHAT[interaction-authority-009] matching user agent continues the exact active root', () => {
  assert.deepEqual(
    decide(
      message({ explicitAgent: 'engineer' }),
      snapshot({ activeParticipant: 'engineer', activeKind: 'HumanRoot' }),
    ),
    {
      case: 'ActiveHumanContinuationIntent',
      sessionId: 'ses-chat',
      physicalUserMessageId: 'msg-chat',
      participant: 'engineer',
      origin: 'HumanMessage',
    },
  )
})
}
