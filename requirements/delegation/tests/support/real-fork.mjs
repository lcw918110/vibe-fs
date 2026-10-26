// 真实插件路径下的委派夹具（纯 Host I/O）。
//
// 这个夹具只做一件事：把宿主消息按生产的顺序摆好，让生产接线自己跑完
// 剩下的路。它不含任何业务判定，也不读写任何持久事实：
//
//   物理 user 消息 → assistant 消息 → `experimental.chat.messages.transform`
//   （provider start 准入）→ 真实程序执行与助手材料 → `message.updated`
//   → `session.idle`（由生产裁决并发布子工作终态）。普通 return 不是业务交付。
//
// 终态由生产自己发布到共享终端端口；夹具**只观察**该端口，从不发布终态、
// 从不写 durable 见证。

import assert from 'node:assert/strict'
import { join } from 'node:path'
import * as eventsSurface from '../../../../dist/OpenCode/Host/EventsSurface.js'
import * as handleJournal from '../../../../dist/Execution/Delegation/Handle/JournalSurface.js'
import * as forkTool from '../../../../dist/Execution/Delegation/Fork/OpenCode/ToolSurface.js'
import {
  acceptAuthorityRoot,
  openIncumbency,
  withExecutablePlugin,
} from '../../../verification-system/tests/support/plugin-fixture.mjs'

/** The Host's physical user message of one child turn (the last one the child received). */
export const physicalUserMessageOf = (runtime, child) => {
  const physical = runtime.messages.filter((message) => message.id?.startsWith(`msg-${child}-`)).at(-1)
  assert.ok(physical, `the child ${child} must have received a physical user message`)
  return physical
}

/**
 * The shared terminal port is where the production terminal publication
 * lands. This observer only reads it; it publishes nothing.
 */
export const observeTerminals = async (runtime) => {
  const port = await runtime.terminalPort
  const seen = []
  const subscription = eventsSurface.subscribe(port, (sessionId, outcome) => {
    seen.push({ sessionId, outcome })
  })
  return {
    seen,
    dispose: () => eventsSurface.dispose(subscription),
    /** Wait until the production published this exact run's terminal. */
    awaitRun: async (child, run, { timeoutMs = 15000, kind = 'Completed' } = {}) => {
      const deadline = Date.now() + timeoutMs
      while (Date.now() < deadline) {
        const published = seen.some(
          (entry) =>
            entry.sessionId === child &&
            entry.outcome?.kind === kind &&
            (run === undefined || entry.outcome?.providerRun === run),
        )
        if (published) return
        await new Promise((resolve) => setTimeout(resolve, 25))
      }
      assert.fail(`the child ${child} never published its ${kind} terminal for ${run}`)
    },
  }
}

/**
 * One real child turn: the assistant message, its delivery, and its terminal.
 * Missing material skips deliver so tests can observe an undelivered stop;
 * any parent obligation remains open.
 */
export const childTurn = async (hooks, runtime, child, run, prose, material, awaitTerminal, role = 'engineer') => {
  // The Host runs the child's provider step only after the chat.message hook of
  // its latest user message returned. A detached send hands control back to the
  // tool while that hook may still be admitting the prompt, so the turn waits
  // for Host ingress first instead of racing it.
  await runtime.awaitIngress()
  const physical = physicalUserMessageOf(runtime, child)
  const assistant = {
    info: {
      id: run,
      role: 'assistant',
      sessionID: child,
      parentID: physical.id,
      agent: role,
      providerID: 'provider',
      modelID: 'engineer-model',
      time: { created: 2 },
    },
    parts: [
      { type: 'text', text: prose },
      ...(material !== null && material !== undefined ? [{ type: 'text', text: String(material) }] : []),
    ],
  }
  runtime.pushHostMessage(child, assistant)
  const history = await runtime.client.session.messages({ path: { id: child } })
  const messages = history.data.map(message => ({
    ...message,
    info: { ...(message.info ?? message), sessionID: child },
  }))
  await hooks['experimental.chat.messages.transform']({ sessionID: child }, { messages })
  if (material !== null && material !== undefined) {
    const result = await hooks.tool[`todowrite-for-${role}`].execute(
      {
        name: `child-program-${run}`,
        intent: 'Observe this child program result before the host reports the provider turn ending.',
        remainingScopeTurnsEstimate: 3,
        program: `class Js extends JsProgram { async run() { return { answer: ${JSON.stringify(material)} }; } }`,
      },
      { sessionID: child, agent: role, callID: `call-${run}`, messageID: run },
    )
    assert.match(String(result), /^# ok/)
  }
  assistant.info.time.completed = 3
  assistant.info.finish = 'stop'
  await hooks.event({ event: { type: 'message.updated', properties: { info: assistant.info } } })
  await hooks.event({ event: { type: 'session.idle', properties: { sessionID: child } } })
  if (awaitTerminal) await awaitTerminal(child, run)
}

let callSequence = 0

/** `this.fork` through the real plugin surface. */
export const forkCall = (hooks, owner, name, charge, callID = 'call-fork-' + (++callSequence)) =>
  hooks.tool['todowrite-for-manager'].execute(
    {
      program: `class Js extends JsProgram { async run() { return await this.fork({ name: ${JSON.stringify(name)}, charge: ${JSON.stringify(charge)} }, []); } }`,
          name: callID,
          intent: 'Wait for this delegation receipt within the single admitted request window.',
          remainingScopeTurnsEstimate: 3
        },
    { sessionID: owner, agent: 'manager', callID, messageID: `msg-${callID}` },
  )

/** `this.resume` through the real plugin surface. */
export const resumeCall = (hooks, owner, name, charge, callID = 'call-resume-' + (++callSequence), attach) =>
  hooks.tool['todowrite-for-manager'].execute(
    {
      program: `class Js extends JsProgram { async run() { return await this.resume({ name: ${JSON.stringify(name)}, charge: ${JSON.stringify(charge)}${attach === undefined ? '' : `, attach: ${JSON.stringify(attach)}`} }, []); } }`,
          name: callID,
          intent: 'Wait for this delegation receipt within the single admitted request window.',
          remainingScopeTurnsEstimate: 3
        },
    { sessionID: owner, agent: 'manager', callID, messageID: `msg-${callID}` },
  )

/** The independent zero-argument join tool uses its existing work ownership. */
export const joinCall = (hooks, owner, callID = 'call-join-' + (++callSequence)) =>
  hooks.tool.join.execute(
    {},
    { sessionID: owner, agent: 'manager', callID, messageID: `msg-${callID}` },
  )

/** The delegated agent id of one fork / resume receipt. */
export const agentIdOf = (receipt) => {
  const agentId = /agentId = "([^"]+)"/.exec(receipt)?.[1]
  assert.ok(agentId, `the receipt must name the delegated agent: ${receipt}`)
  return agentId
}

/**
 * The work unit's lifecycle, read through the registered public surface
 * (`Handle/JournalSurface`). It is the same durable projection the fork
 * runtime reads — no second copy of the state.
 */
export const workLifecycleOf = (runtime, owner, agentId, child, root) =>
  handleJournal.snapshot(runtime.journal, owner, agentId, child, root).record?.lifecycle

/**
 * One real Manager session with its root admitted and its lifecycle opened.
 * The body receives the plugin runtime and the created child ids.
 */
export const withRealFork = async (owner, body) =>
  withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    await acceptAuthorityRoot(runtime, owner, 'manager')
    await openIncumbency(runtime, owner)
    return body({ hooks, directory, createdIds, runtime })
  })

/**
 * The work units of one owner, read back from a **cold** journal: a second
 * writer re-opens the durable event log of the same workspace and folds it
 * from scratch. It is the same cold-recovery surface the fork runtime uses —
 * the fixture adds no state of its own.
 */
export const coldWorkSnapshot = async (directory, owner) =>
  forkTool.coldWorkSnapshot(join(directory, '.Herdchestra'), owner)
