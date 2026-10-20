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

test('WHAT[managed-chat-execution-006] terminal replay performs no acceptance or capacity effect', async () => {
  const result = await run('None', 'Terminal')

  assert.equal(result.ok, true, JSON.stringify(result.error))
  assert.equal(result.outcome, 'AlreadyTerminal')
  assert.deepEqual(result.trace, ['ResolveState'])
  assert.equal(result.acceptCount, 0)
  assert.equal(result.acquireCount, 0)
  assert.equal(result.hostCount, 0)
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

test('WHAT[managed-chat-execution-006] same key terminal conflict', () => {
  const accepted = acceptedWire('msg-terminal')
  const started = startedWire('msg-terminal')
  const completed = terminalWire('msg-terminal', 'Completed')
  assert.deepEqual(
    mustFold([accepted, started, completed, completed]),
    mustFold([accepted, started, completed]),
  )

  const conflict = fold([accepted, started, completed, terminalWire('msg-terminal', 'Failed')])
  assert.equal(conflict.ok, false)
  assert.notEqual(conflict.error, '')
})
test('WHAT[managed-chat-execution-006] Terminal directly after Accepted is rejected', () => {
  const result = fold([
    acceptedWire('msg-pre-provider'),
    preProviderTerminalWire('msg-pre-provider', 'Completed'),
  ])

  assert.equal(result.ok, false)
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

test('WHAT[managed-chat-execution-006] each terminal disposition is durable after provider start', async () => {
  for (const disposition of ['Completed', 'Cancelled', 'Rejected', 'Failed']) {
    const result = await run(accept(), start(), terminal(disposition))

    assert.equal(result.ok, true, JSON.stringify(result.error))
    assert.deepEqual(result.projection, {
      sessionId: evidence().sessionId,
      physicalUserMessageId: evidence().physicalUserMessageId,
      phase: 'Terminal',
      disposition,
    })
    assert.deepEqual(result.appendCounts, { accepted: 1, providerStarted: 1, terminal: 1 })
  }
})
test('WHAT[managed-chat-execution-006] provider terminal before ProviderStarted rejects', async () => {
  const result = await run(accept(), terminal('Completed'))
  assert.equal(result.ok, false)
  assert.equal(result.error.kind, 'ProviderNotStarted')
})
test('WHAT[managed-chat-execution-006] conflicting terminal rejects without a second write', async () => {
  const result = await run(
    accept(),
    start(),
    terminal('Completed'),
    terminal('Failed'),
  )

  assert.equal(result.ok, false)
  assert.equal(result.error.kind, 'TerminalConflict')
  assert.deepEqual(result.appendCounts, { accepted: 1, providerStarted: 1, terminal: 1 })
  assert.equal(result.projection.disposition, 'Completed')
})
test('WHAT[managed-chat-execution-006] each uncertain Terminal append leaves projection provider-started', async () => {
  for (const outcome of ['NotAttempted', 'CommitUnknown']) {
    const result = await run(accept(), start(), terminal('Completed', evidence(), outcome))
    assert.equal(result.ok, false)
    assert.equal(result.error.kind, outcome)
    assert.equal(result.projection.phase, 'ProviderStarted')
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const hostSignals = await import("../../../dist/OpenCode/Host/HostSignalSurface.js");

const terminal = ({
  sessionId = 'ses-terminal',
  physicalUserMessageId = 'msg-terminal',
  providerRun = 'run-terminal',
  finish,
  error,
  completed = 2,
} = {}) => ({
  type: 'message.updated',
  properties: {
    info: {
      sessionID: sessionId,
      id: providerRun,
      role: 'assistant',
      parentID: physicalUserMessageId,
      time: { created: 1, completed },
      ...(finish === undefined ? {} : { finish }),
      ...(error === undefined ? {} : { error }),
    },
  },
})

test('WHAT[managed-chat-execution-006] exact successful Host terminals retain typed finish outcomes', () => {
  for (const [finish, outcome] of [
    ['stop', 'Stop'],
    ['length', 'Length'],
    ['content-filter', 'ContentFiltered'],
  ]) {
    assert.deepEqual(hostSignals.tryDecodeExactProviderTerminal(terminal({ finish })), {
      sessionId: 'ses-terminal',
      physicalUserMessageId: 'msg-terminal',
      providerRun: 'run-terminal',
      outcome,
      failure: '',
      disposition: 'Completed',
    })
  }
})
test('WHAT[managed-chat-execution-006] exact cancel and interruption become closed typed terminal dispositions', () => {
  assert.deepEqual(hostSignals.tryDecodeExactProviderTerminal(terminal({ error: { name: 'AbortError' } })), {
    sessionId: 'ses-terminal',
    physicalUserMessageId: 'msg-terminal',
    providerRun: 'run-terminal',
    outcome: 'Cancelled',
    failure: 'UserCancelled',
    disposition: 'Cancelled',
  })

  assert.deepEqual(hostSignals.tryDecodeExactProviderTerminal(terminal({ error: { name: 'StreamInterruptedError' } })), {
    sessionId: 'ses-terminal',
    physicalUserMessageId: 'msg-terminal',
    providerRun: 'run-terminal',
    outcome: 'ProviderFailure',
    failure: 'ProviderTransient',
    disposition: '',
  })
})
test('WHAT[managed-chat-execution-006] exact provider failure remains typed but awaits retry-owner disposition', () => {
  assert.deepEqual(hostSignals.tryDecodeExactProviderTerminal(terminal({ error: { name: 'TimeoutError', message: 'AbortError' } })), {
    sessionId: 'ses-terminal',
    physicalUserMessageId: 'msg-terminal',
    providerRun: 'run-terminal',
    outcome: 'ProviderFailure',
    failure: 'ProviderTransient',
    disposition: '',
  })
})
test('WHAT[managed-chat-execution-006] ambiguous and deleted evidence fail closed', () => {
  assert.equal(hostSignals.tryDecodeExactProviderTerminal(terminal({ providerRun: '' })), null)
  assert.equal(hostSignals.tryDecodeExactProviderTerminal({ type: 'session.deleted', properties: { sessionID: 'ses-terminal' } }), null)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, readdirSync, readFileSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const recoveryHost = await import("../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");
const { startPluginIncarnation } = await import("../../verification-system/tests/support/plugin-fixture.mjs");

const sessionId = 'ses-terminal-release'
const physicalUserMessageId = 'msg-terminal-release'
const providerRun = 'provider-terminal-release'
// The decoy is a neighbouring execution in its own session: routing keeps one
// active execution per session, so a second physical under the same session
// would atomically supersede the first lease instead of coexisting.
const decoySessionId = 'ses-terminal-decoy'
const decoyPhysicalUserMessageId = 'msg-terminal-decoy'
const decoyProviderRun = 'provider-terminal-decoy'

const ndjsonFiles = (directory) => {
  const found = []
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) found.push(...ndjsonFiles(path))
    else if (entry.name.endsWith('.ndjson')) found.push(path)
  }
  return found
}

// Oracle: count durable Terminal lines on the real disk, not an observer.
const terminalLineCount = (directory) =>
  ndjsonFiles(directory)
    .flatMap((path) => readFileSync(path, 'utf8').split('\n'))
    .filter((line) => line.includes('["ChatExecution",["Terminal"')).length

// Oracle: the real shared capacity snapshot, exact owner only.
const executionCount = (session, physical) =>
  routing.sharedCapacitySnapshot().executions.filter(
    (execution) => execution.sessionId === session && execution.physicalUserMessageId === physical,
  ).length

const acquireLease = (session, physical) =>
  routing.acquireSharedExecutionAdmission(session, physical, 'engineer', 'engineer', null, 'normal')

const withPlugin = async (action) => {
  const workspace = mkdtempSync(join(tmpdir(), 'wxs-terminal-release-'))
  const incarnation = await startPluginIncarnation(workspace)
  try {
    await action()
  } finally {
    await incarnation.hooks.dispose()
    rmSync(workspace, { recursive: true, force: true })
  }
}

const withHost = async (mode, action) => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-terminal-journal-'))
  const host = await recoveryHost.bootControlledRecoveryHost(directory, 'absent', mode)
  const pendingDrains = []
  let barrierReleased = false
  let actionFailure
  const terminalWriter = {
    trackDrain: (drain) => {
      pendingDrains.push(drain)
      return drain
    },
    releaseBarrier: () => {
      if (!barrierReleased) {
        recoveryHost.releaseTerminalBarrier(host)
        barrierReleased = true
      }
    },
  }
  try {
    await action(host, directory, terminalWriter)
  } catch (error) {
    actionFailure = { error }
    throw error
  } finally {
    try {
      if (mode === 'held') terminalWriter.releaseBarrier()
      const settled = await Promise.allSettled(pendingDrains)
      const failures = settled.filter((drain) => drain.status === 'rejected').map((drain) => drain.reason)
      if (!actionFailure && failures.length) throw new AggregateError(failures, 'Held terminal drain failed', { cause: failures[0] })
    } finally {
      recoveryHost.disposeRecoveryHost(host)
      rmSync(directory, { recursive: true, force: true })
    }
  }
}

test('WHAT[managed-chat-execution-006] public Host terminal event waits for durable commit before exact capacity release under held and uncertain append (GAP-126)', async () => {
  await withPlugin(async () => {
    await withHost('held', async (host, directory, terminalWriter) => {
      await recoveryHost.seedProviderStarted(host, sessionId, physicalUserMessageId, providerRun)
      await recoveryHost.seedProviderStarted(host, decoySessionId, decoyPhysicalUserMessageId, decoyProviderRun)
      await acquireLease(sessionId, physicalUserMessageId)
      await acquireLease(decoySessionId, decoyPhysicalUserMessageId)

      const settle = terminalWriter.trackDrain(recoveryHost.signalExactTerminal(host, sessionId, physicalUserMessageId, providerRun, 'Completed'))
      await recoveryHost.awaitTerminalBarrier(host)

      // The barrier holds the terminal writer: the append is not confirmed, so
      // the exact lease is retained, the disk carries no terminal line, and the
      // projection does not fabricate a terminal.
      assert.equal(executionCount(sessionId, physicalUserMessageId), 1)
      assert.equal(executionCount(decoySessionId, decoyPhysicalUserMessageId), 1)
      assert.equal(terminalLineCount(directory), 0)
      assert.equal(recoveryHost.executionStatus(host, sessionId, physicalUserMessageId).phase, 'ProviderStarted')

      terminalWriter.releaseBarrier()
      const settled = await settle
      assert.equal(settled.phase, 'Terminal')
      assert.equal(settled.disposition, 'Completed')

      // Once the commit confirms, only the exact execution releases; the
      // neighbouring physical execution keeps its lease and its facts.
      assert.equal(terminalLineCount(directory), 1)
      assert.equal(executionCount(sessionId, physicalUserMessageId), 0)
      assert.equal(executionCount(decoySessionId, decoyPhysicalUserMessageId), 1)
    })

    await withHost('commitUnknown', async (host, directory) => {
      await recoveryHost.seedProviderStarted(host, sessionId, physicalUserMessageId, providerRun)
      await recoveryHost.seedProviderStarted(host, decoySessionId, decoyPhysicalUserMessageId, decoyProviderRun)
      await acquireLease(sessionId, physicalUserMessageId)
      await acquireLease(decoySessionId, decoyPhysicalUserMessageId)

      await assert.rejects(
        recoveryHost.signalExactTerminal(host, sessionId, physicalUserMessageId, providerRun, 'Completed'),
      )

      // Unknown stays unknown: no release, no terminal line, no fabricated
      // terminal disposition in the durable projection.
      assert.equal(executionCount(sessionId, physicalUserMessageId), 1)
      assert.equal(executionCount(decoySessionId, decoyPhysicalUserMessageId), 1)
      assert.equal(terminalLineCount(directory), 0)
      assert.equal(recoveryHost.executionStatus(host, sessionId, physicalUserMessageId).phase, 'ProviderStarted')
    })

    await withHost('held', async (host, directory, terminalWriter) => {
      await recoveryHost.seedProviderStarted(host, sessionId, physicalUserMessageId, providerRun)
      await acquireLease(sessionId, physicalUserMessageId)

      // A competing terminal parks behind the held first writer.
      const first = terminalWriter.trackDrain(recoveryHost.signalExactTerminal(host, sessionId, physicalUserMessageId, providerRun, 'Completed'))
      await recoveryHost.awaitTerminalBarrier(host)
      const competing = recoveryHost.signalExactTerminal(host, sessionId, physicalUserMessageId, providerRun, 'Failed')
      const competingRejected = terminalWriter.trackDrain(assert.rejects(competing))

      terminalWriter.releaseBarrier()
      const settled = await first
      assert.equal(settled.phase, 'Terminal')
      assert.equal(settled.disposition, 'Completed')

      // The first terminal fact wins; the competing disposition fails closed
      // without a second write or a second release effect.
      await competingRejected
      assert.equal(terminalLineCount(directory), 1)
      assert.equal(executionCount(sessionId, physicalUserMessageId), 0)

      // An equal terminal replay after settlement is an idempotent no-op.
      const replayed = await recoveryHost.signalExactTerminal(host, sessionId, physicalUserMessageId, providerRun, 'Completed')
      assert.deepEqual(replayed, settled)
      assert.equal(terminalLineCount(directory), 1)
    })
  })
})
}
