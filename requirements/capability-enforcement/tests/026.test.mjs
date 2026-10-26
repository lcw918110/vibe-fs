import assert from 'node:assert/strict'
import test from 'node:test'
import fc from 'fast-check'
import { mkdtempSync, rmSync, readdirSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { execFileSync } from 'node:child_process'
import { join } from 'node:path'
import { acceptAuthorityRoot, injectAcceptedAssessment, openIncumbency, withExecutablePlugin, withRestartablePlugin, notifyCompleted } from '../../verification-system/tests/support/plugin-fixture.mjs'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'
import { superviseNodeTest } from '../../verification-system/tests/e2e/support/supervise-node-test.mjs'
import { UNIT_VERDICT_SILENCE_MS } from '../../verification-system/tests/e2e/support/time-budget.js'

// WHAT[capability-enforcement-026]: 在当前迭代的 Review 被系统接纳前，严禁
// Manager 向绑定的固定 DevOps 派发任何任务；已有只读 Engineer 的合法 resume
// 与 fork 只读调查不受误伤。对未接纳评审前向 DevOps 派工的拦截与拒绝，必须
// 发生在任何持久化副作用之前。门禁在真实工具执行入口（ToolRegistry office
// gate + ForkTool 工具体）生效，状态来自统一 Lifecycle EventStore。
//
// 委派动词不再是宿主注册的工具：`fork` / `resume` 是 Manager 编程面的成员，
// 由 `todowrite-for-manager` 的程序调用（`this.fork(payload, extra = [])`、
// `this.resume(payload, extra = [])`）。成员返回数据信封，不渲染散文：成功
// 是 `{ ok: true, name, agentId, outcome }`，拒绝是 `{ ok: false, code }`。
// 拒绝码是稳定的 kebab 码，本文件按码断言，不再匹配散文。

// 稳定拒绝码（数据信封的 `code` 字段）。
const REVIEW_PENDING = 'assessment-pending-for-devops'
const DISPATCH_CLOSED = 'dispatch-closed'
const UNKNOWN_CALLING = 'unknown-calling'
const CALLING_CONFLICT = 'calling-conflict'
const PERSON_UNKNOWN = 'person-unknown'

// 编程面成员的唯一入口：Manager 的 `todowrite-for-manager` 工具执行一段程序，
// 程序内调用 `this.fork` / `this.resume`。返回渲染后的 TOML 字符串。
let programSequence = 0
const runManagerProgram = async (hooks, sessionID, body) =>
  await hooks.tool['todowrite-for-manager'].execute(
    { program: `class Js extends JsProgram { async run() { ${body} } }`, name: 'manager-program-' + (++programSequence),
          intent: 'Wait for the actual manager dispatch receipt without extending the request window.',
          remainingScopeTurnsEstimate: 8,
        },
    { sessionID, agent: 'manager' },
  )

const runAndSettleManagerProgram = async (hooks, sessionID, body, maxWaitMs = 4000) => {
  const name = 'manager-program-' + (++programSequence)
  let result = await hooks.tool['todowrite-for-manager'].execute(
    {
      program: `class Js extends JsProgram { async run() { ${body} } }`,
      name,
      intent: 'Wait for the actual manager dispatch receipt without extending the request window.',
      remainingScopeTurnsEstimate: 8,
    },
    { sessionID, agent: 'manager' },
  )
  const deadline = Date.now() + maxWaitMs
  while (/^state = "parked"$/m.test(String(result)) && Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 40))
    result = await hooks.tool['todowrite-for-manager'].execute(
      {
        resume: name,
        intent: 'Wait for the actual manager dispatch receipt without extending the request window.',
        remainingScopeTurnsEstimate: 8,
      },
      { sessionID, agent: 'manager' },
    )
  }
  return result
}

// 载荷与 carry（整数锚点和字面字符串组成的数组，缺省为空）。
const forkCall = (payload) => `return await this.fork(${JSON.stringify(payload)}, []);`
const resumeCall = (payload) => `return await this.resume(${JSON.stringify(payload)}, []);`

// 数据信封字段（渲染后的 TOML `[data]` 表）。
const rendered = (result) => String(result)
const envelopeString = (result, field) => {
  const match = new RegExp(`^${field} = "(.*)"$`, 'm').exec(rendered(result))
  return match === null ? null : match[1]
}
const envelopeOk = (result) => /^ok = true$/m.test(rendered(result))
const refusalCode = (result) => envelopeString(result, 'code')
const outcomeOf = (result) => envelopeString(result, 'outcome')

test('WHAT[capability-enforcement-026] D01_resume_fixed_devops_rejected_before_review_accepted', async () => {
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const sessionID = 'ses-d01'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)

    const result = await runManagerProgram(
      hooks,
      sessionID,
      resumeCall({ name: 'devops', charge: 'DEVOPS-RUN-TESTS-BEFORE-REVIEW' }),
    )
    assert.equal(typeof result, 'string', 'Resume must return structured consequence')
    assert.equal(refusalCode(result), REVIEW_PENDING,
      'the review gate must refuse the DevOps dispatch before the review is accepted')
    assert.equal(envelopeOk(result), false,
      'the refusal must not report a successful handover before the review is accepted')
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

    const result = await runManagerProgram(
      hooks,
      sessionID,
      resumeCall({ name: 'devops', charge: 'DEVOPS-RUN-TESTS-AFTER-REVIEW' }),
    )
    assert.equal(envelopeOk(result), true, result)
    assert.equal(envelopeString(result, 'name'), 'devops', result)
    assert.equal(outcomeOf(result), 'created', result)
    assert.match(rendered(result), /^agentId = ".+"$/m, result)
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
    const forked = await runManagerProgram(
      hooks,
      sessionID,
      forkCall({ name: 'Bob', charge: 'readonly investigation' }),
    )
    assert.equal(envelopeOk(forked), true, forked)
    assert.equal(envelopeString(forked, 'name'), 'Bob', forked)
    assert.equal(outcomeOf(forked), 'created', forked)
    assert.equal(createdIds.length, 1)

    const resumed = await runManagerProgram(
      hooks,
      sessionID,
      resumeCall({ name: 'Bob', charge: 'continue readonly investigation' }),
    )
    assert.equal(refusalCode(resumed), null,
      'the Engineer resume must not be refused by the DevOps review gate')
    assert.equal(envelopeOk(resumed), true, resumed)
    assert.equal(outcomeOf(resumed), 'nudged',
      'the charge of a busy Engineer becomes guidance appended to its existing work')
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
    const first = await runManagerProgram(
      hooks,
      sessionID,
      resumeCall({ name: 'devops', charge: 'ORIGINAL-WORK' }),
    )
    assert.equal(envelopeOk(first), true, first)
    assert.equal(outcomeOf(first), 'created', first)
    assert.equal(createdIds.length, 1)

    // DevOps 忙时再次 resume：不建立新任务，而是向既有任务追加指导。
    const second = await runManagerProgram(
      hooks,
      sessionID,
      resumeCall({ name: 'devops', charge: 'NEW-ASSIGNMENT' }),
    )
    assert.equal(envelopeOk(second), true, second)
    assert.equal(outcomeOf(second), 'nudged',
      'the busy DevOps charge becomes guidance on the original work, not a new task')
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
      const result = await runManagerProgram(
        hooks,
        sessionID,
        resumeCall({ name: variant, charge: 'RUN' }),
      )
      // 未结识的 name 一律 fail closed：不得建立任何 DevOps 派工。
      assert.equal(envelopeOk(result), false,
        `variant '${variant}' must not establish a DevOps handover`)
      // 'DevOps' 仍被固定 DevOps 的评审门认出；其余写法是不结识的名字。
      const expected = variant === 'DevOps' ? REVIEW_PENDING : PERSON_UNKNOWN
      assert.equal(refusalCode(result), expected, `variant '${variant}' refusal code`)
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
    const forkDevopsBeforeReview = await runManagerProgram(
      hooks,
      sessionID,
      forkCall({ name: 'devops', charge: 'FORK-DEVOPS-ATTEMPT' }),
    )
    assert.equal(refusalCode(forkDevopsBeforeReview), REVIEW_PENDING,
      'the review gate must refuse a DevOps fork before the review is accepted')
    assert.equal(createdIds.length, 0, 'No child may be placed for invalid calling')

    // 评审接纳后，Manager 依然不能 fork DevOps：派生的 calling 不可 fork。
    await injectAcceptedAssessment(runtime, sessionID)
    const forkDevops = await runManagerProgram(
      hooks,
      sessionID,
      forkCall({ name: 'devops', charge: 'FORK-DEVOPS-ATTEMPT' }),
    )
    assert.equal(refusalCode(forkDevops), UNKNOWN_CALLING,
      'the DevOps calling derived from the name is not forkable')
    assert.equal(createdIds.length, 0, 'No child may be placed for invalid calling')

    // 显式 calling=devops 也严格拒绝。
    const callingDevops = await runManagerProgram(
      hooks,
      sessionID,
      forkCall({ calling: 'devops', name: 'Operator1', charge: 'FORK-DEVOPS-ATTEMPT' }),
    )
    assert.equal(refusalCode(callingDevops), CALLING_CONFLICT,
      'the explicit calling conflicts with the calling derived from the name')
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
    const refused = await runManagerProgram(
      hooks,
      sessionID,
      resumeCall({ name: 'devops', charge: 'DEVOPS-RUN-TESTS-BEFORE-REVIEW' }),
    )
    assert.equal(refusalCode(refused), REVIEW_PENDING)
    assert.deepEqual(durableSnapshot(directory), durableBefore,
      'the refused dispatch must not append any durable bytes')
    assert.equal(createdIds.length, 0)
    assert.equal(runtime.prompts.length, 0)

    // 评审接纳后，同一个 charge 正常派工。
    await injectAcceptedAssessment(runtime, sessionID)
    const admitted = await runManagerProgram(
      hooks,
      sessionID,
      resumeCall({ name: 'devops', charge: 'DEVOPS-RUN-TESTS-BEFORE-REVIEW' }),
    )
    assert.equal(envelopeOk(admitted), true, admitted)
    assert.equal(outcomeOf(admitted), 'created', admitted)
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

    const dispatch = await runManagerProgram(
      hooks,
      sessionID,
      resumeCall({ name: 'devops', charge: 'REPAIR-THE-FINDINGS' }),
    )
    assert.equal(envelopeOk(dispatch), true, dispatch)
    assert.equal(outcomeOf(dispatch), 'created', dispatch)
    assert.equal(refusalCode(dispatch), null,
      'the repair window must still admit DevOps dispatch')
    assert.equal(createdIds.length, 1)

    const repairFork = await runManagerProgram(
      hooks,
      sessionID,
      forkCall({ name: 'Carl', charge: 'repair the findings' }),
    )
    assert.equal(envelopeOk(repairFork), true, repairFork)
    assert.equal(outcomeOf(repairFork), 'created',
      'a repair fork must not be refused for not being read-only')
    assert.equal(createdIds.length, 2)
  })
})

// 收口窗一：确认已经落盘（Stayed 两段式的第二笔只差收口）。此时再向绑定的固定
// DevOps 派新任务必须拒绝，且拒绝发生在任何持久化副作用之前。
test('WHAT[capability-enforcement-026] D09_relayed_tenure_refuses_new_devops_dispatch_with_zero_side_effects', async () => {
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const sessionID = 'ses-d09-confirmed'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    await injectAcceptedAssessment(runtime, sessionID) // Stayed

    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
    const surface = lifecycle.LifecycleSurface ?? lifecycle
    const relayed = await surface.submitReview(
      commonDir,
      sessionID,
      'relay-' + sessionID,
      'Manager',
      'run-d09',
      'call-d09-first',
      [{ key: 'opened', value: null }],
      [],
      '',
    )
    assert.equal(relayed.kind, 'Relayed', relayed.reason)
    const inspected = surface.inspected(commonDir, sessionID)
    assert.ok(inspected?.ActiveId, 'relay opened successor tenure')
    assert.equal(inspected.Delivered, false)

    const createdBefore = createdIds.length
    const promptsBefore = runtime.prompts.length
    const durableBefore = durableSnapshot(directory)

    const refused = await runManagerProgram(
      hooks,
      sessionID,
      resumeCall({ name: 'devops', charge: 'NEW-TASK-AFTER-CONFIRMATION' }),
    )
    // The unreviewed successor tenure refuses DevOps dispatch until reviewed
    assert.equal(refusalCode(refused), REVIEW_PENDING,
      'the unreviewed successor tenure must refuse new DevOps dispatch')
    assert.equal(envelopeOk(refused), false,
      'no handover may be reported in the close-out window')
    assert.equal(createdIds.length, createdBefore, 'the refused close-out dispatch must not place a child')
    assert.equal(runtime.prompts.length, promptsBefore, 'the refused close-out dispatch must not emit a prompt')
    assert.deepEqual(durableSnapshot(directory), durableBefore,
      'the refused close-out dispatch must not append any durable bytes')
  })
})

// 收口窗二：已评 Promoted。末任具备终局交付资格后，仍允许向固定 DevOps 派工完成实际收尾；
// 同时评审接纳前已有的只读 Engineer fork/resume 不被本门误伤。
test('WHAT[capability-enforcement-026] D10_promoted_review_admits_devops_dispatch_and_spares_readonly_engineer', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-d10-promoted'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)

    const forked = await runManagerProgram(
      hooks,
      sessionID,
      forkCall({ name: 'Dora', charge: 'readonly investigation' }),
    )
    assert.equal(envelopeOk(forked), true, forked)
    assert.equal(outcomeOf(forked), 'created', forked)
    assert.equal(createdIds.length, 1)

    const resumed = await runManagerProgram(
      hooks,
      sessionID,
      resumeCall({ name: 'Dora', charge: 'continue readonly investigation' }),
    )
    assert.equal(refusalCode(resumed), null,
      'the Engineer resume must not be refused by the DevOps review gate')
    assert.equal(envelopeOk(resumed), true, resumed)
    assert.equal(outcomeOf(resumed), 'nudged',
      'the charge of a busy Engineer becomes guidance appended to its existing work')
    assert.equal(createdIds.length, 1, 'resume of an existing Engineer must not create a new child')

    await injectAcceptedAssessment(runtime, sessionID, `inc-${sessionID}`, 'Promoted')
    const promptsBefore = runtime.prompts.length
    const admitted = await runManagerProgram(
      hooks,
      sessionID,
      resumeCall({ name: 'devops', charge: 'NEW-TASK-PROMOTED' }),
    )
    assert.equal(refusalCode(admitted), null,
      'a Promoted review retains DevOps dispatch capability for substantive closure')
    assert.equal(envelopeOk(admitted), true, admitted)
    assert.equal(outcomeOf(admitted), 'created', admitted)
    assert.equal(createdIds.length, 2)
    assert.equal(runtime.prompts.length, promptsBefore + 1)
  })
})

// 跨进程隔离：DevOps 派工门的能力不得以进程局部状态跨进程走私。produce 进程
// 在评审接纳前被拒、接纳后被放行；recover 进程在同一个工作区重新打开时，未评审
// 的会话仍被 assessment-pending-for-devops 拒绝，已确认的会话被 dispatch-closed
// 拒绝。两个进程必须真的不同。
test('WHAT[capability-enforcement-026] D11_cross_process_devops_dispatch_capability_isolation', async () => {
  const { mkdtempSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { fileURLToPath } = await import('node:url')
  const directory = mkdtempSync(join(tmpdir(), 'wxs-devops-isolation-'))
  const fixture = fileURLToPath(new URL('./support/devops-dispatch-isolation.fixture.mjs', import.meta.url))
  const env = { ...process.env, WXS_DEVOPS_ISOLATION_DIRECTORY: directory, NODE_TEST_CONCURRENCY: '1' }
  delete env.NODE_TEST_CONTEXT
  try {
    const producerSummary = await superviseNodeTest({
      files: [fixture], label: 'capability-026-producer', logPrefix: 'capability-026-producer',
      silenceMs: UNIT_VERDICT_SILENCE_MS, env: { ...env, WXS_DEVOPS_ISOLATION_PHASE: 'produce' },
      throwOnFailure: true,
    })
    assert.deepEqual(producerSummary, { passed: 1, failed: 0 })
    const producer = JSON.parse(readFileSync(join(directory, 'producer.json'), 'utf8'))
    assert.ok(Number.isSafeInteger(producer.pid) && producer.pid > 0)
    assert.notEqual(producer.pid, process.pid)
    assert.equal(producer.session, 'ses-devops-isolation')
    assert.equal(producer.admittedInProducer, true)

    const successorSummary = await superviseNodeTest({
      files: [fixture], label: 'capability-026-successor', logPrefix: 'capability-026-successor',
      silenceMs: UNIT_VERDICT_SILENCE_MS, env: { ...env, WXS_DEVOPS_ISOLATION_PHASE: 'recover' },
      throwOnFailure: true,
    })
    assert.deepEqual(successorSummary, { passed: 1, failed: 0 })
    const successor = JSON.parse(readFileSync(join(directory, 'successor.json'), 'utf8'))
    assert.ok(Number.isSafeInteger(successor.pid) && successor.pid > 0)
    assert.notEqual(successor.pid, process.pid)
    assert.notEqual(successor.pid, producer.pid,
      'the successor must be a distinct OS process from the producer')
    assert.equal(successor.unreviewedRefused, true)
    assert.equal(successor.confirmedRefused, true)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[capability-enforcement-026] D12_devops_session_reuse_property_lands_restart_void_before_handoff_without_conflict', async () => {
  await fc.assert(
    fc.asyncProperty(
      fc.tuple(
        fc.stringMatching(/^[A-Z0-9_-]{1,16}$/),
        fc.array(
          fc.record({
            charge: fc.stringMatching(/^[A-Z0-9_-]{1,16}$/),
            restartBefore: fc.boolean(),
          }),
          { minLength: 1, maxLength: 3 },
        ),
      ).map(([initCharge, continuations]) => {
        if (!continuations.some((c) => c.restartBefore)) {
          continuations[0].restartBefore = true
        }
        return [{ charge: initCharge, restartBefore: false }, ...continuations]
      }),
      async (operations) => {
        await withRestartablePlugin(async (start, _directory, fixture) => {
          const sessionID = `ses-d12-${++programSequence}`
          let currentHooks = await start()
          await fixture.withRuntime(async (runtime) => {
            await acceptAuthorityRoot(runtime, sessionID, 'manager')
            await openIncumbency(runtime, sessionID)
            await injectAcceptedAssessment(runtime, sessionID)
          })

          const initial = await runAndSettleManagerProgram(
            currentHooks,
            sessionID,
            resumeCall({ name: 'devops', charge: 'INIT-' + operations[0].charge, intent: 'PROCEED-SOLO-IF-BUSY' }),
          )
          assert.equal(envelopeOk(initial), true, `Initial dispatch failed: ${rendered(initial)}`)
          assert.equal(outcomeOf(initial), 'created', initial)
          assert.equal(fixture.createdIds.length, 1, 'DevOps initial creation must place exactly 1 child session')
          const devopsChildId = fixture.createdIds[0]

          let initialLogicalRun
          let initialAuthorityRoot
          await fixture.withRuntime(async (runtime) => {
            const profile = dispatch.projectionObservation(runtime.journal, devopsChildId).activeLogicalRun
            assert.ok(profile, 'devops child must have an active logical run')
            initialLogicalRun = profile.logicalRun
            initialAuthorityRoot = profile.authorityRoot
          })

          let currentLogicalRun = initialLogicalRun
          let currentAuthorityRoot = initialAuthorityRoot

          for (let i = 1; i < operations.length; i++) {
            const op = operations[i]

            if (op.restartBefore) {
              // Start a second plugin incarnation without stopping the previous one,
              // simulating host crash recovery where in-memory pendingRuns is empty
              // but durable child session authority remains active.
              currentHooks = await start()

              // crash-reconciliation-020: the load-phase void is a settlement that
              // lands at this incarnation's FIRST admission, not at boot. Before
              // dispatch the durable projection must still hold the previous
              // active logical run and root.
              await fixture.withRuntime(async (runtime) => {
                const preProfile = dispatch.projectionObservation(runtime.journal, devopsChildId).activeLogicalRun
                assert.ok(preProfile, 'Durable child authority must remain active across restart before next dispatch')
                assert.equal(
                  preProfile.logicalRun,
                  currentLogicalRun,
                  'Active logical run identity must survive restart intact',
                )
                assert.equal(
                  preProfile.authorityRoot,
                  currentAuthorityRoot,
                  'Active authority root must survive restart intact',
                )
              })
            }

            const resumed = await runAndSettleManagerProgram(
              currentHooks,
              sessionID,
              resumeCall({ name: 'devops', charge: `ASSIGN-${i}-${op.charge}`, intent: 'PROCEED-SOLO-IF-BUSY' }),
            )

            assert.equal(
              envelopeOk(resumed),
              true,
              `Dispatch ${i} must succeed without ActiveRunIdentityConflict: ${rendered(resumed)}`,
            )

            await fixture.withRuntime(async (runtime) => {
              const currentProfile = dispatch.projectionObservation(runtime.journal, devopsChildId).activeLogicalRun
              assert.ok(currentProfile, 'devops child must hold an active logical run after dispatch')
              if (op.restartBefore) {
                // crash-reconciliation-020: the void retires the previous run BEFORE
                // the next handoff, so this dispatch creates a fresh lawful root —
                // never a second parallel authority, never an identity conflict.
                assert.notEqual(
                  currentProfile.logicalRun,
                  currentLogicalRun,
                  'Restart void must retire the previous run before the new root is registered',
                )
                assert.notEqual(
                  currentProfile.authorityRoot,
                  currentAuthorityRoot,
                  'Restart must replace the retired authority root with exactly one fresh root',
                )
              } else {
                // interaction-authority-013A: same-process reuse continues the
                // active run via ManagedDelegationAssignment, no new root.
                assert.equal(
                  currentProfile.logicalRun,
                  currentLogicalRun,
                  'Active logical run identity must be preserved across same-process continuation',
                )
                assert.equal(
                  currentProfile.authorityRoot,
                  currentAuthorityRoot,
                  'Authority root identity must be preserved without second root collision',
                )
              }
              currentLogicalRun = currentProfile.logicalRun
              currentAuthorityRoot = currentProfile.authorityRoot
            })
          }
        })
      },
    ),
    { numRuns: 16, seed: 0x53554242 },
  )
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

