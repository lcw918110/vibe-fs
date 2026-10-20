import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const chatExecution = await import("../../../dist/Execution/Session/ChatExecution/Surface.js");
const recovery = await import("../../../dist/Execution/Session/ChatExecution/RecoveryRuntimeSurface.js");
const status = await import("../../../dist/Execution/Session/ChatExecution/StatusSurface.js");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const tagged = (name, value) => [name, value]
const keyWire = (sessionId, physicalUserMessageId) => ({
  SessionId: tagged('SessionId', sessionId),
  PhysicalUserMessageId: tagged('PhysicalUserMessageId', physicalUserMessageId),
})
const factWire = (factCase, payload) => JSON.stringify(['Agent', ['ChatExecution', [factCase, payload]]])
const acceptedWire = (physicalUserMessageId, overrides = {}) => {
  const sessionId = overrides.SessionId ?? 'ses-chat'
  const evidence = {
    SessionId: tagged('SessionId', sessionId),
    LogicalRunId: tagged('LogicalRunId', `run-${physicalUserMessageId}`),
    AuthorityRootUserMessageId: tagged('AuthorityRootUserMessageId', `root-${physicalUserMessageId}`),
    AuthorityKind: 'HumanRoot',
    IdentitySeed: [
      'RootSelection',
      {
        InitialTier: 'deep',
        Origin: 'ResolvedAtRoot',
        Persona: 'Engineer',
        PersonaCatalogVersion: 1,
        Role: 'engineer',
        SelectedAgent: 'engineer',
      },
    ],
    PhysicalUserMessageId: tagged('PhysicalUserMessageId', physicalUserMessageId),
    Origin: ['AuthorityRoot', 'HumanRoot'],
    ...overrides.Evidence,
  }

  return factWire('Accepted', {
    Evidence: evidence,
    Key: keyWire(sessionId, physicalUserMessageId),
    SchemaVersion: overrides.SchemaVersion ?? 1,
  })
}
const startedWire = (physicalUserMessageId, providerRun = `provider-${physicalUserMessageId}`, sessionId = 'ses-chat') =>
  factWire('ProviderStarted', {
    Evidence: {
      Accepted: JSON.parse(acceptedWire(physicalUserMessageId, { SessionId: sessionId }))[1][1][1].Evidence,
      ProviderRun: tagged('ProviderRunIdentity', providerRun),
      RequestKind: 'WorkMain',
    },
    Key: keyWire(sessionId, physicalUserMessageId),
    SchemaVersion: 1,
  })
const terminalWire = (physicalUserMessageId, disposition, sessionId = 'ses-chat') =>
  factWire('Terminal', {
    Disposition: disposition,
    Evidence: ['AfterProviderStart', {
      Accepted: JSON.parse(acceptedWire(physicalUserMessageId, { SessionId: sessionId }))[1][1][1].Evidence,
      ProviderRun: tagged('ProviderRunIdentity', `provider-${physicalUserMessageId}`),
      RequestKind: 'WorkMain',
    }],
    Key: keyWire(sessionId, physicalUserMessageId),
    SchemaVersion: 1,
  })
const preProviderTerminalWire = (physicalUserMessageId, disposition) =>
  factWire('Terminal', {
    Disposition: disposition,
    Evidence: ['PreProvider', JSON.parse(acceptedWire(physicalUserMessageId))[1][1][1].Evidence],
    Key: keyWire('ses-chat', physicalUserMessageId),
    SchemaVersion: 1,
  })
const canonical = (wire) => {
  const result = chatExecution.canonicalize(wire)
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.value
}
const fold = (wires) => chatExecution.fold(wires.map(canonical))
const mustFold = (wires) => {
  const result = fold(wires)
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.value
}
const phaseOf = (projection, physicalUserMessageId) =>
  projection.find((entry) => entry.physicalUserMessageId === physicalUserMessageId)

test('WHAT[managed-chat-execution-002] online prefix integration equals replay from the same canonical facts', () => {
  const history = [
    acceptedWire('msg-online-a'),
    startedWire('msg-online-a'),
    terminalWire('msg-online-a', 'Completed'),
    acceptedWire('msg-online-b'),
    startedWire('msg-online-b'),
    terminalWire('msg-online-b', 'Cancelled'),
  ]
  let online
  for (let length = 1; length <= history.length; length += 1) {
    online = mustFold(history.slice(0, length))
  }

  assert.deepEqual(online, mustFold(history))
})
}

{
const { default: assert } = await import("node:assert/strict");
const { readFileSync } = await import("node:fs");
const { default: test } = await import("node:test");
const chatExecution = await import("../../../dist/Execution/Session/ChatExecution/Surface.js");

const fixture = readFileSync(
  new URL('./fixtures/chat-execution-v1.json', import.meta.url),
  'utf8',
).trim()
const keyWire = {
  SessionId: ['SessionId', 'ses-chat-fixture'],
  PhysicalUserMessageId: ['PhysicalUserMessageId', 'msg-chat-fixture'],
}
const factWire = (factCase, payload) => JSON.stringify(['Agent', ['ChatExecution', [factCase, payload]]])
const acceptedEvidence = JSON.parse(fixture)[1][1][1].Evidence
const startedEvidence = {
  Accepted: acceptedEvidence,
  ProviderRun: ['ProviderRunIdentity', 'provider-chat-fixture'],
  RequestKind: 'WorkMain',
}
const started = factWire('ProviderStarted', {
  Evidence: startedEvidence,
  Key: keyWire,
  SchemaVersion: 1,
})
const terminal = factWire('Terminal', {
  Disposition: 'Completed',
  Evidence: ['AfterProviderStart', startedEvidence],
  Key: keyWire,
  SchemaVersion: 1,
})
const canonicalize = (wire) => {
  const result = chatExecution.canonicalize(wire)
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.value
}
const acceptedPayload = (wire) => wire[1][1][1]

test('WHAT[managed-chat-execution-002] schema v1 Accepted ProviderStarted and Terminal round-trip canonically', () => {
  const acceptedCanonical = canonicalize(fixture)
  assert.doesNotMatch(acceptedCanonical, /PeerAgent|EffectiveAgent/, 'canonical encoding drops raw v1 legacy agent fields')
  assert.equal(canonicalize(acceptedCanonical), acceptedCanonical, 'canonical bytes are a fixed point')

  const history = [acceptedCanonical, canonicalize(started), canonicalize(terminal)]
  for (const line of history) assert.equal(canonicalize(line), line)

  const replayed = chatExecution.fold(history)
  assert.equal(replayed.ok, true, replayed.ok ? '' : replayed.error)
  assert.deepEqual(replayed.value, [
    {
      sessionId: 'ses-chat-fixture',
      physicalUserMessageId: 'msg-chat-fixture',
      phase: 'Terminal',
      disposition: 'Completed',
      identity: {
        logicalRunId: 'run-chat-fixture',
        authorityRootUserMessageId: 'msg-chat-root',
        authorityKind: 'HumanRoot',
        identitySeed: {
          kind: 'RootSelection',
          ownerSession: null,
          ownerLogicalRun: null,
          ownerAuthorityRoot: null,
          participantIdentity: {
            origin: 'ResolvedAtRoot',
            participant: 'engineer',
            persona: 'Engineer',
            personaCatalogVersion: 1,
            role: 'engineer',
          },
        },
        providerRun: 'provider-chat-fixture',
        origin: 'HumanRoot',
        participant: 'engineer',
        role: 'engineer',
        requestKind: 'work-main',
      },
    },
  ])
})
test('WHAT[managed-chat-execution-002] unknown schema version fails closed during production fold', () => {
  const unknown = JSON.parse(fixture)
  acceptedPayload(unknown).SchemaVersion = 2
  const result = chatExecution.fold([JSON.stringify(unknown)])

  assert.equal(result.ok, false)
  assert.notEqual(result.error, '')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const chatExecution = await import("../../../dist/Execution/Session/ChatExecution/Surface.js");

const evidence = (overrides = {}) => ({
  sessionId: 'ses-provider-lifecycle',
  physicalUserMessageId: 'msg-provider-lifecycle',
  logicalRunId: 'run-provider-lifecycle',
  authorityRootUserMessageId: 'root-provider-lifecycle',
  authorityKind: 'HumanRoot',
  identitySeed: {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      selectedAgent: 'engineer',
      canonicalRole: 'engineer',
      selectedTier: 'deep',
      persona: 'Engineer',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  },
  providerRun: 'provider-provider-lifecycle',
  origin: 'HumanRoot',
  requestKind: 'work-main',
  ...overrides,
})
const accept = (attempt = evidence(), appendOutcome = 'Committed') => ({
  kind: 'Accept',
  evidence: attempt,
  appendOutcome,
})
const start = (attempt = evidence(), appendOutcome = 'Committed') => ({
  kind: 'ProviderStarted',
  evidence: attempt,
  appendOutcome,
})
const terminal = (disposition, attempt = evidence(), appendOutcome = 'Committed') => ({
  kind: 'Terminal',
  disposition,
  evidence: attempt,
  appendOutcome,
})
const run = (...actions) => chatExecution.providerLifecycleScenario(actions)

test('WHAT[managed-chat-execution-002] writes ProviderStarted before provider work', async () => {
  const result = await run(accept(), start(), { kind: 'ProviderWork' })

  assert.equal(result.ok, true, JSON.stringify(result.error))
  assert.deepEqual(result.trace.slice(-5), [
    'Read',
    'AppendProviderStarted',
    'Committed',
    'ReRead',
    'ProviderStartedWitness',
  ])
  assert.equal(result.providerWorkCount, 1)
  assert.equal(result.projection.phase, 'ProviderStarted')
})
test('WHAT[managed-chat-execution-002] each uncertain ProviderStarted append leaves projection accepted', async () => {
  for (const outcome of ['NotAttempted', 'CommitUnknown']) {
    const result = await run(accept(), start(evidence(), outcome))
    assert.equal(result.ok, false)
    assert.equal(result.error.kind, outcome)
    assert.equal(result.projection.phase, 'Accepted')
  }
})
}
