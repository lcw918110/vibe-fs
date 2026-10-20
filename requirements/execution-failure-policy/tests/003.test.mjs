import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { default: fc } = await import("fast-check");
const policy = await import("../../../dist/Execution/Failure/Surface.js");
const recovery = await import("../../../dist/Execution/Session/ChatExecution/RecoveryRuntimeSurface.js");
const hooks = await import("../../../dist/OpenCode/Host/PluginHooksSurface.js");

const executionKey = {
  sessionId: 'ses-failure-property',
  physicalUserMessageId: 'msg-failure-property',
}
const capacityFence = { reference: 'fence-failure-property' }
const baseProvider = {
  logicalRun: 'logical-failure-property',
  providerRun: 'provider-failure-property',
  requestKind: 'WorkMain',
  breaker: 'Closed',
}
const arbitraryFailure = fc.constantFrom(
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
)
const arbitraryPhase = fc.constantFrom(
  'NoAcceptedFact',
  'AcceptedBeforeProvider',
  'ProviderStarted',
  'Terminal',
)
const arbitraryBudget = fc.constantFrom('Available', 'Exhausted')
const arbitraryBreaker = fc.constantFrom('Closed', 'Open')
const arbitraryRequestKind = fc.constantFrom(
  'WorkMain',
  'InteractionRepair',
)
const arbitraryCapacityFence = fc.constantFrom(null, capacityFence)
const arbitraryExecutionFailureInput = fc.record({
  failure: arbitraryFailure,
  phase: arbitraryPhase,
  executionKey: fc.constant(executionKey),
  capacityFence: arbitraryCapacityFence,
  provider: fc.record({
    logicalRun: fc.constant(baseProvider.logicalRun),
    providerRun: fc.string({ minLength: 1, maxLength: 32 }).map((suffix) => `provider-${suffix}`),
    requestKind: arbitraryRequestKind,
    retryBudget: arbitraryBudget,
    breaker: arbitraryBreaker,
  }),
})
const isRecoveryResolution = (resolution) =>
  resolution === 'RetryFreshAttempt'
const isTerminalResolution = (resolution) =>
  resolution === 'TerminalizeAcceptedPreProvider' || resolution === 'TerminalizeProviderStarted'

test('WHAT[execution-failure-policy-003] finite provider budget matrix fixes policy and recovery outcomes', async () => {
  // Dense model property: temporal invariants over arbitrary failure, phase, budget, breaker, capacity
  fc.assert(
    fc.property(arbitraryExecutionFailureInput, (input) => {
      const decision = policy.decide(input)

      // Invariant 1: Exactly one resolution axis
      assert.equal(typeof decision.resolution, 'string')
      assert.ok([
        'PreserveCurrentFact',
        'AwaitAcceptanceReconciliation',
        'RetryFreshAttempt',
        'TerminalizeAcceptedPreProvider',
        'TerminalizeProviderStarted',
      ].includes(decision.resolution))

      // Invariant 2: Recovery implies no terminal; authorization present iff recovery
      if (isRecoveryResolution(decision.resolution)) {
        assert.equal(decision.terminalDisposition, null)
        assert.ok(decision.authorization)
        assert.equal(decision.authorization.logicalRun, input.provider.logicalRun)
        assert.equal(decision.authorization.providerRun, input.provider.providerRun)
        assert.equal(decision.authorization.requestKind, input.provider.requestKind)
        assert.equal(typeof decision.authorization.decisionId, 'string')
        assert.notEqual(decision.authorization.decisionId, '')
      } else {
        assert.equal(decision.authorization, null)
      }

      // Invariant 3: Terminal implies no recovery; terminalDisposition present iff terminal
      if (isTerminalResolution(decision.resolution)) {
        assert.equal(decision.authorization, null)
        assert.ok(['Completed', 'Cancelled', 'Rejected', 'Failed'].includes(decision.terminalDisposition))
        assert.deepEqual(decision.executionKey, executionKey)
      }

      // Invariant 4: Non-provider failure or non-ProviderStarted phase never recovers
      const isProviderFailure =
        input.failure === 'ProviderTransient' || input.failure === 'ProviderPermanent'
      if (!isProviderFailure || input.phase !== 'ProviderStarted') {
        assert.ok(!isRecoveryResolution(decision.resolution), `${JSON.stringify(input.failure)}/${input.phase} must not recover`)
      }

      // Invariant 4b: Open breaker or exhausted single budget never recovers
      if (input.provider.breaker === 'Open' || input.provider.retryBudget === 'Exhausted') {
        assert.ok(!isRecoveryResolution(decision.resolution), 'open or exhausted single budget must not recover')
      }

      // Invariant 4c: Closed + Available + recoverable provider failure at ProviderStarted always retries
      if (
        isProviderFailure &&
        input.phase === 'ProviderStarted' &&
        input.provider.breaker === 'Closed' &&
        input.provider.retryBudget === 'Available'
      ) {
        assert.equal(decision.resolution, 'RetryFreshAttempt')
      }

      // Invariant 5: LocalInvariant / ProtocolRejection / AuthorizationDenied invariant rules
      if (input.failure === 'LocalInvariant' || input.failure === 'ProtocolRejection' || input.failure === 'AuthorizationDenied') {
        if (input.phase === 'AcceptedBeforeProvider') {
          assert.equal(decision.resolution, 'TerminalizeAcceptedPreProvider')
        } else if (input.phase === 'ProviderStarted') {
          assert.equal(decision.resolution, 'TerminalizeProviderStarted')
        }
      }

      // Invariant 6: Every provider-started confirmed failure produces recovery or terminal, never neither
      if (
        input.phase === 'ProviderStarted' &&
        (input.failure === 'ProviderTransient' || input.failure === 'ProviderPermanent')
      ) {
        assert.ok(
          isRecoveryResolution(decision.resolution) || isTerminalResolution(decision.resolution),
          'provider-started failure must produce recovery or terminal',
        )
      }

      // Invariant 7: Duplicate observation is deterministic and produces identical authorization decisionId
      const replay = policy.decide(input)
      assert.deepEqual(replay, decision)
    }),
    { seed: 0x45584543, numRuns: 200 },
  )

  // Property 2: Distinct provider runs create distinct authorization decisionId
  fc.assert(
    fc.property(
      fc.tuple(
        fc.string({ minLength: 1, maxLength: 16 }),
        fc.string({ minLength: 1, maxLength: 16 }),
      ).filter(([a, b]) => a !== b),
      ([runA, runB]) => {
        const decisionA = policy.decide({
          failure: 'ProviderPermanent',
          phase: 'ProviderStarted',
          executionKey,
          capacityFence,
          provider: {
            ...baseProvider,
            providerRun: `provider-${runA}`,
            retryBudget: 'Available',
          },
        })
        const decisionB = policy.decide({
          failure: 'ProviderPermanent',
          phase: 'ProviderStarted',
          executionKey,
          capacityFence,
          provider: {
            ...baseProvider,
            providerRun: `provider-${runB}`,
            retryBudget: 'Available',
          },
        })

        assert.equal(decisionA.resolution, 'RetryFreshAttempt')
        assert.equal(decisionB.resolution, 'RetryFreshAttempt')
        assert.notEqual(decisionA.authorization.decisionId, decisionB.authorization.decisionId)
      },
    ),
    { seed: 0x52554e49, numRuns: 100 },
  )

  // Recovery runtime interpretation: provider recovery is owned by ProviderRecoveryWorkflow,
  // so chat recovery produces Ignore with ProviderRecoveryOwned and zero effects.
  const transientOutcome = await recovery.interpretFailurePolicy(
    'ProviderTransient',
    'Available',
    'NotCommitted',
    'ExactAbsent',
  )
  assert.deepEqual(transientOutcome, { decision: 'Ignore', effects: [] })

  const permanentOutcome = await recovery.interpretFailurePolicy(
    'ProviderPermanent',
    'Available',
    'NotCommitted',
    'ExactAbsent',
  )
  assert.deepEqual(permanentOutcome, { decision: 'Ignore', effects: [] })

  const terminalOutcome = await recovery.interpretFailurePolicy(
    'ProviderPermanent',
    'Exhausted',
    'NotCommitted',
    'ExactAbsent',
  )
  assert.deepEqual(terminalOutcome, { decision: 'Finalize', effects: ['Finalize:Failed'] })

  // Exercise hook promises
  for (const label of ['transient', 'permanent', 'exhausted']) {
    const wrapped = hooks.policyAwareHook(`policy-matrix-${label}`, () => label)
    assert.equal(await wrapped('args', 'context'), label)

    // The retired ledger's typed rejection is gone; `assume` refuses bad arguments
    // as a tool result, so a ProtocolRejection arrives from the Host wire itself.
    const rejection = new Error(label)
    rejection.name = 'ProtocolRejection'
    const failing = hooks.policyAwareHook(`policy-matrix-${label}`, () => Promise.reject(rejection))
    await assert.rejects(() => failing('args', 'context'), (error) => error === rejection)
  }
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

test('WHAT[execution-failure-policy-003] production retry matrix excludes other failures and gives stable attempt-bound authorization', () => {
  for (const failure of nonProviderFailures) {
    const decision = decide({ failure })
    assert.notEqual(decision.resolution, 'RetryFreshAttempt')
    assert.equal(decision.breaker.kind, 'NoBreakerTransition')
  }

  for (const scenario of providerCases) {
    const decision = decide({ failure: scenario.failure, provider: scenario.facts })
    assert.equal(decision.resolution, scenario.resolution, scenario.label)
    assert.equal(decision.breaker.kind, scenario.breaker, scenario.label)

    if (decision.resolution === 'RetryFreshAttempt') {
      const authorization = decision.authorization
      assert.ok(authorization)
      assert.equal(authorization.providerRun, scenario.facts.providerRun)
      assert.equal(authorization.logicalRun, scenario.facts.logicalRun)
      assert.equal(authorization.requestKind, scenario.facts.requestKind)
      assert.equal(typeof authorization.decisionId, 'string')
      assert.notEqual(authorization.decisionId, '')
    } else {
      assert.equal(decision.authorization, null, scenario.label)
    }
  }

  const first = decide({ failure: 'ProviderPermanent' }).authorization
  const duplicate = decide({ failure: 'ProviderPermanent' }).authorization
  const freshAttempt = decide({
    failure: 'ProviderPermanent',
    provider: { ...provider, providerRun: 'provider-failure-policy-2' },
  }).authorization
  assert.equal(first.decisionId, duplicate.decisionId)
  assert.notEqual(first.decisionId, freshAttempt.decisionId)

  const wrongPhase = decide({ failure: 'ProviderTransient', phase: 'AcceptedBeforeProvider' })
  assert.notEqual(wrongPhase.resolution, 'RetryFreshAttempt')

  const openBreaker = decide({ failure: 'ProviderPermanent', provider: { ...provider, breaker: 'Open' } })
  assert.notEqual(openBreaker.resolution, 'RetryFreshAttempt')

  const exhausted = decide({ failure: 'ProviderPermanent', provider: { ...provider, retryBudget: 'Exhausted' } })
  assert.notEqual(exhausted.resolution, 'RetryFreshAttempt')

  for (const retired of ['BloggerMain', 'StrengthReplica', 'BloggerSquash']) {
    assert.throws(() => decide({
      provider: { ...provider, requestKind: retired },
    }), /unknown provider request kind/)
  }
})
}

test.todo('WHAT[execution-failure-policy-003] GAP-120 caller cannot construct sealed recovery authorization and actual ledger deduplicates physical emission of one identity')
