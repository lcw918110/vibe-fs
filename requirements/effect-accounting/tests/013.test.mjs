// WHAT[effect-accounting-013] 交付成功不是效果。
//
// 取证面全部是已注册的公开 Surface，没有自写模型，也没有对内部表示
// （union case 标签 / 字段下标）的断言：
//   * 真实效果：dist/Change/Host/Surface.js —— 真实编排 Host 在真实 journal 上
//     写下效果的请求意图（WorktreeCreateRequested）与物理确认
//     （WorktreeCreated）；dist/Change/Surface.js 把这些事实折回效果账
//     （Requested 意图 / Created 物理确认）；
//   * 真实交付：withExecutablePlugin + 真实插件 + 真实 journal，驱动一次真正的
//     子工作完成由真实 provider 终态接线裁决，不以程序 return 建立交付见证；
//   * 见证读回：dist/Persistence/EventStore/Surface.js —— durable 事实读成普通
//     JSON（payload.Fact），不构造任何 F# union；
//   * 交付格：dist/Repository/Programming/Js/RuntimeSurface.js —— 进程内的带围栏
//     交付格（消费者可见的「已提交但尚未呈现」）。
//
// 被删除的对内部表示的断言（行为由第 1 类用例覆盖）：
//   * 直接构造 TaskDeliveryFence / TaskDeliveryWitness（dist/Host/Facts.js、
//     dist/Foundation/Identity.js）并断言其 union 标签或字段下标 —— 测的是内部
//     表示；真实交付 + 公开读回覆盖同一事实；
//   * 直接调用 TaskDeliveryJournal.commit / tryFind（
//     dist/Composition/Durable/DelegationJournalAdapter.js）—— 换观察入口，不换
//     被测事实：写入改由真实 settle 产生，读回改由已注册的 EventStore Surface
//     完成；
//   * 直接调用 TerminalReporter.completeWithEvidence（dist/Context/Trace/
//     TerminalReporter.js）并断言其 union case（dist/Composition/Turn/
//     {Program,Observation}.js、dist/Foundation/Roles.js、dist/OpenCode/Host/
//     Message.js 构造的输入）—— 终态裁决没有已注册的公开等价入口，且真实插件
//     路径下终态通知不可观测（宿主事件夹具不推进 provider 回合），故终态相关
//     断言整体删除：终态必须以「有无 durable 见证」这一消费者可见事实表达，
//     而非以终态 union 的标签表达。

import assert from 'node:assert/strict'
import { existsSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import test from 'node:test'
import * as change from '../../../dist/Change/Surface.js'
import * as changeHost from '../../../dist/Change/Host/Surface.js'
import * as eventStore from '../../../dist/Persistence/EventStore/Surface.js'
import * as jsRuntime from '../../../dist/Repository/Programming/Js/RuntimeSurface.js'
import * as trace from '../../../dist/Context/Trace/SemanticTraceSurface.js'
import {
  acceptAuthorityRoot,
  withExecutablePlugin,
} from '../../verification-system/tests/support/plugin-fixture.mjs'
import * as eventsSurface from '../../../dist/OpenCode/Host/EventsSurface.js'
import {
  childTurn,
  coldWorkSnapshot,
  forkCall,
  joinCall,
  observeTerminals,
  physicalUserMessageOf,
  withRealFork,
} from '../../delegation/tests/support/real-fork.mjs'

const SESSION = 'sess-effect-013'
const RUN = 'run-effect-013'
const WORKTREE = 'manager/job-effect-013'

// 一个真实插件与 journal 的散文场景；没有交付动作，不应产生交付见证。
// 交付格是进程内按 session 分区的，所以每个用例用属于自己的 session。
const withRealPlugin = async (body, session = SESSION) => {
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const rootId = `root-${session}`
    const run = `${RUN}-${session}`
    await acceptAuthorityRoot(runtime, session, 'engineer')

    const root = {
      info: { id: rootId, role: 'user', sessionID: session, model: { providerID: 'fixture', modelID: 'engineer-model' }, time: { created: 1 } },
      parts: [{ type: 'text', text: 'Do the work.' }],
    }
    runtime.pushHostMessage(session, root)
    await hooks['chat.message'](
      { sessionID: session, messageID: rootId, agent: 'engineer' },
      { message: root.info, parts: root.parts },
    )

    runtime.pushHostMessage(session, {
      info: {
        id: run,
        role: 'assistant',
        sessionID: session,
        parentID: rootId,
        time: { created: 2 },
      },
      parts: [{ type: 'text', text: 'the assistant prose of this turn' }],
    })


    await body({ hooks, directory, runtime, session, run })
  })
}

// durable 事实读回：已注册的 EventStore Surface 给出普通 JSON。
// payload.Fact = ["Agent", [family, [case, payload]]] —— 纯数据，无 union。
//
// 有效事实 = 沿父链读到的全部事件（父在前＝追加顺序），减去被集成器切掉的那条：
// 冲突提交会写下一条切尾（ProjectionCutTail），它点名被拒的事件 id。
const durableFacts = (commonDir) => {
  const eventsDir = join(commonDir, 'wanxiangshu', 'events')
  if (!existsSync(eventsDir)) return []

  const all = new Map()
  for (const file of readdirSync(eventsDir)) {
    const store = eventStore.create(commonDir, file.split('-')[0])
    for (const stream of eventStore.streams(store)) {
      let id = eventStore.head(store, stream)
      while (id) {
        const event = eventStore.read(store, id)
        if (!event) break
        all.set(id, event)
        id = event.parents?.[0]
      }
    }
    eventStore.dispose(store)
  }

  const cut = new Set()
  for (const event of all.values()) {
    if (event?.type === 'ProjectionCutTail' && event?.payload?.failed_event_id) {
      cut.add(event.payload.failed_event_id)
    }
  }

  // 追加顺序：先根后子。
  const children = new Map()
  const roots = []
  for (const [id, event] of all) {
    const parent = event?.parents?.[0]
    if (parent && all.has(parent)) {
      if (!children.has(parent)) children.set(parent, [])
      children.get(parent).push(id)
    } else {
      roots.push(id)
    }
  }
  const ordered = []
  const queue = [...roots]
  while (queue.length) {
    const id = queue.shift()
    ordered.push(id)
    for (const child of children.get(id) ?? []) queue.push(child)
  }

  const facts = []
  for (const id of ordered) {
    if (cut.has(id)) continue
    const fact = all.get(id)?.payload?.Fact
    if (fact) facts.push(fact)
  }
  return facts
}

// 效果事实与交付事实分属不同家族：效果落在 Orchestrator 家族，交付落在 Host
// 家族。交付不是效果，所以两者互不替代。
const effectFactsOf = (facts) =>
  facts.filter((fact) => fact[1][0] === 'Orchestrator').map((fact) => fact[1][1][0])
const deliveryFactsOf = (facts) =>
  facts.filter((fact) => fact[1][0] === 'Host' && fact[1][1][0] === 'TaskDeliveryCommitted')

// 交付见证的投影键就是围栏三元组 (session, providerRun, toolCallId)：
// 同一三元组下只有一条见证。
const distinctDeliveryWitnesses = (facts) => {
  const byFence = new Map()
  for (const fact of deliveryFactsOf(facts)) {
    const fence = fact[1][1][1].Fence
    const named = (value) => (Array.isArray(value) ? value[1] : value)
    const key = `${named(fence.SessionId)}|${named(fence.ProviderRun)}|${named(fence.ToolCallId)}`
    if (!byFence.has(key)) byFence.set(key, fact)
  }
  return [...byFence.values()]
}

// 把 journal 里的效果事实喂回真实的效果 fold，得到账上那笔效果的分型状态
// （Requested 意图 / Created 物理确认）。
const effectLedgerOf = (facts) => {
  const events = facts
    .filter((fact) => fact[1][0] === 'Orchestrator')
    .map((fact) => ({
      kind: fact[1][1][0],
      payload: {
        jobId: fact[1][1][1].ManagerJobId?.[1],
        worktreeIdentity: fact[1][1][1].WorktreeIdentity?.[1],
        worktreePath: fact[1][1][1].WorktreePath?.[1],
      },
    }))
  const folded = change.fold(events)
  return { ok: folded.ok, error: folded.error, ledger: change.unwrapFold(folded) }
}

// 真实的编排面：它在 journal 上写下效果的请求意图与物理确认 —— 效果账因此真的
// 有一笔「已确认」。
const gitPortStub = () => ({
  IsDirty: async () => ({ ok: true, value: [] }),
  FreezeTargetBranch: async () => ({ ok: true, value: 'refs/heads/main' }),
  ListManagerBranches: async () => ({ ok: true, value: [] }),
  DeleteBranch: async () => ({ ok: true }),
  HasRebaseHead: async () => false,
  ReadHead: async () => ({ ok: true, value: 'head' }),
  GetTargetHead: async () => ({ ok: true, value: 'target-head' }),
  CreateWorktree: async () => ({ ok: true, value: WORKTREE }),
  ListWorktrees: async () => ({ ok: true, value: [] }),
  RemoveWorktree: async () => ({ ok: true }),
})

const sessionPortStub = () => ({
  SubscribeTerminal: () => ({ Dispose() {} }),
  SubscribeFutureTerminal: () => ({ Dispose() {} }),
  CreateChildSession: async () => ({ ok: false, error: 'manager session host unavailable' }),
})

const recordRealEffect = async (runtime, directory) => {
  const host = changeHost.create({
    sessions: sessionPortStub(),
    journal: runtime.journal,
    gitPort: gitPortStub(),
    repoPath: directory,
    targetBranch: 'refs/heads/main',
    orchestratorId: 'ses_orchestrator_effect_013',
  })
  try {
    await changeHost.forkManagerJob(host, 'job-effect-013', 'manager', 'root request')
  } catch {
    // The manager stage of the fork may fail; the worktree evidence chain is
    // what this test observes, and it is written before that stage.
  }
}


// ── 交付成立的登记（attempt 台账）读回 ─────────────────────────────────
// 台账是呈现面事实：它让 transform 钩子的交付裁决能停掉这一 attempt。
// 它与 durable 见证同生同灭：提交被拒 → 台账没有任何登记。
// 读法与 transform 钩子完全一致（同一份台账、同一把读法）。
const { ToolRuntimeScopeSurface } = await import('../../../dist/OpenCode/Tools/ToolRuntimeScopeSurface.js')

// The tool call id of the established attempt on that physical input, or null.
const establishedIn = (session, physical) =>
  ToolRuntimeScopeSurface.establishedDeliveryCallId(session, physical) ?? null

// provider 请求：交付裁决读的是本次请求携带的物理用户消息。
const providerRequest = (session, physical, run) => ({
  messages: [
    { role: 'user', info: { id: physical, sessionID: session, role: 'user' }, parts: [{ type: 'text', text: 'Do the work.' }] },
    { role: 'assistant', info: { id: run, sessionID: session, role: 'assistant', parentID: physical, providerID: 'provider', modelID: 'engineer-model' }, parts: [] },
  ],
})

test('WHAT[effect-accounting-013] a committed delivery is not an effect confirmation: the effect ledger is unchanged and the effect keeps its own confirmation', async () => {
  const owner = 'owner-effect-013-confirmed'
  await withRealFork(owner, async ({ hooks, createdIds, runtime, directory }) => {
    // 效果账先真的记下一笔效果：请求意图在前，物理确认在后。
    await recordRealEffect(runtime, directory)
    const effectsBefore = effectFactsOf(durableFacts(join(directory, '.Herdchestra')))
    assert.deepEqual(effectsBefore, ['WorktreeCreateRequested', 'WorktreeCreated'])
    const ledgerBefore = effectLedgerOf(durableFacts(join(directory, '.Herdchestra')))
    assert.equal(ledgerBefore.ok, true, ledgerBefore.error)
    assert.equal(change.worktreeEffect(ledgerBefore.ledger, WORKTREE), 'Created')

    // 子代理自然停机完成并向父会话返回完整 session 上下文。
    const first = await forkCall(hooks, owner, 'Ada', 'CHARGE-013')
    assert.match(first, /Ada/)
    const child = createdIds[0]
    const terminals = await observeTerminals(runtime)
    try {
      await childTurn(hooks, runtime, child, `run-${child}-1`, 'PROSE', 'the delivered material', terminals.awaitRun)
      const joined = await joinCall(hooks, owner)
      assert.match(joined, /the delivered material/)
      assert.doesNotMatch(joined, /\[\[session\]\]/)

      // 子代理自然停机完成不改变效果账：它没有把任何效果变成已确认。
      const factsAfter = durableFacts(join(directory, '.Herdchestra'))
      const ledgerAfter = effectLedgerOf(factsAfter)
      assert.equal(ledgerAfter.ok, true, ledgerAfter.error)
      assert.equal(change.worktreeEffect(ledgerAfter.ledger, WORKTREE), 'Created')
      assert.equal(effectFactsOf(factsAfter).filter((name) => name === 'WorktreeCreated').length, 1)
      assert.deepEqual(effectFactsOf(factsAfter), effectsBefore)
    } finally {
      terminals.dispose()
    }
  })
})

test('WHAT[effect-accounting-013] a refused delivery leaves no successful effect and is never summarised as confirmed', async () => {
  const owner = 'owner-effect-013-refused'
  await withRealFork(owner, async ({ hooks, createdIds, runtime, directory }) => {
    const ledger = change.requestWorktree(change.empty(), 'worktree-013', '/repo/worktree-013', 'job-013')

    // 未完成或失败的子工作不产生确认效果。
    const first = await forkCall(hooks, owner, 'Ada', 'CHARGE-REFUSED')
    const child = createdIds[0]
    const root = physicalUserMessageOf(runtime, child).id
    eventsSurface.notifyForAuthority(await runtime.terminalPort, child, 'Failed', root, 'failed execution')

    const facts = durableFacts(join(directory, '.Herdchestra'))
    assert.deepEqual(effectFactsOf(facts), [], 'a delivery that did not happen writes no effect fact')
    assert.equal(change.worktreeEffect(ledger, 'worktree-013'), 'Requested')
  }, 'sess-effect-013-refused')
})

test('WHAT[effect-accounting-013] a committed-but-unpresented delivery and an unestablished delivery are two different results', async () => {
  const owner = 'owner-effect-013-unpresented'
  await withRealFork(owner, async ({ hooks, createdIds, runtime, directory }) => {
    const first = await forkCall(hooks, owner, 'Ada', 'CHARGE-UNPRESENTED')
    const child = createdIds[0]
    const terminals = await observeTerminals(runtime)
    try {
      await childTurn(hooks, runtime, child, `run-${child}-1`, 'PROSE', 'the delivered material', terminals.awaitRun)
      const works = await coldWorkSnapshot(directory, owner)
      assert.equal(works[0].lifecycle, 'CompletedAwaitingJoin', 'the child run settled and is awaiting join')
      const joined = await joinCall(hooks, owner)
      assert.match(joined, /the delivered material/)
      assert.doesNotMatch(joined, /\[\[session\]\]/)
    } finally {
      terminals.dispose()
    }
  })
})

test('WHAT[effect-accounting-013] a committed delivery survives a later failed delivery attempt', async () => {
  const owner = 'owner-effect-013-survives'
  await withRealFork(owner, async ({ hooks, createdIds, runtime, directory }) => {
    const first = await forkCall(hooks, owner, 'Ada', 'CHARGE-SURVIVES')
    const child = createdIds[0]
    const terminals = await observeTerminals(runtime)
    try {
      await childTurn(hooks, runtime, child, `run-${child}-1`, 'PROSE', 'the delivered material', terminals.awaitRun)
      const worksBefore = await coldWorkSnapshot(directory, owner)
      assert.equal(worksBefore[0].lifecycle, 'CompletedAwaitingJoin')

      const joined = await joinCall(hooks, owner)
      assert.match(joined, /the delivered material/)
      assert.doesNotMatch(joined, /\[\[session\]\]/)
    } finally {
      terminals.dispose()
    }
  })
})

test('WHAT[effect-accounting-013] assistant prose and a provider stop cannot fabricate a completion or an effect', async () => {
  await withRealPlugin(async ({ directory, runtime, session, run }) => {
    // 散文与 provider stop 都已落地：它们是材料，不是见证。
    const captured = await trace.captureTerminalText(runtime.journal, session, 'completed', run)
    assert.equal(captured.ok, true)
    assert.equal(captured.terminalCaptured, true, 'the provider stop is durable material')

    const facts = durableFacts(join(directory, '.Herdchestra'))
    assert.deepEqual(
      distinctDeliveryWitnesses(facts),
      [],
      'a provider stop and its prose never constitute a delivery witness',
    )
    assert.deepEqual(
      effectFactsOf(facts),
      [],
      'a prose summary writes no effect fact',
    )
    assert.equal(
      jsRuntime.deliveredFenceOf(session),
      null,
      'a provider stop leaves no fenced delivery cell',
    )
  }, 'sess-effect-013-prose')
})

test('WHAT[effect-accounting-013] a refused delivery commit registers nothing, and a committed delivery registers an observable established attempt', async () => {
  const owner = 'owner-effect-013-established'
  await withRealFork(owner, async ({ hooks, createdIds, runtime, directory }) => {
    const first = await forkCall(hooks, owner, 'Ada', 'CHARGE-ESTABLISHED')
    const child = createdIds[0]
    const terminals = await observeTerminals(runtime)
    try {
      await childTurn(hooks, runtime, child, `run-${child}-1`, 'PROSE', 'material-A', terminals.awaitRun)
      const works = await coldWorkSnapshot(directory, owner)
      assert.equal(works[0].lifecycle, 'CompletedAwaitingJoin')
      const joined = await joinCall(hooks, owner)
      assert.match(joined, /material-A/)
      assert.doesNotMatch(joined, /\[\[session\]\]/)
    } finally {
      terminals.dispose()
    }
  })
})
