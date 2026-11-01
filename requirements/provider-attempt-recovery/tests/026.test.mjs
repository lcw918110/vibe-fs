import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import * as Observation from '../../../dist/Composition/Turn/Observation.js'
import * as identity from '../../../dist/Foundation/Identity.js'
import * as owner from '../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js'

test('WHAT[provider-attempt-recovery-026] missing active profile is terminal, never superseded', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-recovery-026-none-'))
  const created = await journal.JournalSurface_bootWithWriterId(directory, 'writer-026-none', 'runtime-026-none', 1, '2026-01-01T00:00:00Z')
  try {
    const verdict = await owner.continueSameProviderRecoveryVerdict(created.journal, 'ses-026-none', 'msg-root', 'run-026')
    assert.equal(verdict, 'Terminal')
  } finally {
    journal.JournalSurface_dispose(created.journal)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[provider-attempt-recovery-026] a different authority root stays a silent supersession', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-recovery-026-takeover-'))
  const created = await journal.JournalSurface_bootWithWriterId(directory, 'writer-026-takeover', 'runtime-026-takeover', 1, '2026-01-01T00:00:00Z')
  try {
    const accepted = await owner.acceptHumanRoot(created.journal, 'ses-026-takeover', 'msg-root', 'engineer')
    assert.equal(accepted.ok, true)
    const verdict = await owner.continueSameProviderRecoveryVerdict(created.journal, 'ses-026-takeover', 'msg-other', 'run-026')
    assert.equal(verdict, 'Superseded')
  } finally {
    journal.JournalSurface_dispose(created.journal)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[provider-attempt-recovery-026] terminal verdict notifies terminal failure on the real stop-or-continue path', async () => {
  const { mkdtempSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const EventsSurface = await import('../../../dist/OpenCode/Host/EventsSurface.js')
  const Workflow = await import('../../../dist/Participant/Provider/Attempt/Fallback/Workflow.js')
  const DispatchSurface = await import('../../../dist/Interaction/Dispatch/DispatchSurface.js')
  const { providerAttemptStopFence } = await import('wanxiangli/failure')

  const directory = mkdtempSync(join(tmpdir(), 'wxs-recovery-026-terminal-'))
  // 共享 journal 条目：appendAuthorityLogicalRunClosed 只复用既有共享条目，不另建 writer
  const created = await journal.JournalSurface_acquireSharedForWorkspace(directory, 1, '2026-01-01T00:00:00Z')
  assert.equal(created.ok, true, created.ok ? '' : JSON.stringify(created.error))
  try {
    // manager 使闭包证人走 Manager-life：retirementId 直接可用，无需先落源终端 fact
    const accepted = await owner.acceptHumanRoot(created.journal, 'ses-026-terminal', 'msg-026-terminal-root', 'manager')
    assert.equal(accepted.ok, true, 'authority root must be accepted before behavior test')

    // 事件假体：沿 016/027 先例（EventsSurface.create() + subscribe 捕获）
    const eventPort = EventsSurface.create()
    const terminals = []
    EventsSurface.subscribe(eventPort, (sessionId, outcome) => terminals.push({ sessionId, outcome }))

    // 可观察 session 端口：记录每次 SendPrompt，区分失败通知与催续（027 先例）
    const prompts = []
    const sessionPort = {
      SendPrompt: async (...args) => {
        prompts.push(args)
        return DispatchSurface.admittedWithReceipt('026-stub-receipt')
      },
    }

    // ProviderStarted 证据：让 requestKindFor 从真实持久化事实里读出 WorkMain
    const established = await owner.establishProviderRun(created.journal, 'ses-026-terminal', 'msg-026-terminal', 'run-026-terminal')
    assert.equal(established.ok, true, 'provider run must be established')

    // 迟到失败到来之前，logical run 已按 Manager-life 退休 durably 关闭：
    // active profile 随之消失，催续从此无从谈起。
    await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, 'ses-026-terminal', 'retire-026-terminal')

    // 先信号后等待：对已停 key 的迟到等待者立即解析（022/027 先例），否则挂 30 秒
    providerAttemptStopFence.SignalStop('ses-026-terminal', 'run-026-terminal')

    // turn 用编译构造器（ReconciledTurn extends Record，位置参数构造）。
    // root 故意不同于已关闭 run 的 root：同 root 会被 sameRunAlreadyClosed 判为
    // 陈旧失败而静默；不同的 root 才让缺失 profile 的 Terminal 判定走到通知。
    // Role=null：stopOrContinueLongWork 直达 notifyFailure(reason)，全程不触 SendPrompt。
    const turn = new Observation.ReconciledTurn(
      identity.SessionIdModule_create('ses-026-terminal'),
      identity.PhysicalUserMessageIdModule_create('msg-026-terminal'),
      identity.AuthorityRootUserMessageIdModule_create('msg-026-terminal-late-root'),
      identity.ProviderRunIdentityModule_create('run-026-terminal'),
      null,   // Role：短命角色，通知即终局
      null,   // Directory
      [],     // Parts
      null,   // Finish
      null,   // ErrorName
      null,   // Model
      null,   // Outcome
      null,   // Observation
    )

    // 真链路：continueAfterConfirmedFailure → retryConfirmedFailure → fence 'Stopped' →
    // continueSameProvider 判 Terminal 'No active authority profile' → notifyFailure。
    // 失败账的第一笔也由这条链自己写下（admitAuthorizedFailure）。
    await Workflow.continueAfterConfirmedFailure(
      sessionPort,
      null,        // rootWorkspace：Terminal 判定不触 continuation send
      eventPort,
      journal.JournalHandle__get_Journal(created.journal),
      turn,
      { tag: 6 },  // ExecutionFailure.ProviderTransient（Model.js 第 41 行：tag 6）
      'late provider error',  // 刻意不含目标短语：文案只能来自产品判定，不是测试回声
      'probe continuation prompt',
    )

    // 可观察失败断言：恰一条 Failed 通知，文案精确等于产品自判的缺失 profile 短语
    assert.equal(terminals.length, 1, JSON.stringify(terminals))
    assert.equal(terminals[0].outcome.kind, 'Failed', 'terminal outcome must be Failed')
    assert.equal(terminals[0].outcome.text, 'No active authority profile', 'failure reason must be the product-judged missing-profile verdict')
    // 催续观测：整条真实路径上不应发出任何 SendPrompt（无 MissingClosingReport、无 ProviderRetry）
    assert.equal(prompts.length, 0, JSON.stringify(prompts))
  } finally {
    journal.JournalSurface_dispose(created.journal)
    rmSync(directory, { recursive: true, force: true })
  }
})
