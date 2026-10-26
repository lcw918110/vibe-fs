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
const lifecycle = await import("../../../dist/Mission/Lifecycle/Surface.js");
const { withSharedJournal } = await import("./support/authority.mjs");

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
    await action(opened.journal, directory)
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
const completeManagerLife = async (handle, session, directory) => {
  const surface = lifecycle.LifecycleSurface ?? lifecycle;
  const step = (requestId, updates) => {
    const receipt = surface.submitReview(directory, session, requestId, 'Manager', 'run-1', requestId, updates, [], '');
    assert.equal(receipt.status, 'Committed', receipt.reason);
    return receipt;
  };
  const reviewed = step('review', [{ key: 'job', value: 'do' }]);
  assert.equal(reviewed.kind, 'Reviewed', reviewed.reason);
  assert.equal(reviewed.milestone, '执行');
  const relayed = step('relay', [{ key: 'job', value: null }]);
  assert.equal(relayed.kind, 'Relayed', relayed.reason);
  const advanced = step('advance', []);
  assert.equal(advanced.kind, 'Advanced', advanced.reason);
  assert.equal(advanced.milestone, '收尾');
  const completed = step('close', []);
  assert.equal(completed.kind, 'Completed', completed.reason);
  assert.equal(completed.milestone, '已完成');
  const view = surface.inspected(directory, session);
  assert.equal(view.Delivered, true);
  assert.equal(view.ActiveId ?? null, null);
  const retirement = Array.from(view.Retirements ?? []).at(-1);
  assert.equal(retirement.Outcome, 'Completed');
  await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, session, retirement.RetirementId);
  assert.equal(dispatch.projectionObservation(handle, session).activeLogicalRun, null);
};

test('WHAT[interaction-authority-005] AgentOwnerRoot rejects RootSelection before Host send', async () => {
  await withJournal('owner-root-selection', async (handle) => {
    let providerSends = 0
    const result = await dispatch.sendAgentOwnerRootAwait(
      hostPort(async () => {
        providerSends += 1
        return dispatch.admittedWithReceipt('accepted-should-not-send')
      }),
      handle,
      'ses-owner-root-selection',
      'must not send',
      rootSelection,
    )

    assert.equal(result.ok, false)
    assert.match(result.error, /identity seed rejected.*ExpectedInheritedFromOwner/i)
    assert.equal(providerSends, 0)
    assert.equal(dispatch.projectionObservation(handle, 'ses-owner-root-selection').activeLogicalRun, null)
  })
})
test('WHAT[interaction-authority-005] inherited identity is durable in PluginPromptClaimed before Host send', async () => {
  await withJournal('claim-before-send', async (handle) => {
    const owner = await acceptOwner(handle)
    const seed = inheritedSeed(owner)
    const observations = []

    const result = await dispatch.sendAgentOwnerRootAwait(
      hostPort(async () => {
        const child = dispatch.projectionObservation(handle, 'ses-claim-before-send')
        observations.push({
          providerSends: 1,
          pendingClaims: child.pendingClaims.length,
          activeAuthority: child.activeLogicalRun,
          seed: child.pendingClaims[0]?.identitySeed,
        })
        return dispatch.admittedWithReceipt('accepted-claim-before-send')
      }),
      handle,
      'ses-claim-before-send',
      'claim before provider work',
      seed,
    )

    assert.equal(result.ok, true, result.ok ? '' : result.error)
    assert.deepEqual(observations, [{
      providerSends: 1,
      pendingClaims: 1,
      activeAuthority: null,
      seed,
    }])
  })
})
test('WHAT[interaction-authority-005] stale owner witness is rejected before Host send', async () => {
  await withJournal('stale-owner', async (handle) => {
    const owner = await acceptOwner(handle)
    const seed = inheritedSeed(owner)
    const staleSeed = { ...seed, ownerLogicalRun: `${seed.ownerLogicalRun}-stale` }
    let providerSends = 0

    const result = await dispatch.sendAgentOwnerRootAwait(
      hostPort(async () => {
        providerSends += 1
        return dispatch.admittedWithReceipt('accepted-should-not-send')
      }),
      handle,
      'ses-stale-child',
      'stale owner must not send',
      staleSeed,
    )

    assert.equal(result.ok, false)
    assert.match(result.error, /OwnerLogicalRunIdMismatch/)
    assert.equal(providerSends, 0)
    assert.equal(dispatch.projectionObservation(handle, 'ses-stale-child').activeLogicalRun, null)
  })
})
test('WHAT[interaction-authority-005] owner superseded after claim rejects physical acceptance without child authority', async () => {
  await withSharedJournal('owner-race', async (handle, _reopen, directory) => {
    const owner = await acceptOwner(handle, 'ses-race-owner')
    const seed = inheritedSeed(owner)
    let providerSends = 0

    const result = await dispatch.sendAgentOwnerRootAwait(
      hostPort(async () => {
        providerSends += 1
        const claimed = dispatch.projectionObservation(handle, 'ses-race-child')
        assert.equal(claimed.pendingClaims.length, 1)
        assert.deepEqual(claimed.pendingClaims[0].identitySeed, seed)

        await completeManagerLife(handle, 'ses-race-owner', directory)

        const superseded = await dispatch.acceptHumanRootSelection(
          handle,
          'ses-race-owner',
          'msg-ses-race-owner-superseded',
          rootSelection,
        )
        assert.equal(superseded.ok, true, superseded.ok ? '' : superseded.error)
        return dispatch.admittedWithPhysicalMessage('msg-race-child')
      }),
      handle,
      'ses-race-child',
      'owner changes before physical acceptance',
      seed,
    )

    const child = dispatch.projectionObservation(handle, 'ses-race-child')
    assert.equal(result.ok, false)
    assert.match(result.error, /OwnerLogicalRunIdMismatch|OwnerAuthorityRootUserMessageIdMismatch/)
    assert.equal(providerSends, 1)
    assert.equal(child.activeLogicalRun, null)
    assert.equal(child.pendingClaims.length, 1)
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const intent = await import("../../../dist/Interaction/Admission/ChatAdmissionIntentSurface.js");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");

const hash = (value) => `H(${value})`
const personas = {
  engineer: 'Engineer',
  coder: 'Coder',
  manager: 'Lead',
  reviewer: 'Auditor',
  inspector: 'Investigator',
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
const inheritedSelection = (agent, physical) => {
  const owner = authority.createAuthorityRoot(
    hash,
    'rt_owner',
    'ses_owner',
    'HumanRoot',
    `owner_${physical}`,
    rootSelection('manager'),
  )
  assert.equal(owner.ok, true, owner.error)
  const inherited = authority.issueInheritedIdentitySeed(agent, owner.value)
  assert.equal(inherited.ok, true, inherited.error)
  return inherited.value
}
const rootFor = (agent = 'engineer', physical = 'msg_u1', kind = 'HumanRoot') => {
  const seed = kind === 'AgentOwnerRoot' ? inheritedSelection(agent, physical) : rootSelection(agent)
  const result = authority.createAuthorityRoot(hash, 'rt_1', 'ses_a', kind, physical, seed)
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

test('WHAT[interaction-authority-005] IA_005_every_continuation_kind_is_parseable_and_not_root', () => {
  const kinds = [
    'InteractionRepair',
    'JoinGuard',
    'ManagerGuard',
    'BusyAgentNudge',
    'HumanMessage',
    'ManagedDelegationAssignment',
    'ProviderRetryAttempt',
    'FissionHandoff',
  ]

  for (const kind of kinds) {
    assert.deepEqual(authority.originForContinuation(kind), { kind: 'Continuation', label: kind })
    assert.deepEqual(authority.tryParseContinuationKind(kind), { kind })
  }
  assert.equal(authority.tryParseContinuationKind('HumanRoot'), null)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const intent = await import("../../../dist/Interaction/Admission/ChatAdmissionIntentSurface.js");

const message = (overrides = {}) => ({
  sessionId: 'ses-chat',
  physicalUserMessageId: 'msg-chat',
  explicitAgent: null,
  promptKey: null,
  hostCompaction: false,
  hostSynthetic: false,
  ...overrides,
})
const snapshot = (overrides = {}) => ({
  available: true,
  activeParticipant: null,
  activeKind: null,
  claims: [],
  acceptedContinuations: [],
  ...overrides,
})
const decide = (decoded, durable = snapshot()) => intent.resolve(decoded, durable)

test('WHAT[interaction-authority-005] exhaustive chat admission intent table', () => {
  const claim = {
    promptKey: 'prompt-1',
    sessionId: 'ses-chat',
    origin: 'InteractionRepair',
    participant: 'engineer',
  }

  const cases = [
    {
      label: 'unmanaged fresh host message',
      decoded: message(),
      expected: { case: 'NoManagedExecution', reason: 'UnmanagedMessage' },
    },
    {
      label: 'fresh external managed root',
      decoded: message({ explicitAgent: 'engineer' }),
      expected: {
        case: 'ExternalRootIntent',
        sessionId: 'ses-chat',
        physicalUserMessageId: 'msg-chat',
        explicitAgent: 'engineer',
        participant: 'engineer',
        origin: 'HumanRoot',
        identitySeed: 'RootSelection',
      },
    },
    {
      label: 'exact claimed plugin prompt',
      decoded: message({ promptKey: 'prompt-1' }),
      durable: snapshot({ claims: [claim] }),
      expected: {
        case: 'PendingPromptIntent',
        sessionId: 'ses-chat',
        physicalUserMessageId: 'msg-chat',
        promptKey: 'prompt-1',
        participant: 'engineer',
        origin: 'InteractionRepair',
        identitySeed: 'RootSelection',
      },
    },
    {
      label: 'host compaction',
      decoded: message({ hostCompaction: true }),
      expected: { case: 'HostInternal', origin: 'HostInternal' },
    },
    {
      label: 'host synthetic',
      decoded: message({ hostSynthetic: true }),
      expected: { case: 'HostInternal', origin: 'HostInternal' },
    },
  ]

  for (const row of cases) {
    assert.deepEqual(intent.resolve(row.decoded, row.durable ?? snapshot()), row.expected, row.label)
  }
})
test('WHAT[interaction-authority-005] rejects managed intent without physical message identity', () => {
  assert.deepEqual(decide(message({ physicalUserMessageId: null, explicitAgent: 'engineer' })), {
    case: 'Reject',
    reason: 'ManagedIntentMissingPhysicalUserMessageId',
  })

  assert.deepEqual(
    decide(
      message({ physicalUserMessageId: null, promptKey: 'prompt-1' }),
      snapshot({
        claims: [
          {
            promptKey: 'prompt-1',
            sessionId: 'ses-chat',
            origin: 'InteractionRepair',
            participant: 'engineer',
          },
        ],
      }),
    ),
    { case: 'Reject', reason: 'ManagedIntentMissingPhysicalUserMessageId' },
  )
})
test('WHAT[interaction-authority-005] rejects insufficient exact identity evidence', () => {
  const rows = [
    [message({ sessionId: null, explicitAgent: 'engineer' }), 'ManagedIntentMissingSessionId'],
    [message({ explicitAgent: 'legacy-coder' }), 'InvalidExplicitAgent'],
    [message({ promptKey: 'missing' }), 'PromptKeyNotClaimed'],
  ]

  for (const [decoded, reason] of rows) {
    assert.deepEqual(decide(decoded), { case: 'Reject', reason })
  }
})
}
