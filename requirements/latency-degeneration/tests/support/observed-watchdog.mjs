import { LatencyWatchdogFactory_createObservedLatencyWatchdog as create } from '../../../../dist/OpenCode/Host/LatencyWatchdog.js'

export const flush = async () => {
  for (let i = 0; i < 16; i++) await Promise.resolve()
}

export function rig(threshold = 10) {
  const events = []
  const timers = []
  let now = 0
  let finishAbort
  const watchdog = create(
    () => now,
    (ms, fire) => {
      const timer = { ms, fire, disposed: false, Dispose() { this.disposed = true } }
      timers.push(timer)
      return timer
    },
    () => {
      events.push('abort')
      return new Promise(resolve => { finishAbort = resolve })
    },
    (_session, _text, accepted) => {
      events.push('continue')
      accepted()
      return Promise.resolve('')
    },
    () => true,
    () => 'Continue.',
    kind => events.push(kind),
    (_session, reason) => events.push('returned:' + reason),
    () => {},
    {
      ThresholdMs: () => threshold,
      ObserveCompleted: (_session, ms) => events.push('completed:' + ms),
      ObserveCensored: () => events.push('censored'),
      Forget: () => {},
    },
  )
  return { watchdog, events, timers, at: time => { now = time }, conclude: value => finishAbort(value) }
}
