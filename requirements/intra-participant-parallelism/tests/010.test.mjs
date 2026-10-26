// 本文件的旧断言（"重启后 resume 该 child，随后它完成交付并被 join 收走"）测的是
// 已退役语义：它成立的前提是 load 阶段把遗留的子 run 作废（ChildWorkVoided）。
// 现行契约（crash-reconciliation [018] 经 60812f575 修订）把遗留的子 run 保持为
// 活跃且可见，load 阶段只作废 fission group 本身；因此那条"续做→完成→join"时序
// 在现行架构下不再有对应物，相应断言已删除。保留的是本条款真正要证的部分：
// 中断的裂变被记为中断，且不自动隐式恢复。
import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import test from 'node:test'
import { acceptAuthorityRoot, openIncumbency, withRestartablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

// durable 事件就是消费者读到的事实：从 journal 的 .ndjson 逐条读回。
const durableFacts = (directory) => {
  const events = join(directory, '.Herdchestra', 'wanxiangshu', 'events')
  return readdirSync(events)
    .filter((name) => name.endsWith('.ndjson'))
    .sort()
    .flatMap((name) =>
      readFileSync(join(events, name), 'utf8')
        .split('\n')
        .filter(Boolean)
        .map((line) => JSON.parse(line).payload?.Fact)
        .filter(Boolean),
    )
}

const fissionFacts = (facts) =>
  facts.filter((fact) => fact?.[0] === 'Agent' && fact?.[1]?.[0] === 'Fission').map((fact) => fact[1][1])

const activateDurability = async (hooks, suffix) => {
  const sessionID = `activation-${suffix}`
  await hooks['chat.message'](
    { sessionID, agent: 'manager' },
    {
      message: { id: `msg-${sessionID}`, sessionID, role: 'user', agent: 'manager' },
      parts: [{ id: `part-${sessionID}`, type: 'text', text: 'activate durable recovery' }],
    },
  )
}

test('WHAT[intra-participant-parallelism-010] an interrupted Fission group is settled as failed at load and is never implicitly resumed', async () => {
  await withRestartablePlugin(async (start, directory, { stop, withRuntime, createdIds, prompts }) => {
    const hooks1 = await start()
    const managerSession = 'ses-fission-restart-manager'
    await withRuntime(async (runtime) => {
      await acceptAuthorityRoot(runtime, managerSession, 'manager')
      await openIncumbency(runtime, managerSession)
    })

    // 派工是编程面成员（宿主 fork 工具已整体退役）：
    // `this.fork(payload, extra = [])`。
    const forked = await hooks1.tool['todowrite-for-manager'].execute(
      {
        name: 'restart-fork',
        intent: '等待本次派工回执，超时后保留原派工状态。',
        program:
          'class Js extends JsProgram { async run() { return await this.fork({ name: "ruling-materials", charge: "collect seven materials" }); } }',
      },
      { sessionID: managerSession, agent: 'manager' },
    )
    assert.match(forked, /ok = true/)
    assert.match(forked, /name = "ruling-materials"/)
    assert.match(forked, /outcome = "created"/)
    const engineerSession = createdIds[0]

    const fissioned = await hooks1.tool.fission.execute(
      { prompts: ['lane 1', 'lane 2'] },
      { sessionID: engineerSession, agent: 'engineer', callID: 'call-fission-orphan', messageID: 'run-pre-crash' },
    )
    assert.match(fissioned, /status\s*=\s*"fissioned"/i)

    // 崩溃前的现场：裂变已开立，lane 已被派出。
    const before = fissionFacts(durableFacts(directory))
    const admitted = before.find((fact) => fact[0] === 'FissionAdmitted')
    assert.ok(admitted, 'the fission group must be admitted before the crash')
    assert.equal(admitted[1].LaneCount, 2)
    const promptsBefore = prompts.length

    // Stop incarnation 1 and boot incarnation 2: load-phase recovery settles
    // the orphaned open Fission group.
    await stop(hooks1)
    const hooks2 = await start()
    await activateDurability(hooks2, 'fission-restart')
    await new Promise((resolve) => setTimeout(resolve, 300))

    // 消费者可见的事实：同一个 group 被记为中断（失败），而不是被静默抹掉。
    const after = fissionFacts(durableFacts(directory))
    const failed = after.find((fact) => fact[0] === 'FissionFailed')
    assert.ok(failed, 'the interrupted fission group must be settled as failed at load')
    assert.equal(failed[1].GroupId, admitted[1].GroupId, 'the same group is settled, not a new one')
    assert.equal(failed[1].Reason, 'process_interrupted', 'an interrupted fission is recorded as interrupted')

    // 不自动隐式恢复：load 阶段不重放任何命令，也不为 lane 重新派工。
    assert.equal(prompts.length, promptsBefore, 'the load phase must not resume the interrupted group')

    // 结算后的 group 不再是活跃 group：join 不再等它，horizon 不再报它在外。
    const joined = await Promise.race([
      hooks2.tool.join.execute({}, { sessionID: managerSession, agent: 'manager' }),
      new Promise((_, reject) => setTimeout(() => reject(new Error('join hung on a settled fission group')), 5000)),
    ])
    assert.match(String(joined), /ruling-materials.*已被作废|voided/i, 'a settled fission group is reported as voided')

    const horizon = await Promise.race([
      hooks2.tool.horizon.execute({}, { sessionID: managerSession, agent: 'manager' }),
      new Promise((_, reject) => setTimeout(() => reject(new Error('horizon hung on a settled fission group')), 5000)),
    ])
    assert.doesNotMatch(String(horizon), /ruling-materials/, 'a settled fission group is not away anymore')
  })
})

test.todo('WHAT[intra-participant-parallelism-010] actual durable Fission facts reconstruct group membership and replacement after restart, recording interruption without automatically restarting lanes or guessing similar Host sessions (GAP-158)')

