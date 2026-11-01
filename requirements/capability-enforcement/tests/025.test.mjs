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

const MANAGER_REVIEW_TOOL = 'todowrite-for-manager'
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

  // 评审接纳后，只读能力 Read/Glob/Grep 均不被允许（reviewOnly 仅限第一次评审前使用）
  assert.equal(isAllowedForManagerFacts(facts, 'Read'), false, 'Read must be denied after assessment accepted')
  assert.equal(isAllowedForManagerFacts(facts, 'Glob'), false, 'Glob must be denied after assessment accepted')
  assert.equal(isAllowedForManagerFacts(facts, 'Grep'), false, 'Grep must be denied after assessment accepted')
  // 评审接纳后，Join/Fork 等管理与编排权限仍被允许
  assert.equal(isAllowedForManagerFacts(facts, 'Fork'), true, 'Fork must remain allowed after assessment accepted')
  assert.equal(isAllowedForManagerFacts(facts, 'Resume'), true, 'Resume must remain allowed after assessment accepted')
  assert.equal(isAllowedForManagerFacts(facts, 'Join'), true, 'Join must remain allowed after assessment accepted')
  assert.equal(isAllowedForManagerFacts(facts, 'Horizon'), true, 'Horizon must remain allowed after assessment accepted')
  assert.equal(isAllowedForManagerFacts(facts, 'Finality'), true, 'Finality must remain allowed after assessment accepted')
  // 评审接纳后，Review 仍被允许，以便后续更新义务账
  assert.equal(isAllowedForManagerFacts(facts, 'ReviewAssessment'), true, 'ReviewAssessment must remain allowed after assessment accepted')
})

test('WHAT[capability-enforcement-025] P08_manager_native_read_glob_grep_projected_as_deny', () => {
  // Direct read/glob/grep host tools are retired from knownToolNames and covered
  // by the wildcard "*": "deny" rule; rolePredicate also fails closed on them.
  const managerPerms = rolePermissionRules('manager')
  assert.equal(managerPerms['*'], 'deny', 'Manager wildcard default must deny retired native tools')
  for (const retired of ['read', 'glob', 'grep']) {
    assert.equal(retired in managerPerms, false, `Retired ${retired} must not appear as an explicit Manager permission key`)
    assert.equal(rolePredicate(retired, 'manager'), false, `Role predicate must deny retired ${retired} for Manager`)
  }
})

test('WHAT[capability-enforcement-025] P09_manager_allows_sole_review_tool_and_retired_tools_not_admitted_to_any_role', () => {
  const managerPerms = rolePermissionRules('manager')
  assert.equal(managerPerms['todowrite-for-engineer'], 'deny', 'Manager must deny todowrite-for-engineer')
  assert.equal(managerPerms['todowrite-for-devops'], 'deny', 'Manager must deny todowrite-for-devops')
  assert.equal(rolePredicate('todowrite-for-engineer', 'manager'), false, 'Role predicate must deny todowrite-for-engineer for Manager')
  assert.equal(rolePredicate('todowrite-for-devops', 'manager'), false, 'Role predicate must deny todowrite-for-devops for Manager')
  assert.equal(managerPerms[MANAGER_REVIEW_TOOL], 'allow', 'Manager must allow the sole review tool todowrite-for-manager')

  for (const tool of RETIRED_REVIEW_TOOLS) {
    for (const role of ['manager', 'engineer', 'devops']) {
      assert.equal(rolePredicate(tool, role), false, `Retired tool ${tool} must not be admitted to ${role}`)
    }
  }
})

test('WHAT[capability-enforcement-025] P10_sole_review_tool_allowed_and_retired_tools_absent_from_manager_surface', () => {
  const managerPerms = rolePermissionRules('manager')
  assert.equal(managerPerms[MANAGER_REVIEW_TOOL], 'allow', 'Manager request projection must allow the sole review tool todowrite-for-manager')
  for (const tool of RETIRED_REVIEW_TOOLS) {
    assert.equal(tool in managerPerms, false, `Retired tool ${tool} must no longer appear in the Manager tool surface`)
  }
})

test('WHAT[capability-enforcement-025] P05_no_active_incumbency_and_no_assessment_denies_review_readonly_admission', () => {
  // 无 active incumbency：当前事实收口为空集，todowrite-for-manager 依赖的只读能力全部 fail closed
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
    assert.equal(rolePredicate('todowrite-for-engineer', caller), false, `Unknown caller '${caller}' must not be admitted to todowrite-for-engineer`)
  }
})

test('WHAT[capability-enforcement-025] P03_unaccepted_or_malformed_review_retains_readonly_permissions', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-p03'
    // 权威确立与任期建立：先确立 Manager 权威，再打开 AuditPending 任期
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)

    // 驱动 malformed review 尝试：`this.review` 成员接收 findings 数组，传入非数组
    // 即为无效评审（standalone `review` 宿主工具已随编程面迁移退役）。
    const reviewResult = await hooks.tool['todowrite-for-manager'].execute(
      { name: 'manager-review', program: "class Js extends JsProgram { async run() { return await this.review('invalid-findings-malformed'); } }",
          intent: 'Wait for the review validation receipt without renewing this deadline.'
        },
      { sessionID, agent: 'manager', callID: 'call-p03-review', messageID: 'msg-p03-review' },
    )
    // 评审未被接纳（返回数据信封 ok = false + 稳定 code 或拒绝提示）
    assert.match(String(reviewResult), /false|invalid|missing|Expected an array|有效评分|valid ratings/i)

    // 评审未被接纳前，todowrite-for-manager 专用只读工具仍准入，不得误封
    const beforeOutput = {
      args: { program: "class Js extends JsProgram { async run() { return await this.cat('src/Model.fs'); } }", contract: 'do-not-use-except-for-review' },
    }
    const originalArgs = beforeOutput.args
    const expectedArgs = { ...originalArgs }
    await hooks['tool.execute.before'](
      { tool: 'todowrite-for-manager', sessionID, callID: 'call-p03' },
      beforeOutput,
    )
    assert.equal(beforeOutput.args, originalArgs, 'admission must preserve the public argument object')
    assert.deepEqual(originalArgs, { program: expectedArgs.program })
    await hooks['tool.execute.after'](
      { tool: 'todowrite-for-manager', sessionID, callID: 'call-p03', args: originalArgs },
      { title: 'todowrite-for-manager', output: '', metadata: {} },
    )
    assert.deepEqual(originalArgs, expectedArgs, 'after must restore the public contract hint')
    assert.deepEqual(Object.keys(originalArgs), Object.keys(expectedArgs))
  })
})

test('WHAT[capability-enforcement-025] P07_cleanup_blocker_or_inactive_incumbency_denies_review_tool', () => {
  const REVIEW_TOOLS = ['todowrite-for-manager']

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
  // 读、清理与收口类动作必须放行，且保留 ReviewAssessment 以便在终局前更新或清空义务账。
  const facts = managerFacts(true, true, true, undefined)
  for (const permission of ['Read', 'Glob', 'Grep', 'Horizon', 'Join', 'Finality', 'ReviewAssessment']) {
    assert.equal(
      isAllowedForManagerFacts(facts, permission),
      true,
      `final incumbent must keep close-out permission ${permission}`,
    )
  }
})

test('WHAT[capability-enforcement-025] final incumbent may still delegate Engineer and DevOps for substantive closure', () => {
  const facts = managerFacts(true, true, true, undefined)
  for (const permission of ['Fork', 'Resume', 'ReviewAssessment', 'Finality']) {
    assert.equal(isAllowedForManagerFacts(facts, permission), true, `final manager retains ${permission}`)
  }
  assert.equal(isAllowedForManagerFacts(managerFacts(false, true, true, undefined), 'Resume'), false,
    'a delivered work without active successor still has no authority')
})

test('WHAT[capability-enforcement-025] P18_final_incumbent_notice_text_present_in_both_languages', () => {
  const zh = readFileSync(new URL('../../../resources/provider/runtime/manager-finish/zh-CN.md', import.meta.url), 'utf8')
  const en = readFileSync(new URL('../../../resources/provider/runtime/manager-finish/en.md', import.meta.url), 'utf8')
  assert.match(zh, /「收尾」/)
  assert.match(zh, /清空义务账的 `this.review` 结案/)
  assert.match(zh, /派出 Engineer 和通过固定 DevOps 收尾/)
  assert.match(en, /reached close-out/)
  assert.match(en, /leaves the ledger empty/)
  assert.match(en, /may continue delegating/)
})

let programSequence = 0
const execute = async (hooks, sessionID, body, callID = 'call-review-' + (++programSequence)) => parseToml(String(
  await hooks.tool['todowrite-for-manager'].execute(
    {
      name: callID,
      program: 'class Js extends JsProgram { async run() { ' + body + ' } }',
      intent: 'Wait for this manager operation and preserve its real outcome at the shared deadline.',
    },
    { sessionID, agent: 'manager', callID, messageID: 'msg-' + callID },
  ),
))

const seed = async (runtime, sessionID, directory) => {
  await acceptAuthorityRoot(runtime, sessionID, 'manager')
  await openIncumbency(runtime, sessionID)
  writeFileSync(join(directory, 'evidence.txt'), 'review evidence', 'utf8')
}

const reads = [
  "return await this.reviewOnly.cat('evidence.txt');",
  "return await this.reviewOnly.stat('evidence.txt');",
  "return await this.reviewOnly.ls('.');",
  "return await this.reviewOnly.rg(['review', 'evidence.txt']);",
]

test('WHAT[capability-enforcement-025] P11_restart keeps the entry open and the reviewed reads closed', async () => {
  await withRestartablePlugin(async (start, directory, { stop, withRuntime }) => {
    const sessionID = 'ses-reopen-review'
    const first = await start()
    await withRuntime(async runtime => {
      await seed(runtime, sessionID, directory)
      await injectAcceptedAssessment(runtime, sessionID)
    })
    await stop(first)
    const reopened = await start()
    assert.equal((await execute(reopened, sessionID, 'return 7;')).data, 7)
    for (const body of reads) assert.equal((await execute(reopened, sessionID, body)).code, 'REVIEW_ONLY')
    await stop(reopened)
  })
})

test('WHAT[capability-enforcement-025] P13_each physical operation is admitted before review and refused after review', async () => {
  await withExecutablePlugin(async (hooks, directory, _ids, runtime) => {
    const sessionID = 'ses-each-operation'
    await seed(runtime, sessionID, directory)
    for (const [index, body] of reads.entries()) {
      const result = await execute(hooks, sessionID, body, 'positive-' + index)
      assert.equal(result.code, undefined, JSON.stringify(result))
      assert.ok(Object.hasOwn(result, 'data'))
    }
    assert.equal((await execute(hooks, sessionID, reads[0])).data, 'review evidence')
    const input = { args: { program: 'class Js extends JsProgram { async run() { return 7; } }' } }
    await hooks['tool.execute.before']({ tool: MANAGER_REVIEW_TOOL, sessionID, callID: 'before-review' }, input)
    await hooks['tool.execute.after']({ tool: MANAGER_REVIEW_TOOL, sessionID, callID: 'before-review', args: input.args }, { output: '', metadata: {} })
    await injectAcceptedAssessment(runtime, sessionID)
    await hooks['tool.execute.before']({ tool: MANAGER_REVIEW_TOOL, sessionID, callID: 'after-review' }, input)
    assert.equal((await execute(hooks, sessionID, 'return 7;')).data, 7)
    for (const body of reads) assert.equal((await execute(hooks, sessionID, body)).code, 'REVIEW_ONLY')
    const pure = await execute(hooks, sessionID, "return this.whereAll('a b', 'b');")
    assert.deepEqual(pure.data, [[2, 3]])
    const scope = await execute(hooks, sessionID, "return { cat: typeof this.cat, raw: typeof this._api.cat, stat: typeof this.stat, ls: typeof this.ls, rg: typeof this.rg, review: typeof this.reviewOnly.cat };")
    assert.deepEqual(scope.data, { cat: 'undefined', raw: 'undefined', stat: 'undefined', ls: 'undefined', rg: 'undefined', review: 'function' })
    for (const name of ['cat', 'stat', 'ls', 'rg']) {
      assert.equal((await execute(hooks, sessionID, 'return await this.' + name + "('evidence.txt');")).code, 'PROGRAM_FAILED')
    }
  })
})

test('WHAT[capability-enforcement-025] P14_saved object, method and raw binding recheck authority after park', async () => {
  await withExecutablePlugin(async (hooks, directory, _ids, runtime) => {
    const sessionID = 'ses-park-review'
    await seed(runtime, sessionID, directory)
    for (const [index, saved] of [
      'const saved = this.reviewOnly; const read = () => saved.cat("evidence.txt");',
      'const read = this.reviewOnly.cat.bind(this.reviewOnly, "evidence.txt");',
      'const raw = this._api.reviewOnly.cat; const read = async () => { const result = await raw("evidence.txt"); if (!result.ok) return result.code; return result.text; };',
    ].entries()) {
      const paused = await execute(hooks, sessionID, saved + ' await this.park({ ready: true }); return await read();', 'park-' + index)
      assert.ok(paused.name)
      if (index === 0) await injectAcceptedAssessment(runtime, sessionID)
      const result = await execute(hooks, sessionID, 'return await this.unpark(' + JSON.stringify(paused.name) + ', null);', 'resume-' + index)
      assert.equal(result.data.name, paused.name)
      if (index === 2) {
        assert.equal(result.data.state, 'completed')
        assert.equal(result.data.value, 'REVIEW_ONLY')
      } else {
        assert.equal(result.data.state, 'failed')
        assert.equal(result.data.code, 'REVIEW_ONLY')
      }
    }
  })
})

test('WHAT[capability-enforcement-025] P15_access denied before path validation, final incumbent has no exception', async () => {
  await withExecutablePlugin(async (hooks, directory, _ids, runtime) => {
    const sessionID = 'ses-final-review'
    await seed(runtime, sessionID, directory)
    await injectAcceptedAssessment(runtime, sessionID, 'inc-' + sessionID, 'Promoted')
    assert.equal((await execute(hooks, sessionID, 'return 7;')).data, 7)
    for (const body of [
      "return await this.reviewOnly.cat('../outside');",
      "return await this.reviewOnly.stat('missing');",
      "return await this.reviewOnly.ls('missing');",
      "return await this.reviewOnly.rg(['--invalid-option', 'missing']);",
    ]) assert.equal((await execute(hooks, sessionID, body)).code, 'REVIEW_ONLY')
    const paused = await execute(hooks, sessionID, 'await this.park({ done: true }); return 9;')
    assert.ok(paused.name)
    const resumed = await execute(hooks, sessionID, 'return await this.unpark(' + JSON.stringify(paused.name) + ', null);')
    assert.equal(resumed.data.name, paused.name)
    assert.equal(resumed.data.state, 'completed')
    assert.equal(resumed.data.value, 9)
  })
})

test('WHAT[capability-enforcement-025] identity and cross-role denial remain independent of Review', async () => {
  await withExecutablePlugin(async (hooks, _directory, _ids, runtime) => {
    await acceptAuthorityRoot(runtime, 'ses-no-life', 'manager')
    assert.equal((await execute(hooks, 'ses-no-life', 'return 7;')).data, 7)
    // No Work is open yet: the first tenure may observe read-only, and its first
    // review opens the Work (work WHAT: 尚未开立 Work 时，首任可只读观察).
    assert.notEqual((await execute(hooks, 'ses-no-life', reads[0])).code, 'REVIEW_ONLY')
    const unknown = await execute(hooks, 'ses-unknown', 'return 7;')
    assert.equal(Object.hasOwn(unknown, 'data'), false)
    const wrongRole = await hooks.tool['todowrite-for-devops'].execute(
      { program: 'class Js extends JsProgram { async run() { return 7; } }', name: 'devops-wrong',
          intent: 'Observe the cross-role refusal before any program effect.'
        },
      { sessionID: 'ses-no-life', agent: 'manager' },
    )
    assert.doesNotMatch(String(wrongRole), /data = 7/)
  })
})

test('WHAT[capability-enforcement-025] denied repository calls perform zero physical target access and never spawn rg', async () => {
  const { default: fs } = await import('node:fs')
  const { default: processes } = await import('node:child_process')
  const { syncBuiltinESMExports } = await import('node:module')
  await withExecutablePlugin(async (hooks, directory, _ids, runtime) => {
    const sessionID = 'ses-physical-review'
    await seed(runtime, sessionID, directory)
    const target = join(directory, 'review-target')
    mkdirSync(target)
    writeFileSync(join(target, 'evidence.txt'), 'review evidence')
    let touches = 0
    let rgSpawns = 0
    const names = ['readFileSync', 'statSync', 'lstatSync', 'realpathSync', 'readdirSync', 'existsSync']
    const saved = new Map(names.map(name => [name, fs[name]]))
    const spawn = processes.spawn
    try {
      for (const name of names) fs[name] = function (...args) {
        if (String(args[0]).includes('review-target')) touches += 1
        return saved.get(name).apply(this, args)
      }
      processes.spawn = function (...args) {
        if (args[0] === 'rg') rgSpawns += 1
        return spawn.apply(this, args)
      }
      syncBuiltinESMExports()
      const operations = [
        "return await this.reviewOnly.cat('review-target/evidence.txt');",
        "return await this.reviewOnly.stat('review-target/evidence.txt');",
        "return await this.reviewOnly.ls('review-target');",
        "return await this.reviewOnly.rg(['review', 'review-target']);",
      ]
      for (const operation of operations) {
        const before = touches
        const result = await execute(hooks, sessionID, operation)
        assert.equal(result.code, undefined, JSON.stringify(result))
        assert.ok(touches > before, 'positive control must reach the physical target adapter')
      }
      assert.equal(rgSpawns, 1, 'positive control must spawn one real rg')
      await injectAcceptedAssessment(runtime, sessionID)
      touches = 0
      rgSpawns = 0
      for (const operation of operations) assert.equal((await execute(hooks, sessionID, operation)).code, 'REVIEW_ONLY')
      assert.equal(touches, 0, 'refused calls must not touch the physical target')
      assert.equal(rgSpawns, 0, 'refused rg must not start a child process')
    } finally {
      for (const [name, original] of saved) fs[name] = original
      processes.spawn = spawn
      syncBuiltinESMExports()
    }
  })
})

test('WHAT[capability-enforcement-025] an admitted physical rg may finish after acceptance, but a later call is refused', { timeout: 5000 }, async () => {
  const { default: processes } = await import('node:child_process')
  const { syncBuiltinESMExports } = await import('node:module')
  await withExecutablePlugin(async (hooks, directory, _ids, runtime) => {
    const sessionID = 'ses-inflight-review'
    await seed(runtime, sessionID, directory)
    const closed = Promise.withResolvers()
    const released = Promise.withResolvers()
    const spawn = processes.spawn
    let spawns = 0
    try {
      processes.spawn = function (...args) {
        const child = spawn.apply(this, args)
        if (args[0] !== 'rg') return child
        spawns += 1
        const on = child.on
        child.on = function (event, observe) {
          if (event !== 'close') return on.call(this, event, observe)
          return on.call(this, event, (...values) => {
            closed.resolve()
            released.promise.then(() => observe(...values))
          })
        }
        return child
      }
      syncBuiltinESMExports()
      const pending = execute(hooks, sessionID, reads[3], 'inflight-rg')
      await closed.promise
      await injectAcceptedAssessment(runtime, sessionID)
      released.resolve()
      const admitted = await pending
      assert.equal(admitted.code, undefined, JSON.stringify(admitted))
      assert.equal(admitted.data.exitCode, 0)
      assert.equal((await execute(hooks, sessionID, reads[3], 'late-rg')).code, 'REVIEW_ONLY')
      assert.equal(spawns, 1)
    } finally {
      released.resolve()
      processes.spawn = spawn
      syncBuiltinESMExports()
    }
  })
})

test('WHAT[capability-enforcement-025] P15_concurrent_physical_rg_overlap_with_review_acceptance_admits_inflight_and_refuses_subsequent', { timeout: 5000 }, async () => {
  const { default: processes } = await import('node:child_process')
  const { syncBuiltinESMExports } = await import('node:module')
  await withExecutablePlugin(async (hooks, directory, _ids, runtime) => {
    const sessionID = 'ses-concurrent-inflight-review'
    await seed(runtime, sessionID, directory)
    const paused = Promise.withResolvers()
    const spawn = processes.spawn
    let spawns = 0
    let liveChild = null
    try {
      processes.spawn = function (...args) {
        const child = spawn.apply(this, args)
        if (args[0] !== 'rg') return child
        spawns += 1
        // Freeze the live OS process while it is genuinely in flight, so the
        // review acceptance below overlaps a running rg, not a finished one.
        process.kill(child.pid, 'SIGSTOP')
        liveChild = child
        paused.resolve(child)
        return child
      }
      syncBuiltinESMExports()
      const pending = execute(hooks, sessionID, reads[3], 'concurrent-inflight-rg')
      const child = await paused.promise
      assert.equal(child.exitCode, null, 'the rg child must still be in flight')
      assert.equal(child.signalCode, null, 'the rg child must not be signalled')
      await injectAcceptedAssessment(runtime, sessionID)
      const overlapped = await execute(hooks, sessionID, reads[3], 'overlapped-after-accept')
      assert.equal(overlapped.code, 'REVIEW_ONLY', JSON.stringify(overlapped))
      process.kill(child.pid, 'SIGCONT')
      const admitted = await pending
      assert.equal(admitted.code, undefined, JSON.stringify(admitted))
      assert.equal(admitted.data.exitCode, 0)
      assert.equal((await execute(hooks, sessionID, reads[3], 'subsequent-rg')).code, 'REVIEW_ONLY')
      assert.equal(spawns, 1)
    } finally {
      if (liveChild !== null && liveChild.exitCode === null && liveChild.signalCode === null) {
        try { process.kill(liveChild.pid, 'SIGCONT') } catch (error) {
          if (error.code !== 'ESRCH') throw error
        }
      }
      processes.spawn = spawn
      syncBuiltinESMExports()
    }
  })
})
