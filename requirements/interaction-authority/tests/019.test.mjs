import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");

const hash = (value) => `H(${value})`
const personas = {
  coder: 'Coder',
  manager: 'Lead',
}
const rootSelection = (agent) => {
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant: agent,
      role: agent,
      selectedTier: 'deep',
      persona: personas[agent] ?? 'Unknown',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
}
const inheritedSeed = (agent, physical) => {
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
const createdRoot = authority.createAuthorityRoot(
  hash,
  'rt_join',
  'ses_jg',
  'AgentOwnerRoot',
  'root-jg',
  inheritedSeed('engineer', 'root-jg'),
)
assert.equal(createdRoot.ok, true, createdRoot.error)
const root = createdRoot.value

test('WHAT[interaction-authority-019] gate_nudge_is_exact_terminal_idempotent_and_unbounded_across_fresh_terminals', () => {
  let state = authority.registerAuthority(root, authority.empty)
  const digest1 = authority.gateNudgePayloadDigest('missing-final-report', 'run-1')
  assert.equal(authority.gateNudgeAlreadyAdmitted('ses_jg', root.logicalRun, 'InteractionRepair', 'missing-final-report', 'run-1', state), false)
  state = authority.registerClaim(
    authority.claimContinuation('pk-repair', 'ses_jg', 'InteractionRepair', root, digest1),
    state,
  )
  assert.equal(authority.gateNudgeAlreadyAdmitted('ses_jg', root.logicalRun, 'InteractionRepair', 'missing-final-report', 'run-1', state), true)
  assert.equal(authority.gateNudgeAlreadyAdmitted('ses_jg', root.logicalRun, 'InteractionRepair', 'missing-final-report', 'run-2', state), false)

  state = authority.abandonClaim('pk-repair', state)
  assert.equal(
    authority.gateNudgeAlreadyAdmitted('ses_jg', root.logicalRun, 'InteractionRepair', 'missing-final-report', 'run-1', state),
    false,
    'a definitely-not-sent abandoned claim must not spend the gate reminder occasion',
  )

  state = authority.registerClaim(
    authority.claimContinuation('pk-repair-retry', 'ses_jg', 'InteractionRepair', root, digest1),
    state,
  )
  state = authority.acceptClaim('pk-repair-retry', 'msg-repair-retry', state)
  assert.equal(
    authority.gateNudgeAlreadyAdmitted('ses_jg', root.logicalRun, 'InteractionRepair', 'missing-final-report', 'run-1', state),
    true,
    'physical acceptance permanently admits the exact terminal occasion',
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const turns = await import("../../../dist/Interaction/Repair/CompletedTurnSurface.js");
const auth = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");
const dispatch = await import("../../../dist/Interaction/Dispatch/DispatchSurface.js");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");

const text = (value) => [{ type: 'text', text: value }]

test('WHAT[interaction-authority-019] repair claim does not turn an in-flight repair into exhaustion', () => {
  assert.equal(turns.repairDefectDecision(false, false, null, []), 'RequestRepair')
  assert.equal(turns.repairDefectDecision(true, false, null, []), 'AwaitRepairTerminal')
  assert.equal(turns.repairDefectDecision(true, false, 'tool-calls', []), 'AwaitRepairTerminal')
})
test('WHAT[interaction-authority-019] fresh invalid repair terminals re-open the gate reminder', () => {
  assert.equal(turns.repairDefectDecision(true, true, 'stop', []), 'RequestRepair')
  assert.equal(turns.repairDefectDecision(true, true, 'length', text('partial')), 'RequestRepair')
  assert.equal(turns.repairDefectDecision(true, true, 'stop', text('done')), 'NoRepair')
})
test('WHAT[interaction-authority-019] a currently-repairing attempt never completes as exhausted or failed repair', () => {
  // Translate the raw DU cases through the registered completed-turn surface:
  // currentAttemptIsRepair + unfinished → AwaitRepairTerminal, terminal+stop+empty
  // → NoRepair, terminal+length → RequestRepair. A repair never lands in an
  // exhausted state.
  assert.equal(turns.repairDefectDecision(true, false, null, []), 'AwaitRepairTerminal')
  assert.equal(turns.repairDefectDecision(true, true, 'stop', text('ok')), 'NoRepair')
  assert.equal(turns.repairDefectDecision(true, true, 'length', []), 'RequestRepair')
})
test('WHAT[interaction-authority-019] duplicate gate-nudge claim is AlreadyAdmitted — single physical send', async () => {
  // Claim admission is the durable fact: a first claim opens the occasion,
  // a second concurrent attempt on the same digest returns AlreadyAdmitted at
  // the durable-authority level, so the port records exactly one send.
  const base = mkdtempSync(join(tmpdir(), 'wxs-ia-019-'))
  try {
    const opened = await journal.JournalSurface_bootWithWriterId(
      base, 'writer-019', 'rt-019', 4244, '2026-01-01T00:00:00Z',
    )
    assert.equal(opened.ok, true)
    try {
      const owner = await dispatch.acceptHumanRootSelection(
        opened.journal, 'ses_019_owner', 'msg-019-owner',
        {
          kind: 'RootSelection',
          ownerSession: null, ownerLogicalRun: null, ownerAuthorityRoot: null,
          participantIdentity: { participant: 'manager', role: 'manager', selectedTier: 'deep', persona: 'Lead', personaCatalogVersion: 1, origin: 'ResolvedAtRoot' },
        },
      )
      assert.equal(owner.ok, true, owner.ok ? '' : owner.error)
      const captured = []
      const results = await dispatch.sendGateNudgesConcurrently(
        { SubscribeTerminal: () => ({ Dispose: () => {} }), SendPrompt: async (s, p, o) => { captured.push(p); return dispatch.admittedWithReceipt('r-019') } },
        opened.journal,
        'ses_019_owner',
        'repair nudge',
        'BusyAgentNudge',
        'interaction-repair',
        'run-019',
        owner.profile,
      )
      assert.equal(results.length, 2)
      assert.equal(results[0].ok, true, JSON.stringify(results[0]))
      assert.equal(results[1].ok, true, 'concurrent duplicate joins, never fails')
      assert.equal(captured.length, 1, 'one physical send — AlreadyAdmitted dedup')
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
  const turns = await import('../../../dist/Interaction/Repair/CompletedTurnSurface.js')
  const text = (value) => ({ type: 'text', text: value })
  const reasoning = (value) => ({ type: 'reasoning', text: value })
  const toolCall = (callID, tool, args) => ({ type: 'tool-call', callID, tool, args })
  const toolResult = (callID, result) => ({ type: 'tool-result', callID, result })
  const classify = (completed, finish, errorName, parts = []) => turns.classifyOutcome(completed, finish, errorName, parts)

  test('WHAT[interaction-authority-019] terminal evidence separates formal content tool activity and visible reasoning', () => {
    for (const empty of [null, []]) {
      assert.equal(turns.partsText(empty), '')
      assert.equal(turns.partsSessionText(empty), '')
      assert.equal(turns.hasToolCallPart(empty), false)
    }
    assert.equal(turns.partsText([text('a'), toolCall('c1', 'read', '{}'), text('b'), reasoning('think'), toolResult('c1', 'out')]), 'ab')
    assert.equal(turns.partsSessionText([text('formal'), reasoning('visible thinking'), toolCall('c1', 'read', '{}'), toolResult('c1', 'raw')]), 'formal\n\nvisible thinking')
    assert.equal(turns.partsSessionText([toolCall('c1', 'exec', '{}')]), '')
    assert.equal(turns.hasToolCallPart([text('prose')]), false)
    assert.equal(turns.hasToolCallPart([toolCall('c1', 'read', '{}')]), true)
    for (const type of ['patch', 'step-start', 'step-finish']) assert.equal(turns.hasToolCallPart([{ type }]), true)
    assert.equal(turns.hasToolCallPart([{ type: 'reasoning' }]), false)
  })

  test('WHAT[interaction-authority-019] production classifier distinguishes stable terminal in-flight and content-damaged results', () => {
    assert.equal(turns.isAbortErrorName(undefined), false)
    assert.equal(turns.isAbortErrorName('RateLimitError'), false)
    for (const name of ['AbortError', 'ABORTED', 'user abort requested']) assert.equal(turns.isAbortErrorName(name), true)
    assert.deepEqual(classify(true, 'stop', 'AbortError', [text('done')]), { kind: 'TurnAborted', reason: 'AbortError' })
    assert.deepEqual(classify(true, undefined, 'StreamDied', [text('half an answer')]), { kind: 'TurnFailed', reason: 'StreamDied' })
    for (const finish of ['aborted', 'Aborted', 'ABORTED']) assert.deepEqual(classify(false, finish, undefined, [text('partial')]), { kind: 'TurnAborted', reason: 'finish=aborted' })
    assert.deepEqual(classify(false, 'error', 'ProviderBoom', [text('partial answer')]), { kind: 'TurnFailed', reason: 'ProviderBoom' })
    assert.deepEqual(classify(false, 'error', undefined, [text('partial answer')]), { kind: 'TurnFailed', reason: 'assistant finish=error' })
    assert.deepEqual(classify(false, 'Error', 'AbortError', [text('partial')]), { kind: 'TurnAborted', reason: 'AbortError' })
    assert.equal(classify(false, 'stop', undefined, [text('the answer')]).kind, 'TurnCompleted')
    assert.match(classify(false, 'stop', undefined, [text('   ')]).reason, /empty terminal/)
    assert.match(classify(false, 'stop', undefined, [text('<tool_call>read</tool_call>')]).reason, /XML-only terminal/)
    assert.equal(classify(false, 'stop', undefined, [reasoning('thoughts but no answer')]).kind, 'TurnNeedsContinuation')
    assert.equal(classify(false, 'tool-calls', undefined, [toolCall('c1', 'exec', '{}')]).kind, 'TurnInProgress')
    assert.equal(classify(false, 'Tool-Calls').kind, 'TurnInProgress')
    assert.deepEqual(classify(false, 'length', undefined, [text('truncated')]), { kind: 'TurnNeedsContinuation', reason: 'assistant finish=length' })
    assert.deepEqual(classify(false, 'content_filter'), { kind: 'TurnFailed', reason: 'assistant finish=content_filter' })
    assert.deepEqual(classify(false, undefined, undefined, [text('streaming')]), { kind: 'TurnUnknown', reason: null })
  })

  test('WHAT[interaction-authority-019] current roles and historical classification distinguish actual tool work from a missing continuation', () => {
    for (const role of ['engineer', 'manager', 'orchestrator', 'devops', 'coder', 'inspector', 'browser', 'inquiry']) {
      assert.equal(turns.needsInteractionRepair(role, false, 'tool-calls', []), true)
      assert.equal(turns.needsInteractionRepair(role, false, 'tool-calls', [toolCall('live', 'write', '{}')]), false)
      assert.equal(turns.needsInteractionRepair(role, false, 'length', []), true)
      for (const finish of ['stop', 'aborted', 'error']) assert.equal(turns.needsInteractionRepair(role, false, finish, [text('done')]), false)
    }
    for (const role of ['distiller', '']) assert.equal(turns.needsInteractionRepair(role, false, 'tool-calls', []), false)
    assert.equal(turns.roleOfAgent(undefined, 'coder'), 'coder')
    assert.equal(turns.roleOfAgent('coder', 'inspector'), 'coder')
    assert.equal(turns.roleOfAgent('not-a-managed-agent', 'inspector'), 'inspector')
    assert.equal(turns.roleOfAgent('not-a-managed-agent', undefined), '')
    const turn = turns.buildTurn('ses_build_turn', 'user-1', 'user-1', {
      id: 'asst-9', role: 'assistant', agent: 'inspector', finish: 'stop', completed: true,
      parts: [text('LGTM'), reasoning('checked twice')], model: { providerID: 'provider-x', modelID: 'model-x' },
    }, 'coder', '/work')
    assert.equal(turn.role, 'inspector')
    assert.equal(turn.outcome, 'TurnCompleted')
    assert.equal(turn.finish, 'stop')
    assert.equal(turn.model.modelID, 'model-x')
    assert.deepEqual(turn.parts, [text('LGTM'), reasoning('checked twice')].map((part) => ({ kind: part.type, text: part.text })))
    const failed = turns.buildTurn('ses_build_turn_fail', 'user-2', 'user-2', {
      id: 'asst-10', finish: 'error', errorName: 'Timeout', completed: true, parts: [text('partial answer before the error')],
    }, 'coder', undefined)
    assert.equal(failed.role, 'coder')
    assert.equal(failed.outcome, 'TurnFailed')
    assert.equal(failed.reason, 'Timeout')
    assert.equal(failed.directory, null)
    assert.equal(failed.providerRun, 'asst-10')
  })
}
