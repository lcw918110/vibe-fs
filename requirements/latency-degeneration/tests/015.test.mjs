import test from 'node:test'
import assert from 'node:assert/strict'
import fc from 'fast-check'
import {LatencyWatchdogFactory_createObservedLatencyWatchdog as create} from '../../../dist/OpenCode/Host/LatencyWatchdog.js'

const flush = async () => {for (let i = 0; i < 16; i++) await Promise.resolve()}

// Two interrupts can be in flight for one session: the second measurement's
// timer fires while the first interrupt's Host receipt is still pending. The
// old state kept ONE recovery slot and ONE parked slot per session, so the
// second interrupt destroyed the first one's parked obligation, and its
// ACCEPTED receipt then found neither a measurement nor its own parked entry
// and settled silently: the accepted AbortSession had stopped the session
// loop, no continuation was sent and no Failed returned — the parent's Join
// waited forever. Regression for both receipt orders.
function rig() {
  const events = [], timers = []
  const receipts = []
  const watchdog = create(
    () => 12,
    (_ms, fire) => {const t = {fire, Dispose() {}}; timers.push(t); return t},
    () => new Promise(resolve => {receipts.push(resolve)}),
    (_s, _t, accepted) => {events.push('continue'); return Promise.resolve('')},
    () => true,
    () => 'Continue.',
    kind => events.push(kind),
    (_s, reason) => events.push('returned:' + reason),
    () => events.push('observer-dropped'),
    {ThresholdMs: () => 10, ObserveCompleted: () => events.push('completed'),
      ObserveCensored: () => events.push('censored'), Forget: () => {}},
  )
  return {watchdog, events, timers,
    begin: run => watchdog.Begin('engineer', run),
    stepEnd: run => watchdog.CompleteStep('engineer', run),
    fire: () => timers.at(-1).fire(),
    settle: (interrupt, accepted) => receipts[interrupt - 1](accepted)}
}

const twoInterruptWindow = async steps => {
  const r = rig()
  try {
    r.begin('run-1')
    r.fire()
    r.stepEnd('run-1')
    r.begin('run-2')
    r.fire()
    r.stepEnd('run-2')
    for (const [interrupt, accepted] of steps) { r.settle(interrupt, accepted); await flush() }
    await flush()
    const continued = r.events.filter(e => e === 'continue').length
    const returned = r.events.some(e => e.startsWith('returned:'))
    const acceptedCount = steps.filter(([, a]) => a).length
    // WHAT[latency-degeneration-015]: every accepted interrupt settles the
    // session loop exactly once (one continuation; later accepted receipts are
    // absorbed, never a second physical prompt), and a refused receipt stops
    // nothing and stays silent. Silence after an acceptance is the lost
    // continuation that used to strand the parent's Join.
    assert.equal(continued, acceptedCount > 0 ? 1 : 0, JSON.stringify({steps, events: r.events}))
    assert.equal(returned, false, JSON.stringify({steps, events: r.events}))
    // Each accepted-parked request records exactly one censored sample; a
    // refused one records none.
    const censored = r.events.filter(e => e === 'censored').length
    assert.equal(censored, acceptedCount, JSON.stringify({steps, events: r.events}))
    assert.equal(r.events.includes('completed'), false)
  } finally { r.watchdog.Dispose() }
}

test('WHAT[latency-degeneration-015] fast-check: an accepted interrupt settles once in every two-receipt order', async () => {
  await fc.assert(fc.asyncProperty(
    fc.tuple(fc.constantFrom(true, false), fc.constantFrom(true, false), fc.constantFrom(1, 2))
      .map(([first, second, last]) => {
        // (receipt outcome of gen1, receipt outcome of gen2, which arrives first)
        return last === 1 ? [[1, first], [2, second]] : [[2, second], [1, first]]
      }),
    async steps => twoInterruptWindow(steps),
  ), {seed: 0xDE6E015, numRuns: 24})
})

test('WHAT[latency-degeneration-015] first receipt accepted, second refused: the loop is continued (regression)', async () => {
  await twoInterruptWindow([[1, true], [2, false]])
})

test('WHAT[latency-degeneration-015] first receipt refused, second accepted: the loop is continued (regression)', async () => {
  await twoInterruptWindow([[2, false], [1, true]])
})

test('WHAT[latency-degeneration-015] both receipts accepted: one continuation, the second is absorbed with its sample', async () => {
  await twoInterruptWindow([[1, true], [2, true]])
  const r = rig()
  try {
    r.begin('run-1'); r.fire(); r.stepEnd('run-1')
    r.begin('run-2'); r.fire(); r.stepEnd('run-2')
    r.settle(1, true)
    await flush()
    assert.deepEqual(r.events.filter(e => e === 'continue').length, 1)
    r.settle(2, true)
    await flush()
    assert.equal(r.events.filter(e => e === 'continue').length, 1,
      'the second accepted receipt must not dispatch a second physical continuation')
    assert.equal(r.events.includes('latency-degeneration-interrupt-absorbed'), true)
    assert.equal(r.events.filter(e => e === 'censored').length, 2)
  } finally { r.watchdog.Dispose() }
})

test('WHAT[latency-degeneration-015] both receipts refused: nothing is continued and nothing fails', async () => {
  await twoInterruptWindow([[2, false], [1, false]])
})

// A normal user cancel (no watchdog interrupt in flight, no recovery) must
// neither resume work nor report failure: the cancel stands.
test('WHAT[latency-degeneration-015] a plain user cancel without an interrupt stays a cancel', async () => {
  const r = rig()
  try {
    r.begin('run-1')
    r.watchdog.OnProviderCancelled('engineer', 'run-1')
    await flush()
    assert.equal(r.events.includes('continue'), false)
    assert.equal(r.events.some(e => e.startsWith('returned:')), false)
    assert.equal(r.watchdog.Snapshot().length, 0)

    // And a user cancel of a newer run while an older interrupt is still
    // pending resumes the owed continuation exactly once (WHAT[011]).
    const s = rig()
    s.begin('run-1')
    s.fire()
    s.stepEnd('run-1')
    s.begin('run-2')
    s.watchdog.OnProviderCancelled('engineer', 'run-2')
    await flush()
    assert.equal(s.events.filter(e => e === 'continue').length, 1)
    assert.equal(s.events.filter(e => e === 'censored').length, 1)
    s.settle(1, true)
    await flush()
    assert.equal(s.events.filter(e => e === 'continue').length, 1,
      'the already dispatched continuation cannot be dispatched again by the late receipt')
  } finally { r.watchdog.Dispose() }
})
