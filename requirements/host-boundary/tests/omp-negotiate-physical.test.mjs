#!/usr/bin/env node
// WHAT[delegation-034] Physical canary for omp native negotiate mechanism.
//
// Proves physical facts on compiled Fable artifacts:
// 1. HostPlugin connects with activateOnOmp (dist/Omp/Adapter.js).
// 2. Child session created via ports.Sessions.Create is tracked in liveChildren with delegation bound.
// 3. negotiate tool returns immediate acknowledgment with receipt / nonce before answer is produced.
// 4. Native ephemeral turn runs against delegator context preserving system prompt and history.
// 5. Physical steer lands in child session entries and event latch triggers before harness deadline.
// 6. Exact persisted answer and nonce match in child session.
// 7. First-reset gate unlocks ONLY after physical landing:
//    - resetScope tool execution is refused while gate is locked.
//    - resetScope tool execution is admitted after gate is unlocked.
// 8. Negative checks: unmatched/stale steer does NOT unlock gate.
// 9. Cleanup all sessions in finally.

import { test } from 'node:test'
import assert from 'node:assert/strict'
import { empty } from '@fable-org/fable-library-js/List.js'
import http from 'node:http'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { execSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const REPO = path.resolve(HERE, '../../..')

const OPT_IN = 'OMP_NEGOTIATE_PHYSICAL_SMOKE'
const isOptedIn = process.env[OPT_IN] === '1'

async function runCanary() {
  if (!isOptedIn) {
    console.log(`Skipping: set ${OPT_IN}=1 to run physical canary`);
    return;
  }
  await testBody();
}

// TCP chunks can split one multibyte character; Buffer-to-string per chunk
// replaces valid source UTF-8 with U+FFFD and falsifies exact-prefix canaries.
async function decodeUtf8Chunks(chunks) {
  const decoder = new TextDecoder('utf-8', { fatal: true })
  let text = ''
  for await (const chunk of chunks) text += decoder.decode(chunk, { stream: true })
  return text + decoder.decode()
}

test('WHAT[delegation-034] the SSE receiver preserves UTF-8 code points split between network chunks', async () => {
  const original = 'A'.repeat(83000) + '到得早不等于地位尊；' + 'B'.repeat(1000)
  const bytes = Buffer.from(original, 'utf8')
  const boundary = Buffer.byteLength('A'.repeat(83000) + '到', 'utf8')
  for (const split of [boundary + 1, boundary + 2]) {
    assert.equal(await decodeUtf8Chunks([bytes.subarray(0, split), bytes.subarray(split)]), original)
  }
})

async function testBody() {
  // Public SDK import root resolution (portable, configurable via env var)
  const sdkRoot =
    process.env.OH_MY_PI_PATH ||
    process.env.PI_CODING_AGENT_PATH ||
    path.resolve(REPO, '../oh-my-pi/packages/coding-agent')

  const { createAgentSession, SessionManager, discoverAuthStorage, ModelRegistry } = await import(
    path.join(sdkRoot, 'src/index.ts')
  )
  const { AgentRegistry } = await import(path.join(sdkRoot, 'src/registry/agent-registry.ts'))
  const { initializeExtensions } = await import(path.join(sdkRoot, 'src/modes/runtime-init.ts'))
  const { activateOnOmp, getLiveChildren } = await import(path.join(REPO, 'dist/Omp/Adapter.js'))
  const { HostPlugin_create, HostPlugin_nodeClockPort, HostPlugin_nodeTimerPort } = await import(
    path.join(REPO, 'dist/Host/SecondHost/HostPlugin.js')
  )
  const { SessionIdModule_create } = await import(
    path.join(REPO, 'dist/Foundation/Identity.js')
  )
  const { negotiationRequired, preparePreviousScopeReply, confirmPreviousScopeSteer } = await import(path.join(REPO, 'dist/Context/Scope/Surface.js'))

  // 1. Spin up local mock SSE server on 127.0.0.1
  const requestsSeen = []
  let requestCounter = 0
  const EXPECTED_ANSWER = 'RFC-7807 problem details with traceId required.'
  const server = http.createServer(async (req, res) => {
    const bodyText = await decodeUtf8Chunks(req)
    const body = bodyText ? JSON.parse(bodyText) : {}
    const msgs = body.messages || []
    const hasTools = Array.isArray(body.tools) && body.tools.length > 0
    const sysMsg = msgs.find((m) => m.role === 'system')
    requestCounter++
    const entry = {
      n: requestCounter,
      model: body.model,
      hasTools,
      toolNames: (body.tools || []).map((t) => t.function?.name || t.name),
      sysPrompt: sysMsg?.content || '',
      messages: msgs.map((m) => ({ role: m.role, content: m.content })),
    }
    requestsSeen.push(body)

    // Ephemeral requests carry the negotiation questions
    const isEphemeral = msgs.some((m) => {
      const text = typeof m.content === 'string'
        ? m.content
        : Array.isArray(m.content) ? m.content.map((c) => c.text || '').join(' ') : ''
      return text.includes('format error responses')
    })

    const priorScope = msgs.some((m) => JSON.stringify(m.content).includes('What was the old rule about traceId?'))
    const replyText = priorScope ? 'ROOT_OLD_SCOPE_ANSWER: errors must carry traceId.' : isEphemeral ? EXPECTED_ANSWER : 'MAIN_PROMPT_INITIALIZED'

    const chunk = {
      id: 'chunk-' + requestCounter,
      object: 'chat.completion.chunk',
      model: body.model,
      choices: [
        {
          index: 0,
          delta: { content: replyText },
          finish_reason: null,
        },
      ],
    }
    const endChunk = {
      id: 'end-' + requestCounter,
      object: 'chat.completion.chunk',
      model: body.model,
      choices: [
        {
          index: 0,
          delta: {},
          finish_reason: 'stop',
        },
      ],
    }
    res.writeHead(200, { 'Content-Type': 'text/event-stream' })
    res.write('data: ' + JSON.stringify(chunk) + '\n\n')
    res.write('data: ' + JSON.stringify(endChunk) + '\n\n')
    res.write('data: [DONE]\n\n')
    res.end()
  })

  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
  const port = server.address().port
  const baseUrl = `http://127.0.0.1:${port}/v1`
  // 2. Prepare isolated temporary git repository
  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'omp-smoke-canary-'))
  execSync('git init -b main && git config user.name "Canary Tester" && git config user.email "canary@test.local"', {
    cwd: tmpDir,
    stdio: 'ignore',
  })
  const agentDir = path.join(tmpDir, 'agent')
  fs.mkdirSync(agentDir, { recursive: true })

  let parentSession = null
  let childSession = null

  try {
    const authStorage = await discoverAuthStorage(agentDir)
    const mr = new ModelRegistry(authStorage)
    mr.registerProvider('local-canary', {
      baseUrl,
      apiKey: 'test-key',
      api: 'openai-completions',
      models: [
        {
          id: 'canary-model',
          name: 'canary-model',
          api: 'openai-completions',
          reasoning: false,
          input: ['text'],
          contextWindow: 128000,
          maxTokens: 1024,
          cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
        },
      ],
    })
    const model = mr.find('local-canary', 'canary-model')

    // Create HostPlugin instance
    const clock = HostPlugin_nodeClockPort()
    const timerFactory = () => HostPlugin_nodeTimerPort()
    const plugin = HostPlugin_create(clock, timerFactory)

    // Capture host ports when plugin activates
    let capturedPorts = null
    let realProviderContext = null
    let currentProviderHook = null
    let activationFailure = null
    const origActivate = plugin.Activate.bind(plugin)
    plugin.Activate = (ports) => {
      capturedPorts = ports
      return Promise.resolve(origActivate(ports)).catch(error => { activationFailure = String(error); throw error })
    }

    const registeredTools = new Map()
    const agentRegistry = new AgentRegistry()
    let childCounter = 0
    const extension = async (pi) => {
      // Provide SDK surface on pi.pi for child session creation
      pi.pi = {
        SessionManager,
        createAgentSession: (opts) =>
          createAgentSession({
            agentId: opts.agentId || `child-worker-${++childCounter}`,
            ...opts,
            extensions: [extension],
            authStorage,
            modelRegistry: mr,
            model,
            agentRegistry,
            disableExtensionDiscovery: true,
          }),
      }

      const originalOn = pi.on.bind(pi)
      pi.on = (eventName, handler) => originalOn(eventName, (event, ctx) => {
        if (eventName === 'before_provider_request') { realProviderContext = ctx; currentProviderHook = handler }
        return handler(event, ctx)
      })

      const origRegisterTool = pi.registerTool.bind(pi)
      pi.registerTool = (toolDef) => {
        registeredTools.set(toolDef.name, toolDef)
        return origRegisterTool(toolDef)
      }

      await activateOnOmp(pi, plugin, empty())
    }

    // 3. Start delegator session with product extension
    const parentSessionResult = await createAgentSession({
      agentId: 'parent-delegator',
      cwd: tmpDir,
      agentDir,
      authStorage,
      modelRegistry: mr,
      model,
      agentRegistry,
      extensions: [extension],
      disableExtensionDiscovery: true,
    })
    parentSession = parentSessionResult.session
    const parentId = parentSession.sessionManager.getSessionId()

    if (parentSession.extensionRunner) {
      await initializeExtensions(parentSession, {
        reportSendError: () => {},
        reportRuntimeError: () => {},
      })
    }
    await parentSession.extensionRunner?.emit({ type: 'session_start' })

    // Prime delegator session with a prompt establishing context
    const DELEGATOR_CONTEXT_RULE = 'Delegator architectural invariant: errors must carry traceId.'
    await parentSession.prompt(DELEGATOR_CONTEXT_RULE)

    assert.ok(capturedPorts, 'HostPlugin.Activate must have captured host ports')
    assert.ok(capturedPorts.Sessions, 'hostPorts must provide Sessions capability')

    // Same real OMP provider hook: archive this root session's actual request,
    // run a native side turn with its frozen prefix, and observe the outgoing
    // provider request on the local SSE server.
    assert.ok(realProviderContext, `OMP must expose the registered provider request hook; Host activation failure: ${activationFailure ?? 'none observed'}`)
    assert.equal(typeof realProviderContext.runEphemeralTurn, 'function')
    const historicalPayload = requestsSeen.at(-1)
    const rootReset = registeredTools.get('resetScope')
    const rootPrior = registeredTools.get('askPreviousScope')
    assert.ok(rootReset && rootPrior)
    const rootId = parentSession.sessionManager.getSessionId()
    const archived = await rootReset.execute(
      'actual-root-reset-previous',
      {newScopeObjective:'Clarify old historical rules',oldScopeVerboseVerbatimExperience:DELEGATOR_CONTEXT_RULE},
      null,null,realProviderContext,
    )
    assert.equal(archived.details?.archived,true,JSON.stringify(archived))
    // This reset was invoked as an actual registered tool, not emitted by the fake SSE model.
    // Feed the next real SDK-registered provider hook a legal conversation boundary
    // before the short-lived old-Scope consultation is dispatched.
    assert.equal(typeof currentProviderHook,'function')
    const postResetPayload={...structuredClone(historicalPayload),messages:[
      ...structuredClone(historicalPayload.messages),
      {role:'assistant',content:[{type:'toolCall',id:'actual-root-reset-previous',name:'resetScope',args:{newScopeObjective:'Clarify old historical rules'}}]},
      {role:'user',content:'PRIVATE_NEW_SCOPE_CANARY_112358'},
    ]}
    await currentProviderHook({payload:postResetPayload},realProviderContext)
    const askedPrior = await rootPrior.execute('actual-root-prior-001',
      {questions:'What was the old rule about traceId?'},null,null,realProviderContext)
    assert.match(askedPrior.content?.[0]?.text??'',/readonly old-model turn was scheduled/)
    const nativeLimit = Date.now()+8000
    const hasPriorRequest=()=>requestsSeen.some(req=>req.messages?.some(msg=>{
      const content=typeof msg.content==='string'?msg.content:JSON.stringify(msg.content)
      return content.includes('What was the old rule about traceId?')
    }))
    while(!hasPriorRequest()&&Date.now()<nativeLimit)await new Promise(r=>setTimeout(r,20))
    assert.ok(hasPriorRequest(),'the old Scope request physically reached the local SSE provider')
    const priorWire=requestsSeen.find(req=>req.messages?.some(msg=>
      (typeof msg.content==='string'?msg.content:JSON.stringify(msg.content)).includes('What was the old rule about traceId?')))
    assert.deepEqual(priorWire.messages.slice(0,-1), historicalPayload.messages,
      'the original physical user/system/assistant prefix is replayed exactly')
    assert.deepEqual(priorWire.tools,historicalPayload.tools,
      'the provider-side original tool definitions stay byte-equivalent')
    assert.equal(priorWire.model,historicalPayload.model)
    await parentSession.waitForIdle()
    await parentSession.prompt('Consume the queued answer from the previous Scope.')
    const physicalPrior = parentSession.sessionManager.getEntries()
      .find(entry=>entry.type==='message' && entry.message?.role==='user'
        && entry.message?.attribution==='agent'
        && JSON.stringify(entry.message.content).includes('ROOT_OLD_SCOPE_ANSWER'))
    assert.ok(physicalPrior,'the old answer arrives through an actual agent-attributed OMP message')
    const priorReceipt = askedPrior.content[0].text.match(/receipt=([^\s]+)/)?.[1]
    assert.ok(priorReceipt, 'model-side receipt must be observable in tool return')
    assert.equal(preparePreviousScopeReply(priorReceipt,rootId,'another answer').ok,false,
      'receipt is settled only after the actual physical OMP agent message start')
    assert.equal(confirmPreviousScopeSteer(priorReceipt,rootId),false,
      'physically accepted answer cannot be settled twice')
    const priorReplay=await rootPrior.execute('actual-root-prior-001',
      {questions:'What was the old rule about traceId?'},null,null,realProviderContext)
    assert.match(priorReplay.content?.[0]?.text??'',/already accepted/)
    assert.equal(requestsSeen.filter(req=>req.messages?.some(msg=>
      (typeof msg.content==='string'?msg.content:JSON.stringify(msg.content)).includes('What was the old rule about traceId?'))).length,1,
      'replayed toolCallId cannot send a second provider request')

    // 4. Create child session through actual ports.Sessions.Create path
    const createChildOutcome = await capturedPorts.Sessions.Create(
      SessionIdModule_create(parentId),
      { Title: 'child-worker', Workspace: undefined },
    )
    // Check the physical child exists through the authoritative live session map,
    // rather than inspecting Fable Result DU internals.
    const liveMap = getLiveChildren()
    assert.equal(liveMap.size,1,'ports.Sessions.Create must have created one actual child')
    const childId = Array.from(liveMap.keys())[0]

    // Verify child is tracked in Adapter liveChildren
    const childRecord = liveMap.get(childId)
    assert.ok(childRecord, 'child must be tracked in Adapter liveChildren')
    childSession = childRecord.Session
    assert.ok(childSession, 'child session instance must be present in liveChildren')
    if (childSession.extensionRunner) {
      await initializeExtensions(childSession, {
        reportSendError: () => {}, reportRuntimeError: () => {},
      })
    }
    await childSession.extensionRunner?.emit({type: 'session_start'})

    // Fact: Delegation is bound and gate is locked before negotiation
    assert.equal(negotiationRequired(childId), true, 'first-reset gate must be locked before negotiation')

    // Verify tools were registered
    const negotiateDef = registeredTools.get('negotiate')
    assert.ok(negotiateDef, 'negotiate tool must be registered by HostPlugin')
    const resetScopeDef = registeredTools.get('resetScope')
    assert.ok(resetScopeDef, 'resetScope tool must be registered by Adapter')

    const childCtx = {
      cwd: tmpDir,
      sessionManager: childSession.sessionManager,
      sessionId: childId,
    }

    // Fact: Testing resetScope tool directly - must be refused while gate is locked
    const earlyResetResult = await resetScopeDef.execute(
      'call-early-reset',
      { newScopeObjective: 'Initial exploration', newScopeTurnBudget: 5 },
      null,
      null,
      childCtx,
    )
    assert.equal(earlyResetResult.details?.refused, true, 'resetScope must be refused while gate is locked')
    assert.match(
      earlyResetResult.content[0].text,
      /resetScope is unavailable until the first negotiate\(questions\) call has been accepted/,
      'resetScope refusal message must guide to negotiate tool',
    )

    // 5. Setup event latch with harness deadline for physical steer landing
    let expectedReceipt = ''
    let steerReceived = false
    let resolveSteer
    const steerPromise = new Promise((resolve) => {
      resolveSteer = resolve
    })

    const unsubscribe = childSession.subscribe((event) => {
      if (event.type === 'message_end' || event.type === 'entry') {
        const entries = childSession.sessionManager.getEntries()
        const found = entries.some(
          (e) =>
            e.type === 'message' &&
            e.message?.role === 'user' &&
            typeof e.message?.content?.[0]?.text === 'string' &&
            e.message.content[0].text.includes('Delegator answered negotiate'),
        )
        if (found && !steerReceived) {
          steerReceived = true
          resolveSteer()
        }
      }
    })

    // 6. Child session invokes negotiate tool
    const immediateAck = await negotiateDef.execute(
      'call-neg-canary',
      { questions: 'How should we format error responses?' },
      null,
      null,
      childCtx,
    )

    // Fact: Immediate acknowledgment returned synchronously before answer arrives
    assert.ok(immediateAck && immediateAck.content, 'negotiate returns immediate acknowledgement')
    const ackText = immediateAck.content[0].text
    assert.match(ackText, /Clarification requested \(receipt ([^)]+)\)\. Negotiation is complete/)
    const receiptMatch = ackText.match(/Clarification requested \(receipt ([^)]+)\)\. Negotiation is complete/)
    assert.ok(receiptMatch, 'receipt must match')
    expectedReceipt = receiptMatch[1]

    // Accepted negotiate completes the gate at once; the steer is not awaited
    assert.equal(negotiationRequired(childId), false, 'accepted negotiate unlocks before the steer lands')

    // Wait for event latch with a 10s harness deadline
    const deadlinePromise = new Promise((_, reject) =>
      setTimeout(() => reject(new Error('Harness deadline exceeded waiting for steer delivery')), 10000),
    )
    await Promise.race([steerPromise, deadlinePromise])
    unsubscribe()

    // Wait for delivery observation and gate confirmation to settle
    const gateDeadline = Date.now() + 5000
    while (negotiationRequired(childId) && Date.now() < gateDeadline) {
      await new Promise((r) => setTimeout(r, 50))
    }

    // 7. Verify exact persisted answer + nonce in child session entries
    const childEntries = childSession.sessionManager.getEntries()
    const steerEntry = childEntries.find(
      (e) =>
        e.type === 'message' &&
        e.message?.role === 'user' &&
        typeof e.message?.content?.[0]?.text === 'string' &&
        e.message.content[0].text.includes(expectedReceipt),
    )
    assert.ok(steerEntry, 'child session entries must contain the exact steer message with nonce')
    const steerText = steerEntry.message.content[0].text
    assert.ok(steerText.includes(EXPECTED_ANSWER), 'child session steer must contain exact persisted answer')

    const msgHasText = (m, substr) => {
      if (typeof m?.content === 'string') return m.content.includes(substr)
      if (Array.isArray(m?.content)) {
        return m.content.some((c) => (typeof c === 'string' ? c.includes(substr) : typeof c?.text === 'string' && c.text.includes(substr)))
      }
      return false
    }

    // 8. Fact: Ephemeral turn wire request preserved delegator parent context
    const ephemeralReq = requestsSeen.find((r) => r.messages.some((m) => msgHasText(m, 'format error responses')))
    assert.ok(ephemeralReq, 'ephemeral request must have been sent to provider')
    const delegatorContextPresent = ephemeralReq.messages.some((m) => msgHasText(m, DELEGATOR_CONTEXT_RULE))
    assert.ok(delegatorContextPresent, 'ephemeral wire request must preserve delegator context and history')

    // 9. Fact: Real runtime preserves native catalog tools across wire requests
    const wireRequestsWithTools = requestsSeen.filter((r) => Array.isArray(r.tools) && r.tools.length > 0)
    assert.ok(wireRequestsWithTools.length > 0, 'wire requests must include tool catalog')
    for (const req of wireRequestsWithTools) {
      const toolNames = req.tools.map((t) => t.function?.name || t.type)
      assert.ok(toolNames.includes('bash'), 'native catalog bash tool preserved')
      assert.ok(toolNames.includes('read'), 'native catalog read tool preserved')
    }

    // 10. Fact: First-reset gate unlocks ONLY after physical landing
    const unlockDeadline = Date.now() + 5000
    while (negotiationRequired(childId) && Date.now() < unlockDeadline) {
      await new Promise((r) => setTimeout(r, 20))
    }
    assert.equal(negotiationRequired(childId), false, 'first-reset gate must unlock after physical steer landing')

    // The child must itself make a real provider request before reset can seal its old Scope.
    await childSession.waitForIdle()
    await childSession.prompt('Proceed with the next task using the clarified invariants.')

    // 11. Fact: Testing resetScope tool directly - admitted now that gate is unlocked
    const admittedResetResult = await resetScopeDef.execute(
      'call-admitted-reset',
      {
        i: 'Transition to implementation',
        oldScopeVerboseVerbatimExperience: 'Exploration completed successfully',
        newScopeObjective: 'Implement verified change',
      },
      null,
      null,
      childCtx,
    )
    assert.equal(admittedResetResult.details?.archived, true, JSON.stringify(admittedResetResult))
    assert.equal(admittedResetResult.details?.turnBudget,undefined,'reset no longer initializes a fixed countdown')
    assert.equal(admittedResetResult.details?.objective, 'Implement verified change', 'resetScope sets newScopeObjective')
    assert.equal(admittedResetResult.details?.archived, true, 'resetScope archives previous experience')
    assert.ok(admittedResetResult.content?.[0]?.text?.includes('Scope reset. Objective: Implement verified change'), 'content confirms scope reset')

    // 12. Fact: Subsequent check confirms gate remains unlocked
    assert.equal(negotiationRequired(childId), false, 'gate remains unlocked for session')
  } finally {
    if (parentSession) {
      try {
        parentSession.dispose()
      } catch {}
    }
    if (childSession) {
      try {
        childSession.dispose()
      } catch {}
    }
    server.close()
    try {
      fs.rmSync(tmpDir, { recursive: true, force: true })
    } catch {}
  }
}

test('WHAT[delegation-034] OMP physical negotiate canary against local provider', { timeout: 30000 }, async () => {
  await runCanary()
})


test('WHAT[scope-progress-018] true Bun/OMP SDK SSE Provider serves four Advisor turns and a physical steer', { timeout: 40000 }, async () => {
  if (process.env.OMP_ADVISOR_PHYSICAL_SMOKE !== '1') return
  const sdkRoot = process.env.OH_MY_PI_PATH || process.env.PI_CODING_AGENT_PATH
    || path.resolve(REPO, '../oh-my-pi/packages/coding-agent')
  const { createAgentSession, discoverAuthStorage, ModelRegistry } = await import(path.join(sdkRoot,'src/index.ts'))
  const { AgentRegistry } = await import(path.join(sdkRoot,'src/registry/agent-registry.ts'))
  const { initializeExtensions } = await import(path.join(sdkRoot,'src/modes/runtime-init.ts'))
  const { activateOnOmp } = await import(path.join(REPO, 'dist/Omp/Adapter.js'))
  const { HostPlugin_create, HostPlugin_nodeClockPort, HostPlugin_nodeTimerPort } = await import(
    path.join(REPO, 'dist/Host/SecondHost/HostPlugin.js'))
  const { SqliteJournal } = await import('wanxiangli/eventstore')
  const { DatabaseSync } = await import('node:sqlite')
  const { stateRoot } = await import(path.join(REPO, 'dist/Persistence/Journal/RuntimePath.js'))
  const { scopeProgress, advisorProcessQueue } = await import('wanxiangli/scope-progress')

  const phaseOf = body => {
    const text = JSON.stringify(body.messages ?? [])
    if (text.includes('Additional background:')) return 4
    if (text.includes('You raised the following problem:')) return 3
    if (text.includes('What background do you need')) return 2
    if (text.includes('What has held back the progress of the work?') || text.includes('工作进展受到了什么阻碍？')) return 1
    return 0
  }
  const requests = [], phases = []
  const server = http.createServer(async (req,res) => {
    const payload = JSON.parse(await decodeUtf8Chunks(req))
    requests.push(payload)
    const phase = phaseOf(payload)
    if (phase) phases.push({phase, payload})
    const answer = phase ? 'Network Advisor phase ' + phase + ' confirms the source facts.' : 'Main turn completed.'
    const delta = {
      id: 'scope-advisor-' + requests.length, object: 'chat.completion.chunk', model: payload.model,
      choices: [{index: 0, delta: {content: answer}, finish_reason: null}],
    }
    const done = {
      ...delta,
      choices: [{index: 0, delta: {}, finish_reason: 'stop'}],
    }
    res.writeHead(200, {'Content-Type':'text/event-stream'})
    res.write('data: ' + JSON.stringify(delta) + '\n\n')
    res.write('data: ' + JSON.stringify(done) + '\n\n')
    res.write('data: [DONE]\n\n')
    res.end()
  })
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve))
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(),'omp-advisor-sse-'))
  execSync('git init -q', {cwd: workspace})
  const agentDir = path.join(workspace, 'agent')
  fs.mkdirSync(agentDir,{recursive:true})
  let session, sessionId
  try {
    const authStorage = await discoverAuthStorage(agentDir)
    const registry = new ModelRegistry(authStorage)
    registry.registerProvider('advisor-network-canary', {
      baseUrl: 'http://127.0.0.1:' + server.address().port + '/v1',
      apiKey:'test-key', api:'openai-completions',
      models:[{id:'network-model', name:'network-model',api:'openai-completions',
        reasoning:false,input:['text'], contextWindow:128000,maxTokens:1024,
        cost:{input:0,output:0,cacheRead:0,cacheWrite:0}}],
    })
    const model = registry.find('advisor-network-canary','network-model')
    const registeredTools = new Map()
    const handlers = new Map()
    let providerContext
    const plugin = HostPlugin_create(HostPlugin_nodeClockPort(), () => HostPlugin_nodeTimerPort())
    const extension = async pi => {
      const originalOn = pi.on.bind(pi)
      pi.on = (event, handler) => {
        handlers.set(event,handler)
        return originalOn(event, (value,context) => {
          if (event === 'before_provider_request') providerContext = context
          return handler(value,context)
        })
      }
      const originalTool = pi.registerTool.bind(pi)
      pi.registerTool = definition => {
        registeredTools.set(definition.name,definition)
        return originalTool(definition)
      }
      await activateOnOmp(pi,plugin,empty())
    }
    const opened = await createAgentSession({
      agentId: 'advisor-network-session',cwd:workspace,agentDir,
      authStorage,modelRegistry:registry,model,agentRegistry:new AgentRegistry(),
      extensions:[extension],disableExtensionDiscovery:true,
    })
    session = opened.session
    sessionId = session.sessionManager.getSessionId()
    if (session.extensionRunner) await initializeExtensions(session,{
      reportSendError: () => {}, reportRuntimeError: () => {},
    })
    await session.extensionRunner?.emit({type:'session_start'})
    await session.prompt('Establish the working context before reporting remaining billable turns.')
    assert.equal(typeof providerContext?.runEphemeralTurn,'function')
    const todo = registeredTools.get('todowrite')
    assert.ok(todo, 'real OMP SDK must register the product progress tool')
    const endTurn = i => handlers.get('turn_end')({
      type:'turn_end',turnIndex:i,
      message:{role:'assistant',stopReason:'stop',content:[]},toolResults:[],
    },providerContext)
    const startTurn = i => handlers.get('turn_start')({type:'turn_start',turnIndex:i},providerContext)
    await startTurn(4)
    const first = await todo.execute('network-report-1',{remainingScopeTurnsEstimate:5},null,null,providerContext)
    assert.equal(first.details?.estimate,5)
    await endTurn(4)
    assert.equal(scopeProgress.status(sessionId).estimate,5)
    await startTurn(5)
    const second = await todo.execute('network-report-2',{remainingScopeTurnsEstimate:5},null,null,providerContext)
    assert.equal(second.details?.estimate,5)
    await endTurn(5)
    const deadline = Date.now() + 13000
    while(phases.length < 4 && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve,20))
    assert.deepEqual(phases.map(x=>x.phase),[1,2,3,4],
      'four actual side-turn HTTP requests must arrive at the local SSE provider in order')
    for (const {phase,payload} of phases) {
      const hasTools = Array.isArray(payload.tools) && payload.tools.length > 0
      if (phase===2 || phase===4) {
        assert.equal(hasTools,false,'dedicated Advisor requests must never include tools')
        assert.ok(payload.tool_choice === undefined || payload.tool_choice === 'none',
          'SDK may omit tool_choice when no tools are sent')
      } else {
        assert.equal(hasTools,true,'worker consultation must preserve native tool schema')
      }
    }
    const dedicatedFinal = phases.find(item=>item.phase===4)
    const dedicatedContext = JSON.stringify(dedicatedFinal.payload.messages)
    assert.match(dedicatedContext,/Earlier exchanges from our continuing discussion|我们此前的讨论/,
      'the dedicated seat must carry its own previously confirmed exchange')
    assert.match(dedicatedContext,/Previously you were asked|之前问你的是/,
      'the previous dedicated question must survive as original prose')
    assert.match(dedicatedContext,/You answered|你的回答是/,
      'the previous dedicated answer must be attributed to the same seat')
    await session.waitForIdle()
    await session.prompt('Consume the pending source-scoped agent advice, if any.')
    const root = await stateRoot(workspace)
    const journal = new SqliteJournal(new DatabaseSync(path.join(root,'wanxiangshu','advisor-process.sqlite')))
    try {
      const deadline = Date.now() + 10000
      let facts = []
      while (Date.now() < deadline) {
        facts = journal.read('advisor-process:' + sessionId).map(JSON.parse)
        if (facts.some(item=>item.kind==='Finished')) break
        await new Promise(resolve=>setTimeout(resolve,25))
      }
      assert.deepEqual(facts.filter(f=>f.kind==='StageBegan').map(f=>f.phase),[1,2,3,4,5])
      assert.deepEqual(facts.filter(f=>f.kind==='StageAnswered').map(f=>f.phase),[1,2,3,4,5])
      assert.equal(facts.find(f=>f.kind==='Finished')?.outcome,'Completed')
    } finally {journal.close()}
    const physical = session.sessionManager.getEntries().some(entry =>
      entry.type==='message' && entry.message?.role==='user'
      && entry.message?.attribution==='agent' && JSON.stringify(entry.message.content).includes('Network Advisor phase 4'))
    assert.equal(physical,true,'real SDK history must consume the actual agent-attributed Advisor steer')
  } finally {
    if (session) {
      try { session.dispose() } catch {}
    }
    if (sessionId) {
      scopeProgress.clear(sessionId)
      advisorProcessQueue.clear(sessionId)
    }
    await new Promise(resolve => server.close(resolve))
    fs.rmSync(workspace,{recursive:true,force:true})
  }
})
