import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { rig, flush } from './support/observed-watchdog.mjs'

// Real OpenCode 1.18.29 order (observed on the Host event stream, see WHAT [013]):
//   assistant message.updated (started) → tool part pending → running → completed
//   → assistant message.updated finish=tool-calls + time.completed.
// The tool runs INSIDE the provider step. Without ToolStarted the watchdog timed
// join/bash/consultation execution as model latency and aborted the session mid-tool.
test('WHAT[latency-degeneration-013] a tool start ends the model request: a long tool is never interrupted', async () => {
  const r = rig()
  r.watchdog.Begin('engineer', 'run-1')
  r.at(3)
  r.watchdog.ToolStarted('engineer')
  assert.equal(r.timers[0].disposed, true, 'the timer is cancelled when the tool starts')
  assert.deepEqual(r.events, ['completed:3'], 'the sample is the model time, not the tool time')
  r.at(100000)
  r.timers[0].fire()
  r.watchdog.CompleteStep('engineer', 'run-1') // finish=tool-calls arrives after the tool
  await flush()
  assert.deepEqual(r.events, ['completed:3'])
  r.watchdog.Dispose()
})

test('WHAT[latency-degeneration-013] parallel tool calls and a tool start without a measurement are no-ops', () => {
  const r = rig()
  r.watchdog.ToolStarted('engineer')
  r.watchdog.Begin('engineer', 'run-1')
  r.watchdog.ToolStarted('engineer')
  r.watchdog.ToolStarted('engineer')
  r.watchdog.ToolStarted('')
  assert.equal(r.events.filter(e => e.startsWith('completed')).length, 1)
  r.watchdog.Begin('engineer', 'run-2')
  assert.equal(r.timers.length, 2, 'the next model step is measured again')
  r.watchdog.Dispose()
})

test('WHAT[latency-degeneration-013] a tool starting while the interrupt is in flight keeps the recovery', async () => {
  const r = rig()
  r.watchdog.Begin('engineer', 'run-1')
  r.at(12)
  r.timers[0].fire()
  r.watchdog.ToolStarted('engineer')
  r.conclude(true)
  await flush()
  assert.deepEqual(r.events.filter(e => e === 'continue' || e === 'censored'), ['censored', 'continue'])
  r.watchdog.Dispose()
})

test('WHAT[latency-degeneration-013] production wiring: tool.execute.before ends the measurement', () => {
  const hooks = readFileSync(new URL('../../../src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs', import.meta.url), 'utf8')
  const before = hooks.slice(hooks.indexOf('let toolBefore (toolInput: obj)'), hooks.indexOf('let toolAfter'))
  assert.match(before, /latencyWatchdog\.ToolStarted sessionId/)
})
