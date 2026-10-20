import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const policy = await import("../../../dist/Execution/Failure/Surface.js");
const recovery = await import("../../../dist/Execution/Session/ChatExecution/RecoveryRuntimeSurface.js");
const hostSignals = await import("../../../dist/OpenCode/Host/HostSignalSurface.js");
const hooks = await import("../../../dist/OpenCode/Host/PluginHooksSurface.js");
const transaction = await import("../../../dist/OpenCode/Host/ChatAdmission/TransactionSurface.js");

const executionKey = {
  sessionId: 'ses-cancel-retry-stream',
  physicalUserMessageId: 'msg-cancel-retry-stream',
}
const capacityFence = { reference: 'fence-cancel-retry-stream' }
const provider = {
  logicalRun: 'logical-cancel-retry-stream',
  providerRun: 'provider-cancel-retry-stream',
  requestKind: 'WorkMain',
  retryBudget: 'Available',
  breaker: 'Closed',
}
const input = (failure, change = {}) => ({
  failure,
  phase: 'ProviderStarted',
  executionKey,
  capacityFence,
  provider,
  ...change,
})
const decide = (failure, change) => policy.decide(input(failure, change))
const terminal = ({ finish, error, providerRun = provider.providerRun } = {}) => ({
  type: 'message.updated',
  properties: {
    info: {
      sessionID: executionKey.sessionId,
      id: providerRun,
      role: 'assistant',
      parentID: executionKey.physicalUserMessageId,
      time: { created: 1, completed: 2 },
      ...(finish === undefined ? {} : { finish }),
      ...(error === undefined ? {} : { error }),
    },
  },
})
const transactionEvidence = (suffix) => ({
  sessionId: `ses-policy-${suffix}`,
  physicalUserMessageId: `msg-policy-${suffix}`,
  logicalRunId: `logical-policy-${suffix}`,
  authorityRootUserMessageId: `root-policy-${suffix}`,
  providerRun: `provider-policy-${suffix}`,
  identitySeed: { participantIdentity: { selectedAgent: 'engineer' } },
})
const assertNoRecovery = (decision) => {
  assert.equal(decision.authorization, null)
  assert.notEqual(decision.resolution, 'RetryFreshAttempt')
}
const assertSingleRecovery = (decision, expected) => {
  assert.equal(decision.resolution, expected)
  assert.ok(decision.authorization)
}

test('WHAT[execution-failure-policy-002] cancel/retry/stream matrix is interpreted by registered owners', async () => {
  const cancelled = decide('UserCancelled')
  assertNoRecovery(cancelled)
  assert.equal(cancelled.resolution, 'TerminalizeProviderStarted')
  assert.equal(cancelled.terminalDisposition, 'Cancelled')
  assert.deepEqual(cancelled.capacitySettlement, {
    kind: 'ReleaseExactFence',
    fenceReference: capacityFence.reference,
  })
  assert.equal(cancelled.fatality.kind, 'NoFatality')
  assert.deepEqual(hostSignals.tryDecodeExactProviderTerminal(terminal({ error: { name: 'AbortError' } })), {
    sessionId: executionKey.sessionId,
    physicalUserMessageId: executionKey.physicalUserMessageId,
    providerRun: provider.providerRun,
    outcome: 'Cancelled',
    failure: 'UserCancelled',
    disposition: 'Cancelled',
  })
  const cancelledPromise = transaction.transactionScenario(
    transactionEvidence('cancelled'),
    'AcquireCancelled',
    'None',
  )
  assert.equal(typeof cancelledPromise.then, 'function')
  const cancelledAdmission = await cancelledPromise
  assert.equal(cancelledAdmission.outcome, 'Cancelled')
  assert.equal(cancelledAdmission.providerCount, 0)
  assert.equal(cancelledAdmission.admission.activeCapacity, 0)
  assert.equal(hooks.hookFailurePolicy('UserCancelled', 'ExactSettlementComplete'), 'RethrowUnchanged')

  const transient = decide('ProviderTransient')
  assertSingleRecovery(transient, 'RetryFreshAttempt')
  assert.equal(transient.terminalDisposition, null)
  assert.equal(transient.fatality.kind, 'NoFatality')
  assert.deepEqual(hostSignals.tryDecodeExactProviderTerminal(terminal({ error: { name: 'TimeoutError' } })), {
    sessionId: executionKey.sessionId,
    physicalUserMessageId: executionKey.physicalUserMessageId,
    providerRun: provider.providerRun,
    outcome: 'ProviderFailure',
    failure: 'ProviderTransient',
    disposition: '',
  })
  assert.deepEqual(
    await recovery.interpretFailurePolicy(
      'ProviderTransient',
      'Available',
      'NotCommitted',
      'ExactAbsent',
    ),
    { decision: 'Ignore', effects: [] },
  )

  const permanentRetry = decide('ProviderPermanent')
  assertSingleRecovery(permanentRetry, 'RetryFreshAttempt')
  assert.deepEqual(hostSignals.tryDecodeExactProviderTerminal(terminal({ error: { name: 'ProviderError' } })), {
    sessionId: executionKey.sessionId,
    physicalUserMessageId: executionKey.physicalUserMessageId,
    providerRun: provider.providerRun,
    outcome: 'ProviderFailure',
    failure: 'ProviderTransient',
    disposition: '',
  })
  assert.deepEqual(
    await recovery.interpretFailurePolicy(
      'ProviderPermanent',
      'Available',
      'NotCommitted',
      'ExactAbsent',
    ),
    { decision: 'Ignore', effects: [] },
  )
  const permanentTerminal = decide('ProviderPermanent', {
    provider: { ...provider, retryBudget: 'Exhausted' },
  })
  assertNoRecovery(permanentTerminal)
  assert.equal(permanentTerminal.resolution, 'TerminalizeProviderStarted')
  assert.equal(permanentTerminal.terminalDisposition, 'Failed')
  assert.deepEqual(
    await recovery.interpretFailurePolicy(
      'ProviderPermanent',
      'Exhausted',
      'NotCommitted',
      'ExactAbsent',
    ),
    { decision: 'Finalize', effects: ['Finalize:Failed'] },
  )

  assert.deepEqual(hostSignals.tryDecodeExactProviderTerminal(terminal({ finish: 'content-filter' })), {
    sessionId: executionKey.sessionId,
    physicalUserMessageId: executionKey.physicalUserMessageId,
    providerRun: provider.providerRun,
    outcome: 'ContentFiltered',
    failure: '',
    disposition: 'Completed',
  })
  assert.deepEqual(await recovery.recoverScenarios(['ProviderTerminalCompleted']), {
    decisions: ['Finalize'],
    effects: ['Finalize:Completed'],
  })

  const superseded = decide('Superseded')
  assertNoRecovery(superseded)
  assert.equal(superseded.resolution, 'TerminalizeProviderStarted')
  assert.equal(superseded.terminalDisposition, 'Cancelled')
  assert.deepEqual(superseded.executionKey, executionKey)
  assert.equal(superseded.capacitySettlement.fenceReference, capacityFence.reference)
  const newer = decide('Superseded', {
    executionKey: { sessionId: executionKey.sessionId, physicalUserMessageId: 'msg-newer' },
    capacityFence: { reference: 'fence-newer' },
  })
  assert.notEqual(superseded.capacitySettlement.fenceReference, newer.capacitySettlement.fenceReference)
  const supersededAdmission = await transaction.transactionScenario(
    transactionEvidence('superseded'),
    'AcquireSuperseded',
    'None',
  )
  assert.equal(supersededAdmission.outcome, 'Superseded')
  assert.equal(supersededAdmission.providerCount, 0)
  assert.equal(supersededAdmission.admission.activeCapacity, 0)

  // The policy algebra still closes this class (provider adapter / legacy
  // boundaries may mint it); the Host error boundary itself now reports every
  // error as a provider failure.
  const interrupted = decide('StreamInterruptedAfterFirstToken')
  assertNoRecovery(interrupted)
  assert.equal(interrupted.resolution, 'TerminalizeProviderStarted')
  assert.equal(interrupted.terminalDisposition, 'Failed')
  assert.deepEqual(hostSignals.tryDecodeExactProviderTerminal(terminal({ error: { name: 'StreamInterruptedError' } })), {
    sessionId: executionKey.sessionId,
    physicalUserMessageId: executionKey.physicalUserMessageId,
    providerRun: provider.providerRun,
    outcome: 'ProviderFailure',
    failure: 'ProviderTransient',
    disposition: '',
  })
  assert.deepEqual(
    await recovery.interpretFailurePolicy(
      'StreamInterruptedAfterFirstToken',
      'Available',
      'NotCommitted',
      'ExactAbsent',
    ),
    { decision: 'Finalize', effects: ['Finalize:Failed'] },
  )

  const queueFull = decide('CapacityQueueFull', { phase: 'AcceptedBeforeProvider' })
  assertNoRecovery(queueFull)
  assert.equal(queueFull.resolution, 'TerminalizeAcceptedPreProvider')
  assert.equal(queueFull.terminalDisposition, 'Failed')
  const fullAdmission = await transaction.transactionScenario(
    transactionEvidence('queue-full'),
    'AcquireQueueFull',
    'None',
  )
  assert.equal(fullAdmission.outcome, 'CapacityQueueFull')
  assert.equal(fullAdmission.providerCount, 0)
  assert.equal(fullAdmission.acquireCount, 1)
  assert.equal(fullAdmission.hostCount, 0)
  assert.equal(fullAdmission.commitCount, 0)
  assert.equal(fullAdmission.admission.activeCapacity, 0)

  const invariant = decide('LocalInvariant')
  assertNoRecovery(invariant)
  assert.equal(invariant.resolution, 'TerminalizeProviderStarted')
  assert.equal(invariant.terminalDisposition, 'Failed')
  assert.equal(invariant.capacitySettlement.kind, 'ReleaseExactFence')
  assert.equal(invariant.fatality.kind, 'FatalAfterSettlement')
  assert.deepEqual(
    await recovery.interpretFailurePolicy(
      'LocalInvariant',
      'Available',
      'NotCommitted',
      'ExactAbsent',
    ),
    { decision: 'Finalize', effects: ['Finalize:Failed'] },
  )
  assert.equal(hooks.hookFailurePolicy('LocalInvariant', 'SettlementIncomplete'), 'RejectFatalBeforeSettlement')
  const exactSettlement = await transaction.preProviderSettlementScenario(
    transactionEvidence('local-invariant'),
    'FatalMembraneInput',
    'Exact',
  )
  const fatal = hooks.hookFailurePolicy('LocalInvariant', 'ExactSettlementComplete')
  assert.deepEqual([...exactSettlement.trace, fatal].slice(-3), [
    'TerminalizeAccepted',
    'ReleaseBeforeProvider',
    'FatalAfterSettlement',
  ])
  assert.equal(exactSettlement.admission.activeCapacity, 0)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const policy = await import("../../../dist/Execution/Failure/Surface.js");

const executionKey = {
  sessionId: 'ses-failure-policy',
  physicalUserMessageId: 'msg-failure-policy',
}
const capacityFence = { reference: 'fence-failure-policy' }
const provider = {
  logicalRun: 'logical-failure-policy',
  providerRun: 'provider-failure-policy',
  requestKind: 'WorkMain',
  retryBudget: 'Available',
  breaker: 'Closed',
}
const baseInput = {
  failure: 'ProtocolRejection',
  phase: 'ProviderStarted',
  executionKey,
  capacityFence,
  provider,
}
const decide = (change = {}) => policy.decide({ ...baseInput, ...change })
const failures = [
  'LocalInvariant',
  'ProtocolRejection',
  'AuthorizationDenied',
  'UserCancelled',
  'Superseded',
  'CapacityQueueFull',
  'ProviderTransient',
  'ProviderPermanent',
  'AcceptanceUnknown',
  'StreamInterruptedAfterFirstToken',
  { kind: 'PersistenceFailure', commitment: 'NotCommitted' },
  { kind: 'PersistenceFailure', commitment: 'Committed' },
  { kind: 'PersistenceFailure', commitment: 'Unknown' },
  { kind: 'PersistenceFailure', commitment: 'NoNewWrite' },
]
const nonProviderFailures = failures.filter(
  (failure) => failure !== 'ProviderTransient' && failure !== 'ProviderPermanent',
)
const phases = ['NoAcceptedFact', 'AcceptedBeforeProvider', 'ProviderStarted', 'Terminal']
const capacityCases = [null, capacityFence]
const providerCases = [
  {
    label: 'transient retry on Closed and Available',
    failure: 'ProviderTransient',
    facts: provider,
    resolution: 'RetryFreshAttempt',
    breaker: 'RecordProviderTransientFailure',
  },
  {
    label: 'transient terminal after retry exhaustion',
    failure: 'ProviderTransient',
    facts: { ...provider, retryBudget: 'Exhausted' },
    resolution: 'TerminalizeProviderStarted',
    breaker: 'RecordProviderTransientFailure',
  },
  {
    label: 'transient terminal around an open breaker',
    failure: 'ProviderTransient',
    facts: { ...provider, breaker: 'Open' },
    resolution: 'TerminalizeProviderStarted',
    breaker: 'RecordProviderTransientFailure',
  },
  {
    label: 'permanent retry on Closed and Available',
    failure: 'ProviderPermanent',
    facts: provider,
    resolution: 'RetryFreshAttempt',
    breaker: 'RecordProviderPermanentFailure',
  },
  {
    label: 'permanent terminal after retry exhaustion',
    failure: 'ProviderPermanent',
    facts: { ...provider, retryBudget: 'Exhausted' },
    resolution: 'TerminalizeProviderStarted',
    breaker: 'RecordProviderPermanentFailure',
  },
  {
    label: 'permanent terminal around an open breaker',
    failure: 'ProviderPermanent',
    facts: { ...provider, breaker: 'Open' },
    resolution: 'TerminalizeProviderStarted',
    breaker: 'RecordProviderPermanentFailure',
  },
  ...['WorkMain', 'InteractionRepair'].map((requestKind) => ({
    label: `${requestKind} remains provider-recoverable`,
    failure: 'ProviderTransient',
    facts: { ...provider, requestKind },
    resolution: 'RetryFreshAttempt',
    breaker: 'RecordProviderTransientFailure',
  })),
]

test('WHAT[execution-failure-policy-002] every phase and failure yields exactly one resolution and orthogonal dimensions', () => {
  const dimensions = [
    'authorization',
    'breaker',
    'capacitySettlement',
    'executionKey',
    'fatality',
    'resolution',
    'terminalDisposition',
  ]

  for (const phase of phases) {
    for (const capacityFence of capacityCases) {
      for (const failure of failures) {
        const decision = decide({ phase, capacityFence, failure })
        assert.deepEqual(Object.keys(decision).sort(), dimensions)
        assert.equal(typeof decision.resolution, 'string')
        assert.equal(typeof decision.breaker.kind, 'string')
        assert.equal(typeof decision.capacitySettlement.kind, 'string')
        assert.equal(typeof decision.fatality.kind, 'string')

        // Re-derive expected resolution and disposition from Policy.fs
        const failureKind = typeof failure === 'string' ? failure : failure.kind
        const commitment = typeof failure === 'object' ? failure.commitment : null
        let expectedResolution = 'PreserveCurrentFact'
        let expectedDisposition = null

        if (failureKind === 'AcceptanceUnknown' || (failureKind === 'PersistenceFailure' && commitment === 'Unknown')) {
          expectedResolution = 'AwaitAcceptanceReconciliation'
        } else if (failureKind === 'PersistenceFailure') {
          expectedResolution = 'PreserveCurrentFact'
        } else if (phase === 'AcceptedBeforeProvider') {
          expectedResolution = 'TerminalizeAcceptedPreProvider'
          expectedDisposition = ['UserCancelled', 'Superseded'].includes(failureKind) ? 'Cancelled'
            : ['ProtocolRejection', 'AuthorizationDenied'].includes(failureKind) ? 'Rejected'
            : 'Failed'
        } else if (phase === 'ProviderStarted') {
          if (['ProviderTransient', 'ProviderPermanent'].includes(failureKind) && provider.breaker === 'Closed' && provider.retryBudget === 'Available') {
            expectedResolution = 'RetryFreshAttempt'
          } else {
            expectedResolution = 'TerminalizeProviderStarted'
            expectedDisposition = ['UserCancelled', 'Superseded'].includes(failureKind) ? 'Cancelled'
              : ['ProtocolRejection', 'AuthorizationDenied'].includes(failureKind) ? 'Rejected'
              : 'Failed'
          }
        }
        assert.equal(decision.resolution, expectedResolution)
        assert.equal(decision.terminalDisposition, expectedDisposition)

        switch (decision.resolution) {
          case 'RetryFreshAttempt':
            assert.ok(decision.authorization)
            assert.equal(decision.executionKey, null)
            assert.equal(decision.terminalDisposition, null)
            assert.equal(decision.fatality.kind, 'NoFatality')
            break
          case 'TerminalizeAcceptedPreProvider':
          case 'TerminalizeProviderStarted':
            assert.equal(decision.authorization, null)
            assert.deepEqual(decision.executionKey, executionKey)
            assert.ok(['Cancelled', 'Rejected', 'Failed'].includes(decision.terminalDisposition))
            assert.equal(phase, decision.resolution === 'TerminalizeAcceptedPreProvider' ? 'AcceptedBeforeProvider' : 'ProviderStarted')
            break
          case 'AwaitAcceptanceReconciliation':
            assert.equal(decision.authorization, null)
            assert.deepEqual(decision.executionKey, executionKey)
            assert.equal(decision.terminalDisposition, null)
            break
          case 'PreserveCurrentFact':
            assert.equal(decision.authorization, null)
            assert.equal(decision.executionKey, null)
            assert.equal(decision.terminalDisposition, null)
            break
          default:
            assert.fail(`unrecognized resolution: ${decision.resolution}`)
        }
      }
    }
  }

  for (const scenario of providerCases) {
    const decision = decide({ failure: scenario.failure, provider: scenario.facts })
    assert.equal(decision.resolution, scenario.resolution, scenario.label)
    assert.equal(decision.breaker.kind, scenario.breaker, scenario.label)
  }

  // Attempt recovery contract: first retry as-is, second retry blacklists the provider
  assert.equal(typeof policy.recoveryStep, 'function')
  assert.equal(policy.recoveryStep(false), 'RetrySameProvider')
  assert.equal(policy.recoveryStep(true), 'BlacklistProviderThenRetry')
})
}
