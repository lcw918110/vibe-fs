import assert from 'node:assert/strict'
import test from 'node:test'
import { parse as parseToml } from 'smol-toml'
import { acceptAuthorityRoot, injectAcceptedAssessment, openIncumbency, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { rolePermissionRules } from '../../../dist/OpenCode/Tools/ToolSurface.js'

// WHAT[capability-enforcement-024]: fork 与 resume 权能分离 — Manager fork
// 仅准入 Engineer，严禁 fork DevOps；固定 DevOps 仅允许经 resume 续做且同一
// 道路至多一个活跃工作单元。委派成员走编程面（`this.fork` / `this.resume`），
// 宿主不再注册 fork / resume 工具；成员返回数据信封（`{ ok, ... }`），拒绝时
// 给稳定 kebab 码，成功时给 `ok = true` 与 `outcome`。门禁在真实工具执行入口
// （ToolRegistry office gate）与工具体内部双重生效，不依赖任何 relay 私有表面。

// DevOps 评审门拒绝码：评审尚未接纳前不得向固定 DevOps 派发。
const devopsReviewGateCode = 'assessment-pending-for-devops'

let programCallCounter = 0

/**
 * Run one Manager program through the `todowrite-for-manager` tool and read
 * back the delegation member's data envelope. The delegation members answer
 * with data, never with rendered prose: a refusal carries a stable kebab
 * `code`, a success carries `ok = true` plus `outcome`.
 */
const runManagerProgram = async (hooks, sessionID, body) => {
  programCallCounter += 1
  const callID = 'call-024-' + programCallCounter
  const rendered = String(
    await hooks.tool['todowrite-for-manager'].execute(
      {
        program: 'class Js extends JsProgram { async run() { ' + body + ' } }',
          name: callID,
          intent: 'Wait for the manager delegation receipt under one absolute deadline.'
        },
      { sessionID, agent: 'manager', callID, messageID: 'msg-' + callID },
    ),
  )
  const parsed = parseToml(rendered)
  const data = parsed.data ?? parsed
  return { rendered, data }
}

test('WHAT[capability-enforcement-024] FORK_manager_fork_rejects_devops_calling', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    await acceptAuthorityRoot(runtime, 'ses-fork-devops', 'manager')
    await openIncumbency(runtime, 'ses-fork-devops')
    const { data } = await runManagerProgram(
      hooks,
      'ses-fork-devops',
      'return await this.fork({ calling: "devops", name: "Op", charge: "run tests" }, []);',
    )
    // 显式 calling=devops：Manager 只能 fork Engineer，不能 fork DevOps。
    // 结构化拒绝：显式 calling 与由 name 派生出的 calling 冲突。
    assert.equal(data.ok, false, 'the fork member refuses with a data envelope')
    assert.equal(data.code, 'calling-conflict')
    assert.equal(createdIds.length, 0)
  })
})

test('WHAT[capability-enforcement-024] FORK_devops_dispatch_denied_until_review_accepted', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-fork-pre'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    // 评审未接纳前：编程面 fork name=devops 与 resume devops 都被 DevOps
    // 评审门拒绝，零子会话、零 prompt（WHAT[026]：拦截发生在任何持久化副作用之前）。
    const forkDevops = await runManagerProgram(
      hooks,
      sessionID,
      'return await this.fork({ name: "devops", charge: "run tests" }, []);',
    )
    assert.equal(forkDevops.data.ok, false)
    assert.equal(forkDevops.data.code, devopsReviewGateCode)
    assert.equal(createdIds.length, 0)

    const resumeDevops = await runManagerProgram(
      hooks,
      sessionID,
      'return await this.resume({ name: "devops", charge: "run tests" }, []);',
    )
    assert.equal(resumeDevops.data.ok, false)
    assert.equal(resumeDevops.data.code, devopsReviewGateCode)
    assert.equal(createdIds.length, 0)

    // 评审接纳后，固定 DevOps 经 resume 正常派工（同一道路一个活跃单元）。
    await injectAcceptedAssessment(runtime, sessionID)
    const admitted = await runManagerProgram(
      hooks,
      sessionID,
      'return await this.resume({ name: "devops", charge: "run tests" }, []);',
    )
    assert.equal(admitted.data.ok, true)
    assert.ok(['created', 'nudged'].includes(admitted.data.outcome), admitted.rendered)
    assert.equal(createdIds.length, 1)
  })
})

test('WHAT[capability-enforcement-024] FORK_manager_fork_engineer_admitted_after_review', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-fork-eng'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    await injectAcceptedAssessment(runtime, sessionID)

    const { data } = await runManagerProgram(
      hooks,
      sessionID,
      'return await this.fork({ calling: "engineer", name: "Op", charge: "run tests" }, []);',
    )
    assert.equal(data.ok, true)
    assert.equal(data.name, 'Op')
    assert.equal(data.outcome, 'created')
    assert.equal(createdIds.length, 1)
  })
})

// 静态门禁：fork/resume 在 Manager 角色层固定 allow，Plan 角色 deny；
// todowrite-for-devops 对 Manager 的跨角色绕路调用在角色层 deny。
test('WHAT[capability-enforcement-024] role_permission_rules_fork_separation', () => {
  const managerRules = rolePermissionRules('manager')
  assert.equal(managerRules.fork, 'allow')
  assert.equal(managerRules.resume, 'allow')
  assert.equal(managerRules['todowrite-for-devops'], 'deny')
  const planRules = rolePermissionRules('plan')
  assert.equal(planRules.fork, 'deny')
  assert.equal(planRules.resume, 'deny')
})
