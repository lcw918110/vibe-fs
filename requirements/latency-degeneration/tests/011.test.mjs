import test from 'node:test'
import assert from 'node:assert/strict'
import fc from 'fast-check'
import {LatencyWatchdogFactory_createObservedLatencyWatchdog as create} from '../../../dist/OpenCode/Host/LatencyWatchdog.js'

const flush = async () => {for (let i = 0; i < 16; i++) await Promise.resolve()}

// The Host interrupt is AbortSession: once accepted it stops the whole session
// loop, including a tool execution or a newer model step that started while
// the interrupt promise was pending. Whatever ended the measured request in
// that window, an accepted interrupt must lead to exactly one continuation
// (or an explicit Failed return) — never silence, which leaves the parent's
// Join waiting forever (Aborted is observation-only there).
function rig() {
  const events = [], timers = []
  let finishAbort, now = 0
  const watchdog = create(
    () => now,
    (_ms, fire) => {const t = {fire, Dispose() {}}; timers.push(t); return t},
    () => new Promise(resolve => {finishAbort = resolve}),
    (_s, _t, accepted) => {events.push('continue'); accepted(); return Promise.resolve('')},
    () => true, () => 'Continue.',
    kind => events.push(kind),
    (_s, reason) => events.push('returned:' + reason),
    () => events.push('observer-dropped'),
    {ThresholdMs: () => 10, ObserveCompleted: () => events.push('completed'),
      ObserveCensored: () => events.push('censored'), Forget: () => {}},
  )
  return {watchdog, events, fire: () => {now = 12; timers[0].fire()}, conclude: v => finishAbort(v)}
}

const window = fc.array(fc.constantFrom('step-end', 'final', 'failure', 'next-start', 'next-cancelled', 'next-discarded', 'idle'),
  {minLength: 1, maxLength: 5})

test('WHAT[latency-degeneration-011] fast-check: an accepted interrupt resumes exactly once whatever ended the request first', async () => {
  await fc.assert(fc.asyncProperty(window, async steps => {
    const r = rig()
    try {
      r.watchdog.Begin('engineer', 'run-1')
      r.fire()
      assert.ok(r.events.includes('latency-degeneration-interrupt'), 'the interrupt reason is visible')
      for (const step of steps) {
        if (step === 'step-end') r.watchdog.CompleteStep('engineer', 'run-1')
        else if (step === 'final') r.watchdog.Complete('engineer', 'run-1')
        else if (step === 'failure') r.watchdog.Discard('engineer', 'run-1')
        else if (step === 'next-start') r.watchdog.Begin('engineer', 'run-2')
        else if (step === 'next-cancelled') r.watchdog.OnProviderCancelled('engineer', 'run-2')
        else if (step === 'next-discarded') r.watchdog.Discard('engineer', 'run-2')
        else r.watchdog.OnIdle('engineer')
      }
      r.conclude(true)
      await flush()
      // A final Completed with no later step means the work was delivered.
      const firstEnd = steps.find(s => s !== 'idle' && s !== 'next-discarded' && s !== 'next-cancelled')
      const delivered = firstEnd === 'final' && !steps.slice(steps.indexOf('final')).includes('next-start')
      const continued = r.events.filter(e => e === 'continue').length
      const returned = r.events.filter(e => e.startsWith('returned:')).length
      assert.equal(continued + returned, delivered ? 0 : 1, JSON.stringify({steps, events: r.events}))
      // One request, one sample: complete when it delivered first, else censored.
      const samples = r.events.filter(e => e === 'completed' || e === 'censored')
      assert.ok(samples.length <= 1, JSON.stringify({steps, events: r.events}))
      if (firstEnd === 'final') assert.deepEqual(samples, ['completed'])
      else assert.equal(samples.includes('completed'), false)
    } finally {r.watchdog.Dispose()}
  }), {seed: 0xDE6E011, numRuns: 300})
})

test('WHAT[latency-degeneration-011] tool boundary before an accepted interrupt: continuation is sent (regression)', async () => {
  const r = rig()
  r.watchdog.Begin('engineer', 'run-1')
  r.fire()
  r.watchdog.CompleteStep('engineer', 'run-1')
  r.conclude(true)
  await flush()
  assert.deepEqual(r.events.filter(e => e === 'continue' || e === 'censored'), ['censored', 'continue'])
  r.watchdog.Dispose()
})

test('WHAT[latency-degeneration-011] refused interrupt after the request ended on its own stops nothing and returns nothing', async () => {
  await fc.assert(fc.asyncProperty(fc.constantFrom('step-end', 'failure'), fc.boolean(), async (end, refuse) => {
    const r = rig()
    r.watchdog.Begin('engineer', 'run-1')
    r.fire()
    if (end === 'step-end') r.watchdog.CompleteStep('engineer', 'run-1')
    else r.watchdog.Discard('engineer', 'run-1')
    r.conclude(refuse ? false : Promise.reject(new Error('host down')))
    await flush()
    assert.equal(r.events.includes('continue'), false)
    assert.equal(r.events.some(e => e.startsWith('returned:')), false)
    r.watchdog.Dispose()
  }), {seed: 0xDE6E012, numRuns: 20})
})

test('WHAT[latency-degeneration-011] a final Completed terminal before the receipt finishes the work: no extra continuation', async () => {
  const r = rig()
  r.watchdog.Begin('engineer', 'run-1')
  r.fire()
  r.watchdog.Complete('engineer', 'run-1')
  r.conclude(true)
  await flush()
  assert.equal(r.events.includes('continue'), false)
  r.watchdog.Dispose()
})

test('WHAT[latency-degeneration-011] a final Completed followed by a newer step before an accepted receipt: the loop was stopped, continue', async () => {
  const r = rig()
  r.watchdog.Begin('engineer', 'run-1')
  r.fire()
  r.watchdog.Complete('engineer', 'run-1')
  r.watchdog.Begin('engineer', 'run-2')
  r.conclude(true)
  await flush()
  assert.equal(r.events.filter(e => e === 'continue').length, 1)
  r.watchdog.Dispose()
})
