import assert from 'node:assert/strict'
import test from 'node:test'
import * as Attempt from '../../../dist/Context/Companion/CompressionSurface.js'
import * as ProviderFailure from '../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js'
import * as Authority from '../../../dist/Interaction/Authority/Surface.js'
import * as Runtime from '../../../dist/Interaction/Authority/RuntimeSurface.js'
import * as Persona from '../../../dist/Participant/Persona/Surface.js'

const hash = (value) => `H(${value})`

const canonicalIdentityOf = (agent) => {
  const resolved = Persona.resolveParticipantIdentityAtRoot(agent)
  assert.equal(resolved.ok, true, resolved.ok ? '' : resolved.error)
  return {
    participant: resolved.identity.name,
    role: resolved.identity.role,
    persona: resolved.identity.persona,
    personaCatalogVersion: resolved.identity.catalogVersion,
    origin: resolved.identity.origin,
  }
}

const rootSelection = (agent) => ({
  kind: 'RootSelection',
  ownerSession: null,
  ownerLogicalRun: null,
  ownerAuthorityRoot: null,
  participantIdentity: canonicalIdentityOf(agent),
})

const rootProfile = (agent, session = `ses_${agent}`) => {
  const result = Runtime.createAuthorityRoot(
    hash,
    'runtime-participant-identity-consumers',
    session,
    'HumanRoot',
    `msg_${agent}`,
    rootSelection(agent),
  )
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.value
}

const attemptPlan = (role, kind = 'work-main') =>
  Attempt.attemptPlan({ role, kind, noCandidateReason: 'NoCoverage' })

const inheritedSeed = (childAgent, owner) => {
  const issued = Runtime.issueInheritedIdentitySeed(childAgent, owner)
  assert.equal(issued.ok, true, issued.ok ? '' : issued.error)
  return issued.value
}

const personaVersion = (identity) => ({
  persona: identity.persona,
  personaCatalogVersion: identity.personaCatalogVersion,
})

const assertCanonicalIdentity = (actual, expected, label) => {
  assert.equal(actual.participant, expected.participant, `${label} participant`)
  assert.equal(actual.role, expected.role, `${label} role`)
  assert.equal(actual.persona, expected.persona, `${label} persona`)
  assert.equal(actual.personaCatalogVersion, expected.personaCatalogVersion, `${label} version`)
  assert.equal(actual.origin, expected.origin, `${label} origin`)
}

const assertNoLegacyIdentityFields = (value, label) => {
  const text = JSON.stringify(value)
  for (const token of ['PeerAgent', 'peerAgent', 'eerAgent', 'EffectiveAgent', 'effectiveAgent', 'ffectiveAgent', 'cursor', 'Cursor']) {
    assert.equal(text.includes(token), false, `${label} must not contain ${token}: ${text}`)
  }
}

test('WHAT[participant-identity-004] terminal dispatch preserves the exact IdentitySeed', () => {
  const profile = rootProfile('devops')
  const promptKey = 'pk_identity_terminal_dispatch'
  const claim = Runtime.claimContinuation(
    promptKey,
    profile.session,
    'BusyAgentNudge',
    profile,
    'digest-identity-terminal-dispatch',
  )
  let projection = Runtime.registerAuthority(profile, Runtime.empty)
  projection = Runtime.registerClaim(claim, projection)
  projection = Runtime.acceptClaim(promptKey, 'msg_identity_terminal_dispatch', projection)

  assert.equal(projection.pendingClaims.length, 0)
  assert.equal(projection.acceptedDispatches.length, 1)
  assert.deepEqual(projection.acceptedDispatches[0].identitySeed, profile.identitySeed)
})


test('WHAT[participant-identity-004] inherited Engineer seed retains owner Persona version and logical run', () => {
  const owner = rootProfile('engineer', 'ses_identity_fission_owner')
  const laneIdentity = inheritedSeed('engineer', owner)

  assert.equal(laneIdentity.participantIdentity.participant, 'engineer')
  assert.equal(laneIdentity.participantIdentity.role, owner.participantIdentity.role)
  assert.deepEqual(personaVersion(laneIdentity.participantIdentity), personaVersion(owner.participantIdentity))
  assert.equal(laneIdentity.ownerLogicalRun, owner.logicalRun)
})

test('WHAT[participant-identity-004] raw legacy PeerAgent/EffectiveAgent/cursor fields are ignored and never re-encoded', () => {
  const canonical = canonicalIdentityOf('engineer')
  const legacySeed = {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      ...canonical,
      peerAgent: 'engineer',
      effectiveAgent: 'reviewer',
      effectiveAgent: 'coder',
      EffectiveAgent: 'coder',
    },
    cursor: { offset: 2, failures: 2 },
    effectiveAgent: 'coder',
  }

  const profile = rootProfile('engineer', 'ses_identity_legacy_drop')
  const accepted = Runtime.createAuthorityRoot(
    hash,
    'runtime-participant-identity-consumers',
    'ses_identity_legacy_drop',
    'HumanRoot',
    'msg_identity_legacy_drop',
    legacySeed,
  )
  assert.equal(accepted.ok, true, accepted.ok ? '' : accepted.error)
  assertCanonicalIdentity(accepted.value.participantIdentity, canonical, 'legacy-dropped identity')
  assertNoLegacyIdentityFields(accepted.value, 'accepted legacy-dropped profile')
  assert.deepEqual(accepted.value.participantIdentity, profile.participantIdentity)

  const plan = attemptPlan('engineer', 'work-main')
  assertNoLegacyIdentityFields(plan, 'attemptPlan output')
})
