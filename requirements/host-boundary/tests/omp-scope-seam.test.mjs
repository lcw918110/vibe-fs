import { JournalHandle__get_Journal } from '../../../dist/Persistence/Journal/Surface.js'
// Host physical canary: raw OMP adapter seam / host-shape
import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { activateOnOmp } from '../../../dist/Omp/Adapter.js'
import { AgentRole, JsonSchema } from '../../../dist/Host/Ports/Vocabulary.js'
import { AgentRoleSpec } from '../../../dist/Host/Ports/Lifecycle.js'
import { ContextEdit } from '../../../dist/Host/Ports/TurnTransforms.js'
import { ofArray } from '../../../dist/fable_modules/fable-library-js.5.13.0/List.js'
import { outboundViewStore } from 'wanxiangli/pair-hint'
import { acquireSharedForWorkspace } from '../../../dist/OpenCode/Host/WorkspaceSharedJournal.js'
import { AgentJournalModule_snapshot } from '../../../dist/Persistence/Journal/AgentJournal.js'
import { SessionIdModule_create } from '../../../dist/Foundation/Identity.js'
import { AgentProjection_tryFind } from '../../../dist/Composition/Durable/Projection.js'
import {
  resetScopeState,
  onTurnStart,
  turnBudget,
  isScopeInitialized,
  isPreviousScopePending,
  clearSessionState,
  bindDelegation,
  askNegotiation,
  prepareNegotiationReply,
  confirmNegotiationSteer,
  negotiationRequired,
  rememberSystem,
  rememberMessages,
  rememberModel,
  modelFor,
  startTemporary,
  finishTemporary,
  isTemporary,
  recordToolViolation,
  toolViolationCount,
} from '../../../dist/Context/Scope/Surface.js'

function createOmpTestEnvironment() {
  const eventHandlers = new Map()
  const registeredTools = []
  let turnContextMessagesMode = 'keep'
  let observedCatalog = null
  let attachedTransport = null

  const mockPi = {
    logger: {
      debug: () => {},
      info: () => {},
      warn: () => {},
      error: () => {},
    },
    on: (eventName, handler) => {
      eventHandlers.set(eventName, handler)
    },
    registerTool: (toolDef) => {
      registeredTools.push(toolDef)
    },
  }

  const mockPlugin = {
    Activate: async (ports) => {
      observedCatalog = ports.Agents
      if (ports.Tools && typeof ports.Tools.Register === 'function') {
        const negotiateSpec = {
          Name: 'negotiate',
          Description: 'Ask your delegator clarifying questions about constraints or intent. The answer will be delivered to you as a steer.',
          Schema: new JsonSchema(JSON.stringify({
            type: 'object', properties: { questions: { type: 'string', description: 'Ask all clarifying questions together in one message.' } }, required: ['questions'],
          })),
          Admission: null,
          Execute: async (inv) => {
            const sid = inv.Session?.fields?.[0] ?? inv.Session ?? ''
            let questions = ''
            try {
              const raw = inv.Args?.fields?.[0] ?? inv.Args ?? '{}'
              const parsed = typeof raw === 'string' ? JSON.parse(raw) : raw
              questions = parsed.questions ?? ''
            } catch {}
            const admitted = askNegotiation(sid, questions)
            if (!admitted.ok) {
              return { Text: admitted.reason ?? 'negotiate refused', Truncated: false, IsError: true, Details: null }
            }
            if (attachedTransport) {
              (async () => {
                const turnReq = {
                  DelegatorSessionId: admitted.delegator,
                  Receipt: admitted.receipt,
                  PromptText: questions,
                  RetryPromptText: 'Retry',
                }
                const turnRes = await attachedTransport.AskEphemeral(turnReq)
                if (turnRes?.tag === 0) {
                  const replyText = turnRes.fields[0]
                  const prep = prepareNegotiationReply(admitted.receipt, replyText)
                  if (prep?.ok) {
                    await attachedTransport.DeliverAndObserve({
                      RecipientSessionId: sid,
                      Receipt: admitted.receipt,
                      SteerText: prep.steer,
                      CorrelationKey: { fields: ['negotiate:' + admitted.receipt] },
                    })
                    confirmNegotiationSteer(admitted.receipt, sid)
                  }
                }
              })()
            }
            return { Text: 'Clarification requested (receipt ' + admitted.receipt + '). Negotiation is complete and the timeline continues; resetScope is available now. Do not wait for an answer; if the delegator\'s answer is obtained, it may arrive later as a steer.', Truncated: false, IsError: false, Details: null }
          },
        }
        await ports.Tools.Register(ofArray([negotiateSpec]))
      }
      const hooks = {
        OnSignal: async () => {},
        Turn: {
          ContextMessages: async (_ctx, _messages) => {
            if (turnContextMessagesMode === 'keep') {
              return new ContextEdit(0, [])
            } else if (turnContextMessagesMode === 'lossy-replace') {
              return new ContextEdit(1, [
                {
                  Address: null,
                  Role: { tag: 0 },
                  Parts: [{ tag: 0, fields: ['lossy prose only'] }],
                },
              ])
            }
            return new ContextEdit(0, [])
          },
          SystemPrompt: async (_ctx, prompt) => prompt,
        },
        ToolGate: null,
      }
      return { tag: 0, fields: [hooks] }
    },
    AttachTransport: (t) => {
      attachedTransport = t
    },
  }

  const agentManager = new AgentRoleSpec(new AgentRole(1), null, null, null)
  const agentEngineer = new AgentRoleSpec(new AgentRole(3), null, null, null)
  const agentsList = ofArray([agentManager, agentEngineer])

  return {
    mockPi,
    mockPlugin,
    agentsList,
    eventHandlers,
    registeredTools,
    getObservedCatalog: () => observedCatalog,
    setTurnMode: (mode) => {
      turnContextMessagesMode = mode
    },
  }
}

const SEP = '\0\uFEFF'
const USER_REMINDERS = ['en', 'zh-CN'].map(language => readFileSync(
  new URL(`../../../resources/provider/host/scope-steer/missing-estimate/${language}.md`, import.meta.url), 'utf8').trim())

const contentText = (content) =>
  typeof content === 'string' ? content : (content ?? []).map((block) => block.text ?? '').join('')

const ompSession = (getSessionId, header = { parentSession: null }) => ({
  cwd: process.cwd(),
  sessionManager: {
    getSessionId,
    getSessionDir: () => process.cwd(),
    getHeader: () => header,
  },
})

async function activateSession(env, sid) {
  clearSessionState(sid)
  const sessionCtx = ompSession(() => sid)
  await env.eventHandlers.get('session_start')({}, sessionCtx)
  return sessionCtx
}

test('WHAT[context-compression-034] resetScope registration carries the new protocol texts', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)
  await activateSession(env, 'ses_test_reg')

  const def = env.registeredTools.find((t) => t.name === 'resetScope')
  assert.ok(def, 'resetScope must be registered')
  assert.equal(
    def.description,
    'End the current Scope; preserve operator and assistant prose/reasoning plus critical consultation tool call/results, remove ordinary old tool process, and report outstanding work. Arguments: experience and objective.',
  )
  assert.equal(
    def.parameters.properties.newScopeObjective.description,
    'The objective for the new scope. The scope must form a self-contained state closed-loop (状态闭环).',
  )
  assert.equal(def.parameters.properties.newScopeTurnBudget,undefined)
  assert.equal(def.parameters.properties.kpiCompletion,undefined)
  assert.equal(
    def.parameters.properties.oldScopeVerboseVerbatimExperience.description,
    'Write out your experience and findings from the closing scope, verbosely: everything between the previous resetScope call (or session start) and this call. Any exact text needed in the next scope (such as function signatures, error stack traces, or exact line numbers) MUST be copied VERBATIM here. Null or omitted if there is no prior work.',
  )
})

test('WHAT[context-compression-034] OMP system projection supplies protocol facts, not a conditional reset command', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)
  const sessionCtx = await activateSession(env, 'ses_omp_scope_prompt')

  const en = readFileSync(
    new URL('../../../resources/provider/host/scope-protocol/en.md', import.meta.url),
    'utf8',
  )
  assert.doesNotMatch(en, /\[System:|Then call resetScope immediately|single exception|If you are not busy/)

  const handler = env.eventHandlers.get('before_agent_start')
  const result = await handler({ systemPrompt: ['base prompt'] }, sessionCtx)
  assert.ok(result?.systemPrompt)
  const section = result.systemPrompt.find(p => p.includes('## Scope & Context Management'))
  assert.ok(section, 'scope protocol is present')
  assert.doesNotMatch(section, /\[System:|Then call resetScope immediately|If you are not busy/)
  assert.match(section, /Calling \x60resetScope\x60 removes the old scope/)

  const stale = '## Scope & Context Management (\x60resetScope\x60)\n[System: Then call resetScope immediately, before doing anything else.]'
  const replaced = await handler({ systemPrompt: ['base prompt', stale] }, sessionCtx)
  const updated = replaced.systemPrompt.filter(p => p.includes('## Scope & Context Management'))
  assert.equal(updated.length, 1, 'old scope fragment is replaced, not duplicated')
  assert.doesNotMatch(updated[0], /\[System:|Then call resetScope immediately/)
  const oldChinese = '## Scope 与上下文\n[System: 由模型自己判断现在是否应重置]'
  const repairedChinese = await handler({ systemPrompt: ['base prompt', oldChinese] }, sessionCtx)
  const scopeLines = repairedChinese.systemPrompt.filter(p => p.includes('## Scope 与上下文') || p.includes('## Scope & Context Management'))
  assert.equal(scopeLines.length, 1, 'Chinese stale scope segment is replaced, never duplicated')
  assert.doesNotMatch(scopeLines[0], /由模型自己判断|\[System:/)
})

test('WHAT[context-compression-034] before first reset OMP adds one fresh agent strong steer without rewriting user or tool bytes', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)
  const sid = 'ses_omp_scope_reminder'
  const sessionCtx = await activateSession(env, sid)
  env.setTurnMode('keep')

  const messages = [
    { role: 'user', content: [{ type: 'text', text: 'ship the scope port' }], timestamp: 1000 },
    { role: 'assistant', content: [{ type: 'toolCall', id: 'read-1', name: 'read', args: {} }] },
    { role: 'toolResult', toolCallId: 'read-1', toolName: 'read', content: [{ type: 'text', text: 'file body' }] },
  ]

  const result = await env.eventHandlers.get('context')({ messages }, sessionCtx)
  assert.ok(result && result.messages, 'context handler returns the request copy')
  assert.equal(contentText(result.messages[0].content), 'ship the scope port')
  assert.equal(contentText(result.messages[2].content), 'file body')
  assert.equal(result.messages.length, 4)
  const initialSteer = result.messages[3]
  assert.equal(initialSteer.attribution, 'agent')
  assert.equal(initialSteer.strong, true)
  assert.equal(initialSteer.steering, true)
  assert.ok(USER_REMINDERS.includes(contentText(initialSteer.content)), 'the baseline steer is the localized resource text')
  assert.equal(contentText(messages[2].content), 'file body', 'raw history stays unchanged')

  const hook = env.eventHandlers.get('tool_result')
  assert.equal(await hook({ toolName: 'read', toolCallId: 'read-1',
    content: [{ type: 'text', text: 'file body' }], isError: false }, sessionCtx), undefined,
    'the tool-result hook does not inject a suffix')

  resetScopeState(sid, 4)
  const nextRaw = [
    ...messages,
    { role: 'assistant', content: [{ type: 'toolCall', id: 'reset-1', name: 'resetScope', args: {} }] },
    { role: 'toolResult', toolCallId: 'reset-1', content: 'Scope reset. Objective: edit' },
    { role: 'assistant', content: [{ type: 'toolCall', id: 'read-2', name: 'read', args: {} }] },
    { role: 'toolResult', toolCallId: 'read-2', content: 'later output' },
  ]
  const next = await env.eventHandlers.get('context')({ messages: nextRaw }, sessionCtx)
  assert.ok(next && next.messages)
  const resetResult = next.messages.find(m => m.toolCallId === 'reset-1')
  const readResult = next.messages.find(m => m.toolCallId === 'read-2')
  assert.equal(resetResult.content, 'Scope reset. Objective: edit')
  assert.equal(readResult.content, 'later output')
  assert.equal(nextRaw.at(-1).content, 'later output', 'raw tool output remains unsuffixed')
  assert.equal(await hook({ toolName: 'read', toolCallId: 'read-2', content: 'later output' }, sessionCtx), undefined)

})

test('WHAT[scope-progress-016] OMP sends no estimate steer while durable Scope progress is unknown', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)
  const { scopeProgress } = await import('wanxiangli/scope-progress')
  const sid = 'ses_omp_scope_progress_unknown'
  const sessionCtx = await activateSession(env, sid)
  env.setTurnMode('keep')
  scopeProgress.reset(sid, '1')
  scopeProgress.bindSessionJournal(sid, () => { throw new Error('journal offline') })
  try {
    assert.equal(scopeProgress.status(sid).status, 'Unknown')
    const messages = [{ role: 'user', content: [{ type: 'text', text: 'ship the scope port' }], timestamp: 1000 }]
    const result = await env.eventHandlers.get('context')({ messages }, sessionCtx)
    const sent = result?.messages ?? messages
    assert.equal(sent.length, 1, 'unknown progress is not a missing estimate')
    assert.ok(!sent.some(message => USER_REMINDERS.includes(contentText(message.content))))
  } finally {
    scopeProgress.unbindSessionJournal(sid)
    scopeProgress.clear(sid)
  }
})

test('WHAT[scope-progress-016] OMP strips the physical delivery receipt before the Provider sees an agent steer', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)
  const sid = 'ses_omp_scope_receipt_strip'
  const sessionCtx = await activateSession(env, sid)
  env.setTurnMode('keep')
  const advice = 'Check the migration order before the next edit.'
  const messages = [
    { role: 'user', content: [{ type: 'text', text: 'ship the scope port' }], timestamp: 1000 },
    { role: 'user', attribution: 'agent', steering: true, content: [{ type: 'text', text: advice + '\n[receipt:r-0123abcd]' }], timestamp: 1001 },
    { role: 'user', content: 'operator text\n[receipt:r-keep]', timestamp: 1002 },
  ]
  const result = await env.eventHandlers.get('context')({ messages }, sessionCtx)
  const sent = result?.messages ?? messages
  assert.equal(contentText(sent[1].content), advice)
  assert.equal(contentText(sent[2].content), 'operator text\n[receipt:r-keep]', 'operator bytes are never rewritten')
  assert.match(contentText(messages[1].content), /\[receipt:r-0123abcd\]$/, 'raw history keeps the receipt for message_start')
})

test('WHAT[context-compression-034] OMP purge preserves a user-attributed custom message and refuses a batch without an operator message', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)
  const sid = 'ses_omp_scope_custom'
  const sessionCtx = await activateSession(env, sid)
  env.setTurnMode('keep')

  const custom = {
    role: 'custom',
    customType: 'collab',
    attribution: 'user',
    content: [{ type: 'text', text: 'peer steer' }],
    timestamp: 1020,
  }
  const messages = [
    { role: 'user', content: [{ type: 'text', text: 'task' }], timestamp: 1000 },
    custom,
    { role: 'assistant', content: [{ type: 'toolCall', id: 'read-1', name: 'read', args: {} }] },
    { role: 'toolResult', toolCallId: 'read-1', content: 'noise' },
    {
      role: 'assistant',
      content: [
        { type: 'toolCall', id: 'reset-1', name: 'resetScope', args: { newScopeObjective: 'g', newScopeTurnBudget: 2 } },
      ],
    },
    { role: 'toolResult', toolCallId: 'reset-1', content: `Scope reset. Objective: g${SEP}[Remaining turnBudget: 2]` },
  ]

  const result = await env.eventHandlers.get('context')({ messages }, sessionCtx)
  assert.ok(result && result.messages, 'request copy returned')
  assert.ok(result.messages.includes(custom), 'user-attributed custom message survives the purge')
  assert.ok(!result.messages.some((m) => m.toolCallId === 'read-1'), 'pre-barrier work is purged')

  // A batch with no operator-authored message: the original messages pass through untouched.
  const sideOnly = [
    {
      role: 'assistant',
      content: [
        { type: 'toolCall', id: 'reset-9', name: 'resetScope', args: { newScopeObjective: 'g', newScopeTurnBudget: 1 } },
      ],
    },
    { role: 'toolResult', toolCallId: 'reset-9', content: 'Scope reset.' },
    { role: 'user', attribution: 'agent', content: [{ type: 'text', text: 'title this session' }], timestamp: 9000 },
  ]
  const noop = await env.eventHandlers.get('context')({ messages: sideOnly }, sessionCtx)
  assert.ok(!noop || !noop.messages, 'no operator message must produce no rewrite at all')
  if (noop && noop.messages) {
    assert.equal(noop.messages[noop.messages.length - 1], sideOnly[sideOnly.length - 1])
    assert.equal(contentText(noop.messages[noop.messages.length - 1].content), 'title this session')
  }
})
test('WHAT[context-compression-033] resetScope tool registration on OMP pi and exposure in role tool policies', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)

  const sessionStartHandler = env.eventHandlers.get('session_start')
  assert.ok(sessionStartHandler, 'session_start handler must be registered')

  const sessionCtx = {
    cwd: process.cwd(),
    sessionManager: {
      getSessionId: () => 'ses_omp_test_035_1',
      getSessionDir: () => process.cwd(),
      getHeader: () => ({ parentSession: null }),
    },
  }
  await sessionStartHandler({}, sessionCtx)

  const resetScopeDef = env.registeredTools.find((t) => t.name === 'resetScope')
  assert.ok(resetScopeDef, 'resetScope tool must be registered on pi')
  assert.equal(resetScopeDef.loadMode, 'essential', 'resetScope loadMode must be essential')
  assert.equal(resetScopeDef.concurrency, 'exclusive', 'resetScope concurrency must be exclusive')
  assert.ok(resetScopeDef.parameters.properties.oldScopeVerboseVerbatimExperience, 'has oldScopeVerboseVerbatimExperience')
  assert.ok(resetScopeDef.parameters.properties.newScopeObjective, 'has newScopeObjective')
  assert.equal(resetScopeDef.parameters.properties.newScopeTurnBudget,undefined)
  assert.equal(resetScopeDef.parameters.properties.kpiCompletion,undefined)
  assert.deepEqual(resetScopeDef.parameters.required,['newScopeObjective'])

  const catalog = env.getObservedCatalog()
  assert.ok(catalog, 'Agents catalog port was bound')
  const rolesOutcome = await catalog.Roles()
  assert.ok(rolesOutcome.fields && rolesOutcome.fields[0], 'Roles() returned Ok')
  const effectiveRoles = rolesOutcome.fields[0]
  for (const roleSpec of effectiveRoles) {
    assert.ok(roleSpec.ToolPolicy, 'role must have ToolPolicy defined')
    assert.equal(roleSpec.ToolPolicy.get('resetScope'), true, 'resetScope must be explicitly allowed for role')
  }
})

test('WHAT[context-compression-034] OMP before_agent_start injects scope protocol system prompt idempotently', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)

  const sessionCtx = {
    cwd: process.cwd(),
    sessionManager: {
      getSessionId: () => 'ses_omp_test_035_2',
      getSessionDir: () => process.cwd(),
      getHeader: () => ({ parentSession: null }),
    },
  }
  await env.eventHandlers.get('session_start')({}, sessionCtx)

  const beforeAgentStartHandler = env.eventHandlers.get('before_agent_start')
  assert.ok(beforeAgentStartHandler, 'before_agent_start handler must be registered')

  const initialEvent = {
    systemPrompt: ['You are a helpful programming assistant.'],
  }
  const result1 = await beforeAgentStartHandler(initialEvent, sessionCtx)
  assert.ok(result1 && result1.systemPrompt, 'before_agent_start returned systemPrompt')
  assert.ok(
    result1.systemPrompt.some((p) => p.includes('## Scope & Context Management (`resetScope`)')),
    'systemPrompt must contain ## Scope & Context Management (`resetScope`) section',
  )

  // Idempotency: repeated call does not duplicate section
  const result2 = await beforeAgentStartHandler({ systemPrompt: result1.systemPrompt }, sessionCtx)
  assert.equal(result2.systemPrompt.length, result1.systemPrompt.length, 'injection must be idempotent')
})

test('WHAT[context-compression-033] OMP context handler preserves raw messages, tool pairing and metadata on Keep', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)

  const sid = 'ses_omp_test_035_3'
  clearSessionState(sid)
  const sessionCtx = {
    cwd: process.cwd(),
    sessionManager: {
      getSessionId: () => sid,
      getSessionDir: () => process.cwd(),
      getHeader: () => ({ parentSession: null }),
    },
  }
  await env.eventHandlers.get('session_start')({}, sessionCtx)

  const rawOmpMessages = [
    {
      role: 'user',
      content: [{ type: 'text', text: 'Task 1: explore code' }],
      attribution: 'user',
      timestamp: 1000,
    },
    {
      role: 'assistant',
      content: [
        { type: 'text', text: 'reading...' },
        { type: 'toolCall', id: 'call_read_1', name: 'read', args: { path: 'file1.txt' } },
      ],
      timestamp: 1010,
    },
    {
      role: 'toolResult',
      toolCallId: 'call_read_1',
      content: 'data 1',
      timestamp: 1020,
    },
    {
      role: 'assistant',
      content: [
        {
          type: 'toolCall',
          id: 'call_reset_1',
          name: 'resetScope',
          args: { newScopeObjective: 'Task 2: modify', newScopeTurnBudget: 5 },
        },
      ],
      timestamp: 1030,
    },
    {
      role: 'toolResult',
      toolCallId: 'call_reset_1',
      content: 'Scope reset. Objective: Task 2: modify\0\uFEFF[Remaining turnBudget: 5]',
      timestamp: 1040,
    },
    {
      role: 'assistant',
      content: [
        { type: 'text', text: 'editing...' },
        { type: 'toolCall', id: 'call_edit_1', name: 'edit', args: { path: 'file2.txt' } },
      ],
      timestamp: 1050,
    },
    {
      role: 'toolResult',
      toolCallId: 'call_edit_1',
      content: 'edit success',
      timestamp: 1060,
    },
  ]

  env.setTurnMode('keep')
  const contextHandler = env.eventHandlers.get('context')
  const result = await contextHandler({ messages: rawOmpMessages }, sessionCtx)
  assert.ok(result && result.messages, 'context handler returned modified messages')
  const kept = result.messages

  // Messages before reset (call_read_1 and its result) must be purged;
  // reset call/result and post-barrier edit call/result survive; cold missing budget adds a separate strong steer
  assert.equal(kept.length, 7, 'old assistant prose survives while ordinary tool calls/results are purged')

  assert.equal(kept[0].role, 'user')
  assert.equal(kept[0].attribution, 'user')
  assert.equal(kept[0].timestamp, 1000)

  assert.equal(kept[1].role, 'assistant')
  assert.equal(kept[1].content[0].text, 'reading...', 'old assistant prose must be retained')
  assert.equal(kept[2].role, 'assistant')
  assert.equal(kept[2].content[0].id, 'call_reset_1')

  assert.equal(kept[3].role, 'toolResult')
  assert.equal(kept[3].toolCallId, 'call_reset_1')
  assert.equal(kept[3].timestamp, 1040)

  assert.equal(kept[4].role, 'assistant')
  assert.equal(kept[4].content[1].id, 'call_edit_1')

  assert.equal(kept[5].role, 'toolResult')
  assert.equal(kept[5].toolCallId, 'call_edit_1')
  assert.equal(kept[5].timestamp, 1060)
  assert.equal(kept[5].content, 'edit success', 'no retired budget suffix may be added')
  assert.equal(kept[6].role, 'user')
  assert.equal(kept[6].attribution, 'agent')
  assert.equal(kept[6].strong, true)
  assert.match(contentText(kept[6].content),/remainingScopeTurnsEstimate/,'first charged report belongs to ScopeProgress')
  assert.equal(rawOmpMessages.at(-1).content, 'edit success', 'persisted result is not edited')

  clearSessionState(sid)
})

test('WHAT[context-compression-033] OMP context handler safely refuses lossy Replace to prevent tool-result corruption', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)

  const sid = 'ses_omp_test_035_4'
  clearSessionState(sid)
  const sessionCtx = {
    cwd: process.cwd(),
    sessionManager: {
      getSessionId: () => sid,
      getSessionDir: () => process.cwd(),
      getHeader: () => ({ parentSession: null }),
    },
  }
  await env.eventHandlers.get('session_start')({}, sessionCtx)

  const rawOmpMessages = [
    { role: 'user', content: [{ type: 'text', text: 'goal' }], timestamp: 100 },
    {
      role: 'assistant',
      content: [{ type: 'toolCall', id: 'call_res', name: 'resetScope', args: { newScopeObjective: 'g', newScopeTurnBudget: 3 } }],
    },
    { role: 'toolResult', toolCallId: 'call_res', content: 'Scope reset. Objective: g\0\uFEFF[Remaining turnBudget: 3]' },
    {
      role: 'assistant',
      content: [{ type: 'toolCall', id: 'call_work', name: 'read', args: {} }],
    },
    { role: 'toolResult', toolCallId: 'call_work', content: 'output' },
  ]

  env.setTurnMode('lossy-replace')
  const contextHandler = env.eventHandlers.get('context')
  const result = await contextHandler({ messages: rawOmpMessages }, sessionCtx)
  assert.ok(result && result.messages, 'context handler returned fallback messages')

  // The lossy edit (which dropped toolResult messages) must be refused,
  // and the raw post-scope messages must be returned intact.
  const guarded = result.messages
  assert.ok(guarded.some((m) => m.role === 'toolResult' && m.toolCallId === 'call_res'), 'resetScope result preserved')
  assert.ok(guarded.some((m) => m.role === 'toolResult' && m.toolCallId === 'call_work'), 'work toolResult preserved')

  clearSessionState(sid)
})

test('WHAT[context-compression-034] OMP side-request snapshots do not decrement/replace turnBudget, move prompt boundary, or receive weak steer', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)

  const sid = 'ses_omp_test_035_5'
  clearSessionState(sid)
  const sessionCtx = {
    cwd: process.cwd(),
    sessionManager: {
      getSessionId: () => sid,
      getSessionDir: () => process.cwd(),
      getHeader: () => ({ parentSession: null }),
    },
  }
  await env.eventHandlers.get('session_start')({}, sessionCtx)

  resetScopeState(sid, 5)
  assert.equal(turnBudget(sid), 5, 'turnBudget initialized to 5')
  assert.equal(isScopeInitialized(sid), true, 'scope is initialized')

  const sideSnapshot = [
    {
      role: 'user',
      content: [{ type: 'text', text: 'primary task' }],
      attribution: 'user',
      timestamp: 1000,
    },
    {
      role: 'assistant',
      content: [
        { type: 'toolCall', id: 'call_res', name: 'resetScope', args: { newScopeObjective: 'phase', newScopeTurnBudget: 5 } },
      ],
    },
    { role: 'toolResult', toolCallId: 'call_res', content: 'Scope reset. Objective: phase\0\uFEFF[Remaining turnBudget: 5]' },
    {
      role: 'user',
      content: [{ type: 'text', text: 'summarize conversation title' }],
      attribution: 'agent',
      steering: false,
      timestamp: 2000,
    },
  ]


  env.setTurnMode('keep')
  const contextHandler = env.eventHandlers.get('context')
  const result = await contextHandler({ messages: sideSnapshot }, sessionCtx)

  // Side request must NOT decrement or reset turnBudget (must stay 5)
  assert.equal(turnBudget(sid), 5, 'side request must NOT decrement or reset turnBudget')
  assert.equal(isScopeInitialized(sid), true, 'side request must NOT un-initialize scope')

  const sideMessages = result ? result.messages : sideSnapshot
  const lastMsg = sideMessages[sideMessages.length - 1]
  assert.equal(lastMsg.content[0].text, 'summarize conversation title', 'side prompt must NOT have user reminder appended')
  assert.equal(sideMessages.some((m) => m.steering === true), false, 'side request must NOT receive weak steer')

  clearSessionState(sid)
})


test('WHAT[prefix-stability-016] OMP context hook preserves the old reset result suffix when a later turn arrives', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)
  const sid = 'ses_omp_prefix_016'
  const ctx = await activateSession(env, sid)
  env.setTurnMode('keep')
  const context = env.eventHandlers.get('context')
  const operator = { role: 'user', timestamp: 1000, content: 'task' }
  await context({ messages: [operator] }, ctx)
  resetScopeState(sid, 6)

  const original = [
    operator,
    { role: 'assistant', content: [{ type: 'toolCall', id: 'r-016', name: 'resetScope', args: {} }] },
    { role: 'toolResult', toolCallId: 'r-016', content: 'Scope reset.' },
  ]
  const first = await context({ messages: original }, ctx)
  assert.ok(first?.messages)
  const originalSuffix = first.messages.find(x => x.toolCallId === 'r-016').content
  assert.equal(originalSuffix, 'Scope reset.')
  onTurnStart(sid)

  const later = [
    ...original,
    { role: 'assistant', content: [{ type: 'toolCall', id: 'read-016', name: 'read', args: {} }] },
    { role: 'toolResult', toolCallId: 'read-016', content: 'data' },
  ]
  const next = await context({ messages: later }, ctx)
  assert.ok(next?.messages)
  assert.equal(next.messages.find(x => x.toolCallId === 'r-016').content, originalSuffix,
    'an earlier tool result is part of the immutable provider prefix')
  assert.equal(next.messages.find(x => x.toolCallId === 'read-016').content,
    'data')
  assert.equal(later[2].content, 'Scope reset.', 'actual tool history is not mutated')
});

test('WHAT[delegation-034] OMP negotiate tool registration, schema, and role tool policy exposure', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)
  await activateSession(env, 'ses_omp_negotiate_reg')
  const negotiateDef = env.registeredTools.find((t) => t.name === 'negotiate')
  assert.ok(negotiateDef, 'negotiate tool registered on pi')
  assert.deepEqual(negotiateDef.parameters.required, ['questions'])

  const catalog = env.getObservedCatalog()
  assert.ok(catalog, 'Agents catalog port was bound')
  const rolesOutcome = await catalog.Roles()
  assert.ok(rolesOutcome.fields && rolesOutcome.fields[0], 'Roles() returned Ok')
  const effectiveRoles = rolesOutcome.fields[0]
  for (const roleSpec of effectiveRoles) {
    assert.equal(roleSpec.ToolPolicy.get('negotiate'), true, 'negotiate must be explicitly allowed for role')
  }
})

test('WHAT[delegation-034] OMP delegated assignment requires negotiate before first resetScope; premature reset is refused', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)
  const child = 'ses_omp_child_007'
  const parent = 'ses_omp_parent_007'
  const ctx = await activateSession(env, child)
  bindDelegation(child, parent)
  assert.equal(negotiationRequired(child), true, 'new delegated assignment requires negotiate first')

  // Calling resetScope tool prematurely returns refusal explanation
  const resetDef = env.registeredTools.find((t) => t.name === 'resetScope')
  assert.ok(resetDef)
  const prematureOutcome = await resetDef.execute(
    'call_premature_1',
    { newScopeObjective: 'too early', newScopeTurnBudget: 5 },
    null,
    null,
    ctx,
  )
  assert.ok(prematureOutcome && prematureOutcome.content)
  assert.match(prematureOutcome.content[0].text, /resetScope is unavailable until the first negotiate/)
  assert.equal(negotiationRequired(child), true, 'premature reset cannot unlock initial gate')
  assert.equal(isScopeInitialized(child), false, 'premature reset cannot initialize scope')
  clearSessionState(child)
})

test('WHAT[delegation-034] Root session negotiate masking in before_provider_request and hard refusal in tool_call', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)
  const rootSession = 'ses_omp_root_008'
  const rootCtx = await activateSession(env, rootSession)

  // Root session before_provider_request filters 'negotiate' from payload tools
  const beforeProvider = env.eventHandlers.get('before_provider_request')
  assert.ok(beforeProvider, 'before_provider_request hook registered')
  const providerPayload = {
    model: 'test-model',
    tools: [{ name: 'read' }, { name: 'negotiate' }, { name: 'write' }],
    messages: [{ role: 'user', content: 'test' }],
  }
  const filteredPayload = await beforeProvider({ payload: providerPayload }, rootCtx)
  assert.ok(filteredPayload, 'returns modified payload')
  assert.ok(Array.isArray(filteredPayload.tools), 'payload has tools array')
  assert.equal(filteredPayload.tools.some((t) => t.name === 'negotiate'), false, 'negotiate tool is filtered out for root session')
  assert.equal(filteredPayload.length ?? filteredPayload.tools.length, 2, 'other tools preserved')
  assert.equal(filteredPayload.model, 'test-model', 'other payload fields preserved')

  // Root session tool_call hook hard-blocks negotiate
  const toolCallHook = env.eventHandlers.get('tool_call')
  const blockedVerdict = await toolCallHook({ toolCallId: 'call_root_neg', toolName: 'negotiate', input: {} }, rootCtx)
  assert.ok(blockedVerdict && blockedVerdict.block === true)
  assert.match(blockedVerdict.reason, /negotiate is not permitted on root/)
  clearSessionState(rootSession)
})

test('WHAT[delegation-034] End-to-end negotiate: immediate ack before answer, native ephemeral turn, prompt correlation, event landing unlocks gate', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)

  const parentId = 'ses_omp_parent_e2e'
  const childId = 'ses_omp_child_e2e'

  // Setup mock delegator context with runEphemeralTurn
  let ephemeralTurnCalls = []
  const parentCtx = {
    cwd: process.cwd(),
    sessionManager: {
      getSessionId: () => parentId,
      getSessionDir: () => process.cwd(),
      getHeader: () => ({ parentSession: null }),
    },
    runEphemeralTurn: async (opts) => {
      ephemeralTurnCalls.push(opts)
      return {
        replyText: 'Use the fast JSON parser with zero copy.',
        assistantMessage: { role: 'assistant', content: [{ type: 'text', text: 'Use the fast JSON parser with zero copy.' }] },
  }
    },
  }
  const sessionStart = env.eventHandlers.get('session_start')
  await sessionStart({}, parentCtx)

  // Setup mock recipient child session
  let steerCalls = []
  let promptCalls = []
  let listeners = []
  const childSession = {
    agent: { isStreaming: true },
    steer: async (text) => {
      steerCalls.push(text)
      // Simulate asynchronous delivery landing via message_end event with content-array
      setTimeout(() => {
        for (const l of listeners) {
          l({
            type: 'message_end',
            message: {
              role: 'user',
              content: [{ type: 'text', text: text }],
            },
})
  }
      }, 10)
    },
    prompt: async (text) => {
      promptCalls.push(text)
      return true
    },
    subscribe: (fn) => {
      listeners.push(fn)
      return () => {
        const idx = listeners.indexOf(fn)
        if (idx >= 0) listeners.splice(idx, 1)
  }
    },
  }

  // Bind delegation
  bindDelegation(childId, parentId)
  assert.equal(negotiationRequired(childId), true, 'first-reset gate locked')

  // Setup mock transport injection into product
  const transport = {
    CanNegotiate: (did) => did === parentId,
    AskEphemeral: async (req) => {
      const res = await parentCtx.runEphemeralTurn({
        promptText: req.PromptText,
        conversationKey: 'negotiate:' + req.Receipt,
})
      return { tag: 0, fields: [res.replyText] }
    },
    DeliverAndObserve: async (req) => {
      // Deliver steer
      await childSession.steer(req.SteerText + '\n[receipt:' + req.Receipt + ']')
      // Wait for landing listener
      return new Promise((resolve) => {
        const unsub = childSession.subscribe((event) => {
          if (event.type === 'message_end' && event.message?.role === 'user') {
            unsub()
            resolve({ tag: 0, fields: [] })
  }
        })
      })
    },
  }

  // Attach transport to plugin
  env.mockPlugin.AttachTransport?.(transport)

  // Execute negotiate tool
  const negotiateDef = env.registeredTools.find((t) => t.name === 'negotiate')
  assert.ok(negotiateDef, 'negotiate tool registered')
    const childCtx = ompSession(() => childId, { parentSession: parentId })

  const immediateAck = await negotiateDef.execute(
    'call_neg_1',
    { questions: 'Which JSON parser should I use?' },
    null,
    null,
    childCtx,
  )

  // 1. Immediate acknowledgment before answer is delivered
  assert.ok(immediateAck && immediateAck.content, 'returns immediate outcome')
  assert.match(immediateAck.content[0].text, /Clarification requested \(receipt/)
  assert.match(immediateAck.content[0].text, /Negotiation is complete/)
  assert.equal(negotiationRequired(childId), false, 'accepted negotiate unlocks immediately upon tool return')

  // 2. Ephemeral turn ran on delegator
  assert.equal(ephemeralTurnCalls.length, 1, 'ephemeral turn was called on delegator')
  assert.match(ephemeralTurnCalls[0].promptText, /Which JSON parser should I use\?/)

  // 3. Steer delivered to recipient child
  await new Promise((r) => setTimeout(r, 50))
  assert.equal(steerCalls.length, 1, 'steer was sent to child')
  assert.match(steerCalls[0], /Use the fast JSON parser with zero copy/)
  assert.equal(negotiationRequired(childId), false, 'gate stays released after the steer lands')

  // 4. resetScope is now admitted
  resetScopeState(childId, 3)
  assert.equal(isScopeInitialized(childId), true, 'resetScope state initialized after negotiation gate released')

  clearSessionState(childId)
  clearSessionState(parentId)
})

test('WHAT[delegation-034] Accepted negotiate releases gate; non-matching steer cannot settle the receipt and subsequent negotiate does not re-lock', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)
  const childId = 'ses_omp_child_stale'
  const parentId = 'ses_omp_parent_stale'
  const ctx = await activateSession(env, childId)

  bindDelegation(childId, parentId)
  assert.equal(negotiationRequired(childId), true)

  const ask = askNegotiation(childId, 'Should I add telemetry?')
  assert.equal(ask.ok, true)
  const receipt = ask.receipt

  assert.equal(prepareNegotiationReply(receipt, 'No telemetry').ok, true)

  // Acceptance already released the gate; a non-matching recipient still cannot settle the reply receipt
  assert.equal(negotiationRequired(childId), false, 'accepted negotiate releases the gate')
  assert.equal(confirmNegotiationSteer(receipt, 'unrelated_child'), false)

  // Matching confirmation releases gate
  assert.equal(confirmNegotiationSteer(receipt, childId), true)
  assert.equal(negotiationRequired(childId), false, 'first-reset gate released')

  // resetScope now succeeds
  const resetDef = env.registeredTools.find((t) => t.name === 'resetScope')
  resetScopeState(childId, 4)
  assert.equal(turnBudget(childId), 4)
  assert.equal(isScopeInitialized(childId), true)

  // Subsequent optional negotiate does not re-lock
  const secondAsk = askNegotiation(childId, 'Follow-up detail')
  assert.equal(secondAsk.ok, true)
  assert.equal(negotiationRequired(childId), false, 'subsequent negotiate does not re-lock gate')


  clearSessionState(childId)
})

test('WHAT[context-compression-048] OMP registers askPreviousScope and refuses to fake an old Scope answer without durable prior evidence', async () => {
  const env = createOmpTestEnvironment()
  await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)
  const sid = 'omp-prior-048'
  const ctx = await activateSession(env, sid)
  const tool = env.registeredTools.find(item => item.name === 'askPreviousScope')
  assert.ok(tool, 'the mandatory first tool must exist in OMP as well as OpenCode')
  assert.deepEqual(tool.parameters.required, ['questions'])
  assert.equal(tool.parameters.properties.questions.type, 'string')
  const catalog = await env.getObservedCatalog().Roles()
  for (const agent of catalog.fields[0]) {
    assert.equal(agent.ToolPolicy.get('askPreviousScope'), true,
      'every role that receives the pending reminder must be able to call the corresponding tool')
  }
  const output = await tool.execute('exact-omp-call', { questions: 'Why was parser B rejected?' }, null, null, ctx)
  const prose = contentText(output.content)
  assert.match(prose, /NoPreviousScope|durable journal|previous Scope/i)
  assert.equal(output.details.answerDelivered, false,
    'never invent a provider reply without an archived old request')
  clearSessionState(sid)
})

test('WHAT[context-compression-049] OMP real native provider hook freezes old catalog then replays it for the readonly previous Scope side turn', async () => {
  const { mkdtempSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const { execFileSync } = await import('node:child_process')
  const workspace = mkdtempSync(join(tmpdir(), 'omp-prior-wire-'))
  execFileSync('git', ['init', '-q', workspace])
  try {
    const env = createOmpTestEnvironment()
    const sid = 'omp-prior-physical-049'
    let liveCtx
    const model = { provider: 'fixture', id: 'old-model' }
    const steers = []
    env.mockPi.sendUserMessage = async (text, opts) => {
      steers.push({text, opts})
      await env.eventHandlers.get('message_start')({
        message: {role:'user',attribution:opts.attribution,
          content:[{type:'text',text}]},
      }, liveCtx)
    }
    const providerHook = () => env.eventHandlers.get('before_provider_request')
    const rawOld = {
      model:'old-model',
      messages:[
        {role:'system',content:'OLD_SYSTEM_KEEP_BYTES'},
        {role:'user',content:'Old original observation: precise sentinel 314159'},
      ],
      tools:[{name:'read',description:'Old read'}, {name:'write',description:'Old write'}],
      tool_choice:'auto',
    }
    let sideRuns = 0
    liveCtx = {
      cwd:workspace, model,
      sessionManager:{
        getSessionId:()=>sid,
        getSessionDir:()=>workspace,
        getHeader:()=>({parentSession:null}),
      },
      runEphemeralTurn: async opts => {
        sideRuns++
        const request = {
          model:'old-model',
          messages:[
            {role:'system',content:'NEW_WRONG_SYSTEM'},
            {role:'user',content:'NEW_WRONG_HISTORY'},
            {role:'user',content:opts.promptText},
          ],
          tools:[],
          tool_choice:'none',
        }
        const native = await providerHook()({payload:request},liveCtx)
        assert.ok(native,'the real before_provider_request physically substitutes old provider payload')
        assert.equal(native.messages[0].content,'OLD_SYSTEM_KEEP_BYTES')
        assert.equal(native.messages[1].content,'Old original observation: precise sentinel 314159')
        assert.deepEqual(native.tools,rawOld.tools,'the exact old tool catalog is retained')
        assert.equal(native.tool_choice,'auto')
        assert.doesNotMatch(native.messages.at(-1).content,/wanxiangshu-prior:/,'the internal marker must never be shown to the model')
        assert.match(native.messages.at(-1).content,/What precise evidence/)
        assert.match(native.messages.at(-1).content,/严禁调用任何工具|strictly forbidden/,
          'the snapshot dialogue carries the absolute tool prohibition')
        assert.match(native.messages.at(-1).content,/正文|body text/)
        assert.equal(native.messages.some(x=>JSON.stringify(x).includes('PRIVATE_CURRENT_SCOPE_112358')),false,'only previous Scope messages may be projected')
        assert.equal(native.messages.some(x=>x.content==='NEW_WRONG_HISTORY'),false)
        return {replyText:'Historical observation 314159 rejects the new parser.',
          assistantMessage:{role:'assistant',content:[{type:'text',text:'Historical observation 314159 rejects the new parser.'}]}}
      },
    }
    await activateOnOmp(env.mockPi,env.mockPlugin,env.agentsList)
    await env.eventHandlers.get('session_start')({},liveCtx)
    const captured = await providerHook()({payload:structuredClone(rawOld)},liveCtx)
    assert.equal(captured.model,'old-model')
    const reset = env.registeredTools.find(x=>x.name==='resetScope')
    const ask = env.registeredTools.find(x=>x.name==='askPreviousScope')
    assert.ok(reset && ask)
    const first = await reset.execute('reset-old-049',
      {newScopeObjective:'validate new parser',newScopeTurnBudget:10,
       kpiCompletion:'met',oldScopeVerboseVerbatimExperience:'Old sentinel 314159'},null,null,liveCtx)
    assert.equal(first.details?.archived,true,JSON.stringify(first))
    assert.equal(isPreviousScopePending(sid),false,'first ever reset has no mandatory predecessor consultation')
    const staleReset = await reset.execute('without-new-provider-evidence-049',
      {newScopeObjective:'this must not use the old sealed request',newScopeTurnBudget:7,kpiCompletion:'met'},
      null,null,liveCtx)
    assert.equal(staleReset.details?.archived,false,
      'a second reset cannot reuse a preceding epoch provider snapshot after it was consumed')

    const newScopeProvider={...structuredClone(rawOld),messages:[
      ...structuredClone(rawOld.messages),
      {role:'assistant',content:[{type:'toolCall',id:'reset-old-049',name:'resetScope',args:{newScopeObjective:'validate new parser'}}]},
      {role:'user',content:'PRIVATE_CURRENT_SCOPE_112358'},
    ]}
    await providerHook()({payload:newScopeProvider},liveCtx)

    const [accepted, parallelDuplicate] = await Promise.all([
      ask.execute('prior-049',
        {questions:'What precise evidence rejected the new parser?'},null,null,liveCtx),
      ask.execute('prior-049',
        {questions:'What precise evidence rejected the new parser?'},null,null,liveCtx),
    ])
    assert.equal([accepted,parallelDuplicate].filter(x =>
      /readonly old-model turn was scheduled/.test(x.content?.[0]?.text??'')).length,1,
      'simultaneous duplicated model toolcall may schedule only one paid Provider side turn')
    for(let i=0;i<60 && steers.length===0;i++)await new Promise(r=>setTimeout(r,10))
    assert.equal(sideRuns,1)
    assert.equal(steers.length,1,'a single actual agent-attributed old-Scope steer is sent')
    assert.equal(steers[0].opts.attribution,'agent','relay cannot masquerade as operator')
    assert.match(steers[0].text,/314159 rejects/)
    const replay=await ask.execute('prior-049',
      {questions:'What precise evidence rejected the new parser?'},null,null,liveCtx)
    assert.match(replay.content[0].text,/already accepted/)
    assert.equal(sideRuns,1,'replayed durable receipt cannot cause a second model call')
    liveCtx.runEphemeralTurn = async opts => {
      sideRuns++
      const wrong = {...liveCtx,model:{provider:'some-other-provider',id:'old-model'}}
      const invalid = {model:'old-model',messages:[{role:'user',content:opts.promptText}],tools:[]}
      await assert.rejects(providerHook()({payload:invalid},wrong),/provider\/model\/catalog does not match/)
      return {replyText:'UNTRUSTED_WRONG_MODEL_ANSWER',
        assistantMessage:{role:'assistant',content:[{type:'text',text:'UNTRUSTED_WRONG_MODEL_ANSWER'}]}}
    }
    const mismatched = await ask.execute('prior-mismatched-model-049',
      {questions:'What did the wrong provider see?'},null,null,liveCtx)
    assert.match(mismatched.content[0].text,/readonly old-model turn was scheduled/)
    for(let i=0;i<60 && sideRuns<2;i++)await new Promise(r=>setTimeout(r,10))
    await new Promise(r=>setTimeout(r,10))
    assert.equal(sideRuns,2)
    assert.equal(steers.length,1,'a model mismatch must never generate a previous-Scope steer')

    // A real old-model reply must not enter a later epoch after a second committed reset.
    let releaseOldAnswer
    liveCtx.runEphemeralTurn = async opts => {
      sideRuns++
      const native = await providerHook()({payload:{
        model:'old-model',
        messages:[{role:'system',content:'DURING_CURRENT_SCOPE'},
          {role:'user',content:opts.promptText}],
        tools:[{name:'edit'}],
      }},liveCtx)
      assert.deepEqual(native.tools,rawOld.tools)
      return new Promise(resolve => {
        releaseOldAnswer = () => resolve({
          replyText:'LATE_PREVIOUS_SCOPE_ANSWER_MUST_NOT_LAND',
          assistantMessage:{role:'assistant',content:[
            {type:'text',text:'LATE_PREVIOUS_SCOPE_ANSWER_MUST_NOT_LAND'}]},
        })
      })
    }
    const late = await ask.execute('prior-late-049',
      {questions:'Which past exception may be useful?'},null,null,liveCtx)
    assert.match(late.content[0].text,/readonly old-model turn was scheduled/)
    for(let i=0;i<60 && !releaseOldAnswer;i++)await new Promise(r=>setTimeout(r,10))
    assert.equal(typeof releaseOldAnswer,'function','the physical old provider turn must actually begin')
    const currentProvider={
      model:'old-model',
      messages:[{role:'system',content:'NEW_EPOCH_PROVIDER_SYSTEM'},
        {role:'user',content:'NEW_EPOCH_OPERATOR_REQUEST'}],
      tools:[{name:'read',description:'New read'}],
    }
    await providerHook()({payload:currentProvider},liveCtx)
    const newReset=await reset.execute('new-epoch-reset-049',
      {newScopeObjective:'start the next real epoch',newScopeTurnBudget:4,kpiCompletion:'met'},
      null,null,liveCtx)
    assert.equal(newReset.details?.archived,true,JSON.stringify(newReset))
    assert.equal(isPreviousScopePending(sid),true,'second reset establishes a real previous-Scope mandatory question')
    releaseOldAnswer()
    await new Promise(resolve=>setTimeout(resolve,25))
    assert.equal(steers.length,1,'a late answer from a retired target epoch must not be delivered')

    // A third consultation requires an actual NEW provider transform after the
    // second reset, not a stale copy silently retained by the old epoch.
    const afterSecondReset = { ...structuredClone(currentProvider), messages: [
      ...structuredClone(currentProvider.messages),
      {role:'assistant',content:[{type:'toolCall',id:'new-epoch-reset-049',name:'resetScope',args:{newScopeObjective:'start the next real epoch'}}]},
      {role:'user',content:'THIRD_SCOPE_PRIVATE_049'},
    ]}
    await providerHook()({payload:afterSecondReset},liveCtx)

    let releaseAfterShutdown
    liveCtx.runEphemeralTurn = async opts => {
      sideRuns++
      const native = await providerHook()({payload:{
        model:'old-model',
        messages:[{role:'user',content:opts.promptText}],
        tools:[],
      }},liveCtx)
      assert.equal(native.messages[0].content,'NEW_EPOCH_PROVIDER_SYSTEM')
      return new Promise(resolve => {
        releaseAfterShutdown = () => resolve({
          replyText:'LATE_AFTER_SHUTDOWN_MUST_NOT_LAND',
          assistantMessage:{role:'assistant',
            content:[{type:'text',text:'LATE_AFTER_SHUTDOWN_MUST_NOT_LAND'}]},
        })
      })
    }
    const shutdownAsk=await ask.execute('prior-shutdown-049',
      {questions:'Which criteria were still unfinished?'},null,null,liveCtx)
    assert.match(shutdownAsk.content[0].text,/readonly old-model turn was scheduled/)
    for(let i=0;i<60 && !releaseAfterShutdown;i++)await new Promise(r=>setTimeout(r,10))
    assert.equal(typeof releaseAfterShutdown,'function')
    await env.eventHandlers.get('session_shutdown')({},liveCtx)
    releaseAfterShutdown()
    await new Promise(resolve=>setTimeout(resolve,25))
    assert.equal(steers.length,1,'a closed native session must not receive the old answer')



  } finally {
    rmSync(workspace,{recursive:true,force:true})
  }
})


test('WHAT[context-compression-043] OMP resetScope refusal on missing snapshot, nativePayload archival via before_provider_request, single-drain, and session_shutdown cleanup', async () => {
  const { mkdtempSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const { execFileSync } = await import('node:child_process')
  const workspace = mkdtempSync(join(tmpdir(), 'omp-043-wire-'))
  execFileSync('git', ['init', '-q', workspace])

  let journalHandle = null
  try {
    const env = createOmpTestEnvironment()
    const sid = 'omp-sess-043-main'
    clearSessionState(sid)
    outboundViewStore.clearSession(sid)

    const model = { provider: 'anthropic', id: 'claude-3-5-sonnet' }
    const liveCtx = {
      cwd: workspace,
      model,
      sessionManager: {
        getSessionId: () => sid,
        getSessionDir: () => workspace,
        getHeader: () => ({ parentSession: null }),
      },
    }

    // Activate OMP adapter and boot session
    await activateOnOmp(env.mockPi, env.mockPlugin, env.agentsList)
    await env.eventHandlers.get('session_start')({}, liveCtx)

    const reset = env.registeredTools.find((x) => x.name === 'resetScope')
    assert.ok(reset, 'resetScope tool must be registered on OMP')

    // Acquire shared journal for physical projection & blob verification
    const journalAcquire = await acquireSharedForWorkspace(workspace, process.pid, new Date().toISOString())
    assert.ok(journalAcquire?.ok, 'acquireSharedForWorkspace must succeed')
    journalHandle = journalAcquire.journal

    // -------------------------------------------------------------------------
    // 1. 缺快照明确拒绝且不产生 epoch / 预算更新 (Fail-closed invariant)
    // -------------------------------------------------------------------------
    assert.equal(isScopeInitialized(sid), false, 'precondition: scope must not be initialized')
    assert.equal(turnBudget(sid), 0, 'precondition: budget must be 0')
    assert.equal(outboundViewStore.tryGetSealed(sid), undefined, 'precondition: no sealed snapshot')

    const premature = await reset.execute(
      'call-omp-unarchived-043',
      { newScopeObjective: 'unarchived objective' },
      null,
      null,
      liveCtx,
    )

    assert.ok(premature && premature.details, 'outcome must include details')
    assert.equal(premature.details.refused, true, 'missing snapshot must refuse resetScope')
    assert.equal(premature.details.archived, false, 'must not be archived')
    assert.match(
      premature.content[0].text,
      /resetScope refused: the preceding real provider request has no complete archived system\/messages\/model observation/,
      'must return exact typed refusal text without silent degradation',
    )

    // Memory invariants: untouched
    assert.equal(isScopeInitialized(sid), false, 'scope must not become initialized on refusal')
    assert.equal(turnBudget(sid), 0, 'budget must not be updated on refusal')

    // Journal projection: no PrefixEpoch committed
    const snap1 = AgentJournalModule_snapshot(JournalHandle__get_Journal(journalHandle))
    const proj1 = AgentProjection_tryFind(SessionIdModule_create(sid), snap1.AgentProjections)
    assert.equal(proj1?.PrefixEpoch, undefined, 'journal must have no prefix epoch committed on refusal')

    // -------------------------------------------------------------------------
    // 2. 真实 before_provider_request 封存 nativePayload (toolsCaptured = true)
    // -------------------------------------------------------------------------
    const providerHook = env.eventHandlers.get('before_provider_request')
    assert.ok(providerHook, 'before_provider_request hook must be registered')

    const nativePayload = {
      messages: [
        { role: 'system', content: 'SYSTEM_OMP_OBSERVED_043' },
        { role: 'user', content: 'USER_OMP_PROMPT_SENTINEL_043' },
      ],
      tools: [
        { name: 'read', description: 'Read file tool' },
        { name: 'write', description: 'Write file tool' },
      ],
      system: ['SYSTEM_OMP_OBSERVED_043'],
    }

    await providerHook({ payload: structuredClone(nativePayload) }, liveCtx)

    // Sealed snapshot check in outboundViewStore
    const sealed = outboundViewStore.tryGetSealed(sid)
    assert.ok(sealed, 'before_provider_request must seal an outbound snapshot in outboundViewStore')
    assert.equal(sealed.sessionId, sid)
    assert.equal(sealed.toolsCaptured, true, 'native payload capture must set toolsCaptured=true')
    assert.ok(sealed.nativePayload, 'nativePayload must be preserved in sealed snapshot')
    assert.equal(sealed.nativePayload.tools.length, 2)
    assert.equal(sealed.nativePayload.tools[0].name, 'read')
    assert.equal(sealed.nativePayload.tools[1].name, 'write')
    assert.deepEqual(sealed.system, ['SYSTEM_OMP_OBSERVED_043'])

    // -------------------------------------------------------------------------
    // 3. 经实际注册 resetScope 工具成功归档、单次排空 (Single-Drain)
    // -------------------------------------------------------------------------
    const committed = await reset.execute(
      'call-omp-archive-success-043',
      {
        newScopeObjective: 'first verified omp scope',
        oldScopeVerboseVerbatimExperience: 'Old experience verbatim sentinel 043',
      },
      null,
      null,
      liveCtx,
    )

    assert.ok(committed && committed.details)
    assert.equal(committed.details.archived, true, 'resetScope must report archived: true on success')
    assert.equal(committed.details.objective, 'first verified omp scope')
    assert.equal(committed.details.turnBudget, undefined, 'resetScope no longer reports a turnBudget')
    assert.match(committed.content[0].text, /Scope reset. Objective: first verified omp scope/)

    // Memory invariants: updated
    assert.equal(isScopeInitialized(sid), true, 'memory scope must be initialized')
    assert.equal(turnBudget(sid), 0, 'retired countdown is no longer initialized by resetScope')

    // Single-drain invariant: sealed snapshot must have been discarded from store
    assert.equal(
      outboundViewStore.tryGetSealed(sid),
      undefined,
      'outboundViewStore.discardSealed must have cleared sealed snapshot',
    )

    // -------------------------------------------------------------------------
    // 4. 通过 Journal 真实投影 / Blob 读取断言内容
    // -------------------------------------------------------------------------
    const snap2 = AgentJournalModule_snapshot(JournalHandle__get_Journal(journalHandle))
    const proj2 = AgentProjection_tryFind(SessionIdModule_create(sid), snap2.AgentProjections)
    assert.ok(proj2?.PrefixEpoch, 'PrefixEpoch must exist in projection')
    const epoch = proj2.PrefixEpoch
    assert.equal(epoch.EpochId?.fields?.[0] ?? epoch.EpochId, 1n, 'epoch id advanced to 1n')
    assert.ok(epoch.LastScopeArchive, 'LastScopeArchive must exist')
    const archive = epoch.LastScopeArchive
    assert.equal(archive.TargetEpochId?.fields?.[0] ?? archive.TargetEpochId, 1n)
    assert.ok(archive.ArchiveRef, 'ArchiveRef must exist')

    // Read blob content directly from journal writer BlobWriter
    const blobRes = await JournalHandle__get_Journal(journalHandle).writer.BlobWriter.Read(archive.ArchiveRef)
    assert.equal(blobRes.tag, 0, 'BlobWriter.Read must succeed (tag 0 = Ok)')
    const blobJson = blobRes.fields[0]
    assert.ok(typeof blobJson === 'string' && blobJson.length > 0)
    const parsedBlob = JSON.parse(blobJson)
    assert.equal(parsedBlob.sessionId, sid)
    assert.equal(parsedBlob.toolsCaptured, true)
    assert.ok(parsedBlob.nativePayload)
    assert.equal(parsedBlob.nativePayload.tools[0].name, 'read')
    assert.equal(parsedBlob.nativePayload.messages[1].content, 'USER_OMP_PROMPT_SENTINEL_043')

    // -------------------------------------------------------------------------
    // 5. 验证新 callID 无新观测拒绝 (无法复用已归档快照)
    // -------------------------------------------------------------------------
    const secondWithoutObservation = await reset.execute(
      'call-omp-second-premature-043',
      { newScopeObjective: 'second premature' },
      null,
      null,
      liveCtx,
    )

    assert.ok(secondWithoutObservation && secondWithoutObservation.details)
    assert.equal(secondWithoutObservation.details.refused, true, 'must be refused without new observation')
    assert.equal(secondWithoutObservation.details.archived, false)
    assert.match(
      secondWithoutObservation.content[0].text,
      /resetScope refused: the preceding real provider request has no complete archived system\/messages\/model observation/,
    )
    assert.equal(turnBudget(sid), 0, 'refusal cannot manufacture a retired budget')

    // -------------------------------------------------------------------------
    // 6. 按真实契约区分幂等重放 (相同 callID 重放返回成功，不推进 epoch)
    // -------------------------------------------------------------------------
    const replay = await reset.execute(
      'call-omp-archive-success-043',
      { newScopeObjective: 'first verified omp scope' },
      null,
      null,
      liveCtx,
    )
    assert.ok(replay)
    assert.match(replay.content[0].text, /Scope reset. Objective: first verified omp scope/)
    const snap3 = AgentJournalModule_snapshot(JournalHandle__get_Journal(journalHandle))
    const proj3 = AgentProjection_tryFind(SessionIdModule_create(sid), snap3.AgentProjections)
    assert.equal(proj3?.PrefixEpoch?.EpochId?.fields?.[0] ?? proj3?.PrefixEpoch?.EpochId, 1n, 'idempotent replay must not advance epoch')

    // -------------------------------------------------------------------------
    // 7. session_shutdown 清理隔离
    // -------------------------------------------------------------------------
    const sid2 = 'omp-sess-043-other'
    const liveCtx2 = {
      cwd: workspace,
      model,
      sessionManager: {
        getSessionId: () => sid2,
        getSessionDir: () => workspace,
        getHeader: () => ({ parentSession: null }),
      },
    }
    const beforeStart = env.eventHandlers.get('before_agent_start')
    assert.ok(beforeStart, 'before_agent_start handler must be registered')
    await beforeStart({ systemPrompt: ['SYSTEM_2'] }, liveCtx2)
    await providerHook({ payload: structuredClone(nativePayload) }, liveCtx2)
    assert.ok(outboundViewStore.tryGetSealed(sid2), 'sid2 has sealed snapshot before shutdown')

    // Trigger session_shutdown
    const shutdownHandler = env.eventHandlers.get('session_shutdown')
    assert.ok(shutdownHandler, 'session_shutdown handler must be registered')
    await shutdownHandler({}, liveCtx)

    // Verify session_shutdown cleared all sessions from outboundViewStore
    assert.equal(outboundViewStore.tryGetSealed(sid), undefined, 'sid must be purged by session_shutdown')
    assert.equal(outboundViewStore.tryGetSealed(sid2), undefined, 'sid2 must be purged by session_shutdown')
  } finally {
    if (journalHandle && typeof journalHandle.Dispose === 'function') {
      try {
        journalHandle.Dispose()
      } catch {}
    }
    rmSync(workspace, { recursive: true, force: true })
  }
})


test('WHAT[scope-progress-016] OMP native todowrite settles min and triggers four Advisor turns', async () => {
  const {mkdtempSync,rmSync}=await import('node:fs')
  const {tmpdir}=await import('node:os')
  const {join}=await import('node:path')
  const {execFileSync}=await import('node:child_process')
  const {SqliteJournal}=await import('wanxiangli/eventstore')
  const {DatabaseSync}=await import('node:sqlite')
  const {stateRoot}=await import('../../../dist/Persistence/Journal/RuntimePath.js')
  const {scopeProgress,advisorProcessQueue}=await import('wanxiangli/scope-progress')
  const workspace=mkdtempSync(join(tmpdir(),'wxs-omp-advisor-'))
  execFileSync('git',['init','-q',workspace])
  const sid='ses_omp_scope_native_016'
  try {
    const env=createOmpTestEnvironment()
    const asks=[],steers=[]
    const ctx={...ompSession(()=>sid),cwd:workspace,
      model:{provider:'fixture',id:'native'},
      runEphemeralTurn:async args=>{
        asks.push(args)
        const reply='The work remaining is a verified backlog.'
        return {replyText:reply,assistantMessage:{role:'assistant',content:[{type:'text',text:reply}]}}
      },
    }
    env.mockPi.sendUserMessage=async (text,options)=>{
      steers.push({text,options})
      await env.eventHandlers.get('message_start')({type:'message_start',message:{
        role:'user',attribution:'agent',content:[{type:'text',text}],
      }},ctx)
    }
    await activateOnOmp(env.mockPi,env.mockPlugin,env.agentsList)
    await env.eventHandlers.get('session_start')({},ctx)
    const todo=env.registeredTools.find(x=>x.name==='todowrite')
    assert.ok(todo)
    assert.equal(todo.parameters.properties.remainingScopeTurnsEstimate.type,'integer')
    const estimateHelp=[readFileSync(new URL('../../../resources/provider/tool/js-program/arg-estimate/en.md',import.meta.url),'utf8').trim(),
      readFileSync(new URL('../../../resources/provider/tool/js-program/arg-estimate/zh-CN.md',import.meta.url),'utf8').trim()]
    assert.ok(estimateHelp.includes(todo.description),'OMP todowrite carries the contract text, not its own wording')
    assert.equal(todo.parameters.properties.remainingScopeTurnsEstimate.description,todo.description)
    const record=(id,n)=>todo.execute(id,{remainingScopeTurnsEstimate:n},null,null,ctx)
    const begin=i=>env.eventHandlers.get('turn_start')({type:'turn_start',turnIndex:i},ctx)
    const end=i=>env.eventHandlers.get('turn_end')({type:'turn_end',turnIndex:i,
      message:{role:'assistant',stopReason:'stop',content:[]},toolResults:[]},ctx)
    await begin(0)
    assert.equal((await record('first',7)).details.estimate,7)
    assert.equal((await record('second',3)).details.minimum,3)
    await end(0)
    assert.equal(scopeProgress.status(sid).estimate,3)
    await begin(1)
    assert.equal((await record('third',3)).details.estimate,3)
    await end(1)
    for(let n=0;n<90&&steers.length===0;n++)await new Promise(resolve=>setTimeout(resolve,15))
    assert.equal(asks.length,4)
    assert.match(JSON.stringify(asks[0]),/工作进展受到了什么阻碍|What has held back the progress/,
      'an unchanged estimate asks the stalled-progress question from the shared resource')
    assert.deepEqual(asks.map(x=>x.tools),[undefined,false,undefined,false],
      'the worker retains its provider tools while the dedicated Advisor has none')
    assert.equal(steers.length,1)
    assert.equal(steers[0].options.attribution,'agent')
    assert.doesNotMatch(steers[0].text,/advisor:|StageBegan|StageAnswered|AdvisorFlow/,
      'the working model must not receive internal Advisor process labels')
    const root=await stateRoot(workspace)
    const journal=new SqliteJournal(new DatabaseSync(join(root,'wanxiangshu','advisor-process.sqlite')))
    try {
      const progress=journal.read('scope-progress:'+sid.length+':'+sid+':1:0').map(JSON.parse)
      assert.deepEqual(progress.filter(x=>x.kind==='Settled').map(x=>x.estimate),[3,3])
      assert.equal(progress.filter(x=>x.kind==='Recorded').length,3)
      let facts=[]
      for(let n=0;n<100;n++){
        facts=journal.read('advisor-process:'+sid).map(JSON.parse)
        if(facts.some(x=>x.kind==='Finished'))break
        await new Promise(resolve=>setTimeout(resolve,10))
      }
      assert.deepEqual(facts.filter(x=>x.kind==='StageBegan').map(x=>x.phase),[1,2,3,4,5])
      assert.equal(facts.filter(x=>x.kind==='StageAnswered').length,5)
      assert.equal(facts.find(x=>x.kind==='Finished')?.outcome,'Completed')
      await begin(2)
      assert.equal((await record('zero',0)).details.estimate,0)
      assert.equal((await record('zero',1)).details.refused,true,'same tool identity may not revise the original estimate')
      assert.equal((await record('negative',-1)).details.refused,true,'negative estimates are never billed')
      await end(2)
      assert.equal(scopeProgress.status(sid).resetDue,true)
      const finalProgress=journal.read('scope-progress:'+sid.length+':'+sid+':1:0').map(JSON.parse)
      assert.deepEqual(finalProgress.filter(x=>x.kind==='Settled').map(x=>x.estimate),[3,3,0])
      await new Promise(resolve=>setTimeout(resolve,20))
      assert.equal(asks.length,4,'a settled AdvisorDue replay cannot cause a second paid consultation')
      assert.equal(steers.length,1)
    }finally{journal.close()}
  }finally{
    scopeProgress.clear(sid)
    advisorProcessQueue.clear(sid)
    clearSessionState(sid)
    rmSync(workspace,{recursive:true,force:true})
  }
})


test('WHAT[scope-progress-017] OMP cold restart settles an already accepted but unfinished native Provider turn', async()=>{
  const {mkdtempSync,rmSync}=await import('node:fs')
  const {tmpdir}=await import('node:os')
  const {join}=await import('node:path')
  const {execFileSync}=await import('node:child_process')
  const {scopeProgress}=await import('wanxiangli/scope-progress')
  const work=mkdtempSync(join(tmpdir(),'omp-scope-cold-'))
  execFileSync('git',['init','-q',work])
  const sid='ses_omp_cold_restart_017'
  const start=async()=>{
    const env=createOmpTestEnvironment()
    await activateOnOmp(env.mockPi,env.mockPlugin,env.agentsList)
    const ctx={...ompSession(()=>sid),cwd:work}
    await env.eventHandlers.get('session_start')({},ctx)
    return {env,ctx}
  }
  try{
    let {env,ctx}=await start()
    await env.eventHandlers.get('turn_start')({type:'turn_start',turnIndex:0},ctx)
    const tool=env.registeredTools.find(x=>x.name==='todowrite')
    assert.ok(tool)
    assert.equal((await tool.execute('call-017',{remainingScopeTurnsEstimate:6},null,null,ctx)).details.estimate,6)
    assert.equal(scopeProgress.status(sid).known,false,'no turn_end has settled the paid round yet')
    await env.eventHandlers.get('session_shutdown')({},ctx)
    assert.equal(scopeProgress.status(sid).known,false)
    ;({env,ctx}=await start())
    await env.eventHandlers.get('turn_start')({type:'turn_start',turnIndex:0},ctx)
    assert.equal(scopeProgress.status(sid).estimate,6,
      'the first provider boundary after cold restart settles the persisted tool call once')
    assert.equal(scopeProgress.status(sid).resetDue,false)
    const next=env.registeredTools.find(x=>x.name==='todowrite')
    assert.equal((await next.execute('call-017b',{remainingScopeTurnsEstimate:3},null,null,ctx)).details.estimate,3)
    await env.eventHandlers.get('turn_end')({type:'turn_end',turnIndex:0,
      message:{role:'assistant',stopReason:'stop',content:[]},toolResults:[]},ctx)
    assert.equal(scopeProgress.status(sid).estimate,3)
    await env.eventHandlers.get('session_shutdown')({},ctx)
  }finally{
    scopeProgress.clear(sid)
    clearSessionState(sid)
    rmSync(work,{recursive:true,force:true})
  }
})
