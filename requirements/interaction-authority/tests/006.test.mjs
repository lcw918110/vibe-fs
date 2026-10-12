import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");
const dispatch = await import("../../../dist/Interaction/Dispatch/DispatchSurface.js");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");
const obligationJournal = await import("../../../dist/Persistence/Journal/ObligationJournalSurface.js");

const participantIdentity = {
  participant: 'manager',
  role: 'manager',
  selectedTier: 'deep',
  persona: 'Lead',
  personaCatalogVersion: 1,
  origin: 'ResolvedAtRoot',
}
const rootSelection = {
  kind: 'RootSelection',
  ownerSession: null,
  ownerLogicalRun: null,
  ownerAuthorityRoot: null,
  participantIdentity,
}
const hostPort = (sendPrompt) => ({
  SubscribeTerminal: () => ({ Dispose: () => {} }),
  SendPrompt: sendPrompt,
})
const withJournal = async (label, action) => {
  const directory = mkdtempSync(join(tmpdir(), `wxs-authority-acceptance-${label}-`))
  const opened = await journal.JournalSurface_bootWithWriterId(
    directory,
    `writer-${label}`,
    `runtime-${label}`,
    4242,
    '2026-08-30T00:00:00Z',
  )
  assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))

  try {
    await action(opened.journal)
  } finally {
    journal.JournalSurface_dispose(opened.journal)
    rmSync(directory, { recursive: true, force: true })
  }
}
const acceptOwner = async (handle, session = 'ses-owner') => {
  const accepted = await dispatch.acceptHumanRootSelection(
    handle,
    session,
    `msg-${session}`,
    rootSelection,
  )
  assert.equal(accepted.ok, true, accepted.ok ? '' : accepted.error)
  return accepted.profile
}
const inheritedSeed = (owner, child = 'engineer') => {
  const issued = authority.issueInheritedIdentitySeed(child, owner)
  assert.equal(issued.ok, true, issued.ok ? '' : issued.error)
  return issued.value
}
const completeManagerLife = async (handle, session) => {
  const lifeId = `life-${session}`
  const opened = await obligationJournal.appendManagerLifecycle(handle, session, 'LifeOpened', {
    sessionId: session,
    lifeId,
    openingCursorSequence: 0,
    openingTextDigest: 'digest-opening',
    openingTextRef: 'blob-opening',
    openingUserMessageId: `msg-${session}`,
  })
  assert.equal(opened.ok, true, opened.ok ? '' : opened.error)
  const completed = await obligationJournal.appendManagerLifecycle(handle, session, 'LifeCompleted', {
    sessionId: session,
    lifeId,
    requestId: `finality-${session}`,
    terminalRef: `terminal-${session}`,
    terminalDigest: `digest-terminal-${session}`,
  })
  assert.equal(completed.ok, true, completed.ok ? '' : completed.error)
}

test('WHAT[interaction-authority-006] HumanRoot missing identity seed is rejected without authority', async () => {
  await withJournal('human-missing', async (handle) => {
    const result = await dispatch.acceptHumanRootSelection(handle, 'ses-human-missing', 'msg-human-missing', null)

    assert.equal(result.ok, false)
    assert.equal(result.error.kind, 'IdentityRejected')
    assert.match(result.error.reason, /explicit root-selection identity seed/i)
    assert.equal(dispatch.projectionObservation(handle, 'ses-human-missing').activeLogicalRun, null)
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");

const hash = (value) => `H(${value})`
const personas = {
  engineer: 'Engineer',
  coder: 'Coder',
  manager: 'Lead',
  reviewer: 'Auditor',
  inspector: 'Investigator',
  devops: 'Operator',
  plan: 'Planner',
}
const rootSelection = (agent) => {
  const role = agent === 'predictor' ? 'inspector' : agent
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant: agent,
      role,
      selectedTier: 'deep',
      persona: personas[agent] ?? 'Unknown',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
}
const rootFor = (agent = 'engineer', physical = 'msg_u1') => {
  const result = authority.createAuthorityRoot(hash, 'rt_1', 'ses_a', 'HumanRoot', physical, rootSelection(agent))
  assert.equal(result.ok, true, result.error)
  return result.value
}
const profile = (value) => ({
  session: value.session,
  logicalRun: value.logicalRun,
  authorityRoot: value.authorityRoot,
  authorityKind: value.authorityKind,
  participant: value.participantIdentity.participant,
  role: value.participantIdentity.role,
})
const register = (root) => authority.registerAuthority(root, authority.empty)
const continuation = (key, root, kind = 'ManagerGuard', payload = 'payload') =>
  authority.claimContinuation(key, 'ses_a', kind, root, payload)

test('WHAT[interaction-authority-006] IA_006_canonical_names_resolve_and_legacy_or_malformed_are_refused', () => {
  for (const name of ['engineer', 'manager', 'devops', 'plan']) {
    const result = authority.createAuthorityRoot(hash, 'rt_1', 'ses_a', 'HumanRoot', 'msg_u1', rootSelection(name))
    assert.equal(result.ok, true, result.error)
    assert.equal(authority.parseAgentName(name).ok, true)
  }
  for (const name of ['coder', 'inspector', 'browser', 'inquiry', 'distiller', 'build', 'student', 'teacher', 'meditator', 'executor', 'fast_coder']) {
    assert.equal(authority.parseAgentName(name).error.kind, 'LegacyAgentName')
    const result = authority.createAuthorityRoot(hash, 'rt_1', 'ses_a', 'HumanRoot', 'msg_u1', rootSelection(name))
    assert.equal(result.ok, false)
  }
  assert.equal(authority.parseAgentName('nonsense').error.kind, 'UnknownManagedAgent')
  assert.equal(authority.parseAgentName('fast-').error.kind, 'Malformed')
  assert.equal(authority.parseAgentName('fast-engineer').error.kind, 'Malformed')
  assert.equal(authority.parseAgentName('Engineer').error.kind, 'Malformed')
})
test('WHAT[interaction-authority-006] IA_006_agent_owner_root_claim_rejects_legacy_name', () => {
  const inherited = authority.issueInheritedIdentitySeed('nonsense', rootFor('manager'))
  assert.match(inherited.error, /invalid|legacy|managed|malformed/i)
  assert.equal(inherited.ok, false)
})
}
