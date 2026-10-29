import test from 'node:test'
import assert from 'node:assert/strict'
import { createLatencyWatchdog, fakeDetector, makePorts } from './support/watchdog.mjs'

test('WHAT[latency-degeneration-005] Cancel 与 OnIdle 清理在途且不记样本，ForgetDetector 转发', async () => {
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )
  state.managed.set('sess-5', true)
  state.managed.set('sess-6', true)
  state.now = 0
  watchdog.Begin('sess-5', 'm5')
  watchdog.Begin('sess-6', 'm6')
  state.now = 200
  watchdog.ForgetDetector('sess-5')
  assert.equal(detector.calls.some(c => c.kind === 'forget' && c.sessionId === 'sess-5'), true)
  watchdog.Cancel('sess-5')
  watchdog.OnIdle('sess-6')
  assert.equal(detector.completed().length, 0)
  assert.equal(detector.censored().length, 0)
  assert.equal(state.diagnostics.some(d => d.kind === 'latency-degeneration-idle-without-completion'), true)
  assert.equal(state.schedules[0].isDisposed(), true)
  assert.equal(state.schedules[1].isDisposed(), true)
  assert.equal(watchdog.Snapshot().length, 0)
})

test('WHAT[latency-degeneration-005] 非受管不启动，Dispose 幂等且之后 Begin 无效', async () => {
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )
  watchdog.Begin('sess-unmanaged', 'run-u')
  assert.equal(state.schedules.length, 0)
  watchdog.Complete('sess-unmanaged', 'run-u')
  assert.equal(detector.completed().length, 0)
  state.managed.set('sess-d', true)
  watchdog.Begin('sess-d', 'run-d')
  assert.equal(state.schedules.length, 1)
  watchdog.Dispose()
  watchdog.Dispose()
  assert.equal(state.schedules[0].isDisposed(), true)
  watchdog.Begin('sess-d', 'run-d2')
  assert.equal(state.schedules.length, 1)
  watchdog.Complete('sess-d', 'run-d2')
  assert.equal(detector.completed().length, 0)
})
