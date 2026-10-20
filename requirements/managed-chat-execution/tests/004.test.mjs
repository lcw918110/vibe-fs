import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const chatExecution = await import("../../../dist/Execution/Session/ChatExecution/Surface.js");
const dispatch = await import("../../../dist/Interaction/Dispatch/DispatchSurface.js");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");

const key = {
  sessionId: 'ses-acceptance',
  physicalUserMessageId: 'msg-acceptance',
}
const evidence = (overrides = {}) => ({
  sessionId: key.sessionId,
  physicalUserMessageId: key.physicalUserMessageId,
  logicalRunId: 'run-acceptance',
  authorityRootUserMessageId: 'root-acceptance',
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
  origin: 'HumanRoot',
  ...overrides,
})
const accepted = (attempt = evidence(), appendOutcome = 'Committed') =>
  chatExecution.acceptanceScenario(attempt, appendOutcome)
const withJournal = async (label, action) => {
  const directory = mkdtempSync(join(tmpdir(), `wxs-chat-acceptance-${label}-`))
  const opened = await journal.JournalSurface_bootWithWriterId(
    directory,
    `writer-${label}`,
    `runtime-${label}`,
    4242,
    '2026-08-30T00:00:00Z',
  )
  assert.equal(opened.ok, true, JSON.stringify(opened.error))

  try {
    await action(opened.journal)
  } finally {
    journal.JournalSurface_dispose(opened.journal)
    rmSync(directory, { recursive: true, force: true })
  }
}
const rootIdentity = {
  kind: 'RootSelection',
  ownerSession: null,
  ownerLogicalRun: null,
  ownerAuthorityRoot: null,
  participantIdentity: {
    selectedAgent: 'manager',
    canonicalRole: 'manager',
    selectedTier: 'deep',
    persona: 'Lead',
    personaCatalogVersion: 1,
    origin: 'ResolvedAtRoot',
  },
}
const hostPort = (sendPrompt) => ({
  SubscribeTerminal: () => ({ Dispose: () => {} }),
  SendPrompt: sendPrompt,
})

test('WHAT[managed-chat-execution-004] durable acceptance is projected before its witness exists', async () => {
  const result = await accepted()

  assert.equal(result.ok, true, JSON.stringify(result.error))
  assert.deepEqual(result.trace, ['Read', 'Append', 'Committed', 'ReRead', 'Witness'])
  assert.deepEqual(result.witness.key, key)
  assert.equal(result.witness.evidence.participant, 'engineer')
  assert.equal(result.witness.evidence.role, 'engineer')
  assert.equal('effectiveAgent' in result.witness.evidence, false, 'witness carries no EffectiveAgent')
  assert.deepEqual(result.witness.evidence.identitySeed.participantIdentity, {
    origin: 'ResolvedAtRoot',
    participant: 'engineer',
    persona: 'Engineer',
    personaCatalogVersion: 1,
    role: 'engineer',
  })
  assert.equal(result.acceptanceAppendCount, 1)
  assert.equal(result.capacityEffectCount, 0)
  assert.equal(result.hostEffectCount, 0)
})
test('WHAT[managed-chat-execution-004] exact duplicate reconstructs an equivalent witness without another append', async () => {
  const result = await chatExecution.acceptanceDuplicateScenario(evidence())

  assert.equal(result.ok, true, JSON.stringify(result.error))
  assert.deepEqual(result.firstWitness, result.secondWitness)
  assert.equal(result.acceptanceAppendCount, 1)
  assert.deepEqual(result.secondTrace, ['Read', 'Witness'])
})
test('WHAT[managed-chat-execution-004] established evidence conflict is typed and appends nothing', async () => {
  const result = await chatExecution.acceptanceConflictScenario(
    evidence(),
    evidence({ logicalRunId: 'run-other' }),
  )

  assert.equal(result.ok, false)
  assert.equal(result.error.kind, 'EstablishedEvidenceConflict')
  assert.equal(result.acceptanceAppendCount, 1)
  assert.equal(result.witness, null)
})
test('WHAT[managed-chat-execution-004] each uncertain persistence outcome acquires no capacity', async () => {
  for (const outcome of ['NotAttempted', 'CommitUnknown']) {
    const result = await accepted(evidence(), outcome)

    assert.equal(result.ok, false)
    assert.equal(result.error.kind, outcome)
    assert.equal(result.witness, null)
    assert.equal(result.capacityEffectCount, 0)
    assert.equal(result.hostEffectCount, 0)
    assert.equal(result.trace.includes('Witness'), false)
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const chatExecution = await import("../../../dist/Execution/Session/ChatExecution/Surface.js");

const durableKey = {
  sessionId: 'ses-admission',
  physicalUserMessageId: 'msg-admission',
}
const tagged = (name, value) => [name, value]
const keyWire = ({ sessionId, physicalUserMessageId }) => ({
  SessionId: tagged('SessionId', sessionId),
  PhysicalUserMessageId: tagged('PhysicalUserMessageId', physicalUserMessageId),
})
const factWire = (factCase, payload) => JSON.stringify(['Agent', ['ChatExecution', [factCase, payload]]])
const plainEvidence = (key = durableKey) => ({
  sessionId: key.sessionId,
  physicalUserMessageId: key.physicalUserMessageId,
  logicalRunId: 'run-admission',
  authorityRootUserMessageId: 'root-admission',
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
  origin: 'HumanRoot',
})
const evidenceWire = (evidence) => ({
  SessionId: tagged('SessionId', evidence.sessionId),
  LogicalRunId: tagged('LogicalRunId', evidence.logicalRunId),
  AuthorityRootUserMessageId: tagged(
    'AuthorityRootUserMessageId',
    evidence.authorityRootUserMessageId,
  ),
  AuthorityKind: evidence.authorityKind,
  IdentitySeed: [
    'RootSelection',
    {
      InitialTier: 'deep',
      Origin: 'ResolvedAtRoot',
      Persona: evidence.identitySeed.participantIdentity.persona,
      PersonaCatalogVersion: evidence.identitySeed.participantIdentity.personaCatalogVersion,
      Role: evidence.identitySeed.participantIdentity.canonicalRole,
      SelectedAgent: evidence.identitySeed.participantIdentity.selectedAgent,
    },
  ],
  PhysicalUserMessageId: tagged('PhysicalUserMessageId', evidence.physicalUserMessageId),
  Origin: ['AuthorityRoot', evidence.origin],
})
const acceptedWire = (evidence) =>
  factWire('Accepted', {
    SchemaVersion: 1,
    Key: keyWire(durableKey),
    Evidence: evidenceWire(evidence),
  })
const providerStartedEvidenceWire = () => ({
  Accepted: evidenceWire(durableEvidence),
  ProviderRun: tagged('ProviderRunIdentity', 'provider-admission'),
  RequestKind: 'WorkMain',
})
const startedWire = () =>
  factWire('ProviderStarted', {
    SchemaVersion: 1,
    Key: keyWire(durableKey),
    Evidence: providerStartedEvidenceWire(),
  })
const terminalWire = (disposition) =>
  factWire('Terminal', {
    SchemaVersion: 1,
    Key: keyWire(durableKey),
    Evidence: ['AfterProviderStart', providerStartedEvidenceWire()],
    Disposition: disposition,
  })
const durableEvidence = plainEvidence()
const states = [
  { label: 'None', facts: [], phase: 'None' },
  { label: 'Accepted', facts: [acceptedWire(durableEvidence)], phase: 'Accepted' },
  {
    label: 'ProviderStarted',
    facts: [acceptedWire(durableEvidence), startedWire()],
    phase: 'ProviderStarted',
  },
  ...['Completed', 'Cancelled', 'Rejected', 'Failed'].map((disposition) => ({
    label: `Terminal(${disposition})`,
    facts: [acceptedWire(durableEvidence), startedWire(), terminalWire(disposition)],
    phase: 'Terminal',
    disposition,
  })),
]
const byState = Object.fromEntries(states.map((state) => [state.label, state]))
const otherKey = {
  sessionId: 'ses-admission-other',
  physicalUserMessageId: 'msg-admission-other',
}
const exactMessage = { ...durableKey, explicitAgent: null }
const conflictEvidence = { ...durableEvidence, logicalRunId: 'run-conflicting' }
const invalidEvidence = { ...durableEvidence, logicalRunId: ' ' }
const cases = [
  {
    label: 'wrong state key cannot borrow a terminal result',
    facts: byState['Terminal(Completed)'].facts,
    message: { ...otherKey, explicitAgent: null },
    attemptedEvidence: plainEvidence(otherKey),
    expected: { error: 'StateKeyMismatch' },
  },
  {
    label: 'exact terminal ignores a malformed new attempt',
    facts: byState['Terminal(Completed)'].facts,
    message: exactMessage,
    attemptedEvidence: invalidEvidence,
    expected: { intent: 'AlreadyTerminal', disposition: 'Completed' },
  },
  {
    label: 'malformed evidence is rejected',
    facts: [],
    message: exactMessage,
    attemptedEvidence: invalidEvidence,
    expected: { error: 'AttemptEvidenceInvalid' },
  },
  {
    label: 'attempt key must match the physical message',
    facts: [],
    message: exactMessage,
    attemptedEvidence: plainEvidence(otherKey),
    expected: { error: 'AttemptKeyMismatch' },
  },
  {
    label: 'explicit agent must match accepted identity',
    facts: [],
    message: { ...durableKey, explicitAgent: 'other-coder' },
    attemptedEvidence: durableEvidence,
    expected: { error: 'ExplicitAgentMismatch' },
  },
  {
    label: 'existing acceptance rejects conflicting evidence',
    facts: byState.Accepted.facts,
    message: exactMessage,
    attemptedEvidence: conflictEvidence,
    expected: { error: 'ExistingEvidenceConflict' },
  },
  {
    label: 'fresh exact attempt needs durable acceptance',
    facts: byState.None.facts,
    message: exactMessage,
    attemptedEvidence: durableEvidence,
    expected: { intent: 'NeedAcceptance', evidence: durableEvidence },
  },
  {
    label: 'equal accepted evidence resumes pre-provider admission',
    facts: byState.Accepted.facts,
    message: exactMessage,
    attemptedEvidence: durableEvidence,
    expected: { intent: 'ResumeAccepted', evidence: durableEvidence },
  },
  {
    label: 'equal provider-started evidence is already started',
    facts: byState.ProviderStarted.facts,
    message: exactMessage,
    attemptedEvidence: durableEvidence,
    expected: { intent: 'AlreadyStarted', evidence: durableEvidence },
  },
  ...['Cancelled', 'Rejected', 'Failed'].map((disposition) => ({
    label: `terminal ${disposition} remains exact`,
    facts: byState[`Terminal(${disposition})`].facts,
    message: exactMessage,
    attemptedEvidence: durableEvidence,
    expected: { intent: 'AlreadyTerminal', disposition },
  })),
]

test('WHAT[managed-chat-execution-004] fixed admission counterworlds distinguish every intent and rejection', () => {

  const observedIntents = new Set()
  const observedErrors = new Set()

  for (const row of cases) {
    const result = chatExecution.admitIntent(row.facts, row.message, row.attemptedEvidence)

    assert.equal(result.ok, !row.expected.error, `${row.label}: success classification`)

    if (row.expected.error) {
      assert.equal(result.intent, null, `${row.label}: rejected decision has no intent`)
      assert.equal(result.error?.kind, row.expected.error, `${row.label}: typed admission error`)
      observedErrors.add(result.error?.kind)
      continue
    }

    assert.equal(result.error, null, `${row.label}: admitted decision has no error`)
    assert.equal(result.intent?.kind, row.expected.intent, `${row.label}: admission intent`)
    observedIntents.add(result.intent?.kind)

    if (row.expected.disposition) {
      assert.equal(
        result.intent?.disposition,
        row.expected.disposition,
        `${row.label}: terminal disposition`,
      )
    } else {
      assert.equal(result.intent?.evidence.participant, 'engineer', `${row.label}: intent participant`)
      assert.equal(result.intent?.evidence.role, 'engineer', `${row.label}: intent role`)
      assert.equal('effectiveAgent' in result.intent?.evidence, false, `${row.label}: intent carries no EffectiveAgent`)
      assert.deepEqual(
        result.intent?.evidence.identitySeed.participantIdentity,
        {
          origin: 'ResolvedAtRoot',
          participant: 'engineer',
          persona: 'Engineer',
          personaCatalogVersion: 1,
          role: 'engineer',
        },
        `${row.label}: intent identity`,
      )
    }
  }

  assert.deepEqual(
    [...observedIntents].sort(),
    ['AlreadyStarted', 'AlreadyTerminal', 'NeedAcceptance', 'ResumeAccepted'],
    'fixed counterworlds must assert every admission intent',
  )
  assert.deepEqual(
    [...observedErrors].sort(),
    [
      'AttemptEvidenceInvalid',
      'AttemptKeyMismatch',
      'ExistingEvidenceConflict',
      'ExplicitAgentMismatch',
      'StateKeyMismatch',
    ],
    'fixed counterworlds must assert every typed admission error',
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const transaction = await import("../../../dist/OpenCode/Host/ChatAdmission/TransactionSurface.js");

const evidence = {
  sessionId: 'ses-transaction',
  physicalUserMessageId: 'msg-transaction',
  logicalRunId: 'run-transaction',
  authorityRootUserMessageId: 'root-transaction',
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
  providerRun: 'provider-transaction',
  origin: 'HumanRoot',
  requestKind: 'work-main',
}
const run = (failurePoint = 'None', state = 'None') =>
  transaction.transactionScenario(evidence, failurePoint, state)

test('WHAT[managed-chat-execution-004] accepted replay reuses acceptance without another append', async () => {
  const result = await run('None', 'Accepted')

  assert.equal(result.ok, true, JSON.stringify(result.error))
  assert.equal(result.outcome, 'Settled')
  assert.equal(result.acceptCount, 1)
  assert.equal(result.appendCount, 0)
  assert.equal(result.acquireCount, 1)
  assert.equal(result.providerCount, 0)
})
}

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

test('WHAT[managed-chat-execution-004] identical Accepted replay is idempotent and conflicting evidence fails closed', () => {
  const accepted = acceptedWire('msg-replay')
  assert.deepEqual(mustFold([accepted, accepted]), mustFold([accepted]))
  assert.deepEqual(
    mustFold([accepted, startedWire('msg-replay'), accepted]),
    mustFold([accepted, startedWire('msg-replay')]),
  )

  const conflict = acceptedWire('msg-replay', { Evidence: { LogicalRunId: tagged('LogicalRunId', 'run-other') } })
  const rejected = fold([accepted, conflict])
  assert.equal(rejected.ok, false)
  assert.notEqual(rejected.error, '')

  const legacyOnly = acceptedWire('msg-replay')
  const parsed = JSON.parse(legacyOnly)
  parsed[1][1][1].Evidence.EffectiveAgent = 'reviewer'
  parsed[1][1][1].Evidence.IdentitySeed[1].PeerAgent = 'reviewer'
  const canonicalOf = (wire) => {
    const result = chatExecution.canonicalize(wire)
    assert.equal(result.ok, true, result.ok ? '' : result.error)
    return result.value
  }
  assert.equal(canonicalOf(JSON.stringify(parsed)), canonicalOf(legacyOnly), 'legacy agent fields cannot alter canonical participant')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const Runtime = await import("../../../dist/Execution/Session/ChatExecution/RecoveryRuntimeSurface.js");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const chatExecution = await import("../../../dist/Execution/Session/ChatExecution/Surface.js");
const recoveryHost = await import("../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js");

const withRecoveryHost = async (label, portOutcome, action) => {
  const directory = mkdtempSync(join(tmpdir(), `wxs-chat-recovery-${label}-`))
  const host = await recoveryHost.bootRecoveryHost(directory, portOutcome)

  try {
    await action(host)
  } finally {
    recoveryHost.disposeRecoveryHost(host)
    rmSync(directory, { recursive: true, force: true })
  }
}
const sessionOf = (suffix) => `ses-recovery-${suffix}`
const physicalOf = (suffix) => `msg-recovery-${suffix}`
const assertManualDisposition = (manual, sessionId, physicalId) => {
  assert.equal(manual.reason, 'NoAuthorizedProviderDisposition')
  assert.equal(manual.observation, 'ProviderAbsent')
  assert.equal(manual.lifecycle, 'Accepted')
  assert.equal(manual.sessionId, sessionId)
  assert.equal(manual.physicalUserMessageId, physicalId)
}
const tagged = (name, value) => [name, value]
const plainEvidence = (sessionId, physicalUserMessageId) => ({
  sessionId,
  physicalUserMessageId,
  logicalRunId: `run-${sessionId}`,
  authorityRootUserMessageId: `root-${sessionId}`,
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
  origin: 'HumanRoot',
})
const evidenceWire = (evidence) => ({
  SessionId: tagged('SessionId', evidence.sessionId),
  LogicalRunId: tagged('LogicalRunId', evidence.logicalRunId),
  AuthorityRootUserMessageId: tagged('AuthorityRootUserMessageId', evidence.authorityRootUserMessageId),
  AuthorityKind: evidence.authorityKind,
  IdentitySeed: [
    'RootSelection',
    {
      InitialTier: 'deep',
      Origin: 'ResolvedAtRoot',
      Persona: evidence.identitySeed.participantIdentity.persona,
      PersonaCatalogVersion: evidence.identitySeed.participantIdentity.personaCatalogVersion,
      Role: evidence.identitySeed.participantIdentity.canonicalRole,
      SelectedAgent: evidence.identitySeed.participantIdentity.selectedAgent,
    },
  ],
  PhysicalUserMessageId: tagged('PhysicalUserMessageId', evidence.physicalUserMessageId),
  Origin: ['AuthorityRoot', evidence.origin],
})
const acceptedWire = (evidence) =>
  JSON.stringify([
    'Agent',
    [
      'ChatExecution',
      [
        'Accepted',
        {
          SchemaVersion: 1,
          Key: {
            SessionId: tagged('SessionId', evidence.sessionId),
            PhysicalUserMessageId: tagged('PhysicalUserMessageId', evidence.physicalUserMessageId),
          },
          Evidence: evidenceWire(evidence),
        },
      ],
    ],
  ])

test('WHAT[managed-chat-execution-004] same admitted plan binds the same admission twice', () => {
  const evidence = plainEvidence('ses-r02-same', 'msg-r02-same')
  const facts = [acceptedWire(evidence)]
  const message = {
    sessionId: evidence.sessionId,
    physicalUserMessageId: evidence.physicalUserMessageId,
    explicitAgent: null,
  }

  const first = chatExecution.admitIntent(facts, message, evidence)
  const second = chatExecution.admitIntent(facts, message, evidence)

  assert.equal(first.ok, true, JSON.stringify(first.error))
  assert.equal(second.ok, true, JSON.stringify(second.error))
  assert.equal(first.intent?.kind, 'ResumeAccepted')
  assert.deepEqual(first.intent?.evidence, second.intent?.evidence)
})
test('WHAT[managed-chat-execution-004] conflicting plan against the same key fails closed', () => {
  const evidence = plainEvidence('ses-r02-conflict', 'msg-r02-conflict')
  const facts = [acceptedWire(evidence)]
  const message = {
    sessionId: evidence.sessionId,
    physicalUserMessageId: evidence.physicalUserMessageId,
    explicitAgent: null,
  }

  const result = chatExecution.admitIntent(facts, message, {
    ...evidence,
    logicalRunId: 'run-r02-other',
  })

  assert.equal(result.ok, false)
  assert.equal(result.intent, null)
  assert.equal(result.error?.kind, 'ExistingEvidenceConflict')
})
}
