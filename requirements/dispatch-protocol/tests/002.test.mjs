import test from 'node:test'
import * as managerWorkflow from '../../../dist/Mission/Manager/WorkflowSurface.js'
import * as quiescence from '../../../dist/OpenCode/Host/QuiescenceSurface.js'
import * as relayJournal from '../../../dist/Persistence/Journal/ObligationJournalSurface.js'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");
const dispatch = await import("../../../dist/Interaction/Dispatch/DispatchSurface.js");

const H = (input) => `H(${input})`
const RUNTIME = 'rt_1'
const SESSION = 'ses_a'
const findClaim = (projection, key) => projection.pendingClaims.find((claim) => claim.promptKey === key)
const promptOrigin = (kind) => authority.originForContinuation(kind)
const personas = {
  engineer: 'Engineer',
  coder: 'Coder',
  manager: 'Lead',
}
const rootSelection = (participant) => {
  const role = participant === 'predictor' ? 'inspector' : participant
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant,
      role,
      selectedTier: 'deep',
      persona: personas[participant] ?? 'Unknown',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
}
const inheritedSeed = (agent, physical) => {
  const owner = authority.createAuthorityRoot(
    H,
    RUNTIME,
    SESSION,
    'HumanRoot',
    physical,
    rootSelection('manager'),
  )
  assert.equal(owner.ok, true, owner.error)
  const inherited = authority.issueInheritedIdentitySeed(agent, owner.value)
  assert.equal(inherited.ok, true, inherited.error)
  return inherited.value
}
const profileOf = () => {
  const built = authority.createAuthorityRoot(
    H,
    RUNTIME,
    SESSION,
    'HumanRoot',
    'msg_u1',
    rootSelection('engineer'),
  )
  assert.equal(built.ok, true, built.ok ? '' : built.error)
  return built.value
}

test('WHAT[dispatch-protocol-002] DP_002_submit_records_the_receipt_without_resolving_the_claim', () => {
  const root = profileOf()
  const key = 'pk_s'
  const claim = authority.claimContinuation(key, SESSION, 'ManagerGuard', root, 'pd-1')

  let projection = authority.registerAuthority(root, authority.empty)
  projection = authority.registerClaim(claim, projection)
  assert.equal(findClaim(projection, key).receipt, null)

  const submitted = authority.submitClaim(key, 'accepted-9f', projection)
  const stored = findClaim(submitted, key)

  assert.deepEqual(
    {
      pending: submitted.pendingClaims.length,
      receipt: stored.receipt,
    },
    { pending: 1, receipt: 'accepted-9f' },
    'Submitted keeps claim pending: only real chat.message resolves it',
  )
})
test('WHAT[dispatch-protocol-002] DP_002_abandon_removes_the_claim_and_leaves_the_active_run_alone', () => {
  const root = profileOf()
  const key = 'pk_x'
  let projection = authority.registerAuthority(root, authority.empty)
  projection = authority.registerClaim(
    authority.claimContinuation(key, SESSION, 'BusyAgentNudge', root, 'pd-n'),
    projection,
  )

  const after = authority.abandonClaim(key, projection)

  assert.equal(after.pendingClaims.length, 0)
  assert.equal(after.activeLogicalRun.logicalRun, root.logicalRun)
})
test('WHAT[dispatch-protocol-002] DP_002_claim_records_payload_digest_and_participant', () => {
  const claim = authority.claimAgentOwnerRoot(
    'pk_o',
    SESSION,
    'pd-owner',
    inheritedSeed('manager', 'msg-claim-owner'),
  )
  assert.equal(claim.ok, true, claim.ok ? '' : claim.error)
  assert.deepEqual(
    {
      origin: claim.value.origin,
      payloadDigest: claim.value.payloadDigest,
      receipt: claim.value.receipt,
    },
    { origin: 'AuthorityRoot', payloadDigest: 'pd-owner', receipt: null },
  )
  assert.deepEqual(
    claim.value.identitySeed.participantIdentity,
    {
      origin: 'InheritedFromOwner',
      participant: 'manager',
      persona: 'Lead',
      personaCatalogVersion: 1,
      role: 'manager',
    },
    'the owner-root claim carries the fixed participant+role, never an effective agent',
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const dispatch = await import("../../../dist/Interaction/Dispatch/DispatchSurface.js");

const inheritedIdentitySeed = (session) => ({
  kind: 'InheritedFromOwner',
  ownerSession: `${session}-owner`,
  ownerLogicalRun: `run-${session}-owner`,
  ownerAuthorityRoot: `root-${session}-owner`,
  participantIdentity: {
    participant: 'engineer',
    role: 'engineer',
    selectedTier: 'deep',
    persona: 'Lead',
    personaCatalogVersion: 1,
    origin: 'InheritedFromOwner',
  },
})
const claim = (session, key, seq) => ({
  kind: 'claim',
  seq,
  runtime: 'rt-claims',
  session,
  promptKey: key,
  continuationKind: 'ManagerGuard',
  logicalRun: `run-${seq}`,
  authorityRoot: `root-${seq}`,
  identitySeed: inheritedIdentitySeed(session),
  payloadDigest: `pd-${seq}`,
})
const started = (seq, runtime) => ({ kind: 'runtime-start', seq, runtime })
const findClaim = (claims, key) => claims.find((value) => value.promptKey === key)

test('WHAT[dispatch-protocol-002] PROMPT_011_RuntimeStarted_advances_a_workspace_watermark_not_every_session', () => {
  const folded = dispatch.foldRuntimeStartWatermark([
    claim('ses_a', 'pk_a', 1),
    claim('ses_b', 'pk_b', 2),
    started(3, 'rt-1'),
    started(4, 'rt-2'),
    claim('ses_a', 'pk_late', 5),
    started(6, 'rt-3'),
  ])
  assert.equal(folded.ok, true, folded.ok ? '' : JSON.stringify(folded.error))

  const projections = folded.value
  assert.equal(projections.runtimeStartCount, 3)

  const earlyA = findClaim(projections.claims, 'pk_a')
  const earlyB = findClaim(projections.claims, 'pk_b')
  const lateA = findClaim(projections.claims, 'pk_late')

  assert.equal(earlyA.claimedAtRuntimeStartCount, 0)
  assert.equal(earlyB.claimedAtRuntimeStartCount, 0)
  assert.equal(lateA.claimedAtRuntimeStartCount, 2)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");
const dispatch = await import("../../../dist/Interaction/Dispatch/DispatchSurface.js");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");

const hash = (value) => `H(${value})`
const capturingPort = () => ({
  SubscribeTerminal: () => ({ Dispose: () => {} }),
  SendPrompt: async () => dispatch.admittedWithReceipt('accepted-006'),
})
const personas = {
  engineer: 'Engineer',
  coder: 'Coder',
  manager: 'Lead',
}
const rootSelection = (participant) => {
  const role = participant === 'predictor' ? 'inspector' : participant
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant,
      role,
      selectedTier: 'deep',
      persona: personas[participant] ?? 'Unknown',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
}
const profileFor = (runtime = 'rt-send', session = 'ses_006', physical = 'msg_u1', participant = 'engineer') => {
  const built = authority.createAuthorityRoot(hash, runtime, session, 'HumanRoot', physical, rootSelection(participant))
  assert.equal(built.ok, true, built.ok ? '' : built.error)
  return built.value
}
const acceptOwner = async (handle, session = 'ses_owner') => {
  const accepted = await dispatch.acceptHumanRootSelection(
    handle,
    session,
    `msg-${session}`,
    rootSelection('manager'),
  )
  assert.equal(accepted.ok, true, accepted.ok ? '' : accepted.error)
  return accepted.profile
}
const observation = (result) => {
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  assert.ok(result.observation)
  return result.observation
}

test('WHAT[dispatch-protocol-002] HOST_004_stale_idle_repair_is_abandoned_at_the_final_physical_send_boundary', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-idle-send-race-'))
  try {
    const opened = await journal.JournalSurface_bootWithWriterId(base, 'writer-idle-race', 'rt-idle-race', 4242, '2026-01-01T00:00:00Z')
    assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
    try {
      const session = 'ses_idle_race'
      const accepted = await dispatch.acceptHumanRoot(opened.journal, session, 'msg-root', 'blogger')
      assert.equal(accepted.ok, true, accepted.ok ? '' : accepted.error)

      let sends = 0
      const port = {
        SubscribeTerminal: () => ({ Dispose: () => {} }),
        SendPrompt: async () => {
          sends += 1
          return dispatch.admittedWithReceipt('should-not-send')
        },
      }

      const outcome = await dispatch.sendIdleContinuation(
        port,
        opened.journal,
        session,
        'repair stale terminal',
        'InteractionRepair',
        accepted.profile,
        'Superseded',
      )

      assert.equal(outcome.outcome, 'Superseded', 'stale final admission must become Superseded, not a send failure')
      assert.equal(outcome.error, 'Superseded', 'the exact typed quiescence failure must survive physical admission')
      assert.equal(sends, 0, 'superseded idle repair must never invoke the physical Host SendPrompt')
      assert.equal(outcome.observation, null, 'no physical send means no Host observation is captured')

      const projection = dispatch.projectionObservation(opened.journal, session)
      assert.equal(projection.pendingClaims.length, 0, 'durable claim must be closed as Abandoned')
      assert.equal(projection.claimSequences.length, 1, 'abandon preserves the spent exact repair occasion')
    } finally {
      journal.JournalSurface_dispose(opened.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import('node:assert/strict')
const { mkdtempSync, rmSync } = await import('node:fs')
const { tmpdir } = await import('node:os')
const { join } = await import('node:path')
const { default: test } = await import('node:test')
const authority = await import('../../../dist/Interaction/Authority/RuntimeSurface.js')
const dispatch = await import('../../../dist/Interaction/Dispatch/DispatchSurface.js')
const journal = await import('../../../dist/Persistence/Journal/Surface.js')

const openJournal = (base, writer, runtime, processId) =>
  journal.JournalSurface_bootWithWriterId(base, writer, runtime, processId, '2026-01-01T00:00:00Z')

const ownerSeedFor = async (handle) => {
  const owner = await dispatch.acceptHumanRoot(handle, 'ses_owner_dp002', 'msg-owner-dp002', 'manager')
  assert.equal(owner.ok, true, owner.error)
  const inherited = authority.issueInheritedIdentitySeed('engineer', owner.profile)
  assert.equal(inherited.ok, true, inherited.error)
  return inherited.value
}

test('WHAT[dispatch-protocol-002] a journal that cannot persist the claim prevents the Host send and leaves no durable trace', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-dp002-unwritable-'))
  try {
    const opened = await openJournal(base, 'writer-dp002-unwritable', 'rt-dp002-unwritable', 4242)
    assert.equal(opened.ok, true, JSON.stringify(opened.error))
    try {
      const seed = await ownerSeedFor(opened.journal)

      let sends = 0
      const port = {
        SubscribeTerminal: () => ({ Dispose() {} }),
        SubscribeFutureTerminal: () => ({ Dispose() {} }),
        SendPrompt: async () => {
          sends += 1
          return dispatch.admittedWithReceipt('must-not-send')
        },
      }

      journal.JournalSurface_dispose(opened.journal)

      await assert.rejects(
        () => dispatch.sendAgentOwnerRootAwait(port, opened.journal, 'ses_child_dp002', 'intent must precede the effect', seed),
        /Journal handle is disposed/,
        'a send whose claim cannot even be persisted must fail at the journal boundary',
      )
      assert.equal(sends, 0, 'the Host SendPrompt must not be entered when the claim append cannot succeed')
    } finally {
      // The handle was disposed inside the scenario; dispose is idempotent here.
      journal.JournalSurface_dispose(opened.journal)
    }

    const reopened = await openJournal(base, 'observer-dp002-unwritable', 'rt-dp002-observe', 4243)
    try {
      const projection = dispatch.projectionObservation(reopened.journal, 'ses_child_dp002')
      assert.equal(projection.pendingClaims.length, 0, 'no claim may survive a send that never persisted one')
      assert.equal(projection.claimSequences.length, 0, 'a failed claim admission must not consume a claim sequence')
    } finally {
      journal.JournalSurface_dispose(reopened.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})

test('WHAT[dispatch-protocol-002] the exact claim is already durable while the transport invocation is still in flight', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-dp002-inflight-'))
  try {
    const opened = await openJournal(base, 'writer-dp002-inflight', 'rt-dp002-inflight', 4242)
    try {
      const seed = await ownerSeedFor(opened.journal)

      let sends = 0
      const coldReads = []
      const port = {
        SubscribeTerminal: () => ({ Dispose() {} }),
        SubscribeFutureTerminal: () => ({ Dispose() {} }),
        SendPrompt: async (_session, _text, options) => {
          sends += 1

          // The transport has been entered but not completed: this is the
          // crash window between durable claim registration and the physical
          // send. A second writer cold-boots and reads the projection.
          const reopened = await openJournal(base, 'observer-dp002-inflight', 'rt-dp002-observe', 4243)
          try {
            const key = options.Metadata.wanxiangshu_prompt_key
            const projection = dispatch.projectionObservation(reopened.journal, 'ses_child_dp002')
            const claim = projection.pendingClaims.find((value) => value.promptKey === key)
            coldReads.push({
              found: Boolean(claim),
              pending: projection.pendingClaims.length,
              sequences: projection.claimSequences.length,
              promptKey: claim ? claim.promptKey : null,
              receipt: claim ? claim.receipt : 'missing',
            })
          } finally {
            journal.JournalSurface_dispose(reopened.journal)
          }

          return dispatch.admittedWithReceipt('receipt-dp002-inflight')
        },
      }

      const sent = await dispatch.sendAgentOwnerRootAwait(port, opened.journal, 'ses_child_dp002', 'intent must precede the effect', seed)
      assert.equal(sent.ok, true, sent.error)
      assert.equal(sends, 1, 'exactly one physical send for one claim')

      const key = sent.key
      assert.equal(coldReads.length, 1, 'the cold read ran inside the transport window')
      assert.deepEqual(coldReads[0], {
        found: true,
        pending: 1,
        sequences: 1,
        promptKey: key,
        receipt: null,
      }, 'a cold reader inside the transport window recovers the exact pending claim with no receipt, and the spent sequence stays consumed')
    } finally {
      journal.JournalSurface_dispose(opened.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import('node:assert/strict')
const { existsSync, mkdtempSync, readFileSync, rmSync } = await import('node:fs')
const { tmpdir } = await import('node:os')
const { join } = await import('node:path')
const dispatch = await import('../../../dist/Interaction/Dispatch/DispatchSurface.js')
const journal = await import('../../../dist/Persistence/Journal/Surface.js')

for (const newPhysicalInput of [false, true]) {
  test(`WHAT[dispatch-protocol-002] production Manager idle dispatch (crash-reconciliation-006 compatibility) ${newPhysicalInput ? 'abandons a captured permit revoked by Human physical ingress before SDK send' : 'sends with the same road and fresh captured permit'}`, async () => {
    const base = mkdtempSync(join(tmpdir(), 'wxs-manager-idle-send-'))
    const writer = 'writer-manager-idle-send'
    let opened
    try {
      opened = await journal.JournalSurface_bootWithWriterId(base, writer, 'rt-manager-idle-send', 4242, '2026-01-01T00:00:00Z')
      assert.equal(opened.ok, true, JSON.stringify(opened.error))
      const session = 'ses-manager-idle-send'
      const rootPhysical = 'msg-manager-root'
      const root = await dispatch.acceptHumanRoot(opened.journal, session, rootPhysical, 'manager')
      assert.equal(root.ok, true, root.error)
      const opening = await relayJournal.openIncumbency(opened.journal, session, 'inc-manager-idle-send')
      assert.equal(opening.ok, true, JSON.stringify(opening.error))

      const gate = quiescence.create()
      quiescence.beginAttempt(gate, session)
      const permit = quiescence.observeIdle(gate, session)
      let sends = 0
      const port = {
        SubscribeTerminal: () => ({ Dispose() {} }),
        SendPrompt: async () => {
          sends += 1
          return dispatch.admittedWithReceipt('receipt-manager-idle-send')
        },
      }

      const held = Promise.withResolvers()
      const waiting = Promise.withResolvers()
      const pending = (async () => {
        waiting.resolve()
        await held.promise
        await managerWorkflow.observeIdle(port, opened.journal, gate, permit, session, rootPhysical, rootPhysical, 'provider-manager-idle-send', base)
      })()
      await waiting.promise
      assert.equal(sends, 0, 'the captured Manager idle owner is held before invocation')
      if (newPhysicalInput) quiescence.observePhysicalMessage(gate, session, 'msg-new-human')
      held.resolve()
      await pending

      const projection = dispatch.projectionObservation(opened.journal, session)
      assert.equal(projection.claimSequences.length, 1, 'the actual Manager workflow must reach the durable dispatch owner')
      assert.equal(sends, newPhysicalInput ? 0 : 1, 'only the still fresh idle owner may enter Host SendPrompt')
      assert.deepEqual(quiescence.tryConsume(gate, permit), {
        accepted: false,
        failure: newPhysicalInput ? 'Revoked' : 'AlreadyConsumed',
      })
      if (newPhysicalInput) {
        assert.equal(projection.pendingClaims.length, 0, 'the rejected dispatch must leave no pending claim')
        const eventsDir = join(base, 'wanxiangshu', 'events')
        const legacy = join(eventsDir, `${writer}.ndjson`)
        const target = existsSync(legacy) ? legacy : join(eventsDir, `${writer}-0000.ndjson`)
        const facts = readFileSync(target, 'utf8')
          .trim().split('\n').map(line => JSON.parse(line).payload.Fact)
          .filter(fact => fact[0] === 'Agent' && fact[1][0] === 'Prompt')
          .map(fact => fact[1][1])
        const claimed = facts.filter(fact => fact[0] === 'PluginPromptClaimed')
        const abandoned = facts.filter(fact => fact[0] === 'PluginPromptAbandoned')
        assert.equal(claimed.length, 1)
        assert.equal(claimed[0][1].ContinuationKind, 'ManagerGuard')
        assert.equal(abandoned.length, 1)
        assert.deepEqual(abandoned[0][1].PromptKey, claimed[0][1].PromptKey)
        assert.equal(abandoned[0][1].Reason, 'SupersededBeforePhysicalSend')
        assert.equal(facts.filter(fact => fact[0] === 'PluginPromptSubmitted' || fact[0] === 'PluginPromptPhysicalAccepted').length, 0)
      }
    } finally {
      try {
        if (opened?.ok) journal.JournalSurface_dispose(opened.journal)
      } finally {
        rmSync(base, { recursive: true, force: true })
      }
    }
  })
}
}
