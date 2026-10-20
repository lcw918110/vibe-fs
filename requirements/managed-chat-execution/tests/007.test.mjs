import test from 'node:test'

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

test('WHAT[managed-chat-execution-007] every acquired pre-commit failure releases exactly once', async () => {
  const expectations = new Map([
    ['LeaseTarget', 'LeaseTargetFailed'],
    ['BindExecution', 'HostProjectionFailed'],
    ['ProjectHost', 'HostProjectionFailed'],
    ['CommitLease', 'LeaseCommitFailed'],
  ])

  for (const [failurePoint, errorKind] of expectations) {
    const result = await run(failurePoint)

    assert.equal(result.ok, false)
    assert.equal(result.error.kind, errorKind)
    assert.equal(result.releaseCount, 1)
    assert.equal(result.commitCount, failurePoint === 'CommitLease' ? 1 : 0)
    assert.equal(result.providerCount, 0)
    assert.equal(result.trace.at(-1), 'ReleaseBeforeProvider')
    assert.ok(result.trace.indexOf('TerminalizeAccepted') < result.trace.indexOf('ReleaseBeforeProvider'))

    if (failurePoint === 'ProjectHost') {
      assert.equal(result.commitCount, 0)
    }
  }
})
test('WHAT[managed-chat-execution-007] release boundary failure is typed without a second release', async () => {
  const result = await run('ReleaseBeforeProvider')

  assert.equal(result.ok, false)
  assert.equal(result.error.kind, 'HostProjectionFailed')
  assert.equal(result.error.release, 'BoundaryFailed')
  assert.equal(result.releaseCount, 1)
  assert.equal(result.hostCount, 1)
  assert.equal(result.commitCount, 0)
  assert.equal(result.providerCount, 0)
})
test('WHAT[managed-chat-execution-007] failed supersession settlement terminalizes the accepted successor before releasing its admission', async () => {
  const result = await run('HandoffRejected')
  assert.equal(result.ok, false)
  assert.equal(result.error.kind, 'LeaseHandoffFailed')
  assert.equal(result.error.cause, 'superseded settlement failed')
  assert.equal(result.error.settlement, 'TerminalCommitted')
  assert.equal(result.error.release, 'Applied')
  assert.equal(result.durableLifecycle, 'Terminal')
  assert.equal(result.terminalDisposition, 'Failed')
  assert.equal(result.releaseCount, 1)
  assert.equal(result.admission.activeCapacity, 0)
  assert.equal(result.commitCount, 0)
  assert.equal(result.providerCount, 0)
  assert.ok(result.trace.indexOf('TerminalizeAccepted') < result.trace.indexOf('ReleaseBeforeProvider'))
  assert.equal(result.trace.filter(value => value === 'TerminalizeAccepted').length, 1)
})
test('WHAT[managed-chat-execution-007] failed supersession settlement preserves ownership while successor settlement is unknown or unavailable', async () => {
  for (const failurePoint of ['HandoffTerminalUnknown', 'HandoffTerminalUnavailable']) {
    const result = await run(failurePoint)
    assert.equal(result.ok, false)
    assert.equal(result.error.kind, 'LeaseHandoffFailed')
    assert.equal(result.error.cause, 'superseded settlement failed')
    assert.equal(result.error.settlement, 'SettlementIncomplete')
    assert.equal(result.durableLifecycle, 'Accepted')
    assert.equal(result.terminalDisposition, null)
    assert.equal(result.releaseCount, 0)
    assert.equal(result.admission.activeCapacity, 1)
    assert.equal(result.commitCount, 0)
    assert.equal(result.providerCount, 0)
    assert.equal(result.trace.filter(value => value === 'TerminalizeAccepted').length, 1)
    assert.equal(result.trace.includes('ReleaseBeforeProvider'), false)
  }
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

test('WHAT[managed-chat-execution-007] pre-provider failure cancellation and rejection settle without a provider run', () => {
  for (const disposition of ['Cancelled', 'Rejected', 'Failed']) {
    const messageId = `msg-pre-provider-${disposition.toLowerCase()}`
    const projection = mustFold([
      acceptedWire(messageId),
      preProviderTerminalWire(messageId, disposition),
    ])

    assert.deepEqual(
      { phase: projection[0].phase, disposition: projection[0].disposition },
      { phase: 'Terminal', disposition },
    )
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const transaction = await import("../../../dist/OpenCode/Host/ChatAdmission/TransactionSurface.js");
const executionStatus = await import("../../../dist/Execution/Session/ChatExecution/StatusSurface.js");

const evidence = (suffix, overrides = {}) => ({
  sessionId: `ses-pre-provider-${suffix}`,
  physicalUserMessageId: `msg-pre-provider-${suffix}`,
  logicalRunId: `run-pre-provider-${suffix}`,
  authorityRootUserMessageId: `root-pre-provider-${suffix}`,
  providerRun: `provider-pre-provider-${suffix}`,
  identitySeed: {
    participantIdentity: {
      selectedAgent: 'engineer',
    },
  },
  ...overrides,
})
const cases = [
  ['IdentityConflict', 'Rejected'],
  ['ProjectionError', 'Failed'],
  ['ExecutionBindingError', 'Failed'],
  ['FatalMembraneInput', 'Failed'],
  ['Supersession', 'Cancelled'],
  ['PluginReplay', 'Rejected'],
]

test('WHAT[managed-chat-execution-007] each typed pre-provider failure settles the exact accepted execution', async () => {
  for (const [failure, disposition] of cases) {
    const result = await transaction.preProviderSettlementScenario(evidence(failure), failure, 'Exact')

    const projected = executionStatus.queryFacts(
      result.facts,
      result.key.sessionId,
      result.key.physicalUserMessageId,
    )

    assert.equal(projected.ok, true)
    assert.deepEqual(projected.status, {
      accepted: true,
      providerStarted: false,
      terminal: true,
      disposition,
    })
    assert.equal(result.acceptedFactCount, 1)
    assert.equal(result.providerEffectCount, 0)
    assert.deepEqual(result.admission, { activeCapacity: 0 })
    assert.equal(result.failure.kind, failure)
    assert.ok(['Recoverable', 'Permanent'].includes(result.failure.classification))
  }
})
test('WHAT[managed-chat-execution-007] rejects hostile legacy AGENT-028 membrane input before it can enter a legal managed flow', async () => {
  // effectiveAgent is not a current builder field: it is injected here solely as
  // raw hostile legacy evidence and must fail closed with zero admission effects.
  const result = await transaction.preProviderSettlementScenario(
    evidence('raw-agent', { effectiveAgent: 'AGENT-028' }),
    'FatalMembraneInput',
    'Exact',
  )
  const projected = executionStatus.queryFacts(
    result.facts,
    result.key.sessionId,
    result.key.physicalUserMessageId,
  )

  assert.equal(projected.ok, true)
  assert.equal(projected.status.disposition, 'Rejected')
  assert.equal(result.admission.activeCapacity, 0)
  assert.equal(result.admission.activeCapacity, 0)
  assert.equal(result.providerEffectCount, 0)
})
test('WHAT[managed-chat-execution-007] detects missing exact pre-provider release', async () => {
  const settled = await transaction.preProviderSettlementScenario(
    evidence('release-control'),
    'ProjectionError',
    'Exact',
  )
  const mutated = await transaction.preProviderSettlementScenario(
    evidence('release-mutation'),
    'ProjectionError',
    'SkipExactRelease',
  )

  assert.equal(settled.admission.activeCapacity, 0)
  assert.equal(mutated.admission.activeCapacity, 1)
  assert.notDeepEqual(mutated.admission, settled.admission)
})
}
