import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { assertJsData } = await import("../../verification-system/tests/support/js-contract.mjs");

const identity = await import('../../../dist/Participant/Persona/Surface.js')
const EXPECTED_ROLES = [
  'manager',
  'orchestrator',
  'engineer',
  'devops',
  'blogger',
  'plan',
]

test('WHAT[participant-identity-001] catalog_has_canonical_roles', () => {
  assertJsData(identity.allRoleLabels, 'allRoleLabels')
  assert.deepEqual([...identity.allRoleLabels].sort(), [...EXPECTED_ROLES].sort())
  assert.equal(identity.allRoleLabels.length, 6)
  assert.equal(identity.allPublicRoleLabels.length + identity.allInternalRoleLabels.length, 6)
  assert.deepEqual(
    [...identity.allPublicRoleLabels, ...identity.allInternalRoleLabels].sort(),
    [...EXPECTED_ROLES].sort(),
  )
})
test('WHAT[participant-identity-001] required_names_are_canonical_and_include_managed_agents', () => {
  assertJsData(identity.requiredNames, 'requiredNames')
  assert.equal(identity.requiredNames.length, 8)
  for (const role of EXPECTED_ROLES) {
    assert.equal(identity.isManagedName(role), true)
  }
  assert.equal(identity.isManagedName('bookkeeper'), true)
  assert.equal(identity.isManagedName('predictor'), true)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { mkdirSync, mkdtempSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { dirname, join } = await import("node:path");
const { spawnSync } = await import("node:child_process");
const { fileURLToPath } = await import("node:url");
const { default: test } = await import("node:test");

const gate = fileURLToPath(new URL('../../../scripts/checks/participant-identity-boundary.mjs', import.meta.url))
const writeFixture = (root, relativePath, text) => {
  const path = join(root, relativePath)
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, text)
}
const writePassingBoundary = (root) => {
  writeFixture(
    root,
    'src/Wanxiangshu/Participant/Persona/Identity.fs',
    [
      'namespace Wanxiangshu.Participant.Persona',
      'type ParticipantIdentity = private { Value: string }',
      'type ParticipantIdentityEvidence = private { Identity: ParticipantIdentity }',
      'module ParticipantIdentity =',
      '    let resolveAtRoot value = value',
      '    let inheritFromOwner value owner = value, owner',
      '    let rehydrate owner input = owner, input',
      '    let selectedAgent value = value',
      '    let role value = value',
      '    let persona value = value',
      '    let personaCatalogVersion value = value',
      '    let origin value = value',
    ].join('\n'),
  )
  writeFixture(
    root,
    'src/Wanxiangshu/Interaction/Authority/Facts.fs',
    [
      'namespace Wanxiangshu.Interaction.Authority',
      'type AuthorityRootAcceptedPayload =',
      '    { SchemaVersion: int',
      '      IdentitySeed: PromptIdentitySeed }',
    ].join('\n'),
  )
  writeFixture(
    root,
    'src/Wanxiangshu/Interaction/Authority/Model.fs',
    [
      'namespace Wanxiangshu.Interaction.Authority',
      'type IdentitySeed = PromptIdentitySeed',
      'type AuthorityExecutionProfile =',
      '    private',
      '        { StoredIdentitySeed: IdentitySeed }',
      '    member this.ParticipantIdentity = PromptIdentitySeed.participantIdentity this.StoredIdentitySeed',
    ].join('\n'),
  )
}

test('WHAT[participant-identity-001] rejects SessionId keyed identity cache', () => {
  const root = mkdtempSync(join(tmpdir(), 'participant-identity-boundary-'))

  try {
    writePassingBoundary(root)
    writeFixture(
      root,
      'src/Wanxiangshu/Interaction/Dispatch/IdentityCache.fs',
      [
        'namespace Wanxiangshu.Interaction.Dispatch',
        'open System.Collections.Generic',
        'open Wanxiangshu.Foundation.Identity',
        'let identities = Dictionary<SessionId, ParticipantIdentityEvidence>()',
      ].join('\n'),
    )

    const result = spawnSync(process.execPath, [gate], {
      cwd: root,
      encoding: 'utf8',
    })

    assert.equal(result.error, undefined, result.error?.message)
    assert.equal(result.status, 1, `stdout:\n${result.stdout}\nstderr:\n${result.stderr}`)
    assert.match(result.stderr, /src\/Wanxiangshu\/Interaction\/Dispatch\/IdentityCache\.fs:4/)
    assert.match(result.stderr, /\[session-identity-cache\].*SessionId-keyed ParticipantIdentity\/IdentitySeed collection is forbidden/)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
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
  blogger: { role: 'blogger', persona: 'Chronicler' },
  plan: { role: 'plan', persona: 'Planner' },
  bookkeeper: { role: 'bookkeeper', persona: 'Curator' },
  predictor: { role: 'engineer', persona: 'Engineer' },
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

test('WHAT[participant-identity-001] resolves every canonical participant identity and persona', () => {
  assert.deepEqual(new Set(identity.requiredNames), new Set(Object.keys(EXPECTED)))

  for (const name of identity.requiredNames) {
    const result = identity.resolveParticipantIdentityAtRoot(name)
    assertJsData(result, name)
    assert.equal(result.ok, true, name)
    assert.equal(result.error, null, name)
    assertCanonicalIdentity(result.identity, expectedView(name), name)

    const restored = rehydrate(result.identity)
    assertJsData(restored, `${name} rehydration`)
    assert.equal(restored.ok, true, name)
    assertCanonicalIdentity(restored.identity, expectedView(name), `${name} rehydrated`)
  }
})
test('WHAT[participant-identity-001] rejects legacy, malformed, blank, and unknown participant names', () => {
  for (const name of identity.legacyNames) {
    assertError(identity.resolveParticipantIdentityAtRoot(name), 'LegacyParticipantName')
  }
  for (const name of ['fast_reviewer']) {
    assertError(identity.resolveParticipantIdentityAtRoot(name), 'LegacyParticipantName')
  }
  for (const name of ['', '   ', null]) {
    assertError(identity.resolveParticipantIdentityAtRoot(name), 'BlankParticipantName')
  }
  for (const name of ['reviewer-fast', 'fast-', 'manager-fast-extra', 'coder-deep']) {
    assertError(identity.resolveParticipantIdentityAtRoot(name), 'MalformedParticipantName')
  }
  assertError(identity.resolveParticipantIdentityAtRoot('unknown'), 'UnknownParticipantName')
})
test('WHAT[participant-identity-001] rejects independently supplied role, persona, and origin', () => {
  const canonical = expectedView('engineer')
  const mismatches = [
    [{ ...canonical, role: 'devops' }, 'RoleMismatch'],
    [{ ...canonical, persona: 'Lead' }, 'PersonaMismatch'],
    [{ ...canonical }, 'OriginMismatch'],
  ]

  for (const [input, error] of mismatches) {
    assertError(rehydrate(input, error === 'OriginMismatch' ? 'engineer' : ''), error)
  }
})
}
