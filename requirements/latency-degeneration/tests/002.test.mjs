import test from 'node:test'
import assert from 'node:assert/strict'
import { createLatencyWatchdog, fakeDetector, makePorts, tick } from './support/watchdog.mjs'

test('WHAT[latency-degeneration-002] 到点中断成立记删失样本并发续行文本，快照字段符合契约', async () => {
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )
  const threshold = detector.ThresholdMs('sess-2')
  state.managed.set('sess-2', true)
  state.texts.set('sess-2', 'CONTINUE-PROSE')
  state.now = 0
  watchdog.Begin('sess-2', 'run-2')
  assert.equal(state.schedules.length, 1)
  const preSnap = watchdog.Snapshot()
  assert.equal(preSnap.length, 1)
  assert.equal(preSnap[0].SessionId, 'sess-2')
  assert.equal(preSnap[0].StartedAt, 0)
  assert.equal(preSnap[0].ThresholdMs, threshold)
  assert.equal(preSnap[0].Armed, true)
  state.now = threshold + 1000
  state.schedules[0].fire()
  await tick()
  assert.equal(detector.censored().length, 1)
  assert.equal(detector.censored()[0].sessionId, 'sess-2')
  assert.ok(detector.censored()[0].censoredMs >= threshold, 'censored sample must be at least the threshold')
  assert.equal(state.abortCalls.length, 1)
  assert.equal(state.continueCalls.length, 1)
  assert.equal(state.continueCalls[0].sessionId, 'sess-2')
  assert.equal(state.continueCalls[0].prompt, 'CONTINUE-PROSE')
  assert.equal(detector.completed().length, 0)
  assert.equal(watchdog.Snapshot().length, 0, 'measurement was removed after interrupt')
})
