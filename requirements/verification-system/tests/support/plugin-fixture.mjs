// tests/unit/Plugin/plugin-fixture.mjs — shared layer-2 plugin fixture (verification-system-008).
//
// Deliberately NOT named `*.test.mjs`. `tests/unit/runner.mjs:98` discovers tests with
// `walk('tests/unit', ['.test.mjs'])`, so a helper carrying that suffix would be run as
// a test file (zero tests inside it, but its top-level import cost paid twice).
// `scripts/architecture-gate.mjs` scans `['.mjs']`, so this file is still gated.

import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

if (!process.env.WANXIANGSHU_PROVIDER_LANGUAGE || process.env.WANXIANGSHU_PROVIDER_LANGUAGE === 'undefined') {
  process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'
}

process.env.WANXIANGSHU_NO_FATAL_EXIT = '1'

const initSpikePlugin = async (input) => {
  const { default: plugin } = await import('wanxiangshu')
  return plugin.server(input)
}
const { requiredNames: managedAgentNames } = await import('../../../../dist/Participant/Persona/Surface.js')
const journalSurface = await import('../../../../dist/Persistence/Journal/Surface.js')
const workspaceHost = await import('../../../../dist/OpenCode/Host/WorkspaceSharedJournal.js')
const eventsSurface = await import('../../../../dist/OpenCode/Host/EventsSurface.js')
const dispatchSurface = await import('../../../../dist/Interaction/Dispatch/DispatchSurface.js')
const workspaceEventStoreSurface = await import('../../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js')
const lifecycleSurface = await import('../../../../dist/Mission/Lifecycle/Surface.js')

const withJournalRuntime = async (directory, action) => {
  const journalResult = await workspaceHost.acquireSharedForWorkspace(
    directory,
    process.pid,
    new Date().toISOString(),
  )
  if (!journalResult?.ok) {
    throw new Error(`journal acquire rejected: ${journalResult?.error ?? 'unknown error'}`)
  }
  const terminalPort = eventsSurface.acquireSharedForWorkspace(directory)
  try {
    return await action({
      journal: journalResult.journal,
      runtimeId: journalSurface.JournalSurface_runtimeId(journalResult.journal),
      terminalPort,
      directory,
    })
  } finally {
    try {
      eventsSurface.releaseSharedForWorkspace(directory, terminalPort)
    } finally {
      journalSurface.JournalSurface_dispose(journalResult.journal)
    }
  }
}

const setupRoutingHome = (directory) => {
  const routingHome = join(directory, 'routing-home')
  const routingDir = join(routingHome, '.config', 'opencode')
  mkdirSync(routingDir, { recursive: true })
  writeFileSync(
    join(routingDir, 'wanxiangshu.mjs'),
    `export const routingProtocol = 2
export default function route(role, running, previous) {
  if (!new Set(['manager', 'orchestrator', 'engineer', 'coder', 'inspector', 'browser', 'inquiry', 'reviewer', 'devops', 'distiller', 'bookkeeper', 'plan']).has(role)) throw new Error('unexpected managed role: ' + role)
  if (Array.isArray(globalThis.__wanxiangshu_test_routing_seen)) {
    globalThis.__wanxiangshu_test_routing_seen.push({ role, running: running.map((item) => ({ ...item })) })
  }
  if (typeof globalThis.__wanxiangshu_test_routing_decision === 'function') {
    return globalThis.__wanxiangshu_test_routing_decision(role, running, previous)
  }
  return { model: 'provider/' + role + '-model', reasoning: 'none' }
}\n`,
    'utf8',
  )
  return routingHome
}


export const configureManagedPlugin = (hooks) => {
  const agent = Object.fromEntries(
    Array.from(managedAgentNames).map((name) => [name, { model: `fixture/${name}-model` }]),
  )
  return hooks.config({ agent })
}

/**
 * The smallest SDK client double: mint child ids, accept prompts. The id list is
 * handed back so tests can address the child a fork actually created.
 */
const promptedWaiters = new Map()

const stubClient = (createdIds, prompts, messages, abortedIds) => {
  let counter = 0
  const childSessions = []
  // PROMPT-011 physical-message store: family recovery（`PromptRecovery.reconcile`
  // → `findPhysical`）读子会话消息来证明 PromptClaim。旧 fixture 对所有 session 返回
  // 同一个空数组，子会话的认领永远 StillPending → join RECOVERY_BLOCKED
  // （`pending claim unknown ...`）。
  const messagesBySession = new Map() // sessionId -> message[]
  const self = {
    __pushHostMessage: (sessionId, message) => {
      const perSession = messagesBySession.get(sessionId) ?? []
      perSession.push(message)
      messagesBySession.set(sessionId, perSession)
      messages.push(message)
    },
    session: {
      create: async (args) => {
        counter += 1
        const id = `host-child-${counter}-${Math.random().toString(16).slice(2)}`
        createdIds.push(id)
        childSessions.push({
          id,
          parentID: args?.body?.parentID,
          agent: args?.body?.agent,
          title: args?.body?.title,
        })
        return { data: { id } }
      },
      get: async (args) => {
        const id = args?.path?.id ?? args?.sessionID ?? args?.sessionId
        const child = childSessions.find((candidate) => candidate.id === id)
        return { data: child ?? { id } }
      },
      list: async (args) => {
        const query = args?.query ?? {}
        const wanted = typeof query.search === 'string' ? query.search : null
        const rootsOnly = query.roots === true
        return {
          data: childSessions.filter((session) => {
            if (rootsOnly && session.parentID != null) return false
            if (wanted != null && !String(session.title ?? '').includes(wanted)) return false
            return true
          }),
        }
      },
      children: async (args) => ({
        data: childSessions.filter((child) => child.parentID === args?.path?.id),
      }),
      messages: async (args) => {
        // SessionSnapshotPort.GetMessages payload: { path: { id }, query, headers }。
        const id = args?.path?.id ?? args?.sessionID ?? args?.sessionId
        const perSession = (id && messagesBySession.get(id)) || []
        // Legacy 数组：测试直接向 `runtime.messages` push（REVIEW_007 compaction
        // fixture）。只合并无 session 归属的消息——parent 仍能读到它们，子会话的
        // prompt 不会跨 session 泄漏。
        const orphaned = messages.filter(
          (m) => ![...messagesBySession.values()].some((list) => list.includes(m)),
        )
        return { data: [...perSession, ...orphaned] }
      },
      promptAsync: async (args) => {
        prompts.push(args)
        // 生产在 terminal 订阅安装之后才发 prompt（OneShotAgentTool.fs:115 → send），
        // 故此调用即「可以安全 NotifyTerminal」的就绪信号。
        // OpenCodePort payload: { path: { id }, body: { parts, agent?, model?, metadata? }, headers }。
        const sessionId = args?.path?.id ?? args?.sessionID ?? args?.sessionId ?? createdIds[createdIds.length - 1]
        // 合成 Host 物理消息（SessionSnapshotPort.projectMessage 可投影的形状），让
        // findPhysical 能证明认领。key 在 body.metadata 与 text part metadata 两侧
        // 都写（OpenCodePort 两处都落，PromptMetadataCodec.PromptKeyField）。
        const textPart = args?.body?.parts?.find((part) => part?.type === 'text')
        const key =
          args?.body?.metadata?.wanxiangshu_prompt_key ?? textPart?.metadata?.wanxiangshu_prompt_key
        const message = {
          id: `msg-${sessionId}-${messagesBySession.get(sessionId)?.length ?? 0}`,
          role: 'user',
          parts: [
            {
              type: 'text',
              text: textPart?.text ?? '',
              metadata: key ? { wanxiangshu_prompt_key: key } : undefined,
            },
          ],
          metadata: key ? { wanxiangshu_prompt_key: key } : undefined,
        }
        const perSession = messagesBySession.get(sessionId) ?? []
        perSession.push(message)
        messagesBySession.set(sessionId, perSession)
        messages.push(message)
        const waiter = promptedWaiters.get(sessionId)
        if (waiter) {
          promptedWaiters.delete(sessionId)
          waiter()
        } else {
          promptedWaiters.set(sessionId, null) // 已就绪：后来的 awaitPrompted 立即返回
        }
        const hk = self.__hooks;
        if (hk && hk['chat.message']) {
          try {
            await hk['chat.message']({ sessionID: sessionId, messageID: message.id, agent: args?.body?.agent }, { message, parts: message.parts })
          } catch (err) {
            console.log('HOOK CHAT MESSAGE FAILED:', err)
          }
        }
        return {}
      },
      delete: async () => ({}),
      abort: async (args) => {
        abortedIds.push(args?.path?.id)
        return {}
      },
    },
  }
  return self
}

/** 等待生产对某个子会话发出首个 prompt——即其 terminal 订阅已安装的就绪信号。 */
export const awaitPrompted = (sessionId) => {
  const state = promptedWaiters.get(sessionId)
  if (state === null) {
    promptedWaiters.delete(sessionId)
    return Promise.resolve()
  }
  return new Promise((resolve) => promptedWaiters.set(sessionId, resolve))
}

/**
 * A plugin instance whose tools can actually execute (EXEC-002, layer 3).
 *
 * `runtime` passed to the body is { journal, runtimeId, terminalPort } — the
 * shared production instances, resolved AFTER `initSpikePlugin` so `acquire`
 * returns the already-registered entries rather than booting second owners.
 */
export const withExecutablePlugin = async (body, options = {}, createHooks = initSpikePlugin) => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-plugin-exec-'))
  const previousHome = process.env.HOME
  const previousUserProfile = process.env.USERPROFILE
  const routingHome = setupRoutingHome(directory)
  process.env.HOME = routingHome
  process.env.USERPROFILE = routingHome
  try {
    execFileSync('git', ['init', '--quiet', directory])
    const createdIds = []
    const abortedIds = []
    const prompts = []
    const messages = []
    const client = stubClient(createdIds, prompts, messages, abortedIds)
    const hooks = await createHooks({
      ...options,
      client,
      directory,
      events: options.events ?? { listen: () => () => {} },
    })
    client.__hooks = hooks
    await configureManagedPlugin(hooks)
    let runtime
    try {
      const journalResult = await workspaceHost.acquireSharedForWorkspace(
        directory,
        process.pid,
        new Date().toISOString(),
      )
      if (!journalResult?.ok) throw new Error(`journal acquire rejected: ${journalResult?.error ?? 'unknown error'}`)
      const terminalPort = eventsSurface.acquireSharedForWorkspace(directory)
      if (terminalPort == null) {
        journalSurface.JournalSurface_dispose(journalResult.journal)
        throw new Error('shared terminal port is unavailable')
      }
      runtime = {
        journal: journalResult.journal,
        runtimeId: journalSurface.JournalSurface_runtimeId(journalResult.journal),
        terminalPort,
        directory,
        client,
        prompts,
        messages,
        abortedIds,
        pushHostMessage: client.__pushHostMessage,
      }
      await body(hooks, directory, createdIds, runtime)
    } finally {
      // Keep the fixture's extra journal reference alive while plugin disposal
      // drains detached HostFork cancellation. AbortSession is sequenced after
      // durable handle abandonment, so observing every created child aborted is
      // a deterministic teardown barrier before releasing the last writer ref.
      try {
        await hooks.dispose()
      } finally {
        if (runtime !== undefined) {
          try {
            eventsSurface.releaseSharedForWorkspace(directory, runtime.terminalPort)
          } finally {
            journalSurface.JournalSurface_dispose(runtime.journal)
          }
        }
      }
    }
  } finally {
    if (previousHome === undefined) delete process.env.HOME
    else process.env.HOME = previousHome
    if (previousUserProfile === undefined) delete process.env.USERPROFILE
    else process.env.USERPROFILE = previousUserProfile
    rmSync(directory, { recursive: true, force: true })
  }
}

/**
 * Two or more real plugin incarnations over one Git-private journal and one
 * persistent Host double. Used for restart recovery contracts that cannot be
 * proved by rebuilding only an in-memory runtime object.
 */
export const withRestartablePlugin = async (body) => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-plugin-restart-'))
  const previousHome = process.env.HOME
  const previousUserProfile = process.env.USERPROFILE
  const routingHome = setupRoutingHome(directory)
  process.env.HOME = routingHome
  process.env.USERPROFILE = routingHome
  const liveHooks = new Set()
  try {
    execFileSync('git', ['init', '--quiet', directory])
    const createdIds = []
    const abortedIds = []
    const prompts = []
    const messages = []
    const client = stubClient(createdIds, prompts, messages, abortedIds)
    const start = async (workspaceDirectory = directory) => {
      const hooks = await initSpikePlugin({
        client,
        directory: workspaceDirectory,
        events: { listen: () => () => {} },
      })
      client.__hooks = hooks
      await configureManagedPlugin(hooks)
      liveHooks.add(hooks)
      return hooks
    }

    const stop = async (hooks) => {
      if (!liveHooks.delete(hooks)) return
      await hooks.dispose()
    }

    const withRuntime = (action) => withJournalRuntime(directory, action)

    await body(start, directory, {
      stop,
      withRuntime,
      client,
      createdIds,
      abortedIds,
      prompts,
      messages,
      pushHostMessage: client.__pushHostMessage,
    })
  } finally {
    for (const hooks of [...liveHooks].reverse()) await hooks.dispose()
    if (previousHome === undefined) delete process.env.HOME
    else process.env.HOME = previousHome
    if (previousUserProfile === undefined) delete process.env.USERPROFILE
    else process.env.USERPROFILE = previousUserProfile
    rmSync(directory, { recursive: true, force: true })
  }
}

/**
 * One caller-owned plugin incarnation over an existing workspace. No teardown
 * is registered: OS-crash tests must be able to terminate without dispose.
 */
export const startPluginIncarnation = async (directory) => {
  const routingHome = setupRoutingHome(directory)
  process.env.HOME = routingHome
  process.env.USERPROFILE = routingHome
  const createdIds = []
  const abortedIds = []
  const prompts = []
  const messages = []
  const client = stubClient(createdIds, prompts, messages, abortedIds)
  const hooks = await initSpikePlugin({
    client,
    directory,
    events: { listen: () => () => {} },
  })
  await configureManagedPlugin(hooks)

  return {
    hooks,
    withRuntime: (action) => withJournalRuntime(directory, action),
  }
}

/** AGENT-007 layer one: a durable HumanRoot names the session's managed agent. */
export const decideAuthorityRoot = (runtime, sessionId, agent, physicalMessageId = `root-${sessionId}`) =>
  dispatchSurface.acceptHumanRoot(runtime.journal, sessionId, physicalMessageId, agent)

export const observeAuthority = (runtime, sessionId) =>
  dispatchSurface.projectionObservation(runtime.journal, sessionId)

export const acceptAuthorityRoot = async (runtime, sessionId, agent, physicalMessageId) => {
  const result = await decideAuthorityRoot(runtime, sessionId, agent, physicalMessageId)
  if (!result?.ok) {
    throw new Error(`AcceptHumanRoot(${sessionId}, ${agent}) rejected: ${JSON.stringify(result?.error ?? 'unknown error')}`)
  }
  return result.profile
}

/**
 * Record a durable parent→child handle link, the production evidence that makes
 * a session a managed child. This is the same HandleLinked writer the fork
 * runtime uses; it is not a test-local binding cache.
 */
export const bindManagedChild = async (runtime, parentId, childId, agent, handleId = `h-${childId}`) => {
  const linked = await journalSurface.JournalSurface_appendAgent(
    runtime.journal,
    { kind: 'Session', session: parentId },
    null,
    {
      family: 'Execution',
      case: 'HandleLinked',
      payload: {
        ParentSessionId: parentId,
        ChildSessionId: childId,
        Handle: handleId,
        TargetAgent: agent,
        Byname: agent,
        CanonicalRole: agent,
        Ownership: 'DurableParentHandle',
      },
    },
  )
  if (linked?.ok !== true) {
    throw new Error(`HandleLinked(${parentId} → ${childId}) rejected: ${linked?.error ?? 'unknown error'}`)
  }
}

/**
 * Accept a pending AgentOwnerRoot claim on a child session so BusyAgentNudge
 * (reuse while run active) can resolve ActiveLogicalRun (PROMPT-005).
 * Call after fork create once promptAsync has recorded the PromptKey in metadata.
 * The agent comes from the pending claim (PromptDispatcher.fs:147-151), not from
 * a separate argument.
 */
export const acceptChildAgentOwnerRoot = async (runtime, childSessionId, promptKey) => {
  const result = await dispatchSurface.acceptAgentOwnerRoot(
    runtime.journal,
    childSessionId,
    promptKey,
    `physical-${childSessionId}`,
  )
  if (!result?.ok) {
    throw new Error(
      `AcceptAgentOwnerRoot(${childSessionId}, ${promptKey}) rejected: ${result?.error ?? 'unknown error'}`,
    )
  }
}

/**
 * The workspace EventStore the running plugin already reads. Lifecycle facts are
 * one vocabulary in one store; a test seeds real unified events there instead of
 * inventing a relay-shaped private surface.
 */
const lifecycleStore = async (runtime) => {
  const directory = runtime?.directory
  if (!directory) throw new Error('lifecycle seeding needs an executable plugin runtime (runtime.directory)')
  const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
  return workspaceEventStoreSurface.acquire(commonDir)
}

export const openIncumbency = async (
  runtime,
  sessionId,
  incumbencyId = `inc-${sessionId}`,
  kind = 'S4',
  ownerId = 'owner',
) => {
  const store = await lifecycleStore(runtime)
  const result = await lifecycleSurface.seedOpenAs(store, kind, sessionId, runtime.directory, 1n, ownerId)
  if (!result?.ok) throw new Error(`openIncumbency(${sessionId}) rejected: ${result?.error ?? 'unknown error'}`)
  return result
}

export const injectAuditPendingIncumbency = openIncumbency

export const lifecycleWorkView = async (runtime, sessionId) => {
  const store = await lifecycleStore(runtime)
  return lifecycleSurface.workView(store, sessionId)
}

// The accepted review is a unified lifecycle fact (Reviewed). It is committed to
// the workspace store the plugin reads, so no second representation crosses here.
export const injectAcceptedAssessment = async (
  runtime,
  sessionId,
  incumbencyId = `inc-${sessionId}`,
  outcome = 'Stayed',
  reviewerId = 'reviewer',
) => {
  const store = await lifecycleStore(runtime)
  const opened = await lifecycleSurface.workView(store, sessionId)
  if (!opened?.found) {
    await openIncumbency(runtime, sessionId, incumbencyId)
  }
  const result = await lifecycleSurface.seedReviewAs(store, sessionId, outcome, reviewerId)
  if (!result?.ok) {
    throw new Error(`injectAcceptedAssessment(${sessionId}) rejected: ${result?.error ?? 'unknown error'}`)
  }
  return result
}

export const grantWorkOwned = injectAcceptedAssessment

export const activateLife = async (runtime, sessionId) => {
  await openIncumbency(runtime, sessionId)
}

export const completeManagerLife = async (runtime, sessionId) => {
  const store = await lifecycleStore(runtime)
  const view = await lifecycleSurface.workView(store, sessionId)
  if (!view?.found) await openIncumbency(runtime, sessionId)
  const reviewed = await lifecycleSurface.workView(store, sessionId)
  if (!reviewed?.reviewed) {
    const seeded = await lifecycleSurface.seedReview(store, sessionId, 'Promoted')
    if (!seeded?.ok) throw new Error(`LifeCompleted(${sessionId}) review rejected: ${seeded?.error ?? 'unknown error'}`)
  }
  const confirm = await lifecycleSurface.seedDeliver(store, sessionId, `run-${sessionId}`, 'call-first', 2n)
  if (!confirm?.ok) throw new Error(`LifeCompleted(${sessionId}) confirmation rejected: ${confirm?.error ?? 'unknown error'}`)
  const retired = await lifecycleSurface.seedDeliver(store, sessionId, `run-${sessionId}`, 'call-second', 3n)
  if (!retired?.ok) throw new Error(`LifeCompleted(${sessionId}) retirement rejected: ${retired?.error ?? 'unknown error'}`)
}

/**
 * Deliver a real terminal completion for one child session, through the same
 * opaque Host event capability the plugin subscribed to.
 *
 * `sessionWideText` maps to AgentRunResult.TerminalText (EXEC-006 IsValid).
 * Join LLM wire (EXEC-004 rev.2) renders the delivered view as entry-local `#`
 * comments before each [[result]], not as a TOML data field. The durable blob
 * still carries the delivered view JSON for HandleCompletionCodec. Fixture
 * completions without an Opening capture yield an empty view → no comment block
 * before [[result]].
 * `turnFormalText` is what a one-shot tool reports as output (COMPANION-005).
 */
export const notifyCompleted = async (runtime, childSessionId, sessionWideText, turnFormalText, roleCaseTag = 2) => {
  const roleLabels = ['manager', 'orchestrator', 'coder', 'inspector', 'browser', 'inquiry', 'reviewer', 'devops', 'distiller', 'engineer']
  const role = roleLabels[roleCaseTag] ?? 'coder'
  await eventsSurface.notifyCompleted(runtime.terminalPort, childSessionId, sessionWideText, turnFormalText, role)
}

/**
 * A plugin instance over a throwaway Git repo.
 *
 * The journal lives under the Git common directory (PERSIST-006), so `git init` is
 * what makes the runtime addressable at all. `events.listen` is the smallest port
 * that satisfies the signal source; no scenario, no HTTP, no mock provider.
 *
 * Measured cost per call: 275-331ms wall, almost entirely `git init` plus
 * `initSpikePlugin`. Callers under the 1000ms per-test bound (`runner.mjs:27`) must
 * count calls, not assertions.
 */
export const withPluginClient = async (client, body) => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-plugin-'))
  const previousHome = process.env.HOME
  const previousUserProfile = process.env.USERPROFILE
  const routingHome = setupRoutingHome(directory)
  process.env.HOME = routingHome
  process.env.USERPROFILE = routingHome

  try {
    execFileSync('git', ['init', '--quiet', directory])
    const hooks = await initSpikePlugin({
      client,
      directory,
      events: { listen: () => () => {} },
    })
    client.__hooks = hooks
    try {
      await body(hooks, directory)
    } finally {
      await hooks.dispose()
    }
  } finally {
    if (previousHome === undefined) delete process.env.HOME
    else process.env.HOME = previousHome
    if (previousUserProfile === undefined) delete process.env.USERPROFILE
    else process.env.USERPROFILE = previousUserProfile
    rmSync(directory, { recursive: true, force: true })
  }
}

export const withPlugin = async (body) => withPluginClient({}, body)
