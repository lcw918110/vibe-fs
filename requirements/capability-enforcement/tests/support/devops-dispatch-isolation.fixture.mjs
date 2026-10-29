// WHAT[capability-enforcement-026]: DevOps 派工门的能力不得以进程局部状态跨进程
// 走私。produce 进程在评审接纳前被拒、接纳后被放行；recover 进程在同一个工作区
// 重新打开时，未评审的会话仍被 assessment-pending-for-devops 拒绝，已确认的会话
// 被 REVIEW_PENDING 拒绝，且拒绝不产生任何持久化副作用或子会话。

import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import test from 'node:test'
import {
  acceptAuthorityRoot,
  openIncumbency,
  injectAcceptedAssessment,
  startPluginIncarnation,
} from '../../../verification-system/tests/support/plugin-fixture.mjs'
import * as lifecycle from '../../../../dist/Mission/Lifecycle/Surface.js'
import * as journalSurface from '../../../../dist/Persistence/Journal/Surface.js'
import * as workspaceEvents from '../../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'

const directory = process.env.WXS_DEVOPS_ISOLATION_DIRECTORY
const phase = process.env.WXS_DEVOPS_ISOLATION_PHASE
assert.equal(typeof directory, 'string')
assert.ok(directory.length > 0)
assert.ok(phase === 'produce' || phase === 'recover', 'unknown fixture phase must fail, not register zero tests')

const REVIEW_PENDING = 'assessment-pending-for-devops'
const DISPATCH_CLOSED = 'dispatch-closed'

const ISOLATED_SESSION = 'ses-devops-isolation'
const UNREVIEWED_SESSION = 'ses-devops-unreviewed'

// 派工门读插件正在读的同一份工作区事件库，与 fixture 的 lifecycleStore 同源。
const storeOf = async (target) => workspaceEvents.acquire(await journalSurface.JournalSurface_initializeStateRoot(target))

// 编程面成员的唯一入口：Manager 的 `todowrite-for-manager` 工具执行一段程序，
// 程序内调用 `this.resume`。
let programSequence = 0
const runManagerProgram = async (hooks, sessionID, body) =>
  await hooks.tool['todowrite-for-manager'].execute(
    {
      program: `class Js extends JsProgram { async run() { ${body} } }`,
      name: 'devops-dispatch-test-' + (++programSequence),
      intent: 'Wait for the actual manager dispatch receipt without extending the request window.',
    },
    { sessionID, agent: 'manager' },
  )

const resumeCall = (payload) => `return await this.resume(${JSON.stringify(payload)}, []);`

const rendered = (result) => String(result)
const envelopeString = (result, field) => {
  const match = new RegExp(`^${field} = "(.*)"$`, 'm').exec(rendered(result))
  return match === null ? null : match[1]
}
const envelopeOk = (result) => /^ok = true$/m.test(rendered(result))
const refusalCode = (result) => envelopeString(result, 'code')
const outcomeOf = (result) => envelopeString(result, 'outcome')

// 隔离的持久边界快照：拒绝路径不得追加任何字节。
const durableSnapshot = (target) => {
  const snapshot = []
  const visit = (relativePath) => {
    const entries = readdirSync(join(target, relativePath), { withFileTypes: true })
      .sort((left, right) => left.name.localeCompare(right.name))
    for (const entry of entries) {
      const path = join(relativePath, entry.name)
      if (entry.isDirectory()) {
        snapshot.push({ path, kind: 'directory' })
        visit(path)
      } else {
        assert.equal(entry.isFile(), true, 'the isolated durable boundary must contain only directories and regular files')
        snapshot.push({ path, kind: 'file', bytes: readFileSync(join(target, path)).toString('base64') })
      }
    }
  }
  visit('')
  return snapshot
}

const prepareWorkspace = () => {
  execFileSync('git', ['init', '--quiet', directory])
}

if (phase === 'produce') {
  test('WHAT[capability-enforcement-026] the producer refuses the DevOps dispatch before the review is accepted and admits it afterwards, leaving a durable confirmation', async () => {
    prepareWorkspace()
    const incarnation = await startPluginIncarnation(directory)
    try {
      await incarnation.withRuntime(async (runtime) => {
        await acceptAuthorityRoot(runtime, ISOLATED_SESSION, 'manager')
        await openIncumbency(runtime, ISOLATED_SESSION)

        const durableBefore = durableSnapshot(directory)

        // 评审接纳前向 DevOps 派工：拒绝，且零持久化副作用、零子会话。
        const refused = await runManagerProgram(
          incarnation.hooks,
          ISOLATED_SESSION,
          resumeCall({ name: 'devops', charge: 'ISOLATED-DEVOPS-CHARGE' }),
        )
        assert.equal(refusalCode(refused), REVIEW_PENDING, refused)
        assert.equal(envelopeOk(refused), false, refused)
        assert.deepEqual(durableSnapshot(directory), durableBefore,
          'the refused dispatch must not append any durable bytes')

        // 评审接纳后，同一个 charge 正常派工。
        await injectAcceptedAssessment(runtime, ISOLATED_SESSION)
        const admitted = await runManagerProgram(
          incarnation.hooks,
          ISOLATED_SESSION,
          resumeCall({ name: 'devops', charge: 'ISOLATED-DEVOPS-CHARGE' }),
        )
        assert.equal(envelopeOk(admitted), true, admitted)
        assert.equal(outcomeOf(admitted), 'created', admitted)

        // 单笔接力交付落盘：后继进程必须读到后继任期已建立这一持久事实。
        const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
        const surface = lifecycle.LifecycleSurface ?? lifecycle
        const relayed = await surface.submitReview(
          commonDir,
          ISOLATED_SESSION,
          'relay-' + ISOLATED_SESSION,
          'Manager',
          'run-devops-isolation',
          'call-devops-isolation',
          [{ key: 'opened', value: null }],
          [],
          '',
        )
        assert.equal(relayed.kind, 'Relayed', relayed.reason)
        const inspected = surface.inspected(commonDir, ISOLATED_SESSION)
        assert.ok(inspected?.ActiveId, 'the producer relays to a successor tenure')
        assert.equal(inspected.Delivered, false)
      })
      writeFileSync(
        join(directory, 'producer.json'),
        JSON.stringify({ pid: process.pid, session: ISOLATED_SESSION, admittedInProducer: true }),
        { flag: 'wx' },
      )
    } finally {
      await incarnation.hooks.dispose()
    }
  })
} else {
  test('WHAT[capability-enforcement-026] a fresh process over the same workspace cannot smuggle the producer process-local DevOps dispatch capability', async () => {
    prepareWorkspace()
    const incarnation = await startPluginIncarnation(directory)
    try {
      await incarnation.withRuntime(async (runtime) => {
        // (a) 未评审的会话在本进程仍然被拒：produce 进程的内存放行不得泄漏。
        await acceptAuthorityRoot(runtime, UNREVIEWED_SESSION, 'manager')
        await openIncumbency(runtime, UNREVIEWED_SESSION)
        const durableBefore = durableSnapshot(directory)
        const unreviewed = await runManagerProgram(
          incarnation.hooks,
          UNREVIEWED_SESSION,
          resumeCall({ name: 'devops', charge: 'UNREVIEWED-DEVOPS-CHARGE' }),
        )
        assert.equal(refusalCode(unreviewed), REVIEW_PENDING, unreviewed)
        assert.equal(envelopeOk(unreviewed), false, unreviewed)
        assert.deepEqual(durableSnapshot(directory), durableBefore,
          'the unreviewed dispatch must not append any durable bytes')

        // (b) 接力后的后继任期在未评审前被拒：门读的是持久事实，不是进程局部记忆。
        const store = await storeOf(directory)
        const view = lifecycle.workView(store, ISOLATED_SESSION)
        assert.equal(view.active, true,
          'the successor process must read the relayed successor tenure the producer left')
        const closed = await runManagerProgram(
          incarnation.hooks,
          ISOLATED_SESSION,
          resumeCall({ name: 'devops', charge: 'CONFIRMED-DEVOPS-CHARGE' }),
        )
        assert.equal(refusalCode(closed), REVIEW_PENDING, closed)
        assert.equal(envelopeOk(closed), false, closed)
      })
      writeFileSync(
        join(directory, 'successor.json'),
        JSON.stringify({
          pid: process.pid,
          session: ISOLATED_SESSION,
          unreviewedRefused: true,
          confirmedRefused: true,
        }),
        { flag: 'wx' },
      )
    } finally {
      await incarnation.hooks.dispose()
    }
  })
}
