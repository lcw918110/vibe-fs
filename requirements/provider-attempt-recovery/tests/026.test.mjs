import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
const runtimePath = { stateRoot: journal.JournalSurface_stateRoot }
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
    await journal.JournalSurface_appendAuthorityLogicalRunClosed(await runtimePath.stateRoot(directory), 'ses-026-terminal', 'retire-026-terminal')

    // 先信号后等待：对已停 key 的迟到等待者立即解析（022/027 先例），否则挂 30 秒
    providerAttemptStopFence.SignalStop('ses-026-terminal', 'run-026-terminal')

    await owner.continueConfirmedFailure(created.journal, sessionPort, eventPort, {
      session: 'ses-026-terminal', physicalMessage: 'msg-026-terminal',
      authorityRoot: 'msg-026-terminal-late-root', providerRun: 'run-026-terminal',
      role: null, failure: 'ProviderTransient', error: 'late provider error',
      continuationPrompt: 'probe continuation prompt',
    })

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
