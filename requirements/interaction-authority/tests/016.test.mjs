import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");

const hash = (value) => `H(${value})`
const personas = {
  engineer: 'Engineer',
  coder: 'Coder',
  manager: 'Lead',
  reviewer: 'Auditor',
  inspector: 'Investigator',
  devops: 'Operator',
}
const rootSelection = (agent) => {
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant: agent,
      role: agent,
      selectedTier: 'deep',
      persona: personas[agent] ?? 'Unknown',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
}
const rootFor = (agent = 'engineer', physical = 'msg_u1') => {
  const result = authority.createAuthorityRoot(hash, 'rt_1', 'ses_a', 'HumanRoot', physical, rootSelection(agent))
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
const continuation = (key, root, kind = 'ManagerGuard', payload = 'payload') =>
  authority.claimContinuation(key, 'ses_a', kind, root, payload)

test('WHAT[interaction-authority-016] IA_016_agent_owner_root_has_no_run_before_physical_acceptance', () => {
  const owner = rootFor('manager')
  const inherited = authority.issueInheritedIdentitySeed('manager', owner)
  assert.equal(inherited.ok, true, inherited.error)
  const claim = authority.claimAgentOwnerRoot('pk_owner', 'ses_a', 'pd-owner', inherited.value)
  assert.equal(claim.ok, true, claim.error)
  assert.deepEqual(
    {
      origin: claim.value.origin,
      label: claim.value.originLabel,
      hasRun: claim.value.logicalRun !== null,
      hasRoot: claim.value.authorityRoot !== null,
      participant: claim.value.identitySeed.participantIdentity.participant,
      role: claim.value.identitySeed.participantIdentity.role,
    },
    { origin: 'AuthorityRoot', label: 'AgentOwnerRoot', hasRun: false, hasRoot: false, participant: 'manager', role: 'manager' },
  )

  let state = authority.registerClaim(claim.value, register(owner))
  state = authority.acceptClaim('pk_owner', 'msg_owner', state)
  assert.equal(state.pendingClaims.length, 0)
  assert.equal(state.acceptedContinuations.length, 0)
  assert.equal(authority.resolveKnownOrigin('msg_owner', '', false, state), 'UnknownOrigin')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const intent = await import("../../../dist/OpenCode/Host/ChatAdmission/IntentSurface.js");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");

const hash = (value) => `H(${value})`
const personas = {
  engineer: 'Engineer',
  coder: 'Coder',
  manager: 'Lead',
  reviewer: 'Auditor',
  inspector: 'Investigator',
}
const rootSelection = (agent) => {
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant: agent,
      role: agent,
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

test('WHAT[interaction-authority-016] IA_016_accepted_root_claim_stays_out_of_continuation_map', () => {
  const claim = authority.claimAgentOwnerRoot(
    'pk_owner',
    'ses_a',
    'pd-owner',
    inheritedSelection('manager', 'msg_owner'),
  )
  assert.equal(claim.ok, true, claim.error)
  let state = authority.registerClaim(claim.value, authority.empty)
  state = authority.acceptClaim('pk_owner', 'msg_owner', state)
  assert.equal(state.pendingClaims.length, 0)
  assert.equal(state.acceptedContinuations.length, 0)
  assert.equal(authority.resolveKnownOrigin('msg_owner', '', false, state), 'UnknownOrigin')
})
}
