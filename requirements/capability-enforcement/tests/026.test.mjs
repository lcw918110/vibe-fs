import assert from 'node:assert/strict'
import test from 'node:test'
import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { acceptAuthorityRoot, injectAcceptedAssessment, openIncumbency, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as workspaceEvents from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'

// WHAT[capability-enforcement-026]: 在当前迭代的 Review 被系统接纳前，严禁
// Manager 向绑定的固定 DevOps 派发任何任务；已有只读 Engineer 的合法 resume
// 与 fork 只读调查不受误伤。对未接纳评审前向 DevOps 派工的拦截与拒绝，必须
// 发生在任何持久化副作用之前。门禁在真实工具执行入口（ToolRegistry office
// gate + ForkTool 工具体）生效，状态来自统一 Lifecycle EventStore。

const devopsDispatchDenied = /尚未被系统接纳|不得向 DevOps 派发|assessment.*not.*accepted|not allowed until/i
const guidanceAppended = /Guidance for \S+ has been appended to the existing work|追加指导|既有任务|没有建立新任务|no new task was created/i
// 收口窗（已确认，或已评 Promoted）的拒绝是自己的一条文案，不是评审未接纳那一句。
const closeoutDispatchDenied = /closing out|收口/i
const dispatchHandedOver = /carries this charge now|现已接下这项托付/i

// 派工门读插件正在读的同一份工作区事件库，与 fixture 的 lifecycleStore 同源。
const storeOf = async (directory) => workspaceEvents.acquire(await journalSurface.JournalSurface_initializeStateRoot(directory))

test('WHAT[capability-enforcement-026] D01_resume_fixed_devops_rejected_before_review_accepted', async () => {
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const sessionID = 'ses-d01'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)

    const result = await hooks.tool.resume.execute(
      { name: 'devops', charge: 'DEVOPS-RUN-TESTS-BEFORE-REVIEW' },
      { sessionID, agent: 'manager' },
    )
    assert.equal(typeof result, 'string', 'Resume must return structured consequence')
    assert.doesNotMatch(result, /carries this charge now|现已接下这项托付/i,
      'Resume response must not establish successful handover before review is accepted')
    assert.match(result, devopsDispatchDenied)
    assert.equal(createdIds.length, 0, 'No child session may be created when resume is rejected')
    assert.equal(runtime.prompts.length, 0, 'No prompt may be emitted when resume is rejected')
  })
})

test('WHAT[capability-enforcement-026] D03_resume_devops_enters_normal_flow_after_review_accepted', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-d03'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    await injectAcceptedAssessment(runtime, sessionID)

    const result = await hooks.tool.resume.execute(
      { name: 'devops', charge: 'DEVOPS-RUN-TESTS-AFTER-REVIEW' },
      { sessionID, agent: 'manager' },
    )
    assert.match(result, /devops/)
    assert.match(result, /carries this charge now|现已接下这项托付/i)
    assert.equal(createdIds.length, 1)
  })
})

test('WHAT[capability-enforcement-026] D02_resume_legitimate_existing_readonly_engineer_unaffected_before_review_acceptance', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-d02'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)

    // 已有只读 Engineer 的合法 resume 不受评审未接纳误伤：先 fork Engineer
    // 建立持续之人，再 resume 追加指导，两者都在评审接纳前放行。
    const forked = await hooks.tool.fork.execute(
      { name: 'Bob', charge: 'readonly investigation' },
      { sessionID, agent: 'manager' },
    )
    assert.match(forked, /现已接下这项托付|carries this charge now/i)
    assert.equal(createdIds.length, 1)

    const resumed = await hooks.tool.resume.execute(
      { name: 'Bob', charge: 'continue readonly investigation' },
      { sessionID, agent: 'manager' },
    )
    assert.doesNotMatch(resumed, devopsDispatchDenied)
    assert.match(resumed, guidanceAppended)
    assert.equal(createdIds.length, 1, 'resume of an existing Engineer must not create a new child')
  })
})

test('WHAT[capability-enforcement-026] D04_review_authorized_busy_devops_receives_guidance_on_the_original_work', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-d04'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    await injectAcceptedAssessment(runtime, sessionID)

    // 第一次派工建立活跃 DevOps 工作单元。
    const first = await hooks.tool.resume.execute(
      { name: 'devops', charge: 'ORIGINAL-WORK' },
      { sessionID, agent: 'manager' },
    )
    assert.match(first, /carries this charge now|现已接下这项托付/i)
    assert.equal(createdIds.length, 1)

    // DevOps 忙时再次 resume：不建立新任务，而是向既有任务追加指导。
    const second = await hooks.tool.resume.execute(
      { name: 'devops', charge: 'NEW-ASSIGNMENT' },
      { sessionID, agent: 'manager' },
    )
    assert.match(second, guidanceAppended)
    assert.equal(createdIds.length, 1, 'busy DevOps must not create a second work unit on the same road')
  })
})

test('WHAT[capability-enforcement-026] D05_case_variation_and_unbound_identity_cannot_bypass_devops_gate', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-d05'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)

    // 大小写变体（DevOps / DEV-OPS 等）不得绕过固定 DevOps 门禁。
    for (const variant of ['DevOps', 'DEV-OPS', 'dev-ops']) {
      const result = await hooks.tool.resume.execute(
        { name: variant, charge: 'RUN' },
        { sessionID, agent: 'manager' },
      )
      // 未结识的 name 一律 fail closed：不得建立任何 DevOps 派工。
      assert.doesNotMatch(result, /carries this charge now|现已接下这项托付/i,
        `variant '${variant}' must not establish a DevOps handover`)
      assert.equal(createdIds.length, 0)
    }
  })
})

test('WHAT[capability-enforcement-026] D06_fork_devops_or_creating_alternative_devops_remains_strictly_denied', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-d06'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)

    // fork devops（评审未接纳）在执行入口拒绝。
    const forkDevops = await hooks.tool.fork.execute(
      { name: 'devops', charge: 'FORK-DEVOPS-ATTEMPT' },
      { sessionID, agent: 'manager' },
    )
    assert.match(forkDevops, devopsDispatchDenied)
    assert.equal(createdIds.length, 0, 'No child may be placed for invalid calling')

    // 显式 calling=devops 也严格拒绝。
    const callingDevops = await hooks.tool.fork.execute(
      { calling: 'devops', name: 'Operator1', charge: 'FORK-DEVOPS-ATTEMPT' },
      { sessionID, agent: 'manager' },
    )
    assert.match(callingDevops, /cannot fork DevOps|fork only targets Engineer|只能 fork Engineer|不能 fork DevOps/i)
    assert.equal(createdIds.length, 0)
  })
})

test('WHAT[capability-enforcement-026] D07_pre_review_devops_refusal_preserves_durable_state_and_accepted_review_admits_the_same_charge', async () => {
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const sessionID = 'ses-d07'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    const durableBefore = durableSnapshot(directory)

    // 评审接纳前向 DevOps 派工：拒绝且不产生任何持久化副作用。
    const refused = await hooks.tool.resume.execute(
      { name: 'devops', charge: 'DEVOPS-RUN-TESTS-BEFORE-REVIEW' },
      { sessionID, agent: 'manager' },
    )
    assert.match(refused, devopsDispatchDenied)
    assert.deepEqual(durableSnapshot(directory), durableBefore,
      'the refused dispatch must not append any durable bytes')
    assert.equal(createdIds.length, 0)
    assert.equal(runtime.prompts.length, 0)

    // 评审接纳后，同一个 charge 正常派工。
    await injectAcceptedAssessment(runtime, sessionID)
    const admitted = await hooks.tool.resume.execute(
      { name: 'devops', charge: 'DEVOPS-RUN-TESTS-BEFORE-REVIEW' },
      { sessionID, agent: 'manager' },
    )
    assert.match(admitted, /carries this charge now|现已接下这项托付/i)
    assert.equal(createdIds.length, 1)
  })
})

// 修复窗（已评 Stayed、尚无确认）是本门唯一放行的格子：修复派工与修复 fork
// 都不得被本门末尾拒绝，因为修复义务的派工本来就落在这里。
test('WHAT[capability-enforcement-026] D08_repair_window_admits_devops_dispatch_and_repair_fork', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-d08-repair-window'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    await injectAcceptedAssessment(runtime, sessionID) // Stayed，尚无确认

    const dispatch = await hooks.tool.resume.execute(
      { name: 'devops', charge: 'REPAIR-THE-FINDINGS' },
      { sessionID, agent: 'manager' },
    )
    assert.match(dispatch, dispatchHandedOver, 'the repair window must still admit DevOps dispatch')
    assert.doesNotMatch(dispatch, closeoutDispatchDenied)
    assert.equal(createdIds.length, 1)

    const repairFork = await hooks.tool.fork.execute(
      { name: 'Carl', charge: 'repair the findings' },
      { sessionID, agent: 'manager' },
    )
    assert.match(repairFork, dispatchHandedOver, 'a repair fork must not be refused for not being read-only')
    assert.equal(createdIds.length, 2)
  })
})

// 收口窗一：确认已经落盘（Stayed 两段式的第二笔只差收口）。此时再向绑定的固定
// DevOps 派新任务必须拒绝，且拒绝发生在任何持久化副作用之前。
test('WHAT[capability-enforcement-026] D09_confirmed_tenure_refuses_new_devops_dispatch_with_zero_side_effects', async () => {
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const sessionID = 'ses-d09-confirmed'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    await injectAcceptedAssessment(runtime, sessionID) // Stayed

    const store = await storeOf(directory)
    const confirmed = await lifecycle.seedDeliver(store, sessionID, 'run-d09', 'call-d09-first', 2n)
    assert.equal(confirmed.ok, true, confirmed.error)
    const view = lifecycle.workView(store, sessionID)
    assert.equal(view.confirmed, true, 'the plugin must read the same confirmation the gate reads')
    assert.equal(view.active, true)

    const createdBefore = createdIds.length
    const promptsBefore = runtime.prompts.length
    const durableBefore = durableSnapshot(directory)

    const refused = await hooks.tool.resume.execute(
      { name: 'devops', charge: 'NEW-TASK-AFTER-CONFIRMATION' },
      { sessionID, agent: 'manager' },
    )
    assert.match(refused, closeoutDispatchDenied, 'the close-out window must refuse new DevOps dispatch')
    assert.doesNotMatch(refused, dispatchHandedOver, 'no handover may be reported in the close-out window')
    assert.equal(createdIds.length, createdBefore, 'the refused close-out dispatch must not place a child')
    assert.equal(runtime.prompts.length, promptsBefore, 'the refused close-out dispatch must not emit a prompt')
    assert.deepEqual(durableSnapshot(directory), durableBefore,
      'the refused close-out dispatch must not append any durable bytes')
  })
})

// 收口窗二：已评 Promoted。新派工不因为已有评审被放行；同时评审接纳前已有的
// 只读 Engineer fork/resume 不被本门误伤。
test('WHAT[capability-enforcement-026] D10_promoted_review_refuses_new_devops_dispatch_and_spares_readonly_engineer', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-d10-promoted'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)

    const forked = await hooks.tool.fork.execute(
      { name: 'Dora', charge: 'readonly investigation' },
      { sessionID, agent: 'manager' },
    )
    assert.match(forked, dispatchHandedOver)
    assert.equal(createdIds.length, 1)

    const resumed = await hooks.tool.resume.execute(
      { name: 'Dora', charge: 'continue readonly investigation' },
      { sessionID, agent: 'manager' },
    )
    assert.doesNotMatch(resumed, devopsDispatchDenied)
    assert.doesNotMatch(resumed, closeoutDispatchDenied)
    assert.match(resumed, guidanceAppended)
    assert.equal(createdIds.length, 1, 'resume of an existing Engineer must not create a new child')

    await injectAcceptedAssessment(runtime, sessionID, `inc-${sessionID}`, 'Promoted')
    const promptsBefore = runtime.prompts.length
    const refused = await hooks.tool.resume.execute(
      { name: 'devops', charge: 'NEW-TASK-PROMOTED' },
      { sessionID, agent: 'manager' },
    )
    assert.doesNotMatch(refused, dispatchHandedOver, 'a Promoted review must not admit a new DevOps task')
    assert.equal(createdIds.length, 1)
    assert.equal(runtime.prompts.length, promptsBefore)
  })
})

const durableSnapshot = (directory) => {
  const snapshot = []
  const visit = (relativePath) => {
    const entries = readdirSync(join(directory, relativePath), { withFileTypes: true })
      .sort((left, right) => left.name.localeCompare(right.name))
    for (const entry of entries) {
      const path = join(relativePath, entry.name)
      if (entry.isDirectory()) {
        snapshot.push({ path, kind: 'directory' })
        visit(path)
      } else {
        assert.equal(entry.isFile(), true, 'the isolated durable boundary must contain only directories and regular files')
        snapshot.push({ path, kind: 'file', bytes: readFileSync(join(directory, path)).toString('base64') })
      }
    }
  }
  visit('')
  return snapshot
}