import test from 'node:test'

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
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant,
      role: participant,
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

test('WHAT[dispatch-protocol-010] authority root public profile exposes identity without a model field', () => {
  const profile = profileOf()
  assert.deepEqual(
    { ...profile, model: profile.model },
    {
      session: 'ses_a',
      logicalRun: 'H(rt_1\nses_a\nmsg_u1)',
      authorityRoot: 'msg_u1',
      authorityKind: 'HumanRoot',
      identitySeed: profile.identitySeed,
      participantIdentity: profile.participantIdentity,
      model: undefined,
    },
  )
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
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant,
      role: participant,
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

test('WHAT[dispatch-protocol-010] PROMPT_006_unknown_authority_kind_fails_closed', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-send-format-invalid-'))
  try {
    const opened = await journal.JournalSurface_bootWithWriterId(base, 'writer-invalid', 'rt-invalid', 4242, '2026-01-01T00:00:00Z')
    assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
    try {
      const result = await dispatch.sendContinuation(
        capturingPort(),
        opened.journal,
        'ses-invalid',
        'reject malformed profile',
        'ProviderRetryAttempt',
        { ...profileFor(), authorityKind: 'UnknownRoot' },
        'Await',
      )
      assert.equal(result.ok, false)
      assert.match(result.error, /Unknown authority root kind/)
    } finally {
      journal.JournalSurface_dispose(opened.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})
test('WHAT[dispatch-protocol-010] PROMPT_006_send_payload_carries_participant_and_no_model', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-send-format-'))
  try {
    const opened = await journal.JournalSurface_bootWithWriterId(base, 'writer-send', 'rt-send', 4242, '2026-01-01T00:00:00Z')
    assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
    try {
      const owner = await acceptOwner(opened.journal, 'ses_006_owner')
      const seed = authority.issueInheritedIdentitySeed('engineer', owner).value
      const ownerRoot = await dispatch.sendAgentOwnerRoot(
        capturingPort(),
        opened.journal,
        'ses_006',
        'dispatch this',
        seed,
      )
      const accepted = await dispatch.acceptAgentOwnerRoot(opened.journal, 'ses_006', ownerRoot.key, 'msg-actual-root')
      assert.equal(accepted.ok, true, accepted.error)
      const continuation = await dispatch.sendContinuation(
        capturingPort(),
        opened.journal,
        'ses_006',
        'retry the fixed participant',
        'ProviderRetryAttempt',
        accepted.profile,
        'Await',
      )
      const captured = [observation(ownerRoot), observation(continuation)]

      assert.deepEqual(
        captured.map((value) => ({ session: value.session, text: value.text })),
        [
          { session: 'ses_006', text: 'dispatch this' },
          { session: 'ses_006', text: 'retry the fixed participant' },
        ],
      )

      assert.deepEqual(
        { agent: captured[0].agent, model: captured[0].model },
        { agent: 'engineer', model: null },
        'SendAgentOwnerRoot must carry Agent = fixed participant and Model = None',
      )
      assert.deepEqual(
        { agent: captured[1].agent, model: captured[1].model },
        { agent: 'engineer', model: null },
        'SendContinuation must carry Agent = fixed participant and Model = None',
      )

      assert.equal(captured[0].directory, null, 'no directory was given')
    } finally {
      journal.JournalSurface_dispose(opened.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})
test('WHAT[dispatch-protocol-010] DP_010_send_agent_owner_root_is_strictly_model_free', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-dp010-model-free-'))
  try {
    const opened = await journal.JournalSurface_bootWithWriterId(base, 'writer-dp010-model-free', 'rt-dp010-model-free', 4242, '2026-01-01T00:00:00Z')
    assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
    try {
      const owner = await acceptOwner(opened.journal, 'ses_dp010_owner')
      const seed = authority.issueInheritedIdentitySeed('engineer', owner).value
      const sentOptions = []
      const port = {
        SubscribeTerminal: () => ({ Dispose: () => {} }),
        SendPrompt: async (sessionId, text, options) => {
          sentOptions.push({
            session: sessionId,
            text,
            agent: options.Agent ?? null,
            model: options.Model ?? null,
          })
          return dispatch.admittedWithReceipt('accepted-dp010')
        },
      }
      const sent = await dispatch.sendAgentOwnerRoot(port, opened.journal, 'ses_dp010', 'strictly model free', seed)
      assert.equal(sent.ok, true, sent.ok ? '' : sent.error)
      assert.deepEqual(
        sentOptions,
        [{ session: 'ses_dp010', text: 'strictly model free', agent: 'engineer', model: null }],
        'the Root send path must construct its Host options with Model = None',
      )
    } finally {
      journal.JournalSurface_dispose(opened.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})
test('WHAT[dispatch-protocol-010] DP_010_send_without_session_agent_cache_succeeds', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-dp010-no-cache-'))
  try {
    const opened = await journal.JournalSurface_bootWithWriterId(base, 'writer-dp010-no-cache', 'rt-dp010-no-cache', 4242, '2026-01-01T00:00:00Z')
    assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
    try {
      // No session agent cache is installed anywhere: the dispatch path must
      // not require one, and the Host send must project the validated seed.
      const owner = await acceptOwner(opened.journal, 'ses_dp010_nocache_owner')
      const seed = authority.issueInheritedIdentitySeed('engineer', owner).value
      const sent = await dispatch.sendAgentOwnerRoot(
        capturingPort(),
        opened.journal,
        'ses_dp010_nocache',
        'send without a session agent cache',
        seed,
      )
      assert.equal(sent.ok, true, sent.ok ? '' : sent.error)
      const observation = sent.observation
      assert.equal(observation.agent, 'engineer')
      assert.equal(observation.model, null)
    } finally {
      journal.JournalSurface_dispose(opened.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})
}

{
const assert = (await import('node:assert/strict')).default
const authority = await import('../../../dist/Interaction/Authority/RuntimeSurface.js')
const dispatch = await import('../../../dist/Interaction/Dispatch/DispatchSurface.js')
const { withJournal, acceptOwner, hostPort } = await import('./support/authority.mjs')

for (const [participant, tools] of [
  ['manager', [['*', false], ['todowrite-for-manager', true]]],
  ['engineer', undefined],
]) {
  test(`WHAT[dispatch-protocol-010] managed ${participant} assignments retain the accepted run and tool boundary across journal reopen`, async () => {
    await withJournal(`assignment-${participant}`, async (handle, reopen) => {
      const seed = authority.issueInheritedIdentitySeed(participant, await acceptOwner(handle))
      assert.equal(seed.ok, true, seed.error)
      const port = hostPort(async () => dispatch.admittedWithReceipt('accepted-assignment'))
      const first = await dispatch.sendManagedAssignment(port, handle, 'child', 'Investigate the current work.', seed.value, tools)
      assert.equal(first.ok, true, first.error)
      const accepted = await dispatch.acceptAgentOwnerRoot(handle, 'child', first.key, 'assignment-root')
      assert.equal(accepted.ok, true, accepted.error)
      handle = await reopen()

      const second = await dispatch.sendManagedAssignment(port, handle, 'child', 'Investigate the next work.', { kind: 'InvalidSeed' }, tools)
      assert.equal(second.ok, true, second.error)
      const continued = await dispatch.acceptManagedPromptClaim(handle, 'child', 'assignment-next', second.key, participant)
      assert.equal(continued.ok, true, JSON.stringify(continued.error))
      const profile = dispatch.projectionObservation(handle, 'child').activeLogicalRun
      assert.equal(profile.logicalRun, accepted.profile.logicalRun)
      assert.equal(profile.authorityRoot, 'assignment-root')
      assert.equal(profile.participantIdentity.participant, participant)
      assert.notEqual(first.key, second.key)
      for (const sent of [first, second]) {
        assert.equal(sent.observation.agent, participant)
        assert.equal(sent.observation.model, null)
        assert.deepEqual(sent.observation.tools, tools ?? null)
      }
    })
  })
}
test('WHAT[dispatch-protocol-010] synthetic send producers leave model selection to admission at the wire', async () => {
  await withJournal('dp010-producers', async (handle) => {
    const owner = await acceptOwner(handle)
    const seed = authority.issueInheritedIdentitySeed('engineer', owner).value
    const profiles = new Map()
    for (const session of ['ses_p_4', 'ses_p_5', 'ses_p_6']) {
      const root = await dispatch.sendAgentOwnerRoot(hostPort(async () => dispatch.admittedWithReceipt('receipt-root')), handle, session, 'root send', seed)
      assert.equal(root.ok, true, root.error)
      const accepted = await dispatch.acceptAgentOwnerRoot(handle, session, root.key, `msg-producers-root-${session}`)
      assert.equal(accepted.ok, true, accepted.error)
      profiles.set(session, accepted.profile)
    }

    const capturing = () => {
      const seen = []
      const port = hostPort(async (session, text, options) => {
        seen.push({ session, text, agent: options?.agent ?? null, model: options?.model ?? null })
        return dispatch.admittedWithReceipt('receipt-producers')
      })
      return { port, seen }
    }

    const cases = [
      ['sendAgentOwnerRoot', capturing(), (p) => dispatch.sendAgentOwnerRoot(p, handle, 'ses_p_1', 'text', seed)],
      ['sendAgentOwnerRootAwait', capturing(), (p) => dispatch.sendAgentOwnerRootAwait(p, handle, 'ses_p_2', 'text', seed)],
      ['sendManagedAssignment', capturing(), (p) => dispatch.sendManagedAssignment(p, handle, 'ses_p_3', 'text', seed, ['read', 'grep'])],
      ['sendContinuation', capturing(), (p) => dispatch.sendContinuation(p, handle, 'ses_p_4', 'text', 'ProviderRetryAttempt', profiles.get('ses_p_4'), 'Await')],
      ['sendGateNudgesConcurrently', capturing(), async (p) => (await dispatch.sendGateNudgesConcurrently(p, handle, 'ses_p_5', 'text', 'ProviderRetryAttempt', 'JoinGate', 'run-terminal', profiles.get('ses_p_5')))[0]],
      ['sendIdleContinuation', capturing(), (p) => dispatch.sendIdleContinuation(p, handle, 'ses_p_6', 'text', 'ProviderRetryAttempt', profiles.get('ses_p_6'), true)],
    ]

    for (const [name, capture, run] of cases) {
      const result = await run(capture.port)
      const failed = result.ok === false || result.outcome === 'Failed'
      assert.equal(failed, false, name + ': ' + (result.error ?? result.outcome))
      assert.equal(capture.seen.length, 1, name + ' sent one prompt')
      assert.equal(capture.seen[0].model, null, name + ' must leave model selection to admission')
    }
  })
})

}

{
const assert = (await import('node:assert/strict')).default
const { integrationTest } = await import('../../verification-system/tests/support/tier-gate.mjs')

// B4 compile isolation for dispatch-protocol-010 (GAP-136: the F# type-level
// proof that the root opaque signature carries no model authority). The
// probes compile against the real dispatch-runtime shard closure: Send.fsi
// declares the root send public constructors, IdentitySeed.fsi declares the
// root identity witness record and the identity seed union.
//
// WHAT-010 keeps model selection at Host admission: neither the root send
// public signature nor the root identity witness may offer a position where
// physical model authority could enter. The wire-level Model=null tests above
// prove the runtime behaviour; these probes prove the same boundary at
// compile time. The positive probe first proves every referenced symbol,
// reference and value is legal, so the only remaining cause of each negative
// failure is the type boundary refusing the model-authority injection:
//   A. a ModelTarget field added to the root identity witness record;
//   B. a model argument appended to the root send public constructor;
//   C. a transport receipt offered where the root identity seed is required
//      (WHAT-003: a receipt is transport acceptance, never authority).
integrationTest('WHAT[dispatch-protocol-010] compiler rejects adding physical model authority to the opaque root', async () => {
  const { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } = await import('node:fs')
  const { createHash } = await import('node:crypto')
  const { dirname: dirnameOf, join: joinPath, relative: relativeOf, resolve: resolveRoot } = await import('node:path')
  const { tmpdir } = await import('node:os')
  const { compileOwnerProject, planOwnerCompile } = await import('../../../scripts/lib/owner-compile.mjs')

  const ROOT = resolveRoot(import.meta.dirname, '../../..')
  const SOURCE_ROOT = joinPath(ROOT, 'src/Wanxiangshu')
  const DISPATCH_RUNTIME_SHARD = joinPath(
    SOURCE_ROOT,
    'Wanxiangshu.Owner.dispatch-protocol.dispatch-runtime.fsproj',
  )

  const POSITIVE_PROBE = [
    'namespace Wanxiangshu.Probe',
    '',
    'open Wanxiangshu.Foundation.Identity',
    'open Wanxiangshu.Foundation.Outcome',
    'open Wanxiangshu.Interaction.Authority',
    'open Wanxiangshu.Interaction.Dispatch',
    'open Wanxiangshu.Participant.Persona',
    '',
    'module LegalRootSendAndWitness =',
    '    /// The root identity witness record: exactly the four owner-declared',
    '    /// fields; no model authority anywhere.',
    '    let ownerWitness',
    '        (session: SessionId)',
    '        (run: LogicalRunId)',
    '        (rootMessage: AuthorityRootUserMessageId)',
    '        (participant: ParticipantIdentityInput)',
    '        : OwnerIdentityWitnessInput =',
    '        { OwnerSessionId = session',
    '          OwnerLogicalRunId = run',
    '          OwnerAuthorityRootUserMessageId = rootMessage',
    '          ParticipantIdentity = participant }',
    '',
    '    /// The root identity seed union: both cases carry identity evidence only.',
    '    let rootSeed (evidence: ParticipantIdentityEvidence) : PromptIdentitySeed =',
    '        PromptIdentitySeed.RootSelection evidence',
    '',
    '    let rehydratedSeed (seedInput: PromptIdentitySeedInput) =',
    '        PromptIdentitySeed.rehydrate seedInput',
    '',
    '    /// A transport receipt is a legal value in its own right (WHAT-003: it',
    '    /// is transport-layer acceptance, never message identity or authority).',
    '    let transportReceipt (outcome: SendOutcome) = outcome',
    '',
    '    /// The real root send public constructor (Send.fsi): port, session,',
    '    /// text, identity seed, directory, await mode, accepted callback —',
    '    /// seven parameters and no model parameter; the implementation fixes',
    '    /// Model = None.',
    '    let sendRoot',
    '        (runtime: PromptDispatcher.Runtime)',
    '        (port: IDispatchSessionPort)',
    '        (session: SessionId)',
    '        (seed: PromptAuthority.IdentitySeed) =',
    '        runtime.SendAgentOwnerRoot',
    '            port session "probe-root-send" seed None',
    '            PromptDispatcher.AwaitMode.Await None',
    '        |> ignore',
    '',
  ].join('\n')

  // Negative probe A: a ModelTarget field is added to the root identity
  // witness record. The record type has exactly four owner-declared fields,
  // so the extra field must be rejected at the record type boundary.
  const WITNESS_MODEL_FIELD_PROBE = [
    'namespace Wanxiangshu.Probe',
    '',
    'open Wanxiangshu.Foundation.Identity',
    'open Wanxiangshu.Interaction.Authority',
    'open Wanxiangshu.Participant.Persona',
    '',
    'module ModelTargetFieldOnRootWitness =',
    '    let witnessWithModelTarget',
    '        (session: SessionId)',
    '        (run: LogicalRunId)',
    '        (rootMessage: AuthorityRootUserMessageId)',
    '        (participant: ParticipantIdentityInput)',
    '        : OwnerIdentityWitnessInput =',
    '        { OwnerSessionId = session',
    '          OwnerLogicalRunId = run',
    '          OwnerAuthorityRootUserMessageId = rootMessage',
    '          ParticipantIdentity = participant',
    '          ModelTarget = "glm-5.3" }',
    '',
  ].join('\n')

  // Negative probe B: a model argument is appended to the root send public
  // constructor. The signature has seven parameters and no model parameter,
  // so the eighth application must be rejected by the type boundary.
  const ROOT_SEND_MODEL_ARGUMENT_PROBE = [
    'namespace Wanxiangshu.Probe',
    '',
    'open Wanxiangshu.Foundation.Identity',
    'open Wanxiangshu.Interaction.Authority',
    'open Wanxiangshu.Interaction.Dispatch',
    '',
    'module ModelTargetArgumentOnRootSend =',
    '    let sendRootWithModelTarget',
    '        (runtime: PromptDispatcher.Runtime)',
    '        (port: IDispatchSessionPort)',
    '        (session: SessionId)',
    '        (seed: PromptAuthority.IdentitySeed) =',
    '        runtime.SendAgentOwnerRoot',
    '            port session "probe-root-send" seed None',
    '            PromptDispatcher.AwaitMode.Await None',
    '            (Some "glm-5.3")',
    '        |> ignore',
    '',
  ].join('\n')

  // Negative probe C: a transport receipt is offered where the root identity
  // seed is required. A receipt is transport acceptance, not authority, so
  // the seed parameter must reject it by type.
  const RECEIPT_AS_SEED_PROBE = [
    'namespace Wanxiangshu.Probe',
    '',
    'open Wanxiangshu.Foundation.Identity',
    'open Wanxiangshu.Foundation.Outcome',
    'open Wanxiangshu.Interaction.Authority',
    'open Wanxiangshu.Interaction.Dispatch',
    '',
    'module TransportReceiptAsRootSeed =',
    '    let sendRootWithReceiptAsSeed',
    '        (runtime: PromptDispatcher.Runtime)',
    '        (port: IDispatchSessionPort)',
    '        (session: SessionId)',
    '        (receipt: SendOutcome) =',
    '        runtime.SendAgentOwnerRoot',
    '            port session "probe-root-send" receipt None',
    '            PromptDispatcher.AwaitMode.Await None',
    '        |> ignore',
    '',
  ].join('\n')

  // Source isolation: every compile input resolves to a copy under a temp
  // root, so the real workspace is never written and a crash mid-test cannot
  // leave the tree mutated. compileOwnerProject's scratchRoot only isolates
  // outputs; the compile items themselves are remapped here.
  const isolate = (plan, probeSource) => {
    const iso = mkdtempSync(joinPath(tmpdir(), 'wxs-dp010-iso-'))
    const items = plan.compileItems.map((item) => {
      const dest = joinPath(iso, 'src', relativeOf(SOURCE_ROOT, item))
      mkdirSync(dirnameOf(dest), { recursive: true })
      cpSync(item, dest)
      return dest
    })
    const probe = joinPath(iso, 'probe-dispatch-root-model.fs')
    writeFileSync(probe, probeSource)
    return { plan: { ...plan, compileItems: [...items, probe] }, iso }
  }

  const digestOf = (file) => ({
    hash: createHash('sha256').update(readFileSync(file)).digest('hex'),
    mtime: statSync(file).mtimeMs,
  })

  // Isolation evidence: the real owner sources keep their bytes and mtime.
  const realSend = joinPath(SOURCE_ROOT, 'Interaction/Dispatch/Send.fs')
  const realSendSignature = joinPath(SOURCE_ROOT, 'Interaction/Dispatch/Send.fsi')
  const realDispatcher = joinPath(SOURCE_ROOT, 'Interaction/Dispatch/Dispatcher.fs')
  const before = [digestOf(realSend), digestOf(realSendSignature), digestOf(realDispatcher)]

  const scratch = mkdtempSync(joinPath(tmpdir(), 'wxs-dp010-compile-'))
  const positiveIso = isolate(planOwnerCompile({ projectPath: DISPATCH_RUNTIME_SHARD }), POSITIVE_PROBE)
  const witnessModelFieldIso = isolate(
    planOwnerCompile({ projectPath: DISPATCH_RUNTIME_SHARD }),
    WITNESS_MODEL_FIELD_PROBE,
  )
  const rootSendModelArgumentIso = isolate(
    planOwnerCompile({ projectPath: DISPATCH_RUNTIME_SHARD }),
    ROOT_SEND_MODEL_ARGUMENT_PROBE,
  )
  const receiptAsSeedIso = isolate(
    planOwnerCompile({ projectPath: DISPATCH_RUNTIME_SHARD }),
    RECEIPT_AS_SEED_PROBE,
  )
  try {
    // Positive: the root witness record, the seed union, a receipt value, and
    // the real root send public constructor all constructed legally. This
    // proves every referenced symbol, every dependency and every value in the
    // negative probes is legal.
    const positive = await compileOwnerProject({
      projectPath: DISPATCH_RUNTIME_SHARD,
      scratchRoot: scratch,
      stdio: 'pipe',
      compilePlan: positiveIso.plan,
    })
    assert.equal(
      positive.ok,
      true,
      'legal root witness/seed/send construction must compile: ' +
        String(positive.stdout ?? '').slice(-400),
    )

    // The positive probe already ruled out syntax, missing references and
    // missing dependencies, so each remaining failure below is the type
    // boundary refusing the model-authority injection.
    const TYPE_BOUNDARY =
      /expected to have type|but here has|should have fields|does not exactly match|type mismatch|incompatible|is not a function|cannot be applied|too many arguments|should not be given this argument|does not contain a field|does not contain a label|field '[^']+' is not defined|FS0001/i

    const witnessModelField = await compileOwnerProject({
      projectPath: DISPATCH_RUNTIME_SHARD,
      scratchRoot: scratch,
      stdio: 'pipe',
      compilePlan: witnessModelFieldIso.plan,
    })
    assert.equal(
      witnessModelField.ok,
      false,
      'a ModelTarget field must not be expressible on the root identity witness',
    )
    assert.match(
      String(witnessModelField.stdout ?? '') + String(witnessModelField.stderr ?? ''),
      TYPE_BOUNDARY,
      'the diagnostic must be a type-boundary rejection, not a syntax or dependency error',
    )

    const rootSendModelArgument = await compileOwnerProject({
      projectPath: DISPATCH_RUNTIME_SHARD,
      scratchRoot: scratch,
      stdio: 'pipe',
      compilePlan: rootSendModelArgumentIso.plan,
    })
    assert.equal(
      rootSendModelArgument.ok,
      false,
      'a model argument must not be admissible on the root send public constructor',
    )
    assert.match(
      String(rootSendModelArgument.stdout ?? '') + String(rootSendModelArgument.stderr ?? ''),
      TYPE_BOUNDARY,
      'the diagnostic must be a type-boundary rejection, not a syntax or dependency error',
    )

    const receiptAsSeed = await compileOwnerProject({
      projectPath: DISPATCH_RUNTIME_SHARD,
      scratchRoot: scratch,
      stdio: 'pipe',
      compilePlan: receiptAsSeedIso.plan,
    })
    assert.equal(
      receiptAsSeed.ok,
      false,
      'a transport receipt must not be admissible where the root identity seed is required',
    )
    assert.match(
      String(receiptAsSeed.stdout ?? '') + String(receiptAsSeed.stderr ?? ''),
      TYPE_BOUNDARY,
      'the diagnostic must be a type-boundary rejection, not a syntax or dependency error',
    )

    // Isolation evidence: the real owner sources were never touched.
    const after = [digestOf(realSend), digestOf(realSendSignature), digestOf(realDispatcher)]
    assert.deepEqual(after, before, 'the real Dispatch sources are untouched (bytes and mtime)')
  } finally {
    rmSync(scratch, { recursive: true, force: true })
    rmSync(positiveIso.iso, { recursive: true, force: true })
    rmSync(witnessModelFieldIso.iso, { recursive: true, force: true })
    rmSync(rootSendModelArgumentIso.iso, { recursive: true, force: true })
    rmSync(receiptAsSeedIso.iso, { recursive: true, force: true })
  }
})
}
