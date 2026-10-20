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

test('WHAT[managed-chat-execution-011] external and plugin roots share AcceptManagedChatIntent', async () => {
  await withJournal('shared-owner', async (handle) => {
    const external = await dispatch.acceptManagedExternal(
      handle,
      'ses-external',
      'msg-external',
      'manager',
    )
    assert.equal(external.ok, true, external.error)
    assert.equal(external.origin, 'HumanRoot')

    const owner = await dispatch.acceptHumanRootSelection(
      handle,
      'ses-owner',
      'msg-owner',
      rootIdentity,
    )
    assert.equal(owner.ok, true, owner.error)

    const inherited = authority.issueInheritedIdentitySeed('engineer', owner.profile)
    assert.equal(inherited.ok, true, inherited.error)

    const sent = await dispatch.sendAgentOwnerRoot(
      hostPort(async () => dispatch.admittedWithReceipt('plugin-root-receipt')),
      handle,
      'ses-plugin',
      'plugin work',
      inherited.value,
    )
    assert.equal(sent.ok, true, sent.error)

    const plugin = await dispatch.acceptManagedPromptClaim(
      handle,
      'ses-plugin',
      'msg-plugin',
      sent.key,
      'engineer',
    )
    assert.equal(plugin.ok, true, plugin.error)
    assert.equal(plugin.origin, 'AgentOwnerRoot')
  })
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

test('WHAT[managed-chat-execution-011] malformed exact identity seed is rejected by the production codec', () => {
  const malformed = JSON.parse(fixture)
  acceptedPayload(malformed).Evidence.IdentitySeed[0] = 'ForgedSeed'
  const result = chatExecution.canonicalize(JSON.stringify(malformed))

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

test('WHAT[managed-chat-execution-011] exact physical provider run and evidence are frozen', async () => {
  for (const [attempt, expected] of [
    [evidence({ physicalUserMessageId: 'msg-wrong' }), 'AttemptKeyMismatch'],
    [evidence({ providerRun: 'provider-wrong' }), 'ProviderRunConflict'],
    [evidence({ logicalRunId: 'run-other' }), 'EstablishedEvidenceConflict'],
  ]) {
    const startResult = await run(accept(), start(), start(attempt))
    assert.equal(startResult.ok, false)
    assert.equal(startResult.error.kind, expected)

    const terminalResult = await run(accept(), start(), terminal('Completed', attempt))
    assert.equal(terminalResult.ok, false)
    assert.equal(terminalResult.error.kind, expected)
  }
})
}
