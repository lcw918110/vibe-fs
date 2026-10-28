import test from 'node:test'
import assert from 'node:assert/strict'
import fc from 'fast-check'
import {readFileSync} from 'node:fs'
import * as codec from '../../../dist/OpenCode/Host/HostSignalSurface.js'
import {LatencyWatchdogFactory_createObservedLatencyWatchdog as create} from '../../../dist/OpenCode/Host/LatencyWatchdog.js'

const flush = async () => {
  for (let index = 0; index < 12; index++) await Promise.resolve()
}

function rig() {
  const events = []
  const timers = []
  let finishAbort
  let acceptContinuation
  let now = 0

  const watchdog = create(
    () => now,
    (_duration, fire) => {
      const timer = {fire, Dispose() {}}
      timers.push(timer)
      return timer
    },
    () => new Promise(resolve => {finishAbort = resolve}),
    (_session, _text, accepted) => {
      events.push('continue')
      acceptContinuation = accepted
      return new Promise(() => {})
    },
    () => true,
    () => 'Continue unfinished Engineer work.',
    event => events.push(event),
    (_session, reason) => events.push('returned:' + reason),
    () => events.push('observer-dropped'),
    {
      ThresholdMs: () => 10,
      ObserveCompleted: () => events.push('completed'),
      ObserveCensored: () => events.push('censored'),
      Forget: () => {},
    },
  )

  return {
    watchdog,
    events,
    begin: run => watchdog.Begin('engineer', run),
    fire: () => {
      now = 12
      timers.at(-1).fire()
    },
    concludeAbort: accepted => finishAbort(accepted),
    accept: () => acceptContinuation(),
  }
}

test('WHAT[latency-degeneration-010] exact physical cancellation resumes while Abort receipt is still pending', async () => {
  const trial = rig()
  try {
    trial.begin('provider-before-reset')
    trial.fire()
    trial.watchdog.OnProviderCancelled('engineer', 'foreign-run')
    assert.equal(trial.events.includes('continue'), false)

    trial.watchdog.OnProviderCancelled('engineer', 'provider-before-reset')
    assert.equal(trial.events.filter(event => event === 'continue').length, 1)
    assert.equal(trial.events.filter(event => event === 'censored').length, 1)
    assert.equal(trial.events.some(event => event.startsWith('returned:')), false)

    trial.concludeAbort(false)
    await flush()
    assert.equal(trial.events.some(event => event.startsWith('returned:')), false,
      'a late rejected Abort receipt cannot erase an already dispatched continuation')

    trial.accept()
    trial.accept()
    await flush()
    assert.equal(trial.events.filter(event => event === 'latency-degeneration-resumed').length, 1)
    assert.equal(trial.events.includes('completed'), false)
  } finally {
    trial.watchdog.Dispose()
  }
})

test('WHAT[latency-degeneration-010] fast-check: cancellation and Abort receipt order cannot strand or double-settle Engineer work', async () => {
  await fc.assert(fc.asyncProperty(
    fc.boolean(),
    fc.boolean(),
    async (receiptFirst, accepted) => {
      const trial = rig()
      try {
        trial.begin('exact-run')
        trial.fire()

        if (receiptFirst) {
          trial.concludeAbort(accepted)
          await flush()
        }

        trial.watchdog.OnProviderCancelled('engineer', 'exact-run')
        await flush()

        if (!receiptFirst) {
          trial.concludeAbort(accepted)
          await flush()
        }

        const shouldResume = !receiptFirst || accepted
        assert.equal(trial.events.filter(event => event === 'continue').length,
          shouldResume ? 1 : 0)
        assert.equal(trial.events.filter(event => event.startsWith('returned:')).length,
          shouldResume ? 0 : 1)

        if (shouldResume) {
          trial.accept()
          await flush()
          assert.equal(trial.events.filter(event => event === 'latency-degeneration-resumed').length, 1)
        }

        trial.watchdog.OnProviderCancelled('engineer', 'exact-run')
        await flush()
        assert.ok(trial.events.filter(event => event === 'continue').length <= 1)
        assert.ok(trial.events.filter(event => event.startsWith('returned:')).length <= 1)
      } finally {
        trial.watchdog.Dispose()
      }
    },
  ), {seed: 0x1A72026, numRuns: 120})
})

test('WHAT[latency-degeneration-010] a cancelled old Provider cannot restart an already active successor', async () => {
  const trial = rig()
  try {
    trial.begin('old-run')
    trial.fire()
    trial.begin('new-run')
    trial.watchdog.OnProviderCancelled('engineer', 'old-run')
    await flush()
    assert.equal(trial.watchdog.Snapshot().length, 1)
    assert.equal(trial.events.includes('continue'), false)
    assert.equal(trial.events.some(event => event.startsWith('returned:')), false)
  } finally {
    trial.watchdog.Dispose()
  }
})

test('WHAT[latency-degeneration-010] physical AbortError is not the superseded turn of an ordinary steer', () => {
  const message = name => ({
    type: 'message.updated',
    sessionID: 'engineer',
    properties: {
      info: {
        sessionID: 'engineer',
        role: 'assistant',
        id: 'provider-run',
        parentID: 'physical-user-message',
        time: {created: 1},
        error: {name, message: name},
      },
    },
  })
  const cancelled = codec.tryDecodeExactProviderTerminal(message('AbortError'))
  const superseded = codec.tryDecodeExactProviderTerminal(message('SupersededError'))
  assert.equal(cancelled?.failure, 'UserCancelled')
  assert.equal(cancelled?.providerRun, 'provider-run')
  assert.equal(superseded?.failure, 'Superseded')

  const source = readFileSync(
    new URL('../../../src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs', import.meta.url),
    'utf8',
  )
  const wiring = source.slice(
    source.indexOf('let settleLatencyWatchdog raw ='),
    source.indexOf('let observeEvent raw ='),
  )
  assert.match(wiring,
    /HostProviderTerminalOutcome\.Cancelled ExecutionFailure\.UserCancelled\s*->\s*latencyWatchdog\.OnProviderCancelled/)
  assert.match(wiring, /\| _ ->\s*latencyWatchdog\.Discard/)
})
