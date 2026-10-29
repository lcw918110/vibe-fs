import assert from 'node:assert/strict'
import { realpathSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import test from 'node:test'
import { acceptAuthorityRoot, awaitPrompted, injectAcceptedAssessment, openIncumbency, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { permissionOfToolName } from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'
import { rolePermissionRules } from '../../../dist/OpenCode/Tools/ToolSurface.js'
import { generateRole } from '../../../dist/Repository/Programming/Js/GeneratorSurface.js'
import { manager, plan } from '../../../dist/Foundation/RolesSurface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as workspaceEvents from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'

// WHAT[work-019]: 执行入口按 sequence 与当前事实收口。无任期时全部拒绝，
// 评审未接纳时只读放行而派工拒绝，评审接纳后固定 DevOps 派工恢复，确认先于
// 评审（非法事实）fail closed。工具名→权限映射按角色解析 deliver，
// 不按列表顺序，也不按后缀猜测。

const denied = /当前不可用|不可用|not available right now|not available to|denied-task-state/i
const devopsDispatchDenied = /尚未被系统接纳|不得向 DevOps 派发|assessment.*not.*accepted|not allowed until|assessment-pending-for-devops/i
const bound = (sessionID, callID) => ({ sessionID, agent: 'manager', callID, messageID: `msg-${callID}`, providerRunID: `run-${callID}` })

const storeOf = async (directory) => workspaceEvents.acquire(await journalSurface.JournalSurface_initializeStateRoot(directory))

// 派工是编程面成员，不是宿主工具：`this.fork(payload, extra = [])` /
// `this.resume(payload, extra = [])`。宿主不再注册 fork/resume，
// 拒绝与放行都必须从成员调用的返回信封里观察。
let programSequence = 0
const runManagerProgram = async (hooks, sessionID, body) =>
  String(
    await hooks.tool['todowrite-for-manager'].execute(
      { program: `class Js extends JsProgram { async run() { ${body} } }`,
          name: 'manager-019-' + (++programSequence),
          intent: 'Observe this manager business result within one request deadline.',
        },
      { sessionID, agent: 'manager' },
    ),
  )

test('WHAT[work-019] a fresh Manager can observe and join before its first Work review', async () => {
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const sessionID = 'ses-fresh-manager-observation-019'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    const stateRoot = await journalSurface.JournalSurface_initializeStateRoot(directory)
    assert.equal(lifecycle.inspected(stateRoot, sessionID), undefined)

    const horizon = String(await hooks.tool.horizon.execute({}, bound(sessionID, 'fresh-horizon')))
    assert.doesNotMatch(horizon, denied)
    assert.match(horizon, /devops/)
    const joined = String(await hooks.tool.join.execute({}, bound(sessionID, 'fresh-join')))
    assert.doesNotMatch(joined, denied)
    assert.match(joined, /nothing away to receive|nothing to join|没有|无可等待/i)

    const placed = await runManagerProgram(hooks, sessionID,
      'return await this.fork({ calling: "engineer", name: "Ada", charge: "Inspect the requested behavior" }, []);')
    assert.match(placed, /ok = true/)
    const child = createdIds.at(-1)
    await awaitPrompted(child)
    assert.match(String(await hooks.tool.horizon.execute({}, bound(sessionID, 'child-horizon'))), /Ada/)
    assert.equal(lifecycle.inspected(stateRoot, sessionID), undefined,
      'observing and joining must not fabricate a Work incumbency')
  })
})

test('WHAT[work-019] observation and join still reject a caller without established authority', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds) => {
    for (const name of ['horizon', 'join']) {
      const result = String(await hooks.tool[name].execute({}, bound('ses-unestablished-observation', name)))
      assert.match(result, /authority|established|身份|权威|确立/i)
    }
    assert.deepEqual(createdIds, [])
  })
})

test('WHAT[work-019] a fresh Manager can inspect before its first review and that review opens work synchronously', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ses-fresh-manager-019'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    writeFileSync(join(directory, 'bootstrap.txt'), 'bootstrap-proof')
    const inspect = await runManagerProgram(hooks, sessionID,
      'return await this.reviewOnly.cat("bootstrap.txt");')
    assert.match(inspect, /bootstrap-proof/)
    assert.doesNotMatch(inspect, /REVIEW_ONLY/)

    const reviewed = String(await hooks.tool.review.execute(
      { findings: [{ key: 'repair', value: 'repair the observed regression' }] },
      bound(sessionID, 'fresh-review'),
    ))
    assert.match(reviewed, /Reviewed/)
    assert.equal(lifecycle.inspected(await journalSurface.JournalSurface_initializeStateRoot(directory), sessionID).Reviewed, true)
    const closed = await runManagerProgram(hooks, sessionID,
      'return await this.reviewOnly.cat("bootstrap.txt");')
    assert.match(closed, /REVIEW_ONLY/)
  })
})



// 执行入口门禁：tool.execute.before 只对评审专用工具（todowrite-for-manager）与跨角色
// 绕路工具（todowrite-for-engineer/todowrite-for-devops）生效；office 工具
// （review/deliver）的准入/拒绝由 ToolRegistry 执行入口决定。
const assertBeforeDenied = async (hooks, sessionID, tool, callID) => {
  await assert.rejects(
    () => hooks['tool.execute.before']({ tool, sessionID, callID }, { args: {} }),
    /not permitted under current manager capability facts|refused on the review gate/i,
  )
}

test('WHAT[work-019] 没有任期时 review 与 deliver 在执行入口被拒绝', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-no-tenure'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    // 无活跃任期：office 工具在 ToolRegistry 执行入口 fail closed。
    const reviewResult = await runManagerProgram(
      hooks,
      sessionID,
      'return await this.review([]);',
    )
    assert.match(reviewResult, /code = "tool\/review\/binding-unavailable"|review-unavailable/)
    assert.equal(hooks.tool.deliver, undefined)
    assert.equal(permissionOfToolName(manager, 'deliver') ?? null, null)
    assert.equal(createdIds.length, 0)
  })
})

test('WHAT[work-019] 调用方权威确立之前，派工成员在执行入口 fail closed 且不创建子会话', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-no-authority'
    // 未确立权威：成员调用被真实执行入口拒绝。
    // 这条拒绝路径是 `tool/registry/denied-unestablished`，它只渲染散文、不带稳定
    // code 字段，所以可断言到的事实只有三层：调用以返回值结束（不是抛错）、
    // 没有成功信封（`ok = true` / `name` / `outcome = "created"` 都不出现）、
    // 零子会话。断言散文措辞会把测试绑死在语言上，故此处不断言措辞。
    let threw = null
    let forkResult = ''
    try {
      forkResult = await runManagerProgram(
        hooks,
        sessionID,
        'return await this.fork({ calling: "engineer", name: "Ada", charge: "look" }, { 1: null });',
      )
    } catch (error) {
      threw = error
    }
    assert.equal(threw, null, 'the refused member call must answer with a value, not throw')
    assert.doesNotMatch(forkResult, /ok = true/)
    assert.doesNotMatch(forkResult, /name = "Ada"/)
    assert.doesNotMatch(forkResult, /outcome = "created"/)
    assert.equal(createdIds.length, 0)
  })
})

test('WHAT[work-019] 有任期且评审尚未接纳时只读与观察放行，派工和 deliver 在执行入口被拒绝', async () => {
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const sessionID = 'ses-unreviewed'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    // deliver binds the invocation to the current physical user message.
    // Without one the call fails before Algorithm E3 can answer.
    await hooks['chat.message']({ sessionID, agent: 'manager' }, {
      message: { id: 'user-root', role: 'user', agent: 'manager', model: { providerID: 'fixture', modelID: 'manager-model' } },
      parts: [{ type: 'text', text: 'Deliver the requested behavior and verification.' }],
    })
    // 评审未接纳：todowrite-for-manager 评审专用只读工具放行（before 门禁通过）。
    await hooks['tool.execute.before']({ tool: 'todowrite-for-manager', sessionID, callID: 'call-js' }, { args: { path: 'x' } })
    // 跨角色绕路工具在评审门前被拒绝。
    await assertBeforeDenied(hooks, sessionID, 'todowrite-for-engineer', 'call-js-engineer')
    await assertBeforeDenied(hooks, sessionID, 'todowrite-for-devops', 'call-js-devops')
    // 向固定 DevOps 派工在评审未接纳时被拒（数据信封，非异常）。
    const forkDevops = await runManagerProgram(
      hooks,
      sessionID,
      'return await this.fork({ calling: "devops", name: "devops", charge: "look" }, []);',
    )
    assert.match(forkDevops, devopsDispatchDenied)
    const resumeDevops = await runManagerProgram(
      hooks,
      sessionID,
      'return await this.resume({ name: "devops", charge: "look" }, []);',
    )
    assert.match(resumeDevops, devopsDispatchDenied)
    const before = lifecycle.workView(await storeOf(directory), sessionID)
    assert.equal(hooks.tool.deliver, undefined)
    const after = lifecycle.workView(await storeOf(directory), sessionID)
    assert.equal(after.delivered, false)
    assert.equal(after.active, before.active)
    assert.equal(createdIds.length, 0)
  })
})

test('WHAT[work-019] 评审已接纳且尚未确认时 read 与固定 DevOps 派工在执行入口放行', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-accepted'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    await injectAcceptedAssessment(runtime, sessionID)
    // 评审接纳后：向固定 DevOps 派工恢复。
    const resumeResult = await runManagerProgram(
      hooks,
      sessionID,
      'return await this.resume({ name: "devops", charge: "look" }, []);',
    )
    assert.match(resumeResult, /ok = true/)
    assert.match(resumeResult, /outcome = "created"/)
    assert.equal(createdIds.length, 1)
  })
})

test('WHAT[work-019] 单笔交付无需确认仪式且不写下确认事实', async () => {
  await withExecutablePlugin(async (_hooks, directory, createdIds, runtime) => {
    const sessionID = 'ses-confirm-first'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    const store = await storeOf(directory)
    const before = lifecycle.workView(store, sessionID)
    const after = lifecycle.workView(store, sessionID)
    assert.equal(after.reviewed, '')
    assert.equal(after.active, before.active)
    assert.equal(after.delivered, before.delivered)
    assert.equal(after.sequence, before.sequence)
    assert.equal(rolePermissionRules('manager').review, 'allow')
    assert.equal(createdIds.length, 0)
  })
})

const permissionName = (value) => (value == null ? null : JSON.parse(JSON.stringify(value)))

test('WHAT[work-019] deliver is not a lifecycle permission for Manager or Plan', () => {
  assert.equal(permissionName(permissionOfToolName(manager, 'deliver')), null)
  assert.equal(permissionName(permissionOfToolName(plan, 'deliver')), null)

  for (const role of [manager, plan]) {
    const unknown = permissionName(permissionOfToolName(role, 'not-a-deliver'))
    assert.notEqual(unknown, 'Finality')
    assert.notEqual(unknown, 'Deliver')
    assert.equal(unknown, null)
  }
})

test('WHAT[work-019] 工具名→权限映射维护在唯一目录，禁止按后缀或启发式规则推测授权', () => {
  // Manager 的 review 解析为 ReviewAssessment，Plan 的 review 解析为 Review。
  assert.equal(permissionName(permissionOfToolName(manager, 'review')), 'ReviewAssessment')
  assert.equal(permissionName(permissionOfToolName(plan, 'review')), 'Review')
  // 未知名字不得按后缀（js-）或启发式规则放行。
  assert.equal(permissionName(permissionOfToolName(manager, 'todowrite-for-manager')), null)
  assert.equal(permissionName(permissionOfToolName(manager, 'js-unknown')), null)
  // 直接文件工具已退役：Manager 的 read 不再出现在任何投影里，默认 `*` 为 deny，
  // 评审只读由 todowrite-for-manager 的成员承载。
  assert.equal(permissionName(permissionOfToolName(manager, 'read')), null)
  assert.equal(rolePermissionRules('manager')['*'], 'deny')
  assert.equal('read' in rolePermissionRules('manager'), false)
})

test('WHAT[work-019] 评审未接纳前 Manager 的写工具在执行门投影上被拒绝', () => {
  const rules = rolePermissionRules('manager')
  // 直接 write/edit 工具已退役，规则表默认 deny；Manager 的编程面也不携带写成员。
  assert.equal(rules['*'], 'deny')
  assert.equal('edit' in rules, false)
  assert.equal('write' in rules, false)
  const surface = generateRole('manager', 'en')
  assert.equal(surface.baseClassSource.includes('async write('), false)
  assert.equal(surface.baseClassSource.includes('async edit('), false)
})

