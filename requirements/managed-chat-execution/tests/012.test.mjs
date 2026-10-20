import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const lifecycle = await import("../../../dist/Execution/Session/ChatExecution/Surface.js");
const recovery = await import("../../../dist/Execution/Session/ChatExecution/RecoveryRuntimeSurface.js");
const transaction = await import("../../../dist/OpenCode/Host/ChatAdmission/TransactionSurface.js");

const evidence = (cut) => ({
  sessionId: `ses-crash-${cut}`,
  physicalUserMessageId: `msg-crash-${cut}`,
  logicalRunId: `run-crash-${cut}`,
  authorityRootUserMessageId: `root-crash-${cut}`,
  identitySeed: {
    participantIdentity: {
      selectedAgent: 'engineer',
    },
  },
  providerRun: `provider-crash-${cut}`,
})
const action = (kind, cut, extra = {}) => ({
  kind,
  evidence: {
    ...evidence(cut),
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
    requestKind: 'work-main',
  },
  appendOutcome: 'Committed',
  ...extra,
})
const CUTS = [
  ['A', 'before Accepted append', [], 'None', { activeCapacity: 0, hostProjected: false }, 0, 'NoDurableExecution'],
  ['B', 'after Accepted before lease', ['Accepted'], 'Accepted', { activeCapacity: 0, hostProjected: false }, 0, 'ResumePreProvider'],
  ['C', 'after lease before execution binding', ['Accepted'], 'Accepted', { activeCapacity: 1, hostProjected: false }, 0, 'ResumePreProvider'],
  ['D', 'after target before Host projection', ['Accepted'], 'Accepted', { activeCapacity: 1, hostProjected: false }, 0, 'ResumePreProvider'],
  ['E', 'after Host projection before ProviderStarted', ['Accepted'], 'Accepted', { activeCapacity: 1, hostProjected: true }, 0, 'ResumePreProvider'],
  ['F', 'after ProviderStarted before Terminal', ['Accepted', 'ProviderStarted'], 'ProviderStarted', { activeCapacity: 1, hostProjected: true }, 1, 'Ignore'],
  ['G', 'after Terminal before exact release', ['Accepted', 'ProviderStarted', 'Terminal'], 'Terminal', { activeCapacity: 1, hostProjected: true }, 1, 'ReconcilePhysical'],
  ['H', 'after exact release before Hook return', ['Accepted', 'ProviderStarted', 'Terminal'], 'Terminal', { activeCapacity: 0, hostProjected: true }, 1, 'Ignore'],
  ['I', 'after exact release before fatal propagation', ['Accepted', 'ProviderStarted', 'Terminal'], 'Terminal', { activeCapacity: 0, hostProjected: true }, 1, 'Ignore'],
]
test('WHAT[managed-chat-execution-012] controlled admission A–E stops and provider lifecycle prefixes preserve their committed phase', async () => {
  const transactionResults = new Map()
  for (const cut of ['A', 'B', 'C', 'D', 'E']) {
    transactionResults.set(cut, await transaction.transactionScenario(evidence(cut), `Crash${cut}`, 'None'))
  }

  const lifecycleResults = new Map()
  lifecycleResults.set('F', await lifecycle.providerLifecycleScenario([
    action('Accept', 'F'),
    action('ProviderStarted', 'F'),
    { kind: 'ProviderWork' },
  ]))
  for (const cut of ['G', 'H', 'I']) {
    lifecycleResults.set(cut, await lifecycle.providerLifecycleScenario([
      action('Accept', cut),
      action('ProviderStarted', cut),
      { kind: 'ProviderWork' },
      action('Terminal', cut, { disposition: 'Completed' }),
    ]))
  }

    for (const [cut, label, facts, phase, local, providerCount] of CUTS) {
      if (transactionResults.has(cut)) {
        const result = transactionResults.get(cut)
        assert.equal(result.crashed, true, label)
        assert.equal(result.durableLifecycle, phase, label)
        assert.deepEqual(result.admission, local, label)
        assert.equal(result.providerCount, providerCount, label)
      } else {
        const result = lifecycleResults.get(cut)
        assert.equal(result.ok, true, `${label}: ${JSON.stringify(result.error)}`)
        assert.equal(result.projection.phase, phase, label)
        assert.deepEqual(result.appendCounts, {
          accepted: facts.includes('Accepted') ? 1 : 0,
          providerStarted: facts.includes('ProviderStarted') ? 1 : 0,
          terminal: facts.includes('Terminal') ? 1 : 0,
        }, label)
        assert.equal(result.semanticTransitionCount, facts.length, label)
        assert.equal(result.providerWorkCount, providerCount, label)
      }
    }
})
test.todo('WHAT[managed-chat-execution-012] durable output of every A–I crash cut feeds actual recovery after process restart without duplicate owner effects (GAP-127)')
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const lifecycle = await import("../../../dist/Execution/Session/ChatExecution/Surface.js");
const recovery = await import("../../../dist/Execution/Session/ChatExecution/RecoveryRuntimeSurface.js");
const transaction = await import("../../../dist/OpenCode/Host/ChatAdmission/TransactionSurface.js");

const CUTS = [...'BCDEFGHI']
const rotations = (values) => values.map((_, index) => [...values.slice(index), ...values.slice(0, index)])
const generatedOrders = (values) => [
  values,
  [...values].reverse(),
  ...rotations(values),
  values.flatMap((value) => [value, value]),
]
const evidence = {
  sessionId: 'ses-crash-property',
  physicalUserMessageId: 'msg-crash-property',
  logicalRunId: 'run-crash-property',
  authorityRootUserMessageId: 'root-crash-property',
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
  providerRun: 'provider-crash-property',
  origin: 'HumanRoot',
  requestKind: 'work-main',
}
const action = (kind, extra = {}) => ({ kind, evidence, appendOutcome: 'Committed', ...extra })

test('WHAT[managed-chat-execution-012] sampled recovery evidence repeats every interpreter effect without observer deduplication', async () => {
  const baseline = (await recovery.admissionPhaseSamples(CUTS, 'NotCommitted', 'Applied')).scenarios

  for (const order of generatedOrders(CUTS)) {
    const result = await recovery.admissionPhaseSamples(order, 'NotCommitted', 'Applied')
    assert.equal(result.scenarios.length, order.length)
    for (const [index, scenario] of result.scenarios.entries()) {
      assert.equal(scenario.cut, order[index])
      assert.deepEqual(scenario, baseline.find((sample) => sample.cut === order[index]))
    }
  }

  const duplicateReplay = await lifecycle.providerLifecycleScenario([
    action('Accept'),
    action('Accept'),
    action('ProviderStarted'),
    action('ProviderStarted'),
    action('Terminal', { disposition: 'Completed' }),
    action('Terminal', { disposition: 'Completed' }),
  ])
  const canonicalReplay = await lifecycle.providerLifecycleScenario([
    action('Accept'),
    action('ProviderStarted'),
    action('Terminal', { disposition: 'Completed' }),
  ])
  assert.equal(duplicateReplay.ok, true, JSON.stringify(duplicateReplay.error))
  assert.equal(canonicalReplay.ok, true, JSON.stringify(canonicalReplay.error))
  assert.deepEqual(duplicateReplay.projection, canonicalReplay.projection)
  assert.equal(duplicateReplay.semanticTransitionCount, canonicalReplay.semanticTransitionCount)
  assert.deepEqual(duplicateReplay.appendCounts, canonicalReplay.appendCounts)

  const unknown = await recovery.admissionPhaseSamples(['B', 'C', 'D', 'E', 'F'], 'Unknown', 'Applied')
  for (const scenario of unknown.scenarios) {
    assert.deepEqual(scenario.decisions, ['MarkManualIntervention', 'MarkManualIntervention'])
    assert.deepEqual(scenario.effects, [
      'MarkManualIntervention:PersistenceOutcomeUnknown',
      'MarkManualIntervention:PersistenceOutcomeUnknown',
    ])
  }

  for (const capacityOutcome of ['Conflict', 'StaleFence']) {
    const rejected = await recovery.admissionPhaseSamples(['G', 'H', 'I'], 'NotCommitted', capacityOutcome)
    for (const scenario of rejected.scenarios) {
      assert.deepEqual(scenario.decisions, ['MarkManualIntervention', 'MarkManualIntervention'])
      assert.deepEqual(scenario.effects, [
        'MarkManualIntervention:PhysicalOutcomeUnknown',
        'MarkManualIntervention:PhysicalOutcomeUnknown',
      ])
    }
  }

  const staleFence = await transaction.transactionScenario(evidence, 'CommitLease', 'None')
  assert.equal(staleFence.ok, false)
  assert.equal(staleFence.error.kind, 'LeaseCommitFailed')
  assert.equal(staleFence.releaseCount, 1)
  assert.equal(staleFence.providerCount, 0)

})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { default: fc } = await import("fast-check");
const Runtime = await import("../../../dist/Execution/Session/ChatExecution/RecoveryRuntimeSurface.js");
const recovery = await import("../../../dist/Execution/Session/ChatExecution/RecoverySurface.js");
const lifecycle = await import("../../../dist/Execution/Session/ChatExecution/Surface.js");

const validScenarios = [
  'CrashAfterAcceptance',
  'AcceptedProviderAlive',
  'AcceptedProviderTerminal',
  'ProviderAlive',
  'ProviderTerminalCompleted',
  'ProviderTerminalFailed',
  'ProviderTerminalCancelled',
  'ProviderTerminalRejected',
  'RetryExhausted',
  'Superseded',
  'MissingReceipt',
  'AmbiguousReceipt',
  'PhysicalOutcomeUnknown',
  'PersistenceUnknown',
  'DuplicateRecovery',
  'StaleProvider',
  'StaleKey',
  'StalePolicy',
  'TerminalResourceHeld',
  'TerminalResourceReleased',
  'ProviderAbsentWithoutPolicy',
]
const staleOrMismatchedScenarios = ['StaleKey', 'StaleProvider', 'StalePolicy']
const evidenceSeed = (sessionId, physicalId) => ({
  sessionId,
  physicalUserMessageId: physicalId,
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
  providerRun: `provider-${sessionId}`,
  origin: 'HumanRoot',
  requestKind: 'work-main',
})
const action = (evidence, kind, extra = {}) => ({ kind, evidence, appendOutcome: 'Committed', ...extra })

test('WHAT[managed-chat-execution-012] duplicate terminal and stale recovery evidence are semantically inert', async () => {
  // Invariant 1: Durable semantic idempotence across duplicate and reordered lifecycle actions
  await fc.assert(
    fc.asyncProperty(
      fc.string({ minLength: 2, maxLength: 10 }),
      fc.constantFrom('Completed', 'Failed', 'Cancelled', 'Rejected'),
      async (id, disposition) => {
        const ev = evidenceSeed(`ses-idem-${id}`, `msg-idem-${id}`)
        const baseActions = [
          action(ev, 'Accept'),
          action(ev, 'ProviderStarted'),
          action(ev, 'Terminal', { disposition }),
        ]
        const single = await lifecycle.providerLifecycleScenario(baseActions)
        assert.equal(single.ok, true)

        // Duplicate and re-applied actions never increase semanticTransitionCount
        const duplicatedActions = [
          action(ev, 'Accept'),
          action(ev, 'Accept'),
          action(ev, 'ProviderStarted'),
          action(ev, 'ProviderStarted'),
          action(ev, 'Terminal', { disposition }),
          action(ev, 'Terminal', { disposition }),
        ]
        const duplicated = await lifecycle.providerLifecycleScenario(duplicatedActions)
        assert.equal(duplicated.ok, true)
        assert.deepEqual(duplicated.projection, single.projection)
        assert.equal(duplicated.semanticTransitionCount, single.semanticTransitionCount)
      },
    ),
    { seed: 0x4944454d, numRuns: 50 },
  )

  // Invariant 2: Terminal absorbs later observations with zero additional transitions
  await fc.assert(
    fc.asyncProperty(
      fc.string({ minLength: 2, maxLength: 10 }),
      fc.constantFrom('Completed', 'Failed', 'Cancelled', 'Rejected'),
      async (id, disposition) => {
        const ev = evidenceSeed(`ses-absorb-${id}`, `msg-absorb-${id}`)
        const baseActions = [
          action(ev, 'Accept'),
          action(ev, 'ProviderStarted'),
          action(ev, 'Terminal', { disposition }),
        ]
        const single = await lifecycle.providerLifecycleScenario(baseActions)
        assert.equal(single.ok, true)

        // Repeat terminal observations after terminal lifecycle state reached
        const absorbed = await lifecycle.providerLifecycleScenario([
          ...baseActions,
          action(ev, 'Terminal', { disposition }),
          action(ev, 'Terminal', { disposition }),
        ])
        assert.equal(absorbed.ok, true)
        assert.deepEqual(absorbed.projection, single.projection)
        assert.equal(absorbed.semanticTransitionCount, single.semanticTransitionCount)
      },
    ),
    { seed: 0x5445524d, numRuns: 50 },
  )

  // Invariant 3: Stale / mismatched evidence sequence cannot act
  await fc.assert(
    fc.asyncProperty(
      fc.array(fc.constantFrom(...staleOrMismatchedScenarios), { minLength: 1, maxLength: 8 }),
      async (staleSequence) => {
        const result = await Runtime.recoverScenarios(staleSequence)
        assert.deepEqual(result.effects, [])
        assert.deepEqual(
          result.decisions,
          staleSequence.map(() => 'Ignore'),
        )
      },
    ),
    { seed: 0x5354414c, numRuns: 50 },
  )

  // Invariant 4: Recovery decision determinism and completeness over all valid scenario primitives
  fc.assert(
    fc.property(
      fc.constantFrom(...validScenarios),
      (scenario) => {
        const decision = recovery.decideScenario(scenario)
        assert.ok(['Ignore', 'ReconcilePhysical', 'ResumePreProvider', 'Finalize', 'MarkManualIntervention'].includes(decision.kind))
        assert.equal(typeof decision.request, 'string')

        // Duplicate evaluation is deterministic and effect-free
        const duplicate = recovery.decideScenario(scenario)
        assert.deepEqual(duplicate, decision)
      },
    ),
    { seed: 0x56414c49, numRuns: 100 },
  )

  const result = await Runtime.recoverScenarios(['AcceptedProviderAlive', 'AcceptedProviderAlive'])
  assert.deepEqual(result.decisions, ['ReconcilePhysical', 'ReconcilePhysical'])
  assert.deepEqual(result.effects, ['ReconcilePhysical:PersistProviderStarted', 'ReconcilePhysical:PersistProviderStarted'])
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

test('WHAT[managed-chat-execution-012] lifecycle recovery interprets every typed decision through its owner port', async () => {
  const cases = [
    ['ProviderAlive', 'Ignore', []],
    ['AcceptedProviderAlive', 'ReconcilePhysical', ['ReconcilePhysical:PersistProviderStarted']],
    ['CrashAfterAcceptance', 'ResumePreProvider', ['ResumePreProvider']],
    ['RetryEligible', 'Ignore', []],
    ['ProviderTerminalCompleted', 'Finalize', ['Finalize:Completed']],
    ['MissingReceipt', 'MarkManualIntervention', ['MarkManualIntervention:MissingExternalReceipt']],
  ]

  for (const [scenario, decision, expected] of cases) {
    const result = await Runtime.recoverScenarios([scenario])
    assert.deepEqual(result.decisions, [decision], scenario)
    assert.deepEqual(result.effects, expected, scenario)
  }
})
test('WHAT[managed-chat-execution-012] absent recovery port publishes exactly one manual disposition and no resume', async () => {
  await withRecoveryHost('absent', 'absent', async (host) => {
    const result = await recoveryHost.resumeAccepted(host, sessionOf('absent'), physicalOf('absent'))

    assert.equal(result.calls, 0, 'absent port makes no acceptance call')
    assert.equal(result.manuals.length, 1)
    assertManualDisposition(result.manuals[0], 'ses-recovery-absent', 'msg-recovery-absent')
  })
})
test('WHAT[managed-chat-execution-012] rejecting port is awaited and publishes the same single manual', async () => {
  await withRecoveryHost('reject', 'reject', async (host) => {
    const result = await recoveryHost.resumeAccepted(host, sessionOf('reject'), physicalOf('reject'))

    assert.equal(result.calls, 1, 'rejection must be awaited exactly once')
    assert.equal(result.manuals.length, 1)
    assertManualDisposition(result.manuals[0], 'ses-recovery-reject', 'msg-recovery-reject')
  })
})
test('WHAT[managed-chat-execution-012] accepting port awaits ordinary admission and emits no manual block', async () => {
  await withRecoveryHost('accept', 'accept', async (host) => {
    const result = await recoveryHost.resumeAccepted(host, sessionOf('accept'), physicalOf('accept'))

    assert.equal(result.calls, 1, 'acceptance must be awaited exactly once')
    assert.deepEqual(result.manuals, [])
  })
})
test('WHAT[managed-chat-execution-012] duplicate resume signals keep exactly one manual', async () => {
  await withRecoveryHost('duplicate', 'absent', async (host) => {
    await recoveryHost.resumeAccepted(host, sessionOf('duplicate'), physicalOf('duplicate'))
    const result = await recoveryHost.resumeAccepted(host, sessionOf('duplicate'), physicalOf('duplicate'))

    assert.equal(result.manuals.length, 1)
    assertManualDisposition(result.manuals[0], 'ses-recovery-duplicate', 'msg-recovery-duplicate')
  })
})
test('WHAT[managed-chat-execution-012] actual recovery Host repeats terminal settlement idempotently when capacity is already absent', async () => {
  await withRecoveryHost('terminal-replay', 'absent', async (host) => {
    await recoveryHost.seedProviderStarted(host, 'ses-terminal-replay', 'msg-terminal-replay', 'provider-terminal-replay')
    const first = await recoveryHost.finalizeCompleted(host, 'ses-terminal-replay', 'msg-terminal-replay', 'provider-terminal-replay')
    assert.equal(first.lifecycle, 'Terminal')
    assert.equal(first.disposition, 'Completed')
    const repeated = await recoveryHost.finalizeCompleted(host, 'ses-terminal-replay', 'msg-terminal-replay', 'provider-terminal-replay')
    assert.deepEqual(repeated, first)
  })
})
}

test.todo('WHAT[managed-chat-execution-012] late recovery release after supersession preserves the newer real capacity owner and rejects genuine conflict (GAP-126)')

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const recovery = await import("../../../dist/Execution/Session/ChatExecution/RecoverySurface.js");

const matrix = [
  ['CrashAfterAcceptance', 'ResumePreProvider', 'ResumeAcceptedAdmission', null],
  ['AcceptedProviderAlive', 'ReconcilePhysical', 'PersistProviderStarted', null],
  ['AcceptedProviderTerminal', 'ReconcilePhysical', 'PersistProviderStartedAndTerminal', 'Completed'],
  ['ProviderAlive', 'Ignore', 'ProviderStillAlive', null],
  ['ProviderTerminalCompleted', 'Finalize', 'PersistTerminal', 'Completed'],
  ['ProviderTerminalFailed', 'Finalize', 'PersistTerminal', 'Failed'],
  ['ProviderTerminalCancelled', 'Finalize', 'PersistTerminal', 'Cancelled'],
  ['ProviderTerminalRejected', 'Finalize', 'PersistTerminal', 'Rejected'],
  ['RetryEligible', 'Ignore', 'ProviderRecoveryOwned', null],
  ['RetryExhausted', 'Finalize', 'PersistTerminal', 'Failed'],
  ['Superseded', 'Finalize', 'PersistTerminal', 'Cancelled'],
  ['MissingReceipt', 'MarkManualIntervention', 'MissingExternalReceipt', null],
  ['AmbiguousReceipt', 'MarkManualIntervention', 'AmbiguousExternalReceipt', null],
  ['PhysicalOutcomeUnknown', 'MarkManualIntervention', 'PhysicalOutcomeUnknown', null],
  ['PersistenceUnknown', 'MarkManualIntervention', 'PersistenceOutcomeUnknown', null],
  ['DuplicateRecovery', 'Ignore', 'RecoveryAlreadyCommitted', null],
  ['StaleProvider', 'Ignore', 'StalePhysicalEvidence', null],
  ['StaleKey', 'Ignore', 'StalePhysicalEvidence', null],
  ['StalePolicy', 'Ignore', 'StalePolicyEvidence', null],
  ['TerminalResourceHeld', 'ReconcilePhysical', 'ReleaseTerminalResource', 'Completed'],
  ['TerminalResourceReleased', 'Ignore', 'DurableTerminalAlreadySettled', null],
  ['ProviderAbsentWithoutPolicy', 'MarkManualIntervention', 'NoAuthorizedProviderDisposition', null],
]

test('WHAT[managed-chat-execution-012] representative typed evidence selects the expected production recovery decisions', () => {
  for (const [scenario, kind, request, disposition] of matrix) {
    assert.deepEqual(recovery.decideScenario(scenario), { kind, request, disposition }, scenario)
  }
})
test('WHAT[managed-chat-execution-012] duplicate evaluation is deterministic and effect-free', () => {
  for (const [scenario] of matrix) {
    const first = recovery.decideScenario(scenario)
    const second = recovery.decideScenario(scenario)
    assert.deepEqual(second, first, scenario)
  }
})
}
