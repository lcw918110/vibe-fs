import test from 'node:test'
import assert from 'node:assert/strict'
import { createLatencyWatchdog, fakeDetector, makePorts, tick } from './support/watchdog.mjs'

test('WHAT[latency-degeneration-003] 中断被拒保留在途（解除武装），随后真实完成仍记完整样本', async () => {
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )
  const threshold = detector.ThresholdMs('sess-3')
  state.managed.set('sess-3', true)
  state.abortMode = 'refuse'
  state.now = 0
  watchdog.Begin('sess-3', 'run-3')
  state.now = threshold + 500
  state.schedules[0].fire()
  await tick()
  assert.equal(state.abortCalls.length, 1)
  assert.equal(detector.censored().length, 0, 'refused abort records no censored sample')
  assert.equal(state.continueCalls.length, 0, 'refused abort sends no continuation')
  assert.equal(state.diagnostics.some(d => d.kind === 'latency-degeneration-abort-refused'), true)
  const snap = watchdog.Snapshot()
  assert.equal(snap.length, 1)
  assert.equal(snap[0].Armed, false)
  state.now = threshold + 1200
  watchdog.Complete('sess-3', 'run-3')
  assert.equal(detector.completed().length, 1)
  assert.equal(detector.completed()[0].durationMs, threshold + 1200)
})
