import * as HostSignalSurface from '../../../../dist/OpenCode/Host/HostSignalSurface.js'
export { LatencyWatchdogFactory_createLatencyWatchdog as createLatencyWatchdog } from '../../../../dist/OpenCode/Host/LatencyWatchdog.js'

export function fakeDetector() {
  const calls = []
  const thresholds = new Map()
  const fallback = 23000
  return {
    calls,
    setThreshold(sessionId, ms) { thresholds.set(sessionId, ms) },
    ThresholdMs(sessionId) { return thresholds.has(sessionId) ? thresholds.get(sessionId) : fallback },
    ObserveCompleted(sessionId, durationMs) { calls.push({ kind: 'completed', sessionId, durationMs }) },
    ObserveCensored(sessionId, censoredMs) { calls.push({ kind: 'censored', sessionId, censoredMs }) },
    Forget(sessionId) { calls.push({ kind: 'forget', sessionId }) },
    completed() { return calls.filter(c => c.kind === 'completed') },
    censored() { return calls.filter(c => c.kind === 'censored') },
  }
}

export function makePorts() {
  const state = {
    now: 0,
    schedules: [],
    abortCalls: [],
    continueCalls: [],
    diagnostics: [],
    managed: new Map(),
    texts: new Map(),
    abortMode: 'accept',
    abortResolve: null,
  }
  const ports = {
    Now: () => state.now,
    Schedule: (milliseconds, callback) => {
      let disposed = false
      const handle = {
        Dispose() { disposed = true },
        isDisposed: () => disposed,
        fire() { if (!disposed) callback() },
      }
      state.schedules.push(handle)
      return handle
    },
    Abort: sessionId => {
      state.abortCalls.push(sessionId)
      if (state.abortMode === 'deferred') {
        return new Promise(resolve => { state.abortResolve = resolve })
      }
      return Promise.resolve(state.abortMode !== 'refuse')
    },
    Continue: async (sessionId, prompt) => {
      state.continueCalls.push({ sessionId, prompt })
      return ''
    },
    Managed: sessionId => state.managed.has(sessionId) && state.managed.get(sessionId),
    Text: sessionId => state.texts.get(sessionId) ?? '',
    Diagnostic: (kind, reason) => { state.diagnostics.push({ kind, reason }) },
  }
  return { ports, state }
}

export const tick = () => new Promise(resolve => setTimeout(resolve, 0))

// This composes the real codec with controlled ports; it does not execute PluginHooks.
export function wireHostEvents(watchdog, activeCompactingSessions = new Set()) {
  return raw => {
    const start = HostSignalSurface.tryDecodeExactProviderStart(raw)
    if (start && !activeCompactingSessions.has(start.sessionId)) {
      watchdog.Begin(start.sessionId, start.providerRun)
    }
    const terminal = HostSignalSurface.tryDecodeExactProviderTerminal(raw)
    if (terminal?.disposition === 'Completed') {
      watchdog.Complete(terminal.sessionId, terminal.providerRun)
    }
    const signal = HostSignalSurface.tryDecode(raw)
    if (signal?.kind === 'SessionIdle') watchdog.OnIdle(signal.sessionId)
  }
}
