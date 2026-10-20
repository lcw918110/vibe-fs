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

test('WHAT[dispatch-protocol-005] DP_005_prompt_key_is_deterministic_and_moves_with_every_component', () => {
  const root = profileOf()
  const base = {
    session: SESSION,
    run: root.logicalRun,
    authorityRootId: root.authorityRoot,
    origin: promptOrigin('ManagerGuard'),
    payload: 'pd-1',
    sequence: 1,
  }

  const derive = (value) =>
    authority.derivePromptKey(
      H,
      value.session,
      value.run,
      value.authorityRootId,
      value.origin,
      value.payload,
      value.sequence,
    )

  assert.equal(
    derive(base),
    `H(${['ses_a', 'H(rt_1\nses_a\nmsg_u1)', 'msg_u1', 'ManagerGuard', 'pd-1', '1'].join('\u001f')})`,
  )
  assert.equal(derive(base), derive(base), 'same logical dispatch is deterministic')

  const variants = {
    session: { ...base, session: 'ses_b' },
    run: { ...base, run: 'another-logical-run' },
    authorityRootId: { ...base, authorityRootId: 'another-authority-root' },
    origin: { ...base, origin: promptOrigin('DegenerationGuard') },
    payload: { ...base, payload: 'pd-2' },
    sequence: { ...base, sequence: 2 },
  }

  for (const name of Object.keys(variants)) {
    assert.notEqual(derive(variants[name]), derive(base), `${name} must participate in PromptKey`)
  }

  // PromptKey is participant-blind: the same logical dispatch under a different
  // participant derives the identical key.
  const otherRoot = (() => {
    const built = authority.createAuthorityRoot(
      H,
      RUNTIME,
      SESSION,
      'HumanRoot',
      'msg_u1',
      rootSelection('manager'),
    )
    assert.equal(built.ok, true, built.ok ? '' : built.error)
    return built.value
  })()
  assert.equal(
    authority.derivePromptKey(H, SESSION, otherRoot.logicalRun, otherRoot.authorityRoot, promptOrigin('ManagerGuard'), 'pd-1', 1),
    derive(base),
    'participant must not participate in PromptKey',
  )
})

test('WHAT[dispatch-protocol-005] DP_005_claim_scope_names_exactly_session_run_origin_and_payload', () => {
  const root = profileOf()
  const scope = authority.claimScopeDigest(
    SESSION,
    root.logicalRun,
    promptOrigin('ManagerGuard'),
    'pd-guard',
  )

  assert.equal(scope, ['ses_a', 'H(rt_1\nses_a\nmsg_u1)', 'ManagerGuard', 'pd-guard'].join('\u001f'))

  assert.equal(
    authority.claimScopeDigest(SESSION, null, { kind: 'HostInternal', label: 'HostInternal' }, 'pd-guard'),
    ['ses_a', '\u0000absent', 'HostInternal', 'pd-guard'].join('\u001f'),
  )
})

test('WHAT[dispatch-protocol-005] DP_005_legacy_identity_fields_are_dropped_never_reencoded', () => {
  const legacyIdentity = {
    participant: 'engineer',
    selectedAgent: 'stale-selected',
    peerAgent: 'stale-peer',
    role: 'engineer',
    canonicalRole: 'stale-role',
    selectedTier: 'deep',
    persona: 'Engineer',
    personaCatalogVersion: 1,
    origin: 'ResolvedAtRoot',
  }
  const built = authority.createAuthorityRoot(
    H,
    RUNTIME,
    SESSION,
    'HumanRoot',
    'msg_legacy',
    {
      kind: 'RootSelection',
      ownerSession: null,
      ownerLogicalRun: null,
      ownerAuthorityRoot: null,
      participantIdentity: legacyIdentity,
    },
  )
  assert.equal(built.ok, true, built.ok ? '' : built.error)
  assert.deepEqual(
    built.value.participantIdentity,
    {
      origin: 'ResolvedAtRoot',
      participant: 'engineer',
      persona: 'Engineer',
      personaCatalogVersion: 1,
      role: 'engineer',
    },
    'legacy peer/selectedAgent/canonicalRole fields must fold into canonical participant',
  )

  const legacySeed = {
    kind: 'InheritedFromOwner',
    ownerSession: 'ses-owner',
    ownerLogicalRun: 'run-owner',
    ownerAuthorityRoot: 'msg-owner',
    effectiveAgent: 'stale-effective',
    participantIdentity: { ...legacyIdentity, origin: 'InheritedFromOwner' },
  }
  const rehydrated = authority.rehydrateIdentitySeed(JSON.stringify(legacySeed))
  assert.equal(rehydrated.ok, true, rehydrated.ok ? '' : rehydrated.error)
  const serialized = authority.serializeIdentitySeed(rehydrated.value)
  assert.equal(serialized.ok, true, serialized.ok ? '' : serialized.error)
  const reparsed = JSON.parse(serialized.value)
  for (const field of ['peerAgent', 'selectedAgent', 'canonicalRole', 'effectiveAgent', 'PeerAgent', 'EffectiveAgent']) {
    assert.equal(Object.hasOwn(reparsed, field), false, `${field} must not be re-encoded at seed top level`)
    assert.equal(Object.hasOwn(reparsed.participantIdentity, field), false, `${field} must not be re-encoded in participantIdentity`)
  }
  assert.deepEqual(
    reparsed.participantIdentity,
    {
      origin: 'InheritedFromOwner',
      participant: 'engineer',
      persona: 'Engineer',
      personaCatalogVersion: 1,
      role: 'engineer',
    },
  )
})
