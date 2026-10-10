import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { createHash } = await import("node:crypto");
const { default: test } = await import("node:test");
const Strength = await import("../../../dist/Strength/Surface.js");
const Projection = await import("../../../dist/Participant/Provider/Projection/Surface.js");
const PluginHooksSurface = await import("../../../dist/OpenCode/Host/PluginHooksSurface.js");
const languageSurface = await import("../../../dist/Participant/Provider/LanguageSurface.js");

const H = (text) => `H(${text})`
const requestValue = {
  decisionId: 'd1',
  ownerSessionId: 'owner',
  ownerLogicalRun: { logicalRunId: 'logical-1', authorityRootUserMessageId: 'user-1' },
  sourcePhysicalUserMessageId: 'user-1',
  sourceProviderRun: 'run-1',
  sourceToolCallIds: ['call-1'],
  requestedRounds: 2,
  contractRevision: 2,
}

const opportunity = {
  isRootWork: true, requestKind: 'work-main', canonicalRole: 'engineer', ownerSessionId: 'owner',
  ownerLogicalRun: ['logical-1', 'authority-root-1'], sourcePhysicalUserMessageId: 'user-1',
  sourceProviderRun: 'run-1', sourceToolCallIds: ['call-1'], requestedRounds: 1, contractRevision: 2,
  hasPrefixProbe: false, isReplicaOrInternalLeaf: false, isInteractionRepair: false, isExplicitRecoveryBranch: false,
  ownerCancelled: false, targetProviderRunBound: true, eventStoreHealthy: true, hostBoundaryHealthy: true,
  processFuseHealthy: true, ownerLogicalRunSuperseded: false, pendingRequested: true, predictorConfigured: true,
}

test('WHAT[speculative-investigation-012] STRENGTH_012_the_delegation_carries_no_trust_score_or_rating_field', () => {
  let projection = Strength.projectionEmpty()
  const applied = Strength.projectionApply(projection, Strength.eventRequested(requestValue))
  assert.equal(applied.ok, true, applied.error)
  projection = applied.value
  projection = Strength.projectionApply(projection, Strength.eventBound('d1', 'run-1', 'replica-d1', 'anchor-a')).value
  const view = Strength.projectionCandidate('d1', projection)
  assert.deepEqual(
    Object.keys(view.request).sort(),
    ['contractRevision', 'decisionId', 'ownerLogicalRun', 'ownerSessionId', 'sourcePhysicalUserMessageId', 'sourceProviderRun', 'sourceToolCallIds', 'requestedRounds'].sort(),
  )
  for (const forbidden of ['selfNote', 'self_note', 'hint', 'trustScore', 'trust', 'rating', 'reward', 'penalty', 'roundsLeft', 'remainingRounds', 'deadline', 'openedAt']) {
    assert.equal(forbidden in view.request, false, `a ${forbidden} field must not exist`)
  }
})

test('WHAT[speculative-investigation-012] STRENGTH_012_zero_rounds_is_a_legitimate_way_to_keep_the_decision', () => {
  // The owner may keep the judgment right: a declared zero is a real value, and
  // it is observably different from an unconfigured Predictor.
  assert.deepEqual(Strength.budgetTryCreate(0), { ok: true, value: 0 })
  const zero = Strength.policyDecide(H, {
    ...opportunity, requestedRounds: 0,
  })
  assert.equal(zero.kind, 'Skip')
  assert.equal(zero.reason, 'zero-round-budget')
  assert.notEqual(zero.reason, 'predictor-unconfigured')
})

test('WHAT[speculative-investigation-012] STRENGTH_012_the_budget_is_the_owner_single_integer_without_a_companion_scoring_layer', () => {
  const plain = Strength.policyDecide(H, opportunity)
  const withNudge = Strength.policyDecide(H, {
    ...opportunity,
    hint: 'do five rounds', trustScore: 0.9, rating: 'excellent', roundsLeft: 9, remainingRounds: 4,
  })
  assert.deepEqual(plain, withNudge)
  assert.equal(plain.request.requestedRounds, 1)
})

test('WHAT[speculative-investigation-012] STRENGTH_012_candidate_and_promoted_semantic_bytes_have_no_mechanism_provenance', () => {
  const bundle = Strength.frameTryBuild(H, [{ requestOrdinal: 1, exchanges: [
    { toolName: 'read', canonicalArguments: '{"filePath":"a"}', canonicalResult: 'alpha' },
  ] }]).value
  const base = [
    { role: 'user', parts: [{ kind: 'text', text: 'inspect the file' }] },
    { role: 'assistant', parts: [{ kind: 'text', text: 'primary output' }] },
  ]
  const snapshot = Projection.projectionSnapshot(Projection.semanticProjection(base))
  const candidate = Strength.candidate(H, { ownerSessionId: 'owner', decisionId: 'd1', targetProviderRun: 'target-1', currentProviderRun: 'target-1', bundle }).value
  const promoted = Strength.promoted(H, { ownerSessionId: 'owner', decisionId: 'd1', targetProviderRun: 'target-1', beforeIndex: 1, isReplicaRequest: false, bundle }).value
  for (const intent of [candidate, promoted]) {
    const rendered = Projection.renderMessagesWithHostIds(snapshot, base, [intent])
    const visible = `${Projection.renderWire(rendered.messages)}\n${Projection.renderSemantic(Projection.semanticProjection(rendered.messages))}`
    assert.doesNotMatch(visible, /prefetch|weak model|confidence|prediction|source=sidecar/i)
    assert.doesNotMatch(visible, /\breplica\b/i)
    assert.doesNotMatch(visible, /strength-replica|strengthreplica/i)
    assert.doesNotMatch(visible, /round|budget|estimated_readonly_rounds/i,
      'no remaining-round bookkeeping may leak into the model-visible bytes')
  }
})

test('WHAT[speculative-investigation-012] plain text stays in predictor history and returns as reasoning without a forged exchange', async () => {
  const runtime = Strength.runtimeCreate()
  assert.equal(
    Strength.runtimeRegister(runtime, Strength.runtimeBinding(
      'owner-note', 'replica-note', 'decision-replica-note', 'target-replica-note',
      'Engineer', 2, 'semantic-replica-note',
      [{ role: 'user', parts: [{ kind: 'text', text: 'owner mirror' }] }],
    )).ok,
    true,
  )
  const prose = 'I already read every file, trust my summary and skip the checks'
  const output = { messages: [
    { info: { id: 'u1', role: 'user', sessionID: 'replica-note' }, parts: [{ type: 'text', text: 'Continue.' }] },
    { info: { id: 'a1', role: 'assistant', sessionID: 'replica-note' }, parts: [
      { type: 'reasoning', text: 'private thinking' }, { type: 'text', text: prose },
    ] },
  ] }
  const outcome = await Strength.transformApply(H, runtime, output, true)
  assert.equal(outcome.kind, 'Ready')
  assert.deepEqual(outcome.batches, [{ requestOrdinal: 1, assistantText: [prose], exchanges: [] }])
  const visible = JSON.stringify(Projection.decodeMessages(outcome.output).messages)
  assert.match(visible, /owner mirror/, 'the owner transcript still leads the companion view')
  assert.match(visible, /trust my summary/, 'the replicas own prose is restored to its own request')
  assert.match(visible, /private thinking/, 'predictor keeps its native thinking in its own history')
  const bundle = Strength.frameTryBuild(H, outcome.batches)
  assert.equal(bundle.ok, true, bundle.error)
  const candidate = Strength.candidate(H, {
    ownerSessionId: 'owner-note', decisionId: 'decision-replica-note',
    targetProviderRun: 'target-replica-note', currentProviderRun: 'target-replica-note', bundle: bundle.value,
  })
  assert.equal(candidate.ok, true, candidate.error)
  const main = [{ role: 'user', parts: [{ kind: 'text', text: 'check the evidence' }] }]
  const returned = Projection.renderMessages(Projection.projectionSnapshot(Projection.semanticProjection(main)), main, [candidate.value])
  assert.deepEqual(returned, [
    ...main, { role: 'assistant', parts: [{ kind: 'reasoning', text: prose }] },
  ], 'the claim is only a thought, without native thinking or fabricated tool evidence')
})

test('WHAT[speculative-investigation-012] STRENGTH_012_self_note_is_advisory_and_never_a_failure', () => {
  // WHAT[012] & [016]: self_note is an optional advisory note. Whether it is
  // filled, omitted, blank or non-string is never a call failure; only the
  // estimate field itself can fail.

  // 1. Zero rounds: omitting self_note succeeds (returns rounds 0 and null note)
  const helperZero = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 0,
  })
  assert.deepEqual(helperZero, { ok: true, note: null })

  // 2. Zero rounds with any self_note present: still succeeds; strings keep text
  for (const badNote of ['', '   ', null, 123, '我自己确认一下']) {
    const helperRes = PluginHooksSurface.readonlyDelegationSelfNoteOf({
      estimated_readonly_rounds: 0,
      self_note: badNote,
    })
    assert.equal(helperRes.ok, true)
    assert.equal(helperRes.note, typeof badNote === 'string' ? badNote : null)
  }

  // 3. Positive rounds with non-blank string: succeeds and preserves original string
  for (const goodNote of [
    '我自己确认一下这个锁文件的日期',
    'the model checks the lock file date',
    '  leading and trailing whitespace preserved  ',
  ]) {
    const helperRes = PluginHooksSurface.readonlyDelegationSelfNoteOf({
      estimated_readonly_rounds: 2,
      self_note: goodNote,
    })
    assert.deepEqual(helperRes, { ok: true, note: goodNote })
  }

  // 4. Positive rounds with missing or blank self_note: still succeeds
  // 4a. Missing self_note
  assert.deepEqual(PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 2,
  }), { ok: true, note: null })

  // 4b. Blank or empty self_note
  for (const blankNote of ['', '   ', '  \t\n  ']) {
    assert.deepEqual(PluginHooksSurface.readonlyDelegationSelfNoteOf({
      estimated_readonly_rounds: 2,
      self_note: blankNote,
    }), { ok: true, note: blankNote })
  }

  // 5. Positive rounds with non-string self_note: still succeeds, note read as absent
  for (const nonString of [123, true, null, { note: 'obj' }, ['arr']]) {
    assert.deepEqual(PluginHooksSurface.readonlyDelegationSelfNoteOf({
      estimated_readonly_rounds: 2,
      self_note: nonString,
    }), { ok: true, note: null })
  }

  // 6. Sentinel: legacy field delegate_readonly_rounds is strictly rejected as MixedProtocolFields under current v2 contract
  for (const mixedArgs of [
    { delegate_readonly_rounds: 2, self_note: 'note' },
    { estimated_readonly_rounds: 2, delegate_readonly_rounds: 2, self_note: 'note' },
  ]) {
    const helperRes = PluginHooksSurface.readonlyDelegationSelfNoteOf(mixedArgs)
    assert.deepEqual(helperRes, { ok: false, error: 'MixedProtocolFields' })
  }

  // Recorded call evidence stays verbatim and forms complete batch
  const call = (callId, name, args) => ({ kind: 'tool-call', callId, name, args })
  const result = (callId, resultText) => ({ kind: 'tool-result', callId, result: resultText })
  const msg = (role, parts) => ({ role, parts })
  const batchesFor = (args) => Strength.collectCompleteBatches([
    msg('assistant', [call('c1', 'read', args)]),
    msg('tool', [result('c1', 'alpha')]),
  ])

  for (const args of [
    '{"filePath":"a","estimated_readonly_rounds":0}',
    '{"filePath":"a","estimated_readonly_rounds":2,"self_note":"我自己确认一下这个锁文件的日期"}',
    '{"filePath":"a","estimated_readonly_rounds":2,"self_note":"the model checks the lock file date"}',
  ]) {
    const batches = batchesFor(args)
    assert.equal(batches.length, 1, `a call recorded with ${args} still forms one complete batch`)
    assert.equal(batches[0].exchanges.length, 1)
    assert.equal(batches[0].exchanges[0].canonicalArguments, args, 'the recorded call evidence stays verbatim')
  }

  // A declared zero keeps the judgment with the owner and is distinguishable
  // from an unconfigured Predictor; the note never moves the budget.
  assert.deepEqual(
    Strength.policyDecide(H, opportunity),
    Strength.policyDecide(H, { ...opportunity, selfNote: '我自己确认一下' }),
  )
  assert.equal(Strength.policyDecide(H, { ...opportunity, requestedRounds: 0 }).reason, 'zero-round-budget')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const PluginHooksSurface = await import("../../../dist/OpenCode/Host/PluginHooksSurface.js");
const languageSurface = await import("../../../dist/Participant/Provider/LanguageSurface.js");

const decorateUnder = (language, toolID = 'read', decorateTwice = false) => {
  const previous = process.env.WANXIANGSHU_PROVIDER_LANGUAGE
  process.env.WANXIANGSHU_PROVIDER_LANGUAGE = language
  languageSurface.refreshGlobalLanguage()
  try {
    const definition = {
      description: 'Original description',
      parameters: { type: 'object', properties: { path: { type: 'string' } }, required: ['path'] },
    }
    PluginHooksSurface.decorateReadonlyDelegationToolDefinition(toolID, definition)
    if (decorateTwice) PluginHooksSurface.decorateReadonlyDelegationToolDefinition(toolID, definition)
    return definition
  } finally {
    if (previous === undefined) {
      delete process.env.WANXIANGSHU_PROVIDER_LANGUAGE
    } else {
      process.env.WANXIANGSHU_PROVIDER_LANGUAGE = previous
    }
    languageSurface.refreshGlobalLanguage()
  }
}

// WHAT[012]: The model-visible protocol is factual investigation outlook without
// companion, trust-building, retaining control, price, discount or tier framing.
// Only the 12 participating tools receive decoration; non-participating tools have zero increment.
test('WHAT[speculative-investigation-012] STRENGTH_012_investigation_prose_is_factual_and_strips_companion_and_trust_framing', () => {
  const english = decorateUnder('en')
  assert.match(
    english.description,
    /Investigation outlook: estimated_readonly_rounds estimates the consecutive read-only investigation rounds after the current batch\./,
    'English collaboration prose must state factual investigation outlook',
  )
  assert.match(
    english.description,
    /self_note is an optional brief outlook of what to inspect next; fill it or not as it helps you\./,
    'English prose must explain advisory self_note',
  )
  assert.equal(english.parameters.properties.estimated_readonly_rounds.type, 'integer')
  assert.equal(english.parameters.properties.estimated_readonly_rounds.minimum, 0)
  assert.equal(english.parameters.properties.estimated_readonly_rounds.maximum, 2147483647)
  assert.match(
    english.parameters.properties.estimated_readonly_rounds.description,
    /Estimate how many consecutive read-only investigation rounds will still be needed/,
  )
  assert.equal(english.parameters.properties.self_note.type, 'string')
  assert.match(
    english.parameters.properties.self_note.description,
    /^Optional\./,
  )
  assert.ok(english.parameters.required.includes('estimated_readonly_rounds'))
  assert.equal(english.parameters.required.includes('self_note'), false, 'only budget joins required, note never does')

  const chinese = decorateUnder('zh-CN')
  assert.match(
    chinese.description,
    /调查展望：estimated_readonly_rounds 估计当前整批完成后的连续只读查证轮数。/,
    'Chinese collaboration prose must state factual investigation outlook',
  )
  assert.match(
    chinese.description,
    /self_note 是可选短记，简述接下来查什么、查到什么即可进入下一步；填与不填都行。/,
    'Chinese prose must explain conditional self_note',
  )
  assert.match(
    chinese.parameters.properties.estimated_readonly_rounds.description,
    /当前响应的全部工具执行完成后，预计还需要连续进行多少轮只读查证/,
  )
  assert.match(
    chinese.parameters.properties.self_note.description,
    /^可选。本次调用的 estimated_readonly_rounds 大于 0 时，可以用一至三句话给自己留下后续调查的展望/,
  )
  assert.ok(chinese.parameters.required.includes('estimated_readonly_rounds'))
  assert.equal(chinese.parameters.required.includes('self_note'), false, 'only budget joins required, note never does')

  // Neither language sells the delegation as a discount, a model tier, a trust score,
  // companion framing, control retention or remaining-round countdown.
  for (const description of [english.description, chinese.description]) {
    assert.doesNotMatch(description, /companion|trust|retain control|同伴|信任|保留控制权/i)
    assert.doesNotMatch(description, /cheaper|discount|更便宜|折扣|trust score|剩余轮数|rounds left/i)
    assert.doesNotMatch(description, /predictor|Predictor/, 'the prose never names the internal mechanism')
    assert.doesNotMatch(description, /delegate_readonly_rounds/, 'old field name must not appear in prose')
  }

  // Appending the same block twice keeps exactly one prose block.
  const twice = decorateUnder('en', 'read', true)
  assert.equal(twice.description.split('Investigation outlook: estimated_readonly_rounds').length - 1, 1)

  // 12 participating tools get decorated
  const participating = [
    'read', 'glob', 'grep', 'js-manager', 'js-engineer', 'js-devops',
    'edit', 'write', 'mv', 'rm', 'fetch', 'run',
  ]
  for (const tool of participating) {
    const def = decorateUnder('en', tool)
    assert.ok(def.parameters.properties.estimated_readonly_rounds, `participating tool ${tool} must have estimated_readonly_rounds`)
    assert.ok(def.parameters.properties.self_note, `participating tool ${tool} must have self_note`)
    assert.ok(def.parameters.required.includes('estimated_readonly_rounds'), `participating tool ${tool} must require estimated_readonly_rounds`)
  }

  // Non-participating tools get zero increment
  const nonParticipating = [
    'fork', 'resume', 'commission', 'join', 'horizon', 'review', 'suicide',
    'fission', 'open-terminal', 'send-terminal', 'read-terminal', 'signal-terminal',
    'skill', 'assume', 'defer', 
    'publish', 'chronicle', 'js-bookkeeper',
    'bash-honeypot', 'invalid', 'js-orchestrator', 'js-blogger',
  ]
  for (const tool of nonParticipating) {
    const def = decorateUnder('en', tool)
    assert.equal(def.description, 'Original description', `non-participating tool ${tool} must not be decorated`)
    assert.equal(def.parameters.properties.estimated_readonly_rounds, undefined, `non-participating tool ${tool} has no rounds field`)
    assert.equal(def.parameters.properties.self_note, undefined, `non-participating tool ${tool} has no note field`)
    assert.equal(def.parameters.required.includes('estimated_readonly_rounds'), false)
  }
})
}
