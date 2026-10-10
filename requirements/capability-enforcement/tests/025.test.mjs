import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { parse as parseToml } from 'smol-toml'
import './support/manager-programming.mjs'
import * as office from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'
import { rolePermissionRules, reviewToolPermissions } from '../../../dist/OpenCode/Tools/ToolSurface.js'
import { rolePredicate } from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'
import {
  acceptAuthorityRoot,
  openIncumbency,
  injectAcceptedAssessment,
  withExecutablePlugin,
  withRestartablePlugin,
} from '../../verification-system/tests/support/plugin-fixture.mjs'

const MANAGER_REVIEW_TOOL = 'js-manager'
const RETIRED_REVIEW_TOOLS = ['read-manager', 'glob-manager', 'grep-manager']
const managerFacts = (hasActiveIncumbency, hasAssessment, isFinalIncumbent, cleanupBlockerDigest) =>
  office.managerFacts(hasActiveIncumbency, hasAssessment, isFinalIncumbent, cleanupBlockerDigest)
const isAllowedForManagerFacts = (facts, permission) => office.managerFactsAllowed(facts, permission)

test('WHAT[capability-enforcement-025] P01_unaccepted_review_manager_static_permissions_include_read_glob_grep', () => {
  const perms = office.permissions('manager')
  assert.ok(perms.includes('Read'), 'Manager static permissions must include Read')
  assert.ok(perms.includes('Glob'), 'Manager static permissions must include Glob')
  assert.ok(perms.includes('Grep'), 'Manager static permissions must include Grep')
})

test('WHAT[capability-enforcement-025] P02_review_accepted_facts_exclude_review_readonly_capabilities', () => {
  // OfficeCapabilitySurface 构造已接纳评审事实（HasActiveIncumbency=true, HasAssessment=true）
  const facts = managerFacts(true, true, false, undefined)

  // 评审接纳后，只读能力 Read/Glob/Grep 均不被允许
  assert.equal(isAllowedForManagerFacts(facts, 'Read'), false, 'Read must be denied after assessment accepted')
  assert.equal(isAllowedForManagerFacts(facts, 'Glob'), false, 'Glob must be denied after assessment accepted')
  assert.equal(isAllowedForManagerFacts(facts, 'Grep'), false, 'Grep must be denied after assessment accepted')

  // 评审接纳后，Join/Fork 等管理与编排权限仍被允许
  assert.equal(isAllowedForManagerFacts(facts, 'Fork'), true, 'Fork must remain allowed after assessment accepted')
  assert.equal(isAllowedForManagerFacts(facts, 'Resume'), true, 'Resume must remain allowed after assessment accepted')
  assert.equal(isAllowedForManagerFacts(facts, 'Join'), true, 'Join must remain allowed after assessment accepted')
  assert.equal(isAllowedForManagerFacts(facts, 'Horizon'), true, 'Horizon must remain allowed after assessment accepted')
  assert.equal(isAllowedForManagerFacts(facts, 'Finality'), true, 'Finality must remain allowed after assessment accepted')
})

test('WHAT[capability-enforcement-025] P08_manager_native_read_glob_grep_projected_as_deny', () => {
  const managerPerms = rolePermissionRules('manager')
  assert.equal(managerPerms.read, 'deny', 'Native read must be projected as deny for Manager')
  assert.equal(managerPerms.glob, 'deny', 'Native glob must be projected as deny for Manager')
  assert.equal(managerPerms.grep, 'deny', 'Native grep must be projected as deny for Manager')
})

test('WHAT[capability-enforcement-025] P09_manager_allows_sole_review_tool_and_retired_tools_not_admitted_to_any_role', () => {
  const managerPerms = rolePermissionRules('manager')
  assert.equal(managerPerms['js-engineer'], 'deny', 'Manager must deny js-engineer')
  assert.equal(managerPerms['js-devops'], 'deny', 'Manager must deny js-devops')
  assert.equal(rolePredicate('js-engineer', 'manager'), false, 'Role predicate must deny js-engineer for Manager')
  assert.equal(rolePredicate('js-devops', 'manager'), false, 'Role predicate must deny js-devops for Manager')
  assert.equal(managerPerms[MANAGER_REVIEW_TOOL], 'allow', 'Manager must allow the sole review tool js-manager')

  for (const tool of RETIRED_REVIEW_TOOLS) {
    for (const role of ['manager', 'engineer', 'devops']) {
      assert.equal(rolePredicate(tool, role), false, `Retired tool ${tool} must not be admitted to ${role}`)
    }
  }
})

test('WHAT[capability-enforcement-025] P10_sole_review_tool_allowed_and_retired_tools_absent_from_manager_surface', () => {
  const managerPerms = rolePermissionRules('manager')
  assert.equal(managerPerms[MANAGER_REVIEW_TOOL], 'allow', 'Manager request projection must allow the sole review tool js-manager')
  for (const tool of RETIRED_REVIEW_TOOLS) {
    assert.equal(tool in managerPerms, false, `Retired tool ${tool} must no longer appear in the Manager tool surface`)
  }
})

test('WHAT[capability-enforcement-025] P05_no_active_incumbency_and_no_assessment_denies_review_readonly_admission', () => {
  // 无 active incumbency：当前事实收口为空集，js-manager 依赖的只读能力全部 fail closed
  const noIncumbency = managerFacts(false, false, false, undefined)
  for (const permission of ['Read', 'Glob', 'Grep']) {
    assert.equal(
      isAllowedForManagerFacts(noIncumbency, permission),
      false,
      `Without active incumbency, review-readonly permission ${permission} must fail closed`,
    )
  }

  // retired 工具在角色层即不被识别，任何角色都 fail closed
  for (const tool of RETIRED_REVIEW_TOOLS) {
    assert.equal(rolePredicate(tool, 'manager'), false, `Retired tool ${tool} must fail closed at the role layer`)
  }
})

test('WHAT[capability-enforcement-025] P06_unknown_identity_never_authorized_by_tool_name_suffix', () => {
  const unknownCallers = ['unknown', 'guest', 'coder', 'inspector', '']
  for (const caller of unknownCallers) {
    assert.equal(rolePredicate('js-unknown', caller), false, `Unknown caller '${caller}' must not be admitted by js- suffix`)
    for (const tool of [...RETIRED_REVIEW_TOOLS, MANAGER_REVIEW_TOOL]) {
      assert.equal(rolePredicate(tool, caller), false, `Unknown caller '${caller}' must not be admitted to ${tool}`)
    }
    assert.equal(rolePredicate('js-engineer', caller), false, `Unknown caller '${caller}' must not be admitted to js-engineer`)
  }
})

test('WHAT[capability-enforcement-025] P03_unaccepted_or_malformed_review_retains_readonly_permissions', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-p03'
    // 权威确立与任期建立：先确立 Manager 权威，再打开 AuditPending 任期
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)

    // 驱动 malformed review 尝试：传入无效评分或缺少规范字段
    const reviewResult = await hooks.tool.review.execute(
      { findings: 'invalid-findings-malformed' },
      { sessionID, agent: 'manager' },
    )
    // 评审未被接纳（返回 recorded = false 或拒绝提示）
    assert.match(String(reviewResult), /false|invalid|missing|有效评分|valid ratings/i)

    // 评审未被接纳前，js-manager 专用只读工具仍准入，不得误封
    const beforeOutput = {
      args: { program: "class Js extends JsProgram { async run() { const f = await this.file('src/Model.fs'); return f.text('^', '$'); } }", contract: 'do-not-use-except-for-review' },
    }
    const originalArgs = beforeOutput.args
    const expectedArgs = { ...originalArgs }
    await hooks['tool.execute.before'](
      { tool: 'js-manager', sessionID, callID: 'call-p03' },
      beforeOutput,
    )
    assert.equal(beforeOutput.args, originalArgs, 'admission must preserve the public argument object')
    assert.deepEqual(originalArgs, { program: expectedArgs.program })
    await hooks['tool.execute.after'](
      { tool: 'js-manager', sessionID, callID: 'call-p03', args: originalArgs },
      { title: 'js-manager', output: '', metadata: {} },
    )
    assert.deepEqual(originalArgs, expectedArgs, 'after must restore the public contract hint')
    assert.deepEqual(Object.keys(originalArgs), Object.keys(expectedArgs))
  })
})

test('WHAT[capability-enforcement-025] P07_cleanup_blocker_or_inactive_incumbency_denies_review_tool', () => {
  const REVIEW_TOOLS = ['js-manager']

  // 1. 清理阻塞状态（CleanupBlockerDigest 有值）下：收窄为仅 Join/Finality，评审专用只读工具必须拒绝
  const factsCleanup = managerFacts(true, false, false, 'blocker-digest-xyz')
  for (const tool of REVIEW_TOOLS) {
    const perms = reviewToolPermissions(tool)
    for (const p of perms) {
      assert.equal(
        isAllowedForManagerFacts(factsCleanup, p),
        false,
        `${tool} permission ${p} must be denied under cleanup blocker`,
      )
    }
  }

  // 2. 退任冻结与非活跃状态：无活跃任期（HasActiveIncumbency = false）评审专用工具收口拒绝
  const factsFrozenOrInactive = managerFacts(false, false, false, undefined)
  for (const tool of REVIEW_TOOLS) {
    const perms = reviewToolPermissions(tool)
    for (const p of perms) {
      assert.equal(
        isAllowedForManagerFacts(factsFrozenOrInactive, p),
        false,
        `${tool} permission ${p} must be denied when incumbency is not active`,
      )
    }
  }
})

test('WHAT[capability-enforcement-025] P16_final_incumbent_keeps_read_cleanup_and_closeout_permissions', () => {
  // 末任：已接纳空集 findings 的评估（HasAssessment = true, IsFinalIncumbent = true）。
  // 读、清理与收口类动作必须放行。
  const facts = managerFacts(true, true, true, undefined)
  for (const permission of ['Read', 'Glob', 'Grep', 'Horizon', 'Join', 'Finality']) {
    assert.equal(
      isAllowedForManagerFacts(facts, permission),
      true,
      `final incumbent must keep close-out permission ${permission}`,
    )
  }
})

test('WHAT[capability-enforcement-025] P17_final_incumbent_loses_every_new_work_capability', () => {
  // 末任不得开新工作：委托、续做与评审一律拒绝。
  const facts = managerFacts(true, true, true, undefined)
  for (const permission of ['Fork', 'Resume', 'ReviewAssessment']) {
    assert.equal(
      isAllowedForManagerFacts(facts, permission),
      false,
      `final incumbent must lose new-work permission ${permission}`,
    )
  }
})

test('WHAT[capability-enforcement-025] P18_final_incumbent_notice_text_present_in_both_languages', () => {
  const zh = readFileSync(new URL('../../../resources/provider/runtime/manager-finish/zh-CN.md', import.meta.url), 'utf8')
  const en = readFileSync(new URL('../../../resources/provider/runtime/manager-finish/en.md', import.meta.url), 'utf8')
  assert.match(zh, /最后一任/)
  assert.match(zh, /findings 为空集/)
  assert.match(zh, /不得开新工作/)
  assert.match(en, /final Manager/)
  assert.match(en, /findings is empty/)
  assert.match(en, /do not start new work/)
})

test('WHAT[capability-enforcement-025] P11_plugin_reopen_evaluates_durable_incumbency_facts_without_resetting_assessment', async () => {
  await withRestartablePlugin(async (start, _directory, { stop, withRuntime }) => {
    const sessionAccepted = 'ses-p11-accepted'
    const sessionUnaccepted = 'ses-p11-unaccepted'

    const firstPlugin = await start()

    await withRuntime(async (runtime) => {
      await acceptAuthorityRoot(runtime, sessionAccepted, 'manager')
      await acceptAuthorityRoot(runtime, sessionUnaccepted, 'manager')
      // sessionAccepted: 活跃任期且已接纳评审
      await openIncumbency(runtime, sessionAccepted)
      await injectAcceptedAssessment(runtime, sessionAccepted)

      // sessionUnaccepted: 活跃任期但未接纳评审
      await openIncumbency(runtime, sessionUnaccepted)
    })

    // 同进程关闭并重开插件，读取同一份持久事实；不声称杀进程或 Host compaction。
    await stop(firstPlugin)
    const restartedPlugin = await start()

    // 重启后验证已接纳评审的 session：js-manager 专用只读工具仍严格拒绝，不重置评审
    const acceptedBeforeOutput = {
      args: { program: "class Js extends JsProgram { async run() { const f = await this.file('src/Model.fs'); return f.text('^', '$'); } }", contract: 'do-not-use-except-for-review' },
    }
    await assert.rejects(
      async () => {
        await restartedPlugin['tool.execute.before'](
          { tool: 'js-manager', sessionID: sessionAccepted, callID: 'call-p11-acc' },
          acceptedBeforeOutput,
        )
      },
      /not permitted under current manager capability facts/i,
      'js-manager must remain denied after restart when assessment was already accepted',
    )

    // 重启后未接纳评审的 session 仍准入，after 恢复原始参数。
    const unacceptedBeforeOutput = {
      args: { program: "class Js extends JsProgram { async run() { const f = await this.file('src/Model.fs'); return f.text('^', '$'); } }", contract: 'do-not-use-except-for-review' },
    }
    const originalArgs = unacceptedBeforeOutput.args
    const expectedArgs = { ...originalArgs }
    await restartedPlugin['tool.execute.before'](
      { tool: 'js-manager', sessionID: sessionUnaccepted, callID: 'call-p11-unacc' },
      unacceptedBeforeOutput,
    )
    assert.equal(unacceptedBeforeOutput.args, originalArgs)
    assert.deepEqual(originalArgs, { program: expectedArgs.program })
    await restartedPlugin['tool.execute.after'](
      { tool: 'js-manager', sessionID: sessionUnaccepted, callID: 'call-p11-unacc', args: originalArgs },
      { title: 'js-manager', output: '', metadata: {} },
    )
    assert.deepEqual(originalArgs, expectedArgs, 'after reopening, after must restore the public contract hint')
    assert.deepEqual(Object.keys(originalArgs), Object.keys(expectedArgs))

    await stop(restartedPlugin)
  })
})

test('WHAT[capability-enforcement-025] P12_current_fact_classifier_distinguishes_accepted_and_unaccepted_assessment', () => {
  // 两个不同任期状态的 facts 独立评估：
  // 任期 1：活跃但已接纳评审
  const incumbency1Facts = managerFacts(true, true, false, undefined)
  // 任期 2：合法新任期，活跃且未接纳评审
  const incumbency2Facts = managerFacts(true, false, false, undefined)

  // 任期 1 专用只读能力被封禁
  assert.equal(isAllowedForManagerFacts(incumbency1Facts, 'Read'), false)
  assert.equal(isAllowedForManagerFacts(incumbency1Facts, 'Glob'), false)
  assert.equal(isAllowedForManagerFacts(incumbency1Facts, 'Grep'), false)

  // 任期 2（新任期）不继承任期 1 的已评审限制，专用只读能力正常开启
  assert.equal(isAllowedForManagerFacts(incumbency2Facts, 'Read'), true)
  assert.equal(isAllowedForManagerFacts(incumbency2Facts, 'Glob'), true)
  assert.equal(isAllowedForManagerFacts(incumbency2Facts, 'Grep'), true)

  // 两个任期的调用授权互不借用，也不把旧 assessment 错封新任期
})

test('WHAT[capability-enforcement-025] P13_review_accepted_after_before_hook_is_rejected_at_execution', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-p13'
    const callID = 'call-p13'
    // 权威确立与任期建立：先确立 Manager 权威根，再打开 AuditPending 任期
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)

    const beforeOutput = {
      args: {
        program: "class Js extends JsProgram { async run() { const f = await this.file('src/App.fs'); return f.text('^', '$'); } }",
        contract: 'do-not-use-except-for-review',
      },
    }

    const originalArgs = beforeOutput.args
    const expectedArgs = { ...originalArgs }

    // Step 1: before hook 检查通过并放行，隐藏执行视图的提示字段。
    await hooks['tool.execute.before'](
      { tool: 'js-manager', sessionID, callID },
      beforeOutput,
    )
    assert.equal(beforeOutput.args, originalArgs)
    assert.deepEqual(originalArgs, { program: expectedArgs.program })

    // Step 2: 在执行前注入已接纳评审事实（AcceptedAssessment）
    await injectAcceptedAssessment(runtime, sessionID)

    // Step 3: 真实运行入口重新检查当前事实；文件读取次数还未在此观察。
    const execResult = await hooks.tool['js-manager'].execute(
      beforeOutput.args,
      { sessionID, agent: 'manager' },
    )
    assert.match(
      String(execResult),
      /当前不可用|not available right now|denied-task-state/i,
      'ToolRegistry gate must reject execution due to accepted assessment facts',
    )
    assert.doesNotMatch(
      String(execResult),
      /权威确立之前|authority is established/i,
      'Rejection must not be due to unestablished authority',
    )
    assert.equal(beforeOutput.args, originalArgs)
    assert.deepEqual(originalArgs, { program: expectedArgs.program })
    await hooks['tool.execute.after'](
      { tool: 'js-manager', sessionID, callID, args: originalArgs },
      { title: 'js-manager', output: execResult, metadata: {} },
    )
    assert.deepEqual(originalArgs, expectedArgs, 'after execution denial, after must restore the original public arguments')
    assert.deepEqual(Object.keys(originalArgs), Object.keys(expectedArgs))
  })
})

test('WHAT[capability-enforcement-025] P14_completed_readonly_call_keeps_after_hook_while_subsequent_calls_are_denied', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ses-p14'
    const call1ID = 'call-p14-1'
    const call2ID = 'call-p14-2'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    mkdirSync(join(directory, 'src'), { recursive: true })
    writeFileSync(join(directory, 'src/App.fs'), 'review evidence', 'utf8')

    // Call 1 启动并在 Review 接纳前获得最终准入与执行
    const call1Output = {
      args: {
        program: "class Js extends JsProgram { async run() { const f = await this.file('src/App.fs'); return { text: f.text('^', '$') }; } }",
        contract: 'do-not-use-except-for-review',
      },
    }
    const originalArgs = call1Output.args
    const expectedArgs = { ...originalArgs }
    await hooks['tool.execute.before'](
      { tool: 'js-manager', sessionID, callID: call1ID },
      call1Output,
    )
    const call1ExecResult = await hooks.tool['js-manager'].execute(
      call1Output.args,
      { sessionID, agent: 'manager', callID: call1ID, messageID: 'msg-p14' },
    )
    assert.deepEqual(parseToml(String(call1ExecResult)).data, { text: 'review evidence' })
    assert.equal(
      call1Output.args.contract,
      undefined,
      'the execution view keeps contract hidden until after',
    )

    // 在 Call 1 获准后，系统接纳 Review
    await injectAcceptedAssessment(runtime, sessionID)

    // Call 1 的后置回调须恢复原始参数，不受稍后的评审接纳影响。
    await hooks['tool.execute.after'](
      { tool: 'js-manager', sessionID, callID: call1ID, args: call1Output.args },
      { title: 'js-manager', output: call1ExecResult, metadata: {} },
    )
    assert.equal(
      call1Output.args.contract,
      'do-not-use-except-for-review',
      'the after callback must preserve the restored contract',
    )
    assert.equal(call1Output.args, originalArgs)
    assert.deepEqual(originalArgs, expectedArgs)
    assert.deepEqual(Object.keys(originalArgs), Object.keys(expectedArgs))

    // 后续新调用（Call 2）发起，在入口即被拒绝
    const call2Output = {
      args: {
        program: "class Js extends JsProgram { async run() { const f = await this.file('src/App.fs'); return f.text('^', '$'); } }",
        contract: 'do-not-use-except-for-review',
      },
    }
    await assert.rejects(
      async () => {
        await hooks['tool.execute.before'](
          { tool: 'js-manager', sessionID, callID: call2ID },
          call2Output,
        )
      },
      /not permitted under current manager capability facts/i,
      'Subsequent call must be rejected after review acceptance',
    )
  })
})
test('WHAT[capability-enforcement-025] an admitted call reads the file and before-hook denial preserves arguments and journal revision', async () => {
  const { mkdirSync, writeFileSync } = await import('node:fs')
  const { join } = await import('node:path')
  const { parse: parseToml } = await import('smol-toml')
  const revisionSurface = await import('../../../dist/Persistence/Journal/RevisionSurface.js')
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ses-p15'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    mkdirSync(join(directory, 'src'), { recursive: true })
    writeFileSync(join(directory, 'src/App.fs'), 'review evidence', 'utf8')

    const program = "class Js extends JsProgram { async run() { const f = await this.file('src/App.fs'); return { text: f.text('^', '$') }; } }"
    const tool = hooks.tool['js-manager']

    // The counter observes this registered tool body. Revision observes
    // successful commits published by this journal, not physical I/O attempts.
    let executions = 0
    const originalExecute = tool.execute.bind(tool)
    tool.execute = (...callArgs) => {
      executions += 1
      return originalExecute(...callArgs)
    }
    const revisionOf = () => revisionSurface.revision(runtime.journal)

    // Positive control: before review acceptance the same program drives the
    // full call chain (before → execute → after). The executed result carries
    // the file's bytes, and the counter must observe this registered body.
    const admittedOutput = { args: { program, contract: 'do-not-use-except-for-review' } }
    await hooks['tool.execute.before'](
      { tool: 'js-manager', sessionID, callID: 'call-p15-control' },
      admittedOutput,
    )
    const controlResult = await tool.execute(
      admittedOutput.args,
      { sessionID, agent: 'manager', callID: 'call-p15-control', messageID: 'msg-p15-control' },
    )
    assert.deepEqual(
      parseToml(String(controlResult)).data,
      { text: 'review evidence' },
      'positive control: the admitted call really reads the file through the full chain',
    )
    await hooks['tool.execute.after'](
      { tool: 'js-manager', sessionID, callID: 'call-p15-control', args: admittedOutput.args },
      { title: 'js-manager', output: controlResult, metadata: {} },
    )
    assert.equal(executions, 1, 'positive control: exactly one registered tool call was observed')

    // This scenario exercises the rejecting before hook, not the full Host
    // invocation. The revision observation is validated by a known commit:
    // accepting the assessment writes a durable fact, so the revision must
    // advance here — otherwise "unchanged" in the denial scenario would be
    // vacuous.
    const revisionBeforeAssessment = revisionOf()
    await injectAcceptedAssessment(runtime, sessionID)
    assert.ok(revisionOf() > revisionBeforeAssessment, 'observation check: a real append advances the revision')
    const deniedOutput = { args: { program, contract: 'do-not-use-except-for-review' } }
    const argsSnapshot = JSON.stringify(deniedOutput.args)
    const executionsBeforeDenial = executions
    const revisionBeforeDenial = revisionOf()

    await assert.rejects(
      async () => {
        await hooks['tool.execute.before'](
          { tool: 'js-manager', sessionID, callID: 'call-p15-denied' },
          deniedOutput,
        )
      },
      /not permitted under current manager capability facts/i,
    )

    assert.equal(executions, executionsBeforeDenial, 'the rejecting before hook does not call the wrapped tool body')
    assert.equal(JSON.stringify(deniedOutput.args), argsSnapshot, 'denial does not mutate the caller arguments')
    assert.equal(revisionOf(), revisionBeforeDenial, 'the rejecting before hook leaves the observed journal revision unchanged')
  })
})

test.todo('WHAT[capability-enforcement-025] denial performs zero physical reads and write attempts through the production invocation (GAP-075: investigation 2026-10-03 — the production chain ToolRegistry → JsToolSpec → ToolWorkflow keeps read snapshots internal; the only wired observation port is RequirementGrounding.programObservation, which fires solely for paths that have grounding materials in the workspace, so a generic physical-read observer does not exist. Needed: a read-path observation port on JsToolSpec/ToolWorkflow (or an equivalent production entry) that the test fixture can subscribe to; the execute counter and journal revision in the passing test above remain the closest available proxies)')

test.todo('WHAT[capability-enforcement-025] an in-flight admitted read finishes across review acceptance (GAP-075: requires a controlled causal barrier driving real overlap; the sequential fixture cannot prove concurrency)')
