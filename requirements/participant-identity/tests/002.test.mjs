import test from 'node:test'

{
const { default: assert } = await import('node:assert/strict')
const Temporal = await import('../../../dist/Verification/TemporalSurface.js')

test('WHAT[participant-identity-002] durable provider failure fold preserves the exact IdentitySeed', () => {
  const session = 'ses-identity-fold'
  const scope = {
    SessionId: session,
    LogicalRunId: 'run-identity-fold',
    AuthorityRootUserMessageId: 'root-identity-fold',
  }
  const seed = {
    Kind: 'RootSelection',
    OwnerSessionId: null,
    OwnerLogicalRunId: null,
    OwnerAuthorityRootUserMessageId: null,
    ParticipantIdentity: {
      participant: 'engineer', role: 'engineer', Persona: 'Engineer',
      PersonaCatalogVersion: 1, Origin: 'ResolvedAtRoot',
    },
  }
  const envelope = (seq, family, caseName, payload, run = null) => ({
    id: `identity-fold-${seq}`, seq, stream: { kind: 'Session', session }, run,
    fact: { family, case: caseName, payload },
  })
  const root = envelope(1, 'Prompt', 'AuthorityRootAccepted', {
    ...scope, SchemaVersion: 2, AuthorityKind: 'HumanRoot', IdentitySeed: seed,
  })
  const events = [root]
  for (const count of [1, 2]) {
    const providerRun = `provider-${count}`
    events.push(envelope(count + 1, 'ProviderFailure', 'FailureRecorded', {
      ...scope, ProviderRun: providerRun, ConsecutiveFailureCount: count, Reason: 'provider_error',
    }, providerRun))
    const folded = Temporal.fold(events)
    assert.equal(folded.ok, true, JSON.stringify(folded.error))
    const current = folded.value.sessions[session]
    assert.equal(current.providerFailures.failures, count)
    assert.equal(current.providerFailures.exhausted, false)
    assert.deepEqual(current.activeLogicalRun.identitySeed, seed)
    assert.deepEqual(current.activeLogicalRun.participantIdentity, seed.ParticipantIdentity)
    assert.equal(current.activeLogicalRun.logicalRun, scope.LogicalRunId)
    assert.equal(current.activeLogicalRun.authorityRoot, scope.AuthorityRootUserMessageId)
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { dirname, join } = await import("node:path");
const { fileURLToPath } = await import("node:url");
const { default: test } = await import("node:test");
const { scanEntries, scanRepo } = await import("../../../scripts/checks/participant-identity-boundary.mjs");

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '../../..')
const AUTHORITY_FACTS = 'src/Wanxiangshu/Interaction/Authority/Facts.fs'
const AUTHORITY_MODEL = 'src/Wanxiangshu/Interaction/Authority/Model.fs'
const violation = (file, line, rule, message) => ({ file, line, rule, message })
const authorityFacts = [
  'namespace Wanxiangshu.Interaction.Authority',
  'type AuthorityRootAcceptedPayload =',
  '    { SchemaVersion: int',
  '      IdentitySeed: PromptIdentitySeed }',
].join('\n')
const authorityModel = [
  'namespace Wanxiangshu.Interaction.Authority',
  'type IdentitySeed = PromptIdentitySeed',
  'type AuthorityExecutionProfile =',
  '    private',
  '        { StoredIdentitySeed: IdentitySeed }',
  '    member this.ParticipantIdentity = PromptIdentitySeed.participantIdentity this.StoredIdentitySeed',
].join('\n')

test('WHAT[participant-identity-002] the identity boundary entries are accepted for clean authority shape', () => {
  assert.deepEqual(scanEntries([
    { file: AUTHORITY_FACTS, text: authorityFacts },
    { file: AUTHORITY_MODEL, text: authorityModel },
  ]), [])
})
test('WHAT[participant-identity-002] AuthorityRootAcceptedPayload must store the IdentitySeed', () => {
  const seedless = authorityFacts.replace('      IdentitySeed: PromptIdentitySeed }', '      AcceptedAt: int }')
  assert.deepEqual(
    scanEntries([{ file: AUTHORITY_FACTS, text: seedless }]),
    [
      violation(
        AUTHORITY_FACTS,
        2,
        'authority-identity-seed',
        'AuthorityRootAcceptedPayload must store IdentitySeed',
      ),
    ],
  )
})
test('WHAT[participant-identity-002] AuthorityRootAcceptedPayload cannot flatten IdentitySeed fields', () => {
  const duplicated = authorityFacts.replace(
    '      IdentitySeed: PromptIdentitySeed }',
    '      IdentitySeed: PromptIdentitySeed\n      SelectedAgent: AgentId }',
  )
  assert.deepEqual(
    scanEntries([{ file: AUTHORITY_FACTS, text: duplicated }]),
    [
      violation(
        AUTHORITY_FACTS,
        2,
        'flat-identity-duplicate',
        'AuthorityRootAcceptedPayload duplicates IdentitySeed fields: SelectedAgent',
      ),
    ],
  )
})
test('WHAT[participant-identity-002] AuthorityExecutionProfile must derive identity from its seed', () => {
  const undivided = authorityModel.replace(
    '    member this.ParticipantIdentity = PromptIdentitySeed.participantIdentity this.StoredIdentitySeed',
    '    member this.SchemaVersion = 1',
  )
  assert.deepEqual(
    scanEntries([{ file: AUTHORITY_MODEL, text: undivided }]),
    [
      violation(
        AUTHORITY_MODEL,
        1,
        'authority-derived-identity',
        'AuthorityExecutionProfile must derive ParticipantIdentity from IdentitySeed',
      ),
    ],
  )
})
test('WHAT[participant-identity-002] a SessionId-keyed identity collection is forbidden', () => {
  const file = 'src/Wanxiangshu/Interaction/Dispatch/Cache.fs'
  const source = [
    'namespace Wanxiangshu.Interaction.Dispatch',
    'open System.Collections.Generic',
    'let issued = Dictionary<SessionId, ParticipantIdentityEvidence>()',
  ].join('\n')
  assert.deepEqual(
    scanEntries([{ file, text: source }]),
    [
      violation(
        file,
        3,
        'session-identity-cache',
        'SessionId-keyed ParticipantIdentity/IdentitySeed collection is forbidden',
      ),
    ],
  )
})
test('WHAT[participant-identity-002] a SessionId-keyed identity registry is forbidden', () => {
  const file = 'src/Wanxiangshu/Interaction/Dispatch/Registry.fs'
  const source = [
    'namespace Wanxiangshu.Interaction.Dispatch',
    'let identityRegistry (sessionId: SessionId) : ParticipantIdentity option = None',
  ].join('\n')
  assert.deepEqual(
    scanEntries([{ file, text: source }]),
    [
      violation(
        file,
        2,
        'session-identity-registry',
        'SessionId-keyed ParticipantIdentity/IdentitySeed registry is forbidden',
      ),
    ],
  )
})
test('WHAT[participant-identity-002] a second identity fact owner is forbidden', () => {
  const file = 'src/Wanxiangshu/Interaction/Authority/Second.fs'
  const source = [
    'namespace Wanxiangshu.Interaction.Authority',
    'let fact = ParticipantIdentityEstablished.name',
  ].join('\n')
  assert.deepEqual(
    scanEntries([{ file, text: source }]),
    [
      violation(
        file,
        2,
        'duplicate-identity-fact',
        'ParticipantIdentityEstablished would create a second identity fact owner',
      ),
    ],
  )
})
test('WHAT[participant-identity-002] the production participant identity boundary is clean', () => {
  assert.deepEqual(scanRepo(ROOT), [])
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const Attempt = await import("../../../dist/Context/Companion/CompressionSurface.js");
const ProviderFailure = await import("../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js");
const Dispatch = await import("../../../dist/Interaction/Dispatch/DispatchSurface.js");
const Fission = await import("../../../dist/Execution/Fission/Surface.js");
const Authority = await import("../../../dist/Interaction/Authority/Surface.js");
const Runtime = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");

const Persona = await import("../../../dist/Participant/Persona/Surface.js");

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

test('WHAT[participant-identity-002] ProviderAttempt carries its ParticipantIdentity as one nested value', () => {
  const attempt = attemptPlan('engineer', 'work-main')
  const expected = canonicalIdentityOf('engineer')

  assert.equal(attempt.participant, expected.participant)
  assert.equal(attempt.role, expected.role)
  assertCanonicalIdentity(attempt.participantIdentity, expected, 'attempt.participantIdentity')
  assertNoLegacyIdentityFields(attempt, 'attemptPlan')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { assertJsData } = await import("../../verification-system/tests/support/js-contract.mjs");

const identity = await import('../../../dist/Participant/Persona/Surface.js')
const EXPECTED = {
  orchestrator: { role: 'orchestrator', persona: 'Director' },
  manager: { role: 'manager', persona: 'Lead' },
  engineer: { role: 'engineer', persona: 'Engineer' },
  devops: { role: 'devops', persona: 'Operator' },
  bookkeeper: { role: 'bookkeeper', persona: 'Curator' },
}
const expectedView = (name, origin = 'ResolvedAtRoot') => ({
  name,
  role: EXPECTED[name].role,
  persona: EXPECTED[name].persona,
  catalogVersion: 1,
  origin,
})
const assertCanonicalIdentity = (actual, expected, label) => {
  assert.equal(actual.name, expected.name, `${label} participant`)
  assert.equal(actual.role, expected.role, `${label} role`)
  assert.equal(actual.persona, expected.persona, `${label} persona`)
  assert.equal(actual.catalogVersion, expected.catalogVersion, `${label} version`)
  assert.equal(actual.origin, expected.origin, `${label} origin`)
}
const rehydrate = (view, ownerName = '') =>
  identity.rehydrateParticipantIdentity(
    ownerName,
    view.name,
    view.role,
    'deep',
    'retired-peer-slot',
    view.persona,
    view.catalogVersion,
    view.origin,
  )
const assertError = (result, error) => {
  assertJsData(result, error)
  assert.equal(result.ok, false)
  assert.equal(result.identity, null)
  assert.equal(result.error, error)
}

test('WHAT[participant-identity-002] the retired peer slot is ignored and never affects identity', () => {
  const canonical = expectedView('engineer')
  const withLegacyPeer = identity.rehydrateParticipantIdentity(
    '',
    canonical.name,
    canonical.role,
    'fast',
    'some-other-peer',
    canonical.persona,
    canonical.catalogVersion,
    canonical.origin,
  )
  assert.equal(withLegacyPeer.ok, true)
  assertCanonicalIdentity(withLegacyPeer.identity, canonical, 'peer-slot-ignored')
})
}
