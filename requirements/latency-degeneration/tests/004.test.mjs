import test from 'node:test'
import assert from 'node:assert/strict'
import * as ProviderLanguageSurface from '../../../dist/Participant/Provider/LanguageSurface.js'
import { createLatencyWatchdog, fakeDetector, makePorts, tick, wireHostEvents } from './support/watchdog.mjs'

test('WHAT[latency-degeneration-004] idle precedes abort receipt without losing the continuation', async () => {
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )
  state.managed.set('session', true)
  state.texts.set('session', 'continue')
  state.abortMode = 'deferred'
  watchdog.Begin('session', 'run')
  state.now = detector.ThresholdMs('session')
  state.schedules[0].fire()
  await tick()
  watchdog.OnIdle('session')
  state.abortResolve(true)
  await tick()
  assert.deepEqual(state.continueCalls, [{ sessionId: 'session', prompt: 'continue' }])
  assert.equal(detector.censored().length, 1)
  assert.equal(watchdog.Snapshot().length, 0)
})

test('WHAT[latency-degeneration-004] 物理接线验证：实际续行接线与 resources 双语文件无缝读取', async () => {
  assert.equal(ProviderLanguageSurface.exists('zh-CN', 'runtime/latency-degeneration'), true)
  assert.equal(ProviderLanguageSurface.exists('en', 'runtime/latency-degeneration'), true)
  const zhText = ProviderLanguageSurface.readText('zh-CN', 'runtime/latency-degeneration')
  const enText = ProviderLanguageSurface.readText('en', 'runtime/latency-degeneration')
  assert.match(zhText, /你的上一条回复因为耗时远远超过本会话的正常水平而被中断/)
  assert.match(enText, /Your previous response was interrupted because it took far longer/)
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  ports.Text = sessionId => {
    const lang = sessionId.endsWith('-en') ? 'en' : 'zh-CN'
    return ProviderLanguageSurface.readText(lang, 'runtime/latency-degeneration')
  }
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )
  const settleLatencyWatchdog = wireHostEvents(watchdog)
  state.managed.set('sess-zh', true)
  state.managed.set('sess-en', true)
  const threshold = detector.ThresholdMs('sess-zh')
  state.now = 0
  settleLatencyWatchdog({
    type: 'message.updated', sessionID: 'sess-zh',
    properties: { info: { sessionID: 'sess-zh', role: 'assistant', id: 'ast-zh', parentID: 'usr-zh', time: { created: 0 } } },
  })
  state.now = threshold + 500
  state.schedules[0].fire()
  await tick()
  assert.equal(state.continueCalls.length, 1)
  assert.equal(state.continueCalls[0].sessionId, 'sess-zh')
  assert.equal(state.continueCalls[0].prompt, zhText)
})
