import { JournalHandle__get_Journal } from '../../../dist/Persistence/Journal/Surface.js'
import { AdvisorSeat_consult } from '../../../dist/Execution/Delegation/Advisor/Seat.js'
import test from 'node:test'
import assert from 'node:assert/strict'
import { createWith } from '../../../dist/OpenCode/Host/ProviderSystemTransform.js'
import { normalTransform, DeliveryAdjudication } from '../../../dist/OpenCode/Plugin/PluginTransforms.js'
import { resetScopeState, clearSessionState, turnBudget, isScopeInitialized, isPreviousScopePending } from '../../../dist/Context/Scope/Surface.js'
import { ofArray } from '../../../dist/fable_modules/fable-library-js.5.13.0/List.js'
import { SessionMessage, SessionToolPart, SnapshotToolPartState } from '../../../dist/Foundation/HostSessionSnapshot.js'
import { MessagePart } from '../../../dist/Foundation/HostMessage.js'
import { ToolCallIdModule_create, HostToolPartIdModule_create, SessionIdModule_create } from '../../../dist/Foundation/Identity.js'
import { outboundViewStore } from 'wanxiangli/pair-hint'
import { scopeProgress } from 'wanxiangli/scope-progress'
import { AgentJournalModule_snapshot } from '../../../dist/Persistence/Journal/AgentJournal.js'
import { AgentProjection_tryFind } from '../../../dist/Composition/Durable/Projection.js'
import { filterSnapshot } from '../../../dist/Context/Scope/SnapshotScope.js'
import * as lifecycle052 from '../../../dist/Mission/Lifecycle/Surface.js'
import * as journal052 from '../../../dist/Persistence/Journal/Surface.js'
import { withExecutablePlugin, acceptAuthorityRoot, openIncumbency } from '../../verification-system/tests/support/plugin-fixture.mjs'

const SEP = '\0\uFEFF'
const user = (id, text, created) => ({ info: { id, role: 'user', time: { created } }, parts: [{ type: 'text', text }] })
const assistant = (id, ...parts) => ({ info: { id, role: 'assistant', time: { created: 2 } }, parts })
const tool = (callID, name, output, status = 'completed') => ({ type: 'tool', callID, tool: name, state: { status, input: {}, ...(status === 'error' ? { error: output } : { output }) } })
const toolText = (msg) => msg.parts[0].state.output ?? msg.parts[0].state.error

function safeCaps() {
  const yes = async () => {}
  return {
    BeginPhysicalProviderAttempt: yes,
    BindSessionStartedAt: async () => null,
    ApplyRelayProjection: async () => null,
    ApplyTenureIsolation: yes,
    CaptureXTraceMessages: async () => ({ Current: null, RawMessages: [], Receipt: null }),
    RefreshCompanionXTrace: () => {},
    ApplyCompanion: yes,
    ApplyXWire: yes,
    FreezeProviderAttemptPlan: yes,
    InjectPairGuideline: yes,
    SettleAndReplaceDeferredInspections: yes,
    SanitizeMessages: () => {},
    AdjudicateDelivery: async () => new DeliveryAdjudication(0, []),
    StopAfterDelivery: yes,
  }
}

async function outbound(sessionId, messages) {
  const out = { messages }
  await normalTransform(safeCaps(), sessionId, {}, out)
  return out.messages
}

test('WHAT[context-compression-039] OpenCode prompts for a first chargeable estimate without mutating actual user or tool bytes', async () => {
  const sid = 'ses_scope_039_initial'
  clearSessionState(sid)
  const raw = [user('u1', 'read this file', 1), assistant('a1', tool('c1', 'read', 'file body'))]
  const original = [...raw]
  const copy = await outbound(sid, raw)
  assert.equal(copy, raw, 'OpenCode requires the provider request array to be edited in place')
  assert.equal(copy[0].parts[0].text, 'read this file')
  assert.equal(copy[1].parts[0].state.output, 'file body')
  assert.equal(copy[0].content, undefined)
  assert.equal(copy.length,3)
  assert.equal(copy[2].attribution,'agent')
  assert.equal(copy[2].strong,true)
  assert.match(copy[2].parts[0].text,/remainingScopeTurnsEstimate/)
  assert.equal(original[0].parts[0].text, 'read this file')
  assert.equal(toolText(original[1]), 'file body')
  clearSessionState(sid)
})

test('WHAT[context-compression-039] OpenCode applies a reset and preserves critical calls, later work and a new progress-baseline steer', async () => {
  const sid = 'ses_scope_039_reset'
  clearSessionState(sid)
  const operator = user('u1', 'scope initial', 100)
  await outbound(sid, [operator])
  resetScopeState(sid, 4)
  const raw = [operator, assistant('a1', tool('old', 'read', 'old')), assistant('a2', tool('reset', 'resetScope', 'Scope reset')), assistant('a3', tool('next', 'read', 'new'))]
  const original = [...raw]
  const copy = await outbound(sid, raw)
  assert.equal(copy.length, 4)
  assert.equal(copy[1].parts[0].tool, 'resetScope')
  assert.equal(copy[1].parts[0].state.output, 'Scope reset')
  assert.equal(copy[2].parts[0].state.output, 'new')
  assert.equal(copy[3].attribution,'agent')
  assert.equal(copy[3].strong,true)
  assert.match(copy[3].parts[0].text,/remainingScopeTurnsEstimate/)
  assert.equal(original[3].parts[0].state.output, 'new', 'the original result object is not edited')
  clearSessionState(sid)
})

const call = (id, tool) => new MessagePart(2, [id, tool, '{}'])
const returned = (id, text) => new MessagePart(3, [id, text])
const text = (body) => new MessagePart(0, [body])
const msg = (id, role, parts, toolParts = []) => new SessionMessage(
  id, role, undefined, undefined, undefined, undefined, undefined, undefined,
  true, false, undefined, parts, parts.map(() => undefined), toolParts,
)
const physicalResult = (id, output) => new SessionToolPart(
  HostToolPartIdModule_create('part-' + id), ToolCallIdModule_create(id),
  id === 'reset' ? 'resetScope' : 'read', '{}', new SnapshotToolPartState(1, [output]),
)

test('WHAT[context-compression-040] SnapshotScope keeps the result co-located with an earlier reset call', () => {
  const raw = [
    msg('u1', 'user', [text('Task')]),
    msg('a1', 'assistant', [text('work'), call('old','read'), returned('old','old result'), call('r1','resetScope'), returned('r1','first reset')]),
    msg('a2', 'assistant', [call('new','read'), call('r2','resetScope'), returned('r2','second reset')]),
  ]
  const retained = filterSnapshot(ofArray(raw))
  assert.deepEqual(retained.map(m => m.id), ['u1', 'a1', 'a2'])
  assert.deepEqual(retained[1].parts.map(p => p.id), [undefined, 'r1', 'r1'], 'assistant prose and both halves of the older reset survive')
  assert.deepEqual(retained[2].parts.map(p => p.id), ['r2', 'r2'])
  assert.equal(raw[1].Parts.length, 5, 'the session snapshot remains unchanged')
})

test('WHAT[context-compression-040] SnapshotScope preserves completed reset tool states and removes unrelated old work', () => {
  const raw = [
    msg('u1', 'user', [text('Task')]),
    msg('a1', 'assistant', [call('old', 'read'), call('reset', 'resetScope')], [physicalResult('old', 'read output'), physicalResult('reset', 'Scope reset.')]),
    msg('a2', 'assistant', [call('r2', 'resetScope')]),
  ]
  const kept = filterSnapshot(ofArray(raw))
  assert.equal(kept.length, 3)
  assert.deepEqual(kept[1].parts.map(p => p.id), ['reset','reset'], 'the prior reset result is paired with its call')
  assert.deepEqual(kept[2].parts.map(p => p.id), ['r2'])
})

test('WHAT[context-compression-041] OpenCode session.deleted releases only its own scope actor', async () => {
  await withExecutablePlugin(async (hooks) => {
    const closed = 'ses_deleted_scope_041'
    const active = 'ses_active_scope_041'
    clearSessionState(closed)
    clearSessionState(active)
    resetScopeState(closed, 5)
    resetScopeState(active, 7)
    assert.equal(turnBudget(closed), 5)
    await hooks.event({event: {type: 'session.deleted', properties: {sessionID: closed}}})
    assert.equal(turnBudget(closed), 0)
    assert.equal(isScopeInitialized(closed), false)
    assert.equal(turnBudget(active), 7, 'other session actor must not be touched')
    clearSessionState(active)
  })
})


test('WHAT[prefix-stability-016] OpenCode provider transform never adds retired countdown suffixes to old or new tool results', async () => {
  const sid = 'ses_scope_016_cache'
  clearSessionState(sid)
  const operator = user('user-016', 'do the work', 100)
  await outbound(sid, [operator])
  resetScopeState(sid, 7)
  const reset = assistant('asst-reset-016', tool('reset-016', 'resetScope', 'Scope reset'))
  const rawResetText = reset.parts[0].state.output
  const first = await outbound(sid, [operator, reset])
  const firstRendered = JSON.stringify(first[1])
  assert.equal(first[1].parts[0].state.output,'Scope reset')

  const read = assistant('asst-read-016', tool('read-016', 'read', 'new data'))
  const second = await outbound(sid, [operator, reset, read])
  assert.equal(JSON.stringify(second[1]), firstRendered,
    'the historical reset result must remain a verbatim prefix, including its old budget suffix')
  assert.equal(second[2].parts[0].state.output,'new data')
  assert.equal(reset.parts[0].state.output, rawResetText,
    'raw Host history must remain untouched even though the outgoing array changed')
  clearSessionState(sid)
})


test('WHAT[delegation-negotiation-001] the Host records only the question while the provider sees an immutable parent prefix', async () => {
  const {
    negotiationRememberSystem, negotiationRememberMessages,
    negotiationRememberModel, negotiationModelFor,
    negotiationStartTemporary, negotiationProjectSystem,
    negotiationProjectMessages, negotiationIsTemporary,
    negotiationFinishTemporary, negotiationForgetSession,
  } = await import('../../../dist/OpenCode/Plugin/PluginTransforms.js')
  const parent = 'ses-neg-parent-001', temporary = 'ses-neg-grandchild-001'
  const prefix = [
    { info: { id: 'u1', sessionID: parent, role: 'user' }, parts: [{ type: 'text', text: 'Existing instruction' }] },
    { info: { id: 'a1', sessionID: parent, role: 'assistant' }, parts: [{ type: 'text', text: 'Already answered' }] },
  ]
  const originalBytes = JSON.stringify(prefix)
  const system = ['Common law', 'Role law', 'Provider configuration']
  negotiationRememberSystem({ sessionID: parent }, { system })
  negotiationRememberMessages(parent, { messages: prefix })
  const withoutModel = negotiationStartTemporary(temporary, parent)
  assert.equal(withoutModel.tag, 1, 'do not improvise a different model when the delegator model is unknown')
  negotiationRememberModel({ sessionID: parent, model: { providerID: 'provider', id: 'deep-model' }, message: {model:{variant:'high'}} })
  assert.deepEqual(negotiationModelFor(parent), { providerID: 'provider', modelID: 'deep-model', variant: 'high' })
  const admitted = negotiationStartTemporary(temporary, parent)
  assert.equal(admitted.tag, 0, 'the parent must have a complete provider wire')
  assert.equal(negotiationIsTemporary(temporary), true)

  const hostRecord = { info: { id: 'tmp-question', sessionID: temporary, role: 'user' }, parts: [{ type: 'text', text: 'Which parser?' }] }
  const outgoing = { messages: [hostRecord] }
  const hostArray = outgoing.messages
  const tmpSystem = { system: ['Different grandchild system'] }
  const liveSystemArray = tmpSystem.system
  assert.equal(negotiationProjectSystem({ sessionID: temporary }, tmpSystem), true)
  assert.equal(tmpSystem.system, liveSystemArray, 'OpenCode may retain the original system array identity')
  assert.deepEqual(tmpSystem.system, system, 'system bytes are exactly the last parent provider system')
  assert.equal(negotiationProjectMessages(temporary, outgoing), true)
  assert.equal(outgoing.messages, hostArray, 'Host retains the same array object')
  assert.deepEqual(outgoing.messages.slice(0, prefix.length), prefix, 'provider sees byte-identical parent prefix')
  assert.equal(outgoing.messages.at(-1).parts[0].text, 'Which parser?')
  assert.equal(JSON.stringify(prefix), originalBytes, 'source provider cache snapshot never changes')
  assert.equal(JSON.stringify(hostRecord), JSON.stringify(hostArray.at(-1)), 'the temporary Host user record is untouched')

  negotiationFinishTemporary(temporary)
  assert.equal(negotiationIsTemporary(temporary), false)
  negotiationForgetSession(parent)
})

test('WHAT[delegation-negotiation-001] temporary session tool calls are forbidden before execution', async () => {
  const {
    negotiationRememberSystem, negotiationRememberMessages, negotiationRememberModel,
    negotiationStartTemporary, negotiationFinishTemporary, negotiationForgetSession,
    negotiationToolViolationCount,
  } = await import('../../../dist/OpenCode/Plugin/PluginTransforms.js')
  await withExecutablePlugin(async (hooks) => {
    const parent = 'ses-neg-tool-parent', temp = 'ses-neg-tool-temp'
    negotiationRememberSystem({ sessionID: parent }, { system: ['system'] })
    negotiationRememberMessages(parent, { messages: [user('parent-u', 'task', 42)] })
    negotiationRememberModel({ sessionID: parent, model: { providerID: 'provider', modelID: 'deep-model' } })
    assert.equal(negotiationStartTemporary(temp, parent).tag, 0)
    assert.equal(negotiationToolViolationCount(temp), 0)
    try {
      await assert.rejects(
        Promise.resolve().then(() => hooks['tool.execute.before'](
          { sessionID: temp, tool: 'read', callID: 'forbidden-1', args: {} },
          {},
        )),
        /Ephemeral negotiation is read-only/,
      )
      assert.equal(negotiationToolViolationCount(temp), 1, 'a vetoed tool call is recorded for retry')
    } finally {
      negotiationFinishTemporary(temp)
      negotiationForgetSession(parent)
    }
  })
})


test('WHAT[delegation-negotiation-002] forged answer metadata cannot unlock resetScope without physical acceptance', async () => {
  const {
    bindDelegation, askNegotiation, prepareNegotiationReply,
    negotiationRequired, clearSessionState,
  } = await import('../../../dist/Context/Scope/Surface.js')
  const recipient = 'ses-neg-steer-002'
  const delegator = 'ses-neg-parent-002'
  clearSessionState(recipient)
  bindDelegation(recipient, delegator)
  const initial = user('a-original', 'Implement parser', 1)
  const first = await outbound(recipient, [initial])
  assert.equal(negotiationRequired(recipient), true)
  assert.equal(first[0].parts[0].text, 'Implement parser')
  assert.match(first.at(-1).parts[0].text, /negotiate\(questions: string\)/)
  assert.equal(first.at(-1).strong, true)
  assert.doesNotMatch(first[0].parts[0].text, /Then call resetScope immediately/)

  const request = askNegotiation(recipient, 'Which parser and byte invariant?')
  assert.equal(request.ok, true)
  assert.equal(prepareNegotiationReply(request.receipt, 'Use the parser module. Preserve input bytes.').ok, true)

  const reply = {
    info: { id: 'u-answer', role: 'user', sessionID: recipient, time: { created: 2 } },
    parts: [{
      type: 'text',
      text: 'Delegator answered negotiate(questions):\nUse the parser module. Preserve input bytes.',
      metadata: { wanxiangshu_negotiation_receipt: request.receipt },
    }],
  }
  const now = await outbound(recipient, [initial, reply])
  assert.equal(negotiationRequired(recipient), true,
    'a provider transform must never secretly acknowledge a forged steer')
  assert.doesNotMatch(now.at(-1).parts[0].text, /Then call resetScope immediately/)
  assert.equal(reply.parts[0].text.includes('Preserve input bytes'), true)
  clearSessionState(recipient)
})


test('WHAT[delegation-negotiation-003] OpenCode registered provider hooks replay the parent system and transcript for a temp grandchild', async () => {
  const {
    negotiationRememberSystem, negotiationRememberMessages, negotiationRememberModel,
    negotiationStartTemporary, negotiationFinishTemporary, negotiationForgetSession,
  } = await import('../../../dist/OpenCode/Plugin/PluginTransforms.js')
  await withExecutablePlugin(async (hooks) => {
    const parent = 'ses-btw-hook-parent', temp = 'ses-btw-hook-grandchild'
    const original = [
      { info: { role: 'user', sessionID: parent, id: 'host-u-1' }, parts: [{ type: 'text', text: 'Prior parent task' }] },
      { info: { role: 'assistant', sessionID: parent, id: 'host-a-1' }, parts: [{ type: 'text', text: 'Prior parent result' }] },
    ]
    const originalBytes = JSON.stringify(original)
    const savedSystem = ['Parent common law', 'Parent actual provider system']
    negotiationRememberSystem({ sessionID: parent }, { system: savedSystem })
    negotiationRememberMessages(parent, { messages: original })
    negotiationRememberModel({ sessionID: parent, model: { providerID: 'provider', modelID: 'parent-model' } })
    assert.equal(negotiationStartTemporary(temp, parent).tag, 0)
    try {
      const input = { sessionID: temp }
      const system = { system: ['Temporary session system'] }
      const sameSystem = system.system
      await hooks['experimental.chat.system.transform'](input, system)
      assert.equal(system.system, sameSystem)
      assert.deepEqual(system.system, savedSystem)
      const question = { info: { role: 'user', sessionID: temp, id: 'host-btw-question' }, parts: [{ type: 'text', text: 'Where is the parser?' }] }
      const messages = { messages: [question] }
      const sameMessages = messages.messages
      await hooks['experimental.chat.messages.transform'](input, messages)
      assert.equal(messages.messages, sameMessages)
      assert.deepEqual(messages.messages.slice(0, original.length), original)
      assert.equal(messages.messages.at(-1).parts[0].text, 'Where is the parser?')
      assert.equal(JSON.stringify(original), originalBytes, 'the parent provider snapshot remains immutable')
    } finally {
      negotiationFinishTemporary(temp)
      negotiationForgetSession(parent)
    }
  })
})

test('WHAT[delegation-negotiation-004] registered negotiate sends ephemeral BTW and delivers an authenticated steer after a vetoed tool attempt', async () => {
  const { acceptAuthorityRoot } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
  const { bindDelegation, negotiationRequired, clearSessionState } =
    await import('../../../dist/Context/Scope/Surface.js')
  const {
    negotiationRememberSystem, negotiationRememberMessages,
    negotiationRememberModel, negotiationForgetSession,
  } = await import('../../../dist/OpenCode/Plugin/PluginTransforms.js')

  await withExecutablePlugin(async (hooks, _dir, _created, runtime) => {
    const parent = 'ses-negotiate-real-parent', child = 'ses-negotiate-real-child'
    await acceptAuthorityRoot(runtime, parent, 'manager')
    await acceptAuthorityRoot(runtime, child, 'engineer')
    clearSessionState(child)
    bindDelegation(child, parent)
    const transcript = [
      { info: { id: 'parent-u', role: 'user', sessionID: parent }, parts: [{ type: 'text', text: 'Known parent history' }] },
    ]
    negotiationRememberSystem({ sessionID: parent }, { system: ['Reusable parent system'] })
    negotiationRememberMessages(parent, { messages: transcript })
    negotiationRememberModel({ sessionID: parent, model: { providerID: 'provider', modelID: 'parent-model' } })

    const session = runtime.client.session
    let prompts = 0
    let blocked = 0
    session.prompt = async (args) => {
      prompts += 1
      const tempId = args.path.id
      const system = { system: ['temporary system'] }
      await hooks['experimental.chat.system.transform']({ sessionID: tempId }, system)
      assert.deepEqual(system.system, ['Reusable parent system'])
      const messages = { messages: [{ info: { role: 'user', sessionID: tempId }, parts: args.body.parts }] }
      await hooks['experimental.chat.messages.transform']({ sessionID: tempId }, messages)
      assert.deepEqual(messages.messages[0], transcript[0])
      assert.equal(args.body.model?.modelID, 'parent-model')
      if (prompts === 1) {
        await assert.rejects(
          hooks['tool.execute.before']({ sessionID: tempId, tool: 'read', callID: 'btw-forbidden' }, { args: {} }),
          /Ephemeral negotiation is read-only/,
        )
        blocked += 1
        throw new Error('tool call intercepted before execution')
      }
      return { data: { info: { role: 'assistant' }, parts: [{ type: 'text', text: 'Use the byte-preserving parser.' }] } }
    }
    const premature = await hooks.tool.resetScope.execute(
      { newScopeObjective: 'too early' },
      { sessionID: child, agent: 'engineer', callID: 'premature-reset' },
    )
    assert.match(premature, /unavailable until the first negotiate/)
    assert.equal(negotiationRequired(child), true, 'a rejected reset cannot unlock the initial gate')
    const result = await hooks.tool.negotiate.execute(
      { questions: 'Which parser? Preserve which invariant?' },
      { sessionID: child, agent: 'engineer', callID: 'negotiate-1' },
    )
    assert.match(result, /Questions accepted|问题已受理/)
    const outgoingGuidance = () => runtime.prompts.filter(prompt =>
      prompt.path?.id === child && prompt.body?.parts?.some(p => p.type === 'text' && /byte-preserving parser/.test(p.text)),
    )
    for (let i = 0; i < 160 && outgoingGuidance().length === 0; i++) {
      await new Promise(resolve => setTimeout(resolve, 20))
    }
    assert.equal(blocked, 1, 'the first ephemeral tool call was vetoed before execution')
    assert.equal(prompts, 2, 'the temporary answer retried without losing the cached parent prefix')
    assert.equal(outgoingGuidance().length, 1, 'one genuine PromptDispatcher continuation is queued to the child')
    const guidance = outgoingGuidance()[0]
    const visibleAnswer = guidance.body.parts.find(part => part.type === 'text')?.text
    assert.match(visibleAnswer, /byte-preserving parser/)
    assert.ok(guidance.body.metadata?.wanxiangshu_prompt_key,
      'real continuation must carry a durable PromptKey')
    assert.equal(guidance.body.noReply, undefined,
      'do not fake a private promptAsync steer with a status-dependent noReply flag')
    const childHistory = (await session.messages({ path: { id: child } })).data
    assert.equal(childHistory.some(message =>
      message.role === 'user' && message.parts.some(part => /byte-preserving parser/.test(part.text ?? '')),
    ), true, 'the answer must be a visible, Host-owned user message')
    for (let i = 0; i < 80 && negotiationRequired(child); i++) {
      await new Promise(resolve => setTimeout(resolve, 20))
    }
    assert.equal(negotiationRequired(child), false,
      'only the real PhysicalAccepted callback, not the dispatch queue receipt, unlocks resetScope')
    negotiationForgetSession(parent)
    clearSessionState(child)
  })
})


test('WHAT[delegation-negotiation-005] dedicated Advisor has no tools in Host config or execution policy', async () => {
  const { advisorAgentConfig } = await import('../../../dist/OpenCode/Tools/StaticTools.js')
  const { rolePredicate } = await import('../../../dist/OpenCode/Tools/ToolRegistrySurface.js')
  const agent = advisorAgentConfig('Advisor role law')
  const permission = agent.permission
  const allowed = Object.entries(permission).filter(([, status]) => status === 'allow').map(([name]) => name).sort()
  assert.deepEqual(allowed, [])
  assert.equal(permission.external_directory, 'deny', 'Advisor never receives broad external path permission')
  for (const name of ['skill', 'assume', 'fetch', 'bash-honeypot', 'todowrite-for-engineer', 'negotiate', 'resetScope', 'askPreviousScope']) {
    assert.equal(permission[name], 'deny', name + ' must be explicitly denied in Host agent config')
    assert.equal(rolePredicate(name, 'advisor'), false, name + ' must be denied at the product tool admission layer')
  }
})

test('WHAT[delegation-negotiation-006] user-facing root hides negotiate while a real managed child keeps it', async () => {
  await withExecutablePlugin(async (hooks, _directory, _ids, runtime) => {
    const root = 'ses-user-facing-negotiate-006'
    const message = {
      id: 'user-root-negotiate-006', sessionID: root, role: 'user', agent: 'manager',
      model: { providerID: 'provider', modelID: 'manager-model' },
      tools: { fetch: true },
    }
    const parts = [{ type: 'text', text: 'User instruction, no delegator above this session' }]
    runtime.pushHostMessage(root, { info: message, parts })
    await hooks['chat.message']({ sessionID: root, messageID: message.id, agent: 'manager' }, { message, parts })
    assert.equal(message.tools.negotiate, false, 'the root provider request MUST NOT expose negotiate')
    assert.equal(message.tools.fetch, true, 'unrelated tools remain unchanged')
    await assert.rejects(
      hooks['tool.execute.before']({ sessionID: root, tool: 'negotiate', callID: 'n-root-006' }, { args: { questions: 'Bad root attempt' } }),
      /user-facing sessions/,
      'even forged tool calls must be stopped before execution',
    )
  })

  const { withRealFork, forkCall } = await import('../../delegation/tests/support/real-fork.mjs')
  await withRealFork('ses-parent-negotiate-006', async ({ hooks, createdIds }) => {
    const receipt = await forkCall(hooks, 'ses-parent-negotiate-006', 'Ada', 'Current instruction only')
    assert.match(receipt, /Ada/)
    assert.equal(createdIds.length, 1)
    const child = createdIds[0]
    const message = {
      id: 'user-managed-negotiate-006', sessionID: child, role: 'user', agent: 'engineer',
      model: { providerID: 'provider', modelID: 'engineer-model' },
    }
    const parts = [{ type: 'text', text: 'Current instruction only' }]
    await hooks['chat.message']({ sessionID: child, messageID: message.id, agent: 'engineer' }, { message, parts })
    assert.notEqual(message.tools?.negotiate, false, 'a managed child must not lose first negotiation')
    await hooks['tool.execute.before'](
      { sessionID: child, tool: 'negotiate', callID: 'n-child-006' },
      { args: { questions: 'Which parser should I use?' } },
    )
  })
})


test('WHAT[delegation-negotiation-007] detached queue receipt cannot masquerade as a physically visible steer', async () => {
  const { acceptAuthorityRoot } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
  const { bindDelegation, negotiationRequired, clearSessionState } =
    await import('../../../dist/Context/Scope/Surface.js')
  const {
    negotiationRememberSystem, negotiationRememberMessages,
    negotiationRememberModel, negotiationForgetSession,
  } = await import('../../../dist/OpenCode/Plugin/PluginTransforms.js')
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const parent = 'ses-neg-queued-parent', child = 'ses-neg-queued-child'
    await acceptAuthorityRoot(runtime, parent, 'manager')
    await acceptAuthorityRoot(runtime, child, 'engineer')
    bindDelegation(child, parent)
    negotiationRememberSystem({ sessionID: parent }, { system: ['system'] })
    negotiationRememberMessages(parent, {
      messages: [{ info: { role: 'user', id: 'p1', sessionID: parent }, parts: [{ type: 'text', text: 'parent' }] }],
    })
    negotiationRememberModel({ sessionID: parent, model: { providerID: 'provider', modelID: 'model' } })
    runtime.client.session.prompt = async () =>
      ({ data: { info: { role: 'assistant' }, parts: [{ type: 'text', text: 'The answer.' }] } })
    runtime.nextPromptReceiptOnly()
    const response = await hooks.tool.negotiate.execute(
      { questions: 'Which file?' },
      { sessionID: child, agent: 'engineer', callID: 'neg-queued-1' },
    )
    assert.match(response, /Questions accepted|问题已受理/)
    for (let i = 0; i < 80 && !runtime.prompts.some(item =>
      item.path?.id === child && item.body.parts.some(part => /The answer/.test(part.text ?? '')),
    ); i++) await new Promise(resolve => setTimeout(resolve, 20))
    const queued = runtime.prompts.find(item =>
      item.path?.id === child && item.body.parts.some(part => /The answer/.test(part.text ?? '')),
    )
    assert.ok(queued, 'Dispatcher must attempt a visible Host send')
    assert.ok(queued.body.metadata?.wanxiangshu_prompt_key, 'even a detached queue is durably keyed')
    assert.equal(negotiationRequired(child), true,
      'a detached transport receipt with no physical user message must not unlock resetScope')
    const history = (await runtime.client.session.messages({ path: { id: child } })).data
    assert.equal(history.some(message =>
      message.parts?.some(part => /The answer/.test(part.text ?? '')),
    ), false, 'no covert provider transform may inject an answer into the raw Host history')
    negotiationForgetSession(parent)
    clearSessionState(child)
  })
})

test('WHAT[context-compression-039] real OpenCode system transform upgrades stale owned scope sections without duplicate output', async () => {
  const transform = createWith(() => undefined, async () => undefined)
  const stale = '## Scope & Context Management (`resetScope`)\n[System: Then call resetScope immediately, before doing anything else.]'
  const system = ['unrelated host prompt', stale]
  const output = { system }
  await transform({ sessionID: 'ce017-stale-scope' })(output)
  assert.equal(output.system, system, 'reconcile an existing section in place')
  assert.equal(system.length, 2)
  assert.equal(system[0], 'unrelated host prompt')
  assert.match(system[1], /Scope & Context Management|Scope 与上下文/)
  assert.doesNotMatch(system[1], /\[System:|Then call resetScope immediately/)
  const first = system[1]
  await transform({ sessionID: 'ce017-stale-scope' })(output)
  assert.equal(system.length, 2, 'repeated current section must not be duplicated')
  assert.equal(system[1], first, 're-entry preserves identical current bytes')

  const staleZh = '## Scope 与上下文\n[System: 先自行判定是否应重置]'
  const zhSystem = ['another host prompt', staleZh]
  const zhOutput = { system: zhSystem }
  await transform({ sessionID: 'ce017-stale-chinese' })(zhOutput)
  assert.equal(zhSystem.length, 2)
  assert.doesNotMatch(zhSystem[1], /先自行判定|\[System:/)
  await transform({ sessionID: 'ce017-stale-chinese' })(zhOutput)
  assert.equal(zhSystem.length, 2, 'Chinese section must not duplicate on replay')

  const fresh = { system: ['an independent system prompt'] }
  await transform({ sessionID: 'ce017-fresh-scope' })(fresh)
  assert.equal(fresh.system.length, 2)
  assert.equal(fresh.system[0], 'an independent system prompt')
  assert.match(fresh.system[1], /Scope & Context Management|Scope 与上下文/)
  assert.doesNotMatch(fresh.system[1], /\[System:/)
})

test('WHAT[context-compression-047] registered OpenCode reset archives physical previous wire and mandatory ask actually queries it through the readonly BTW pipeline', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const sid = 'previous-physical-047'
    await acceptAuthorityRoot(runtime, sid, 'engineer')
    clearSessionState(sid)
    const prefix = [
      { info: { id: 'prior-physical-user', role: 'user', sessionID: sid, time: { created: 1 } },
        parts: [{ type: 'text', text: 'Do not use the replacement parser. Exact counterexample 37.' }] },
    ]
    const originalBytes = JSON.stringify(prefix)
    const system = { system: ['Prior real provider system: preserve counterexample 37 verbatim'] }
    await hooks['experimental.chat.system.transform']({ sessionID: sid }, system)
    await hooks['chat.params']({ sessionID: sid, model: { providerID: 'fixture', modelID: 'old-model' } }, {})
    const outbound = { messages: structuredClone(prefix) }
    await normalTransform(safeCaps(), sid, {}, outbound)
    assert.equal(JSON.stringify(prefix), originalBytes, 'real raw Host message is untouched')

    const reset = await hooks.tool.resetScope.execute(
      { newScopeObjective: 'verify new parser against previous evidence',
        oldScopeVerboseVerbatimExperience: 'We chose parser A' },
      { sessionID: sid, agent: 'engineer', callID: 'reset-047', messageID: 'provider-reset-047' },
    )
    assert.match(String(reset), /Scope reset. Objective: verify new parser/)
    assert.equal(isPreviousScopePending(sid), false, 'the very first reset does not require a previous-Scope question')
    assert.equal(scopeProgress.status(sid).known,false,'new scope has no chargeable estimate yet')
    const resetReplay = await hooks.tool.resetScope.execute(
      {newScopeObjective:'verify new parser against previous evidence'},
      {sessionID:sid,agent:'engineer',callID:'reset-047',messageID:'provider-reset-047'})
    assert.match(String(resetReplay),/Scope reset. Objective:/)
    assert.equal(isPreviousScopePending(sid),false,'the first reset replay cannot manufacture a mandatory previous-Scope question')

    const currentRequest={messages:[
      ...structuredClone(prefix),
      {info:{id:'old-reset-047',role:'assistant',sessionID:sid},parts:[
        {type:'tool',id:'reset-047',name:'resetScope',state:{status:'completed',output:'Scope reset'}}]},
      {info:{id:'new-scope-047',role:'user',sessionID:sid},parts:[{type:'text',text:'PRIVATE_CURRENT_SCOPE_112358'}]},
    ]}
    await normalTransform(safeCaps(),sid,{},currentRequest)
    // The latest physical source has advanced past reset; the consultation
    // projects its immediately preceding scope when the ephemeral request runs.
    const session = runtime.client.session
    let providerAttempts = 0, toolVetoes = 0
    session.prompt = async args => {
      providerAttempts += 1
      const tempId = args.path.id
      assert.equal(args.body.model?.modelID, 'old-model', 'reuse the exact old model identity')
      const sys = { system: ['Ephemeral empty system'] }
      await hooks['experimental.chat.system.transform']({ sessionID: tempId }, sys)
      assert.ok(sys.system.some(x => x.includes('counterexample 37')), 'old system was projected')
      const request = {
        messages: [{ info: { role: 'user', sessionID: tempId }, parts: args.body.parts }],
      }
      await hooks['experimental.chat.messages.transform']({ sessionID: tempId }, request)
      const projected = JSON.stringify(request.messages)
      assert.match(projected, /Exact counterexample 37/, 'the latest real provider history reveals old-Scope evidence')
      assert.doesNotMatch(projected,/PRIVATE_CURRENT_SCOPE_112358/,'new Scope cannot leak into previous-Scope consultation')
      assert.doesNotMatch(projected,/archived provider request|Ephemeral clarification|<btw>/,'no hidden orchestration is shown to the temporary model')
      if (providerAttempts === 1) {
        await assert.rejects(
          hooks['tool.execute.before']({ sessionID: tempId, tool: 'write', callID: 'forbidden-previous' }, { args: {} }),
          /Ephemeral negotiation is read-only/,
        )
        toolVetoes += 1
        throw new Error('read-only veto before tool execution')
      }
      return {
        data: {
          info: { role: 'assistant' },
          parts: [{ type: 'text', text: 'Previous evidence: counterexample 37 proves the replacement parser loses data.' }],
        },
      }
    }

    const ask = await hooks.tool.askPreviousScope.execute(
      { questions: 'Which evidence disproved the replacement parser?' },
      { sessionID: sid, agent: 'engineer', callID: 'previous-call-047', messageID: 'provider-ask-047' },
    )
    assert.match(String(ask), /accepted|受理/)
    assert.equal(isPreviousScopePending(sid), false, 'actual first model tool call, not a background reset, clears pending')
    const replay = await hooks.tool.askPreviousScope.execute(
      { questions: 'Which evidence disproved the replacement parser?' },
      { sessionID: sid, agent: 'engineer', callID: 'previous-call-047', messageID: 'provider-ask-047' },
    )
    assert.match(String(replay), /already accepted|previously accepted|受理/)
    clearSessionState(sid)
    const coldReplay = await hooks.tool.askPreviousScope.execute(
      { questions: 'Which evidence disproved the replacement parser?' },
      { sessionID: sid, agent: 'engineer', callID: 'previous-call-047', messageID: 'provider-ask-047' })
    assert.match(String(coldReplay), /previously accepted/, 'the durable receipt survives loss of PairHint session cache')
    const conflict = await hooks.tool.askPreviousScope.execute(
      { questions: 'Different request with same toolCallId' },
      { sessionID: sid, agent: 'engineer', callID: 'previous-call-047', messageID: 'provider-ask-047' })
    assert.match(String(conflict), /conflict/, 'durable question digest forbids altered replay')
    const outboundSteers = () => runtime.prompts.filter(p =>
      p.path?.id === sid && p.body?.parts?.some(part => /counterexample 37 proves/.test(part.text ?? '')))
    for (let i = 0; i < 100 && !outboundSteers().length; i++) {
      await new Promise(resolve => setTimeout(resolve, 20))
    }
    assert.equal(providerAttempts, 2, 'one real old-model answer after bounded tool-veto retry, no replay duplicate')
    assert.equal(toolVetoes, 1)
    assert.equal(outboundSteers().length, 1, 'old answer delivered through one real PromptDispatcher continuation')
    clearSessionState(sid)
  })
})

test('WHAT[work-052] Delivered Manager work can durably Reopen as a distinct later relay tenure, never revive predecessor',async()=>{
  await withExecutablePlugin(async (_hooks,directory,_ids,runtime)=>{
    const session='manager-post-delivery-052'
    await acceptAuthorityRoot(runtime,session,'manager')
    await openIncumbency(runtime,session,undefined,'S4',session)
    const common=await journal052.JournalSurface_initializeStateRoot(directory)
    const surface=lifecycle052.LifecycleSurface ?? lifecycle052
    const originalIncumbency=surface.inspected(common,session)?.ActiveId
    assert.ok(originalIncumbency)
    const review=(id, updates)=>surface.submitReview(common, session, id, 'Manager', 'run-'+id, 'call-'+id, updates, [], '')
    assert.equal(review('fill', [{ key: 'opened', value: 'do' }]).kind, 'Reviewed')
    assert.equal(review('relay', [{ key: 'opened', value: null }]).kind, 'Relayed')
    const advanced=review('advance', [])
    assert.equal(advanced.kind, 'Advanced', advanced.reason)
    assert.equal(advanced.milestone, '收尾')
    const closed=review('close', [])
    assert.equal(closed.kind, 'Completed', closed.reason)
    const ended=surface.inspected(common, session)
    assert.equal(ended.ActiveId ?? null, null, 'original Manager tenure must be retired')
    assert.equal(ended.Delivered, true)
    const messageId='operator-052'
    const reopened=surface.execute(common, session, 'operator-052', {
      type: 'OperatorMessage', kind: 'Operator', messageId,
    })
    assert.equal(reopened.Status, 'Committed', reopened.Reason)
    assert.equal(reopened.Kind, 'Reopened')
    const after=surface.inspected(common, session)
    assert.equal(after.Stage, '执行')
    assert.ok(after.ActiveId && after.ActiveId !== originalIncumbency,
      'real durable reopen creates a distinct active Manager incumbency')
    assert.equal(after.Delivered, false)
    const same=surface.execute(common, session, 'operator-052-again', {
      type: 'OperatorMessage', kind: 'Operator', messageId,
    })
    assert.equal(same.Status, 'Observed')
    assert.equal(surface.inspected(common, session)?.ActiveId, after.ActiveId, 'same later head must not start a third tenure')
  })
})

test('WHAT[context-compression-050] real OpenCode first reset has no previous-scope MUST, second reset does', async()=>{
  await withExecutablePlugin(async (hooks, _dir, _created, runtime) => {
    const sid='reset-first-versus-second-050'
    clearSessionState(sid)
    await acceptAuthorityRoot(runtime,sid,'engineer')
    async function physicallyObserve(run) {
      const first={system:['Observed real provider system for '+run]}
      await hooks['experimental.chat.system.transform']({sessionID:sid},first)
      await hooks['chat.params']({sessionID:sid,model:{providerID:'fixture',modelID:'old-model'}},{})
      await normalTransform(safeCaps(),sid,{},{messages:[
        {info:{id:'operator-'+run,role:'user',sessionID:sid,time:{created:run}},
         parts:[{type:'text',text:'Provider task '+run}]},
      ]})
    }
    const resetArgs=objective=>({
      newScopeObjective:objective,
      oldScopeVerboseVerbatimExperience:'Work from the preceding scope.',
    })
    const resetCtx=id=>({sessionID:sid,agent:'engineer',callID:id,messageID:'run-'+id})
    await physicallyObserve(101)
    const first=await hooks.tool.resetScope.execute(resetArgs('first initialized scope'),resetCtx('reset-one-050'))
    assert.match(String(first),/Scope reset. Objective: first initialized scope/)
    assert.equal(isPreviousScopePending(sid),false,'first ever reset does not require a predecessor consultation')
    assert.equal(scopeProgress.status(sid).known,false)
    const resumed=await outbound(sid,[
      user('original-050','original mission',101),
      assistant('rst-one',tool('reset-one-050','resetScope','Scope reset. Objective: first initialized scope')),
      user('later-050','new actual operator instruction',102),
    ])
    assert.equal(resumed.find(x=>x.info?.id==='later-050').parts[0].text,'new actual operator instruction')
    assert.match(resumed.at(-1).parts[0].text,/remainingScopeTurnsEstimate/, 'new scope needs first estimate, not another reset')
    assert.doesNotMatch(resumed.at(-1).parts[0].text,/现在调用 resetScope/)
    await physicallyObserve(102)
    const second=await hooks.tool.resetScope.execute(resetArgs('second initialized scope'),resetCtx('reset-two-050'))
    assert.match(String(second),/Scope reset. Objective: second initialized scope/)
    assert.equal(isPreviousScopePending(sid),true,'second reset must request clarification of the preceding scope')
    assert.equal(scopeProgress.status(sid).known,false,'second scope begins without a charged estimate')
    const pending=await outbound(sid,[
      user('original-050','original mission',101),
      assistant('rst-two',tool('reset-two-050','resetScope','Scope reset. Objective: second initialized scope')),
      user('later-051','real third instruction',103),
      assistant('normal-tool',tool('noop-050','read','ordinary result')),
    ])
    assert.match(pending.at(-1).parts[0].text,/askPreviousScope\(questions\)/)
    assert.equal(pending.at(-1).strong,true)
    assert.equal(pending.find(x=>x.info?.id==='later-051').parts[0].text,'real third instruction')
    assert.equal(pending.find(x=>x.info?.id==='normal-tool').parts[0].state.output,'ordinary result')
    assert.equal(scopeProgress.status(sid).known,false,'pending ask does not invent a new estimate')
    clearSessionState(sid)
  })
})

test('WHAT[context-compression-051] a cold OpenCode request with historical reset requests its first chargeable estimate rather than inventing budget', async () => {
  const sid='scope-051-cold-budget'
  clearSessionState(sid)
  const source=[
    user('u-cold-051','Continue work after process restart',1),
    assistant('a-reset-051',tool('historical-reset-051','resetScope','historical reset output')),
    user('u-later-051','Actual operator follow-up',2),
    assistant('a-read-051',tool('ordinary-read-051','read','read data')),
  ]
  const before=structuredClone(source)
  const cold=await outbound(sid,structuredClone(source))
  assert.equal(cold.at(-1).attribution,'agent')
  assert.equal(cold.at(-1).strong,true)
  assert.match(cold.at(-1).parts[0].text,/remainingScopeTurnsEstimate/,'first report requirement is explicit in the request copy')
  assert.equal(cold.some(msg=>msg.weak===true),false)
  assert.equal(cold.find(msg=>msg.info?.id==='a-read-051')?.parts[0].state.output,'read data',
    'unknown budget cannot fake [Remaining turnBudget: 0]')
  assert.deepEqual(source,before,'outbound request-copy must not modify the original provider history')
  assert.equal(isScopeInitialized(sid),false)
  scopeProgress.reset(sid,'epoch-051')
  assert.equal(scopeProgress.record(sid,'epoch-051','charged-051','todo-051',4).ok,true)
  assert.equal(scopeProgress.settle(sid,'epoch-051','charged-051').estimate,4)
  const known=await outbound(sid,structuredClone(before))
  assert.equal(known.some(msg=>msg.strong===true),false,'a real charged estimate suppresses first-baseline prompt')
  assert.equal(known.find(msg=>msg.info?.id==='a-read-051')?.parts[0].state.output,'read data')
  scopeProgress.clear(sid)
  clearSessionState(sid)
})


test('WHAT[context-compression-043] OpenCode resetScope strictly refused when no sealed outbound view snapshot exists', async () => {
  await withExecutablePlugin(async (hooks, _dir, _created, runtime) => {
    const sid = 'test-unarchived-refusal-043'
    clearSessionState(sid)
    outboundViewStore.clearSession(sid)
    await acceptAuthorityRoot(runtime, sid, 'engineer')

    assert.equal(isScopeInitialized(sid), false, 'precondition: scope uninitialized')
    assert.equal(turnBudget(sid), 0, 'precondition: budget is 0')
    assert.equal(outboundViewStore.tryGetSealed(sid), undefined, 'precondition: no sealed snapshot')

    // Calling resetScope directly without any prior provider outbound observations
    const outcome = await hooks.tool.resetScope.execute(
      {
        newScopeObjective: 'attempt reset without outbound observation',
        oldScopeVerboseVerbatimExperience: 'None',
      },
      {
        sessionID: sid,
        agent: 'engineer',
        callID: 'call-unarchived-refused-001',
        messageID: 'msg-unarchived-001',
      },
    )

    // 1. Strict refusal text - fail closed, no silent degradation
    assert.equal(
      String(outcome),
      'resetScope failed: no sealed outbound view snapshot captured for this session.',
      'must fail with exact missing sealed snapshot error text; no silent degradation',
    )

    // 2. Memory invariants untouched
    assert.equal(isScopeInitialized(sid), false, 'scope must NOT become initialized on refusal')
    assert.equal(turnBudget(sid), 0, 'budget must NOT be updated on refusal')
    assert.equal(isPreviousScopePending(sid), false, 'no previous scope consultation pending')

    // 3. Durable journal untouched: no ScopeReanchored fact committed
    const state = AgentJournalModule_snapshot(JournalHandle__get_Journal(runtime.journal))
    const sessionProj = AgentProjection_tryFind(SessionIdModule_create(sid), state.AgentProjections)
    const epochOpt = sessionProj?.PrefixEpoch
    assert.equal(epochOpt, undefined, 'no prefix epoch must be created in journal for failed reset')
  })
})

test('WHAT[context-compression-043] OpenCode resetScope commits durable archive and discards sealed snapshot (single-drain)', async () => {
  await withExecutablePlugin(async (hooks, _dir, _created, runtime) => {
    const sid = 'test-archive-drain-043'
    clearSessionState(sid)
    outboundViewStore.clearSession(sid)
    await acceptAuthorityRoot(runtime, sid, 'engineer')

    // 1. Establish full outbound observation triad via actual host hooks
    const system = { system: ['System instruction for scope verification 043'] }
    await hooks['experimental.chat.system.transform']({ sessionID: sid }, system)
    await hooks['chat.params']({ sessionID: sid, model: { providerID: 'anthropic', modelID: 'claude-3-5-sonnet' } }, {})

    const outboundMessages = {
      messages: [
        {
          info: { id: 'user-043-1', role: 'user', sessionID: sid, time: { created: 10 } },
          parts: [{ type: 'text', text: 'Execute step 1 verified change' }],
        },
      ],
    }
    await normalTransform(safeCaps(), sid, {}, outboundMessages)

    // Verify sealed snapshot was captured by the triad in the store
    const captured = outboundViewStore.tryGetSealed(sid)
    assert.ok(captured, 'sealed outbound snapshot must be created by host transforms')
    assert.equal(captured.sessionId, sid)
    assert.ok(captured.system.some((s) => s.includes('System instruction for scope verification 043')))
    assert.equal(captured.model.providerID, 'anthropic')
    assert.equal(captured.model.modelID, 'claude-3-5-sonnet')

    // 2. Execute resetScope
    const outcome1 = await hooks.tool.resetScope.execute(
      {
        newScopeObjective: 'first verified scope objective',
        oldScopeVerboseVerbatimExperience: 'Observed evidence 43',
      },
      {
        sessionID: sid,
        agent: 'engineer',
        callID: 'call-reset-success-043',
        messageID: 'msg-reset-043-1',
      },
    )

    assert.equal(String(outcome1), 'Scope reset. Objective: first verified scope objective')
    assert.equal(isScopeInitialized(sid), true, 'scope is now initialized in memory')
    assert.equal(scopeProgress.status(sid).known,false,'newly reset Scope awaits its first chargeable estimate')
    assert.equal(scopeProgress.status(sid).resetDue,false)

    // 3. Verify durable commit in Journal: blob written and ScopeReanchored fact committed
    const snap1 = AgentJournalModule_snapshot(JournalHandle__get_Journal(runtime.journal))
    const sessionProj1 = AgentProjection_tryFind(SessionIdModule_create(sid), snap1.AgentProjections)
    const epoch1 = sessionProj1?.PrefixEpoch
    assert.ok(epoch1, 'PrefixEpoch must exist in projection')
    assert.equal(epoch1.EpochId?.fields?.[0] ?? epoch1.EpochId, 1n, 'first reset advances epoch to 1n')

    // 4. Verify single-drain semantics: sealed snapshot must have been discarded from store
    const drained = outboundViewStore.tryGetSealed(sid)
    assert.equal(drained, undefined, 'outboundViewStore.discardSealed must have cleared the sealed snapshot')

    // 5. Subsequent call with NEW callID and NO new outbound observation must be rejected (cannot reuse old snapshot)
    const outcome2 = await hooks.tool.resetScope.execute(
      {
        newScopeObjective: 'second premature reset without new observation',
      },
      {
        sessionID: sid,
        agent: 'engineer',
        callID: 'call-second-premature-043',
        messageID: 'msg-reset-043-2',
      },
    )

    assert.equal(
      String(outcome2),
      'resetScope failed: no sealed outbound view snapshot captured for this session.',
      'subsequent resetScope without new outbound observation must fail; drained snapshot cannot be reused',
    )
    assert.equal(scopeProgress.status(sid).known,false,'rejected reset must preserve the previous progress state')
    assert.equal(scopeProgress.status(sid).epoch,'1','rejected reset must not advance the progress epoch')

    // 6. Idempotent replay with SAME callID returns previous success without error or second epoch
    const outcomeReplay = await hooks.tool.resetScope.execute(
      {
        newScopeObjective: 'first verified scope objective',
      },
      {
        sessionID: sid,
        agent: 'engineer',
        callID: 'call-reset-success-043',
        messageID: 'msg-reset-043-1',
      },
    )
    assert.equal(String(outcomeReplay), 'Scope reset. Objective: first verified scope objective')
    const snapReplay = AgentJournalModule_snapshot(JournalHandle__get_Journal(runtime.journal))
    const sessionProjReplay = AgentProjection_tryFind(SessionIdModule_create(sid), snapReplay.AgentProjections)
    assert.equal(sessionProjReplay?.PrefixEpoch?.EpochId?.fields?.[0] ?? sessionProjReplay?.PrefixEpoch?.EpochId, 1n, 'replay must not advance epoch')
  })
})

test('WHAT[context-compression-043] OpenCode resetScope write blob failure leaves memory state and sealed snapshot intact', async () => {
  await withExecutablePlugin(async (hooks, _dir, _created, runtime) => {
    const sid = 'test-blob-failure-043'
    clearSessionState(sid)
    outboundViewStore.clearSession(sid)
    await acceptAuthorityRoot(runtime, sid, 'engineer')

    // Establish full observation
    await hooks['experimental.chat.system.transform']({ sessionID: sid }, { system: ['System prompt'] })
    await hooks['chat.params']({ sessionID: sid, model: { providerID: 'anthropic', modelID: 'claude-3-5-sonnet' } }, {})
    await normalTransform(safeCaps(), sid, {}, {
      messages: [{ info: { id: 'u1', role: 'user', sessionID: sid, time: { created: 1 } }, parts: [{ type: 'text', text: 'Task' }] }],
    })

    const sealedBefore = outboundViewStore.tryGetSealed(sid)
    assert.ok(sealedBefore, 'sealed snapshot must exist before resetScope')

    // Simulate WriteBlob failure at the journal boundary (tag 1 = FSharpResult Error)
    const originalWriteBlob = JournalHandle__get_Journal(runtime.journal).writer.BlobWriter.Write.bind(JournalHandle__get_Journal(runtime.journal).writer.BlobWriter)
    JournalHandle__get_Journal(runtime.journal).writer.BlobWriter.Write = async (_content) => {
      return { tag: 1, fields: ['simulated blob disk write failure (ENOSPC)'] }
    }

    try {
      const outcome = await hooks.tool.resetScope.execute(
        {
          newScopeObjective: 'should fail to write archive blob',
        },
        {
          sessionID: sid,
          agent: 'engineer',
          callID: 'call-blob-fail-043',
          messageID: 'msg-blob-fail-043',
        },
      )

      assert.equal(
        String(outcome),
        'resetScope failed: failed to write archive blob: simulated blob disk write failure (ENOSPC)',
        'must return explicit blob write failure message',
      )

      // Invariants: memory NOT updated
      assert.equal(isScopeInitialized(sid), false, 'scope must NOT be initialized')
      assert.equal(turnBudget(sid), 0, 'budget must NOT be updated')

      // Invariant: sealed snapshot must NOT have been discarded (remains available for retry)
      const sealedAfter = outboundViewStore.tryGetSealed(sid)
      assert.ok(sealedAfter, 'sealed snapshot must NOT be discarded when blob write fails')

      // Invariant: no ScopeReanchored in journal
      const snap = AgentJournalModule_snapshot(JournalHandle__get_Journal(runtime.journal))
      const proj = AgentProjection_tryFind(SessionIdModule_create(sid), snap.AgentProjections)
      assert.equal(proj?.PrefixEpoch, undefined, 'journal must NOT contain prefix epoch')
    } finally {
      JournalHandle__get_Journal(runtime.journal).writer.BlobWriter.Write = originalWriteBlob
    }
  })
})

test('WHAT[context-compression-043] OpenCode resetScope appendAgent failure propagates exception without updating memory or draining snapshot', async () => {
  await withExecutablePlugin(async (hooks, _dir, _created, runtime) => {
    const sid = 'test-append-failure-043'
    clearSessionState(sid)
    outboundViewStore.clearSession(sid)
    await acceptAuthorityRoot(runtime, sid, 'engineer')

    // Establish full observation
    await hooks['experimental.chat.system.transform']({ sessionID: sid }, { system: ['System prompt for append failure'] })
    await hooks['chat.params']({ sessionID: sid, model: { providerID: 'anthropic', modelID: 'claude-3-5-sonnet' } }, {})
    await normalTransform(safeCaps(), sid, {}, {
      messages: [{ info: { id: 'u1', role: 'user', sessionID: sid, time: { created: 1 } }, parts: [{ type: 'text', text: 'Task' }] }],
    })

    const sealedBefore = outboundViewStore.tryGetSealed(sid)
    assert.ok(sealedBefore, 'sealed snapshot must exist before resetScope')

    // Simulate appendAgent failure at the journal writer boundary
    const originalAppend = JournalHandle__get_Journal(runtime.journal).writer.Append.bind(runtime.journal.writer)
    JournalHandle__get_Journal(runtime.journal).writer.Append = async (_runId, _fact, _cancellation) => {
      return { tag: 1, fields: [{ tag: 0, fields: ['simulated durable stream append conflict'] }] }
    }

    try {
      await assert.rejects(
        hooks.tool.resetScope.execute(
          {
            newScopeObjective: 'should throw JournalAppendException',
          },
          {
            sessionID: sid,
            agent: 'engineer',
            callID: 'call-append-fail-043',
            messageID: 'msg-append-fail-043',
          },
        ),
        (err) => {
          return err != null && (err.name === 'JournalAppendException' || String(err).includes('JournalAppendException') || String(err).includes('append conflict'))
        },
        'must raise JournalAppendException on durable append rejection',
      )

      // Invariants: memory NOT updated
      assert.equal(isScopeInitialized(sid), false, 'scope must NOT be initialized when append fails')
      assert.equal(turnBudget(sid), 0, 'budget must NOT be updated when append fails')

      // Invariant: sealed snapshot must NOT have been discarded (remains available for retry)
      const sealedAfter = outboundViewStore.tryGetSealed(sid)
      assert.ok(sealedAfter, 'sealed snapshot must NOT be discarded when durable append fails')
    } finally {
      JournalHandle__get_Journal(runtime.journal).writer.Append = originalAppend
    }
  })
})


test('WHAT[host-boundary-011] physically registered Advisor provider hook excludes fictitious assistant dot from continuing consultation',async()=>{
  await withExecutablePlugin(async(hooks,_directory,_created,runtime)=>{
    const sid='ses_advisor_no_fake_dot_011'
    const u1=user('a-user-011','What actually happened?',101)
    const onlyReasoning=assistant('a-thought-011',{type:'reasoning',text:'No final prose in this run'})
    const u2=user('a-user-012','Please provide the missing observation.',102)
    await hooks['chat.message']({sessionID:sid,agent:'advisor',messageID:'a-user-012'},
      {message:{...u2.info,sessionID:sid},parts:u2.parts})
    const providerAssistant={info:{id:'assistant-provider-011',sessionID:sid,parentID:'a-user-012',
      role:'assistant',agent:'advisor',providerID:'fixture',modelID:'advisor-model',time:{created:103}},parts:[]}
    runtime.pushHostMessage(sid,providerAssistant)
    await hooks.event({event:{type:'message.updated',properties:{info:providerAssistant.info}}})
    const payload={messages:[u1,onlyReasoning,u2]}
    const snapshot=structuredClone(payload.messages)
    await hooks['experimental.chat.messages.transform']({sessionID:sid},payload)
    assert.equal(payload.messages.filter(m=>m.info?.role==='assistant'
      && m.parts?.some(p=>p.text==='.'||p.text==='...')).length,0,
      'the real dedicated Advisor must never see a synthetic assistant answer')
    assert.equal(payload.messages.some(m=>m.info?.id==='a-thought-011'),false,
      'reasoning-only assistant run remains in original evidence, not as fake provider reply')
    assert.deepEqual(snapshot,[u1,onlyReasoning,u2],
      'the persisted/raw transcript is not rewritten by provider projection')
  })
})


test('WHAT[advisor-031] a durable Advisor owner cannot create another Advisor seat or consume model capacity', async()=>{
  await withExecutablePlugin(async (_hooks,_directory, createdIds,runtime)=>{
    const caller='advisor-caller-031'
    await acceptAuthorityRoot(runtime,caller,'advisor')
    const before=createdIds.length
    const deps={
      Journal:JournalHandle__get_Journal(runtime.journal),
      Snapshot:null,
      Sessions:{
        FamilyRootOf:()=>{throw Error('must not inspect or create Advisor child')},
        ListChildren:()=>{throw Error('must not list Advisor children')},
        CreateChildSession:()=>{throw Error('must not create Advisor child')},
      },
      Directory:runtime.directory,
      Participant:'advisor',
      BindDelegation:()=>{throw Error('must not bind nested Advisor')},
    }
    const result=await AdvisorSeat_consult(deps,caller,'consult a second Advisor')
    assert.match(JSON.stringify(result),/dedicated Advisor cannot open another dedicated Advisor seat/)
    assert.equal(createdIds.length,before,'no child session was dispatched')
    assert.equal(runtime.prompts.length,0,'no physical root prompt nor provider admission was attempted')
  })
})


test('WHAT[context-compression-052] one actual OpenCode provider run charges once and takes min across real registered todowrites',async()=>{
  await withExecutablePlugin(async(hooks,_dir,_created,runtime)=>{
    const estimateIntent='Observe this real LLM request and preserve existing work.'
    const estimateProgram='class Js extends JsProgram { async run() { return { observed: true }; } }'
    const estimateArgs=(name,remainingScopeTurnsEstimate)=>({name,program:estimateProgram,intent:estimateIntent,remainingScopeTurnsEstimate})
    const sid='ses-progress-min-052'
    scopeProgress.clear(sid)
    await acceptAuthorityRoot(runtime,sid,'devops')
    const tool=hooks.tool['todowrite-for-devops']
    assert.ok(tool)
    assert.equal(tool.args.remainingScopeTurnsEstimate.safeParse(undefined).success,false)
    assert.equal(tool.args.remainingScopeTurnsEstimate.safeParse(-1).success,false)
    assert.equal(tool.args.remainingScopeTurnsEstimate.safeParse(0).success,true)
    const context=(callID,messageID)=>({sessionID:sid,agent:'devops',callID,messageID})
    const [left,right]=await Promise.all([
      tool.execute(estimateArgs('estimate-high',8),context('todo-high-052','physical-run-one-052')),
      tool.execute(estimateArgs('estimate-low',4),context('todo-low-052','physical-run-one-052')),
    ])
    assert.match(String(left),/# ok/)
    assert.match(String(right),/# ok/)
    assert.equal(scopeProgress.status(sid).known,false,'recording individual calls cannot settle the round early')
    const model={providerID:'fixture',modelID:'devops'}
    await hooks['chat.params']({sessionID:sid,model},{})
    assert.equal(scopeProgress.status(sid).estimate,4,'next actual provider request settles the preceding min')
    assert.equal(scopeProgress.status(sid).resetDue,false)
    const replay=await tool.execute(estimateArgs('estimate-low',4),context('todo-low-052','physical-run-one-052'))
    assert.ok(typeof replay==='string')
    await hooks['chat.params']({sessionID:sid,model},{})
    assert.equal(scopeProgress.status(sid).estimate,4,'same run cannot double charge or invent advisor trigger')
    const zero=await tool.execute(estimateArgs('scope-complete',0),context('todo-zero-052','physical-run-two-052'))
    assert.match(String(zero),/# ok/)
    await hooks['chat.params']({sessionID:sid,model},{})
    assert.equal(scopeProgress.status(sid).estimate,0)
    assert.equal(scopeProgress.status(sid).resetDue,true)
    const input={sessionID:sid}
    const output={messages:[{info:{id:'operator-052',role:'user',sessionID:sid,time:{created:1}},
      parts:[{type:'text',text:'Continue the actual session'}]}]}
    await normalTransform(safeCaps(),sid,input,output)
    assert.equal(output.messages.at(-1).attribution,'agent')
    assert.equal(output.messages.at(-1).strong,true)
    assert.match(output.messages.at(-1).parts[0].text,/resetScope/)
    scopeProgress.clear(sid)
  })
})


test('WHAT[delegation-negotiation-035] temporary SDK chat admission is isolated from managed lease and retains the original tool schema', async () => {
  const {
    negotiationRememberSystem, negotiationRememberMessages, negotiationRememberModel,
    negotiationStartTemporary, negotiationIsTemporary,
    negotiationFinishTemporary, negotiationForgetSession,
  } = await import('../../../dist/OpenCode/Plugin/PluginTransforms.js')
  const routing = await import('../../../dist/OpenCode/Host/ModelRoutingSurface.js')
  await withExecutablePlugin(async (hooks, _dir, _created, runtime) => {
    const parent = 'ses-ephemeral-source-035', temp = 'ses-ephemeral-child-035'
    negotiationRememberSystem({sessionID:parent},{system:['System from the actual worker']})
    negotiationRememberMessages(parent,{messages:[{info:{id:'parent-u',role:'user',sessionID:parent},
      parts:[{type:'text',text:'Actual worker request'}]}]})
    negotiationRememberModel({sessionID:parent,model:{providerID:'provider',modelID:'worker-model'}})
    assert.equal(negotiationStartTemporary(temp,parent).tag,0)
    assert.equal(negotiationIsTemporary(temp),true)
    try {
      const tools={read:true,negotiate:true,resetScope:true,askPreviousScope:true}
      const before=structuredClone(tools)
      const capacityBefore=routing.sharedCapacitySnapshot()
      await hooks['chat.message'](
        {sessionID:temp,agent:'devops',messageID:'msg-ephemeral-035'},
        {message:{id:'msg-ephemeral-035',sessionID:temp,role:'user',agent:'devops',tools},
          parts:[{type:'text',text:'Please explain the actual evidence'}]})
      await hooks['chat.params']({
        sessionID:temp,
        message:{id:'msg-ephemeral-035',sessionID:temp,role:'user'},
        agent:'devops',
        model:{providerID:'provider',modelID:'worker-model'},
      },{})
      assert.deepEqual(tools,before,'a temporary request retains the native tool schema')
      assert.deepEqual(routing.sharedCapacitySnapshot(),capacityBefore,
        'a read-only ephemeral request must not allocate a managed chat lease')
      await assert.rejects(
        hooks['tool.execute.before']({sessionID:temp,tool:'read',callID:'forbidden-035'},{args:{}}),
        /Ephemeral negotiation is read-only/)
    } finally {
      negotiationFinishTemporary(temp)
      negotiationForgetSession(parent)
    }
  })
})


test('WHAT[delegation-negotiation-036] a temporary SDK lease rejection never becomes an invented incomplete-answer follow-up', async () => {
  const { bindDelegation, clearSessionState } =
    await import('../../../dist/Context/Scope/Surface.js')
  const {
    negotiationRememberSystem, negotiationRememberMessages,
    negotiationRememberModel, negotiationForgetSession,
  } = await import('../../../dist/OpenCode/Plugin/PluginTransforms.js')
  await withExecutablePlugin(async (hooks,_dir,_created,runtime) => {
    const parent='ses-lease-error-parent-036', child='ses-lease-error-child-036'
    await acceptAuthorityRoot(runtime,parent,'manager')
    await acceptAuthorityRoot(runtime,child,'engineer')
    clearSessionState(child)
    bindDelegation(child,parent)
    negotiationRememberSystem({sessionID:parent},{system:['Existing worker system']})
    negotiationRememberMessages(parent,{messages:[
      {info:{id:'actual-u-036',sessionID:parent,role:'user'},parts:[{type:'text',text:'Evidence 42'}]},
    ]})
    negotiationRememberModel({sessionID:parent,model:{providerID:'provider',modelID:'worker-model'}})
    const session=runtime.client.session
    const originalDelete=session.delete
    let signal
    const completed=new Promise(resolve=>signal=resolve)
    session.delete=async args=>{try{return await originalDelete(args)}finally{signal()}}
    const seen=[]
    session.prompt=async args=>{
      seen.push(args.body.parts[0].text)
      return {error:{message:"PROMPT-006: managed provider run has no committed execution lease"}}
    }
    try {
      const accepted=await hooks.tool.negotiate.execute(
        {questions:'What evidence is reliable?'},
        {sessionID:child,agent:'engineer',callID:'negotiate-lease-036'})
      assert.match(String(accepted),/Questions accepted|问题已受理/)
      await Promise.race([completed,new Promise((_,reject)=>setTimeout(()=>reject(new Error('temporary cleanup not observed')),3000))])
      assert.deepEqual(seen,['What evidence is reliable?'],
        'an SDK protocol failure is not a blank LLM answer and must not generate another user prompt')
      assert.equal(runtime.prompts.some(p=>JSON.stringify(p).includes('PROMPT-006')),false,
        'the managed lease error must never be sent to the worker as a purported answer')
    } finally {
      session.delete=originalDelete
      negotiationForgetSession(parent)
      clearSessionState(child)
    }
  })
})


test('WHAT[delegation-negotiation-037] thrown temporary provider admission/capacity error stops without chat retry', async () => {
  const { bindDelegation,clearSessionState } = await import('../../../dist/Context/Scope/Surface.js')
  const { negotiationRememberSystem,negotiationRememberMessages,
    negotiationRememberModel,negotiationForgetSession } =
    await import('../../../dist/OpenCode/Plugin/PluginTransforms.js')
  await withExecutablePlugin(async (hooks,_dir,_created,runtime)=>{
    const parent='ses-capacity-parent-037', child='ses-capacity-child-037'
    await acceptAuthorityRoot(runtime,parent,'manager')
    await acceptAuthorityRoot(runtime,child,'engineer')
    clearSessionState(child)
    bindDelegation(child,parent)
    negotiationRememberSystem({sessionID:parent},{system:['Observed worker system']})
    negotiationRememberMessages(parent,{messages:[
      {info:{id:'u-capacity-037',role:'user',sessionID:parent},parts:[{type:'text',text:'Original evidence 37'}]},
    ]})
    negotiationRememberModel({sessionID:parent,model:{providerID:'provider',modelID:'worker-model'}})
    const session=runtime.client.session
    const originalDelete=session.delete
    let finish
    const completed=new Promise(resolve=>finish=resolve)
    session.delete=async arg=>{try{return await originalDelete(arg)}finally{finish()}}
    const prompted=[]
    session.prompt=async arg=>{
      prompted.push(arg.body.parts[0].text)
      throw new Error('TransactionStopped (CapacityQueueFull)')
    }
    try {
      const receipt=await hooks.tool.negotiate.execute(
        {questions:'What should the worker investigate next?'},
        {sessionID:child,agent:'engineer',callID:'negotiate-037'})
      assert.match(String(receipt),/Questions accepted|问题已受理/)
      await Promise.race([completed,new Promise((_,reject)=>setTimeout(()=>reject(new Error('cleanup not observed')),3000))])
      assert.deepEqual(prompted,['What should the worker investigate next?'],
        'transport capacity rejection cannot be confused with an incomplete assistant response')
      assert.equal(runtime.prompts.some(x=>JSON.stringify(x).includes('刚才的回答不完整')),false)
    } finally {
      session.delete=originalDelete
      negotiationForgetSession(parent)
      clearSessionState(child)
    }
  })
})


test('WHAT[delegation-negotiation-038] a normally completed empty SDK reply terminates without an invented second user request', async () => {
  const { bindDelegation,clearSessionState } = await import('../../../dist/Context/Scope/Surface.js')
  const { negotiationRememberSystem,negotiationRememberMessages,
    negotiationRememberModel,negotiationForgetSession } =
    await import('../../../dist/OpenCode/Plugin/PluginTransforms.js')
  await withExecutablePlugin(async (hooks,_dir,_created,runtime)=>{
    const parent='ses-empty-parent-038', child='ses-empty-child-038'
    await acceptAuthorityRoot(runtime,parent,'manager')
    await acceptAuthorityRoot(runtime,child,'engineer')
    clearSessionState(child)
    bindDelegation(child,parent)
    negotiationRememberSystem({sessionID:parent},{system:['Observed worker system']})
    negotiationRememberMessages(parent,{messages:[
      {info:{id:'u-empty-038',role:'user',sessionID:parent},parts:[{type:'text',text:'Original evidence 38'}]},
    ]})
    negotiationRememberModel({sessionID:parent,model:{providerID:'provider',modelID:'worker-model'}})
    const session=runtime.client.session
    const originalDelete=session.delete
    let finish
    const completed=new Promise(resolve=>finish=resolve)
    session.delete=async arg=>{try{return await originalDelete(arg)}finally{finish()}}
    const prompted=[]
    session.prompt=async arg=>{
      prompted.push(arg.body.parts[0].text)
      return {data:{info:{role:'assistant',finish:'stop'},parts:[]}}
    }
    try {
      const receipt=await hooks.tool.negotiate.execute(
        {questions:'What should the worker investigate next?'},
        {sessionID:child,agent:'engineer',callID:'negotiate-038'})
      assert.match(String(receipt),/Questions accepted|问题已受理/)
      await Promise.race([completed,new Promise((_,reject)=>setTimeout(()=>reject(new Error('cleanup not observed')),3000))])
      assert.deepEqual(prompted,['What should the worker investigate next?'],
        'a completed empty answer is an explicit host failure, not grounds for a fictional follow-up')
      assert.equal(runtime.prompts.some(x=>JSON.stringify(x).includes('刚才的回答不完整')),false)
    } finally {
      session.delete=originalDelete
      negotiationForgetSession(parent)
      clearSessionState(child)
    }
  })
})
