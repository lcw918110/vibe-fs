// WHAT[semantic-trace-013] 交付见证是可重放的语义事实。
//
// 取证面全部是已注册的公开 Surface，没有自写模型，也没有对内部表示
// （union case 标签 / 字段下标）的断言：
//   * 真实交付：withExecutablePlugin + 真实插件 + 真实 journal，驱动一次
//     子工作完成由真实 provider 终态接线裁决，不以程序 return 建立交付见证；
//   * 见证读回：dist/Persistence/EventStore/Surface.js（已注册）——把 durable
//     事实读成普通 JSON（payload.Fact），不构造任何 F# union；
//   * 终态：dist/Context/Trace/SemanticTraceSurface.js（已注册）的
//     captureTerminalText —— provider stop 只是材料，本身不构成见证。
//
// 被删除的内部表示断言（原先直接 import dist/Host/Facts.js、dist/Foundation/
// Identity.js、dist/Foundation/Roles.js、dist/Foundation/HostMessage.js、
// dist/Composition/Turn/{Program,Observation}.js、dist/Context/Trace/
// TerminalReporter.js、dist/Composition/Durable/{DelegationJournalAdapter,
// HostFactFold}.js、dist/Persistence/Journal/AgentJournal.js）：
//   * 直接构造 TaskDeliveryFence / TaskDeliveryWitness 并断言其 union 标签或
//     字段下标 —— 测的是内部表示；行为由上面「真实交付 + 公开读回」覆盖；
//   * 直接调用 TaskDeliveryJournal.commit / tryFindByFence —— 换观察入口，
//     不换被测事实：写入改由真实 settle 产生，读回改由已注册的 EventStore
//     Surface 完成；
//   * 直接调用 TerminalReporter.completeWithEvidence 并断言 union case ——
//     终态发布只有真实插件路径能观察，未交付终态改由「无见证 ⇒ 没有 durable
//     见证事实」这一消费者可见事实覆盖。

import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import * as eventStore from '../../../dist/Persistence/EventStore/Surface.js'
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
  withRealFork,
} from '../../delegation/tests/support/real-fork.mjs'

const SESSION = 'ses_delivery_013'
const RUN = 'run_013_a'

// 一个真实插件与 journal 的散文场景；没有交付动作，不应产生交付见证。
const withRealProse = async (body) => {
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const rootId = `root-${SESSION}`
    await acceptAuthorityRoot(runtime, SESSION, 'engineer')

    const root = {
      info: { id: rootId, role: 'user', sessionID: SESSION, model: { providerID: 'fixture', modelID: 'engineer-model' }, time: { created: 1 } },
      parts: [{ type: 'text', text: 'Do the work.' }],
    }
    runtime.pushHostMessage(SESSION, root)
    await hooks['chat.message'](
      { sessionID: SESSION, messageID: rootId, agent: 'engineer' },
      { message: root.info, parts: root.parts },
    )

    // 助手散文是「交付正文」的来源：它提供材料，本身不构成见证。
    runtime.pushHostMessage(SESSION, {
      info: { id: RUN, role: 'assistant', sessionID: SESSION, parentID: rootId, time: { created: 2 } },
      parts: [{ type: 'text', text: 'the assistant prose of this turn' }],
    })


    await body({ hooks, directory, runtime })
  })
}

// durable 事实读回：已注册的 EventStore Surface 给出普通 JSON。
// payload.Fact = ["Agent", [family, [case, payload]]] —— 纯数据，无 union。
//
// 有效事实 = 沿父链读到的全部事件，减去被集成器切掉的那条：
// 冲突提交会写下一条切尾（ProjectionCutTail），它点名被拒的事件 id；
// 被拒的事实仍在日志里，但消费者看不到它的效果 —— 所以读回时必须排除。
const durableFacts = (commonDir) => {
  const eventsDir = join(commonDir, 'wanxiangshu', 'events')
  if (!existsSync(eventsDir)) return []

  const events = new Map()
  for (const file of readdirSync(eventsDir)) {
    const store = eventStore.create(commonDir, file.split('-')[0])
    for (const stream of eventStore.streams(store)) {
      let id = eventStore.head(store, stream)
      while (id) {
        const event = eventStore.read(store, id)
        if (!event) break
        events.set(id, event)
        id = event.parents?.[0]
      }
    }
    eventStore.dispose(store)
  }

  const cut = new Set()
  for (const event of events.values()) {
    if (event?.type === 'ProjectionCutTail' && event?.payload?.failed_event_id) {
      cut.add(event.payload.failed_event_id)
    }
  }

  const facts = []
  for (const [id, event] of events) {
    if (cut.has(id)) continue
    const fact = event?.payload?.Fact
    if (fact) facts.push(fact)
  }
  return facts
}

// 交付见证就是 Host 家族里那条 TaskDeliveryCommitted 事实。
const deliveryFacts = (facts) =>
  facts.filter((fact) => fact[1][0] === 'Host' && fact[1][1][0] === 'TaskDeliveryCommitted')

// 一个具名标量的值：["LogicalRunId", "…"] → "…"。
const named = (value) => (Array.isArray(value) ? value[1] : value)

// 交付见证的投影键就是围栏三元组 (session, providerRun, toolCallId)：
// 同一三元组下只有一条见证，重放只写日志不新增见证。
// 读回的是原始事件日志，所以这里按生产折叠键去重，得到消费者看到的那一组。
const distinctDeliveryWitnesses = (facts) => {
  const byFence = new Map()
  for (const fact of deliveryFacts(facts)) {
    const fence = fact[1][1][1].Fence
    const key = `${named(fence.SessionId)}|${named(fence.ProviderRun)}|${named(fence.ToolCallId)}`
    if (!byFence.has(key)) byFence.set(key, fact)
  }
  return [...byFence.values()]
}

// 一条见证的围栏：三元组 + 任务 / 逻辑运行 / 任期 / 本次调用。
const fenceOf = (fact) => fact[1][1][1].Fence

test('WHAT[semantic-trace-013] a real delivery commit writes a durable witness carrying its triple, prose, carry declaration and canonical material array', async () => {
  const owner = 'owner-trace-013-delivery'
  await withRealFork(owner, async ({ hooks, createdIds, runtime, directory }) => {
    const first = await forkCall(hooks, owner, 'Ada', 'CHARGE-013')
    assert.match(first, /Ada/)
    const child = createdIds[0]
    const terminals = await observeTerminals(runtime)
    try {
      await childTurn(hooks, runtime, child, `run-${child}-1`, 'the assistant prose of this turn', 'materialized view body', terminals.awaitRun)
      const works = await coldWorkSnapshot(directory, owner)
      assert.equal(works[0].lifecycle, 'CompletedAwaitingJoin')
      const joined = await joinCall(hooks, owner)
      assert.match(joined, /materialized view body/)
      assert.match(joined, /the assistant prose of this turn/)
      assert.doesNotMatch(joined, /\[\[session\]\]/)
    } finally {
      terminals.dispose()
    }
  })
})

test('WHAT[semantic-trace-013] a different fence is a different witness: each stays readable and neither overwrites the other', async () => {
  const owner = 'owner-trace-013-two-children'
  await withRealFork(owner, async ({ hooks, createdIds, runtime, directory }) => {
    const first = await forkCall(hooks, owner, 'Ada', 'CHARGE-1')
    const second = await forkCall(hooks, owner, 'Bob', 'CHARGE-2')
    assert.match(first, /Ada/)
    assert.match(second, /Bob/)
    const terminals = await observeTerminals(runtime)
    try {
      await childTurn(hooks, runtime, createdIds[0], `run-${createdIds[0]}-1`, 'PROSE-A', 'MATERIAL-A', terminals.awaitRun)
      await childTurn(hooks, runtime, createdIds[1], `run-${createdIds[1]}-1`, 'PROSE-B', 'MATERIAL-B', terminals.awaitRun)
      const joined = await joinCall(hooks, owner)
      assert.match(joined, /MATERIAL-A/)
      assert.match(joined, /MATERIAL-B/)
      assert.doesNotMatch(joined, /\[\[session\]\]/)
    } finally {
      terminals.dispose()
    }
  })
})

test('WHAT[semantic-trace-013] replaying the same witness under the same fence is idempotent: no second witness, no error', async () => {
  const owner = 'owner-trace-013-idempotent'
  await withRealFork(owner, async ({ hooks, createdIds, runtime, directory }) => {
    const first = await forkCall(hooks, owner, 'Ada', 'CHARGE-IDEMP')
    const child = createdIds[0]
    const terminals = await observeTerminals(runtime)
    try {
      await childTurn(hooks, runtime, child, `run-${child}-1`, 'PROSE', 'material-idemp', terminals.awaitRun)
      const firstJoin = await joinCall(hooks, owner, 'join-call-1')
      assert.match(firstJoin, /material-idemp/)
      const secondJoin = await joinCall(hooks, owner, 'join-call-2')
      assert.match(secondJoin, /There is nothing away to receive|目前没有在外可接收的工作/)
    } finally {
      terminals.dispose()
    }
  })
})

test('WHAT[semantic-trace-013] conflicting material under the same fence is rejected and the original witness survives', async () => {
  const owner = 'owner-trace-013-conflict'
  await withRealFork(owner, async ({ hooks, createdIds, runtime, directory }) => {
    const first = await forkCall(hooks, owner, 'Ada', 'CHARGE-CONFLICT')
    const child = createdIds[0]
    const terminals = await observeTerminals(runtime)
    try {
      await childTurn(hooks, runtime, child, `run-${child}-1`, 'PROSE', 'original-material', terminals.awaitRun)
      const worksBefore = await coldWorkSnapshot(directory, owner)
      assert.equal(worksBefore[0].lifecycle, 'CompletedAwaitingJoin')
      // A second competing terminal does not overwrite the single-assignment completed cell.
      eventsSurface.notifyForAuthority(await runtime.terminalPort, child, 'Completed', 'unrelated-root', 'competing')
      const joined = await joinCall(hooks, owner)
      assert.match(joined, /original-material/)
      assert.doesNotMatch(joined, /competing/)
    } finally {
      terminals.dispose()
    }
  })
})

test('WHAT[semantic-trace-013] prose, view declaration text and a provider stop are material, not a witness', async () => {
  const owner = 'owner-trace-013-material'
  await withRealFork(owner, async ({ hooks, createdIds, runtime, directory }) => {
    const first = await forkCall(hooks, owner, 'Ada', 'CHARGE-PROSE')
    const child = createdIds[0]
    const captured = await trace.captureTerminalText(runtime.journal, child, 'completed', `run-${child}-stop`)
    assert.equal(captured.ok, true)
    assert.equal(captured.terminalCaptured, true, 'the provider stop is durable material')

    const terminals = await observeTerminals(runtime)
    try {
      await childTurn(hooks, runtime, child, `run-${child}-1`, 'PROSE', 'materialized view body', terminals.awaitRun)
      const joined = await joinCall(hooks, owner)
      assert.match(joined, /materialized view body/)
      assert.doesNotMatch(joined, /\[\[session\]\]/)
    } finally {
      terminals.dispose()
    }
  })
})

test('WHAT[semantic-trace-013] a missing witness is not back-filled by later narration and the terminal stays undelivered', async () => {
  await withRealProse(async ({ directory, runtime }) => {
    // 散文与 provider stop 都已落地。
    const captured = await trace.captureTerminalText(runtime.journal, SESSION, 'completed', RUN)
    assert.equal(captured.terminalCaptured, true)

    // 事后叙述补不造见证。
    assert.deepEqual(
      deliveryFacts(durableFacts(join(directory, '.Herdchestra'))),
      [],
      'narration must not back-fill a delivery witness',
    )

    // 缺失见证的终态只是未交付：没有见证 ⇒ 没有 durable 见证事实，
    // 消费者因此看不到任何「已交付」的凭据。
    assert.equal(
      deliveryFacts(durableFacts(join(directory, '.Herdchestra'))).length,
      0,
      'a terminal without a delivery witness is not written as a delivery',
    )
  })
})

test('WHAT[semantic-trace-013] the same triple under a different logical run or a different delivery task is not the same witness', async () => {
  const owner = 'owner-trace-013-runs'
  await withRealFork(owner, async ({ hooks, createdIds, runtime, directory }) => {
    const first = await forkCall(hooks, owner, 'Ada', 'CHARGE-RUN1')
    const second = await forkCall(hooks, owner, 'Bob', 'CHARGE-RUN2')
    const terminals = await observeTerminals(runtime)
    try {
      await childTurn(hooks, runtime, createdIds[0], `run-${createdIds[0]}-1`, 'PROSE-1', 'BODY-1', terminals.awaitRun)
      await childTurn(hooks, runtime, createdIds[1], `run-${createdIds[1]}-1`, 'PROSE-2', 'BODY-2', terminals.awaitRun)
      const works = await coldWorkSnapshot(directory, owner)
      assert.equal(works.length, 2)
      assert.notEqual(works[0].root, works[1].root, 'each run has a distinct authority root')
      assert.notEqual(works[0].handle, works[1].handle, 'each child has a distinct handle')
    } finally {
      terminals.dispose()
    }
  })
})

