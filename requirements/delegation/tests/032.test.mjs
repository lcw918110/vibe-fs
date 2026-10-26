import assert from 'node:assert/strict'
import test from 'node:test'
import { permissions, isAllowed } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'
import * as sync from '../../../dist/Execution/Delegation/SyncDelegate/Surface.js'
import {
  agentIdOf,
  childTurn,
  coldWorkSnapshot,
  forkCall,
  joinCall,
  observeTerminals,
  workLifecycleOf,
  withRealFork,
} from './support/real-fork.mjs'
import { injectAcceptedAssessment } from '../../verification-system/tests/support/plugin-fixture.mjs'

// 拒绝码取自数据信封的 `code` 字段（渲染后的 TOML `[data]` 表）。按码断言，
// 不匹配散文，也不用 `ok = false` 这种「任何失败都能满足」的宽条件。
const refusalCodeOf = (result) => {
  const match = new RegExp('^code = "(.*)"$', 'm').exec(String(result))
  return match === null ? null : match[1]
}

test('WHAT[delegation-032] office capability projection denies cross-role dispatch while retaining each role’s own work', () => {
  for (const [role, denied, allowed] of [
    ['engineer', ['Fork', 'Resume', 'Exec', 'Pty', 'Join', 'Horizon'], ['Read', 'Write', 'Edit', 'Glob', 'Grep', 'Move', 'Remove', 'BashHoneypot', 'Fission']],
    ['devops', ['Fork', 'Resume', 'Pty', 'Fission'], ['Read', 'Write', 'Edit', 'Glob', 'Grep', 'Move', 'Remove', 'Exec', 'Join', 'Horizon']],
  ]) {
    const actual = permissions(role)
    for (const capability of denied) {
      assert.equal(actual.includes(capability), false)
      assert.equal(isAllowed(role, capability), false)
    }
    for (const capability of allowed) {
      assert.equal(actual.includes(capability), true)
      assert.equal(isAllowed(role, capability), true)
    }
  }
})

test('WHAT[delegation-032] SyncDelegate selects standard Engineer capabilities without DevOps authority', () => {
  const vocabulary = sync.vocabulary('Engineer', 'Fast', 'sphinx-scope')
  assert.equal(vocabulary.role, 'engineer')
  assert.equal(vocabulary.agent, 'engineer')
  assert.equal(vocabulary.scope, 'sphinx-scope')
  assert.equal(isAllowed(vocabulary.agent, 'Write'), true)
  assert.equal(isAllowed(vocabulary.agent, 'Fission'), true)
  assert.equal(isAllowed(vocabulary.agent, 'Exec'), false)
})

// 真实交付路径：Manager 的差遣经由真实插件（`todowrite-for-manager` 的
// `this.fork`），拒绝与放行都由生产自己裁决。测试只观察生产留下的事实：
// 宿主创建的子会话、宿主发出的 prompt、冷 journal 折出的 durable 工作单元。
//
// 拒绝必须落在**角色边界**上，不能是别的门：同一条调用在「评估未落」时
// 停在 `assessment-pending-for-devops`（生命周期门），在「评估已落」时改
// 停在 `unknown-calling`（角色门：Manager 只能差遣 Engineer，
// `managerForkableRoles = [ Role.Engineer ]`）。两次拒绝码不同，说明
// 后一次确实是角色边界而不是顺手撞上的另一个门。
test('WHAT[delegation-032] Manager fork refuses a DevOps calling at the role boundary and dispatches nothing, while the Engineer calling still delivers for real', async () => {
  const owner = 'owner-cross-role-dispatch'
  await withRealFork(owner, async ({ hooks, createdIds, directory, runtime }) => {
    const terminals = await observeTerminals(runtime)
    try {
      // 对照一：评估未落时，同一条 DevOps 差遣停在生命周期门上。
      const beforeAssessment = await hooks.tool['todowrite-for-manager'].execute(
        {
          program:
            'class Js extends JsProgram { async run() { return await this.fork({ name: "devops", charge: "DEVOPS-CHARGE" }, { 1: null }); } }',
          name: 'fork-devops-before-review',
          intent: 'Observe the role-bound delegation result without dispatching unauthorized work.'
        },
        { sessionID: owner, agent: 'manager', callID: 'call-fork-devops-pending', messageID: 'msg-call-fork-devops-pending' },
      )
      assert.equal(
        refusalCodeOf(beforeAssessment),
        'assessment-pending-for-devops',
        `without an accepted assessment the DevOps dispatch stops at the lifecycle gate: ${beforeAssessment}`,
      )
      assert.equal(createdIds.length, 0, 'a refused DevOps dispatch must not create a child')

      // Manager 持 ReviewAssessment：DevOps 差遣的前置评估已就位，
      // 于是拒绝只能来自「Manager 不得差遣 DevOps」这一条角色边界。
      await injectAcceptedAssessment(runtime, owner)

      // 先证明同一条真实路径上的放行：Engineer 差遣真的派发。
      const engineerFork = await forkCall(hooks, owner, 'Ada', 'ENGINEER-CHARGE', 'call-fork-ada')
      assert.match(engineerFork, /Ada/, engineerFork)
      const engineerAgent = agentIdOf(engineerFork)
      assert.equal(createdIds.length, 1, 'the engineer fork creates exactly one child')
      const child = createdIds[0]
      const promptsAfterEngineer = runtime.prompts.length
      assert.ok(promptsAfterEngineer >= 1, 'the engineer fork dispatches its charge')
      assert.equal(
        runtime.prompts.some((prompt) => JSON.stringify(prompt).includes('ENGINEER-CHARGE')),
        true,
        'the engineer charge reaches the child',
      )

      // Manager 不得差遣 DevOps：真实插件上的同一条 `this.fork` 成员。
      const devopsFork = await hooks.tool['todowrite-for-manager'].execute(
        {
          program:
            'class Js extends JsProgram { async run() { return await this.fork({ name: "devops", charge: "DEVOPS-CHARGE" }, { 1: null }); } }',
          name: 'fork-devops-after-review',
          intent: 'Observe the role-bound delegation result without dispatching unauthorized work.'
        },
        { sessionID: owner, agent: 'manager', callID: 'call-fork-devops', messageID: 'msg-call-fork-devops' },
      )
      assert.equal(
        refusalCodeOf(devopsFork),
        'unknown-calling',
        `the DevOps calling is refused at the role boundary, not at another gate: ${devopsFork}`,
      )
      assert.notEqual(
        refusalCodeOf(devopsFork),
        'assessment-pending-for-devops',
        'the assessment is in place, so the refusal is not the lifecycle gate',
      )
      assert.doesNotMatch(devopsFork, /agentId = "/, 'a refused cross-role fork must not place an agent')
      assert.equal(createdIds.length, 1, 'a refused cross-role fork must not create a child')
      assert.equal(runtime.prompts.length, promptsAfterEngineer, 'a refused cross-role fork must not dispatch a prompt')
      assert.equal(
        runtime.prompts.some((prompt) => JSON.stringify(prompt).includes('DEVOPS-CHARGE')),
        false,
        'the refused charge must reach no session',
      )

      // durable 侧：冷 journal 折出的工作单元里只有 Engineer 那一个。
      const cold = await coldWorkSnapshot(directory, owner)
      assert.deepEqual(
        cold.map((unit) => unit.byname),
        ['Ada'],
        'a refused cross-role fork must leave no durable handle',
      )

      // 合法的那份工作仍走完真实交付：生产落 durable 交付见证，生命周期随之推进。
      const root = runtime.messages.filter((message) => message.id?.startsWith(`msg-${child}-`)).at(-1).id
      assert.equal(workLifecycleOf(runtime, owner, engineerAgent, child, root), 'Active')
      await childTurn(hooks, runtime, child, `run-${child}-1`, 'ENGINEER PROSE', 'ENGINEER-DONE', terminals.awaitRun)
      assert.equal(workLifecycleOf(runtime, owner, engineerAgent, child, root), 'CompletedAwaitingJoin')

      const joined = await joinCall(hooks, owner)
      assert.match(joined, /ENGINEER-DONE/, joined)
      assert.match(joined, /Ada/)
      assert.match(joined, /ENGINEER PROSE/)
    } finally {
      terminals.dispose()
    }
  })
})
