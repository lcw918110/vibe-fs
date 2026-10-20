import assert from 'node:assert/strict'
import test from 'node:test'
import * as authority from '../../../dist/Interaction/Authority/RuntimeSurface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'

const H = (input) => `H(${input})`

const RUNTIME = 'rt_1'

const SESSION = 'ses_a'

const findClaim = (projection, key) => projection.pendingClaims.find((claim) => claim.promptKey === key)

const promptOrigin = (kind) => authority.originForContinuation(kind)

const personas = {
  engineer: 'Engineer',
  coder: 'Coder',
  manager: 'Lead',
}

const rootSelection = (participant) => {
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant,
      role: participant,
      selectedTier: 'deep',
      persona: personas[participant] ?? 'Unknown',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
}

const inheritedSeed = (agent, physical) => {
  const owner = authority.createAuthorityRoot(
    H,
    RUNTIME,
    SESSION,
    'HumanRoot',
    physical,
    rootSelection('manager'),
  )
  assert.equal(owner.ok, true, owner.error)
  const inherited = authority.issueInheritedIdentitySeed(agent, owner.value)
  assert.equal(inherited.ok, true, inherited.error)
  return inherited.value
}

const profileOf = () => {
  const built = authority.createAuthorityRoot(
    H,
    RUNTIME,
    SESSION,
    'HumanRoot',
    'msg_u1',
    rootSelection('engineer'),
  )
  assert.equal(built.ok, true, built.ok ? '' : built.error)
  return built.value
}

test('WHAT[dispatch-protocol-006] DP_006_abandon_keeps_the_claim_sequence_consumed', () => {
  const root = profileOf()
  const key = 'pk_x'
  let projection = authority.registerAuthority(root, authority.empty)
  projection = authority.registerClaim(
    authority.claimContinuation(key, SESSION, 'BusyAgentNudge', root, 'pd-n'),
    projection,
  )

  const after = authority.abandonClaim(key, projection)

  assert.equal(after.claimSequences.length, 1)
})

test('WHAT[dispatch-protocol-006] DP_006_claim_sequence_advances_on_registration_not_on_resolution', () => {
  const root = profileOf()
  const scope = authority.claimScopeDigest(
    SESSION,
    root.logicalRun,
    promptOrigin('DegenerationGuard'),
    'pd-same',
  )

  let projection = authority.registerAuthority(root, authority.empty)
  assert.equal(authority.nextClaimSequence(scope, projection), 1)

  const claimAt = (n) =>
    authority.claimContinuation(`pk_${n}`, SESSION, 'DegenerationGuard', root, 'pd-same')

  projection = authority.registerClaim(claimAt(1), projection)
  assert.equal(authority.nextClaimSequence(scope, projection), 2)

  projection = authority.abandonClaim('pk_1', projection)
  assert.equal(authority.nextClaimSequence(scope, projection), 2)

  projection = authority.registerClaim(claimAt(2), projection)
  assert.equal(authority.nextClaimSequence(scope, projection), 3)
})

test('WHAT[dispatch-protocol-006] DP_006_same_payload_landings_stay_exact_per_physical_message', () => {
  // Dispatches with one fixed instruction text: two independent acts,
  // one payload digest. The payload view keeps only the
  // latest landing, and abandoning a later same-payload claim clears that
  // occasion slot; the exact landing of every physical message survives both.
  const root = profileOf()
  const claimAt = (key) => authority.claimContinuation(key, SESSION, 'ManagedDelegationAssignment', root, 'pd-same')

  let projection = authority.registerAuthority(root, authority.empty)
  projection = authority.registerClaim(claimAt('pk_1'), projection)
  projection = authority.acceptClaim('pk_1', 'msg_1', projection)
  projection = authority.registerClaim(claimAt('pk_2'), projection)
  projection = authority.acceptClaim('pk_2', 'msg_2', projection)
  projection = authority.registerClaim(claimAt('pk_3'), projection)
  projection = authority.abandonClaim('pk_3', projection)

  const landedAs = (physical) => projection.physicalLandings.find((landing) => landing.physical === physical)
  assert.equal(landedAs('msg_1')?.promptKey, 'pk_1')
  assert.equal(landedAs('msg_2')?.promptKey, 'pk_2')
  assert.equal(landedAs('msg_3'), undefined, 'an abandoned claim never lands')
})

for (const boundary of ['new root', 'exact run close']) {
  test(`WHAT[dispatch-protocol-006] an accepted None-root scope keeps its consumed sequence across ${boundary}`, () => {
    const session = `accepted-none-root-${boundary}`
    const seed = inheritedSeed('engineer', 'manager-owning-none-root-sequence')
    const origin = { kind: 'AuthorityRoot', label: 'AgentOwnerRoot' }
    const payload = 'same original root payload'
    const scope = authority.claimScopeDigest(session, null, origin, payload)
    const firstSequence = authority.nextClaimSequence(scope, authority.empty)
    assert.equal(firstSequence, 1)
    const firstKey = authority.derivePromptKey(H, session, null, null, origin, payload, firstSequence)
    const claim = authority.claimAgentOwnerRoot(firstKey, session, payload, seed)
    assert.equal(claim.ok, true, claim.error)
    let state = authority.registerClaim(claim.value, authority.empty)
    state = authority.acceptClaim(firstKey, 'physical-accepted-none-root', state)
    assert.equal(state.pendingClaims.length, 0,
      'this consumed None scope has no Pending claim from which to recover its counter')
    assert.equal(authority.nextClaimSequence(scope, state), 2)
    const built = authority.createAuthorityRoot(H, RUNTIME, session,
      'AgentOwnerRoot', 'physical-accepted-none-root', seed)
    assert.equal(built.ok, true, built.error)
    state = authority.registerAuthority(built.value, state)
    if (boundary === 'exact run close') {
      const closed = authority.closeAuthority(built.value.logicalRun, built.value.authorityRoot, state)
      assert.equal(closed.ok, true, closed.error)
      state = closed.value
      assert.equal(state.activeLogicalRun, null)
    }
    const nextSequence = authority.nextClaimSequence(scope, state)
    const nextKey = authority.derivePromptKey(H, session, null, null, origin, payload, nextSequence)
    assert.notEqual(nextKey, firstKey,
      'a later independent same-payload root act cannot reuse an already physically accepted PromptKey')
    assert.equal(nextSequence, 2)
    assert.equal(state.physicalLandings.find(landing => landing.physical === 'physical-accepted-none-root').promptKey, firstKey)
  })
}

test('WHAT[dispatch-protocol-006] exact close clears a Some continuation scope even when its run id equals the absence marker', () => {
  for (const [label, hasher] of [['normal', H], ['absence-marker', () => '\u0000absent']]) {
    const session = `some-run-sequence-${label}`
    const built = authority.createAuthorityRoot(hasher, RUNTIME, session,
      'HumanRoot', `physical-some-run-${label}`, rootSelection('engineer'))
    assert.equal(built.ok, true, built.error)
    const root = built.value
    if (label === 'absence-marker') assert.equal(root.logicalRun, '\u0000absent')
    const origin = authority.originForContinuation('ManagerGuard')
    const scope = authority.claimScopeDigest(session, root.logicalRun, origin, 'owned-continuation')
    const key = authority.derivePromptKey(H, session, root.logicalRun, root.authorityRoot,
      origin, 'owned-continuation', 1)
    let state = authority.registerAuthority(root, authority.empty)
    state = authority.registerClaim(
      authority.claimContinuation(key, session, 'ManagerGuard', root, 'owned-continuation'), state)
    assert.equal(state.pendingClaims[0].logicalRun, root.logicalRun)
    assert.equal(authority.nextClaimSequence(scope, state), 2)
    const closed = authority.closeAuthority(root.logicalRun, root.authorityRoot, state)
    assert.equal(closed.ok, true, closed.error)
    assert.equal(closed.value.activeLogicalRun, null)
    assert.deepEqual(closed.value.pendingClaims, [])
    assert.equal(authority.nextClaimSequence(scope, closed.value), 1,
      `${label}: scope ownership is the typed Some run, not the spelling of its encoded prefix`)
    assert.deepEqual(closed.value.claimSequences, [])
  }
})
