import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const intent = await import("../../../dist/Interaction/Admission/ChatAdmissionIntentSurface.js");

test('WHAT[interaction-authority-008] HostInternal_companion_carrier_marks_internal', () => {
  assert.equal(intent.hostInternal({}), false)
  assert.equal(intent.hostInternal({ hostCompaction: false, hostSynthetic: false }), false)
  assert.equal(intent.hostInternal({ hostCompaction: true }), true)
  assert.equal(intent.hostInternal({ hostSynthetic: true }), true)
  assert.equal(intent.hostInternal({ hostCompaction: false, hostSynthetic: true }), true)
  assert.equal(intent.hostInternal({ hostCompaction: true, hostSynthetic: true }), true)
})

test('WHAT[interaction-authority-008] HostInternal_raw_compaction_and_synthetic_carriers', async () => {
  const wire = await import("../../../dist/OpenCode/Codec/ProviderProjectionSurface.js")
  const flags = (envelope) => {
    const decoded = wire.decodeIngress({}, envelope)
    return { hostCompaction: decoded.isHostCompaction, hostSynthetic: decoded.isHostSynthetic }
  }
  assert.equal(intent.hostInternal(flags({ parts: [{ type: 'compaction' }] })), true)
  assert.equal(intent.hostInternal(flags({ message: { summary: true } })), true)
  assert.equal(intent.hostInternal(flags({ message: { agent: 'compaction' } })), true)
  assert.equal(intent.hostInternal(flags({ message: { mode: 'compaction' } })), true)
  assert.equal(intent.hostInternal(flags({ parts: [{ synthetic: true }] })), true)
  assert.equal(intent.hostInternal(flags({ parts: [{ synthetic: true }, { synthetic: true }] })), true)
  assert.equal(intent.hostInternal(flags({ parts: [{ type: 'text', text: 'a' }, { synthetic: true }] })), false)
  assert.equal(intent.hostInternal(flags({ parts: [{ synthetic: true }, {}] })), false)
  assert.equal(intent.hostInternal(flags({ message: { mode: 'chat' } })), false)
  assert.equal(intent.hostInternal(flags({})), false)
})
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");

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

test('WHAT[interaction-authority-008] IA_008_resolution_order_is_accepted_then_claimed_then_compaction_then_root', () => {
  const root = rootFor('engineer', 'msg_u1', 'AgentOwnerRoot')
  let state = register(root)

  const claimed = authority.claimContinuation('pk_claimed', 'ses_a', 'ManagerGuard', root, 'pd-c')
  state = authority.registerClaim(claimed, state)
  const accepted = authority.claimContinuation('pk_accepted', 'ses_a', 'BusyAgentNudge', root, 'pd-a')
  state = authority.registerClaim(accepted, state)
  state = authority.acceptClaim('pk_accepted', 'msg_accepted', state)

  assert.deepEqual(
    {
      accepted: authority.resolveKnownOrigin('msg_accepted', '', false, state),
      claimed: authority.resolveKnownOrigin('msg_unseen', 'pk_claimed', false, state),
      compaction: authority.resolveKnownOrigin('msg_unseen', '', true, state),
      registeredRoot: authority.resolveKnownOrigin('msg_unseen', 'pk_unknown', false, state),
      nothing: authority.resolveKnownOrigin('msg_unseen', '', false, state),
    },
    {
      accepted: 'Continuation',
      claimed: 'Continuation',
      compaction: 'HostInternal',
      registeredRoot: 'AuthorityRoot',
      nothing: 'UnknownOrigin',
    },
  )
})
test('WHAT[interaction-authority-008] IA_008_accepted_continuation_outranks_compaction', () => {
  const root = rootFor()
  let state = register(root)
  state = authority.registerClaim(
    authority.claimContinuation('pk_both', 'ses_a', 'ManagerGuard', root, 'pd-b'),
    state,
  )
  state = authority.acceptClaim('pk_both', 'msg_both', state)
  assert.equal(authority.resolveKnownOrigin('msg_both', '', true, state), 'Continuation')
})
}

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

test('WHAT[interaction-authority-008] accepted Host identity outranks claim and compaction', () => {
  const durable = snapshot({
    claims: [
      {
        promptKey: 'prompt-1',
        sessionId: 'ses-chat',
        origin: 'InteractionRepair',
        participant: 'engineer',
      },
    ],
    acceptedContinuations: [{ physicalUserMessageId: 'msg-chat', origin: 'JoinGuard' }],
  })

  assert.deepEqual(
    decide(message({ promptKey: 'prompt-1', hostCompaction: true }), durable),
    { case: 'NoManagedExecution', reason: 'AlreadyAcceptedHostMessage', origin: 'JoinGuard' },
  )
})
test('WHAT[interaction-authority-008] registered AgentOwnerRoot outranks external root inference', () => {
  assert.deepEqual(
    decide(
      message({ promptKey: 'unclaimed-owner-root', explicitAgent: 'reviewer' }),
      snapshot({ activeParticipant: 'engineer', activeKind: 'AgentOwnerRoot' }),
    ),
    { case: 'Reject', reason: 'AgentOwnerRootPromptNotClaimed' },
  )
})
test('WHAT[interaction-authority-008] plugin claim is frozen even if the later projection changes', () => {
  const durable = snapshot({
    claims: [
      {
        promptKey: 'prompt-1',
        sessionId: 'ses-chat',
        origin: 'InteractionRepair',
        participant: 'engineer',
      },
    ],
  })

  const resolved = decide(message({ promptKey: 'prompt-1' }), durable)
  durable.claims[0].participant = 'reviewer'
  durable.claims.length = 0

  assert.equal(resolved.case, 'PendingPromptIntent')
  assert.equal(resolved.promptKey, 'prompt-1')
  assert.equal(resolved.participant, 'engineer')
  assert.equal('effectiveAgent' in resolved, false, 'intent carries no EffectiveAgent')
  assert.equal('selectedAgent' in resolved, false, 'intent carries no SelectedAgent')
})
test('WHAT[interaction-authority-008] a landed AgentOwnerRoot physical message is a no-op replay, not an unclaimed rejection', () => {
  const durable = snapshot({
    activeParticipant: 'engineer',
    activeKind: 'AgentOwnerRoot',
    landings: [
      {
        promptKey: 'landed-owner-root',
        physicalUserMessageId: 'msg-chat',
        origin: 'AgentOwnerRoot',
        participant: 'engineer',
      },
    ],
  })

  assert.deepEqual(
    decide(message({ promptKey: 'landed-owner-root', explicitAgent: 'engineer' }), durable),
    { case: 'NoManagedExecution', reason: 'AlreadyAcceptedHostMessage', origin: 'AgentOwnerRoot' },
  )
})
}
