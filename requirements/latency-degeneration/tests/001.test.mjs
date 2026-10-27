import test from 'node:test'
import assert from 'node:assert/strict'
import * as HostSignalSurface from '../../../dist/OpenCode/Host/HostSignalSurface.js'
import * as ProviderLanguageSurface from '../../../dist/Participant/Provider/LanguageSurface.js'
import {
  LatencyWatchdogFactory_createLatencyWatchdog as createLatencyWatchdog,
  LatencyWatchdog,
} from '../../../dist/OpenCode/Host/LatencyWatchdog.js'

/// Controllable fake of the library detector (wanxiangli/degeneration).
/// Thresholds and samples are recorded without real statistics, so the state
/// machine's pairing and accounting contracts can be tested in isolation.
/// No prior scale is hard-coded; expected thresholds are always read from the
/// detector itself.
function fakeDetector() {
  const calls = []
  const thresholds = new Map()
  let fallback = 23000
  return {
    calls,
    setThreshold(sessionId, ms) {
      thresholds.set(sessionId, ms)
    },
    ThresholdMs(sessionId) {
      return thresholds.has(sessionId) ? thresholds.get(sessionId) : fallback
    },
    ObserveCompleted(sessionId, durationMs) {
      calls.push({ kind: 'completed', sessionId, durationMs })
    },
    ObserveCensored(sessionId, censoredMs) {
      calls.push({ kind: 'censored', sessionId, censoredMs })
    },
    Forget(sessionId) {
      calls.push({ kind: 'forget', sessionId })
    },
    completed() {
      return calls.filter((c) => c.kind === 'completed')
    },
    censored() {
      return calls.filter((c) => c.kind === 'censored')
    },
  }
}

function makePorts() {
  const state = {
    now: 0,
    schedules: [], // armed one-shot handles, in Begin order
    abortCalls: [],
    continueCalls: [],
    diagnostics: [],
    managed: new Map(),
    texts: new Map(),
    abortMode: 'accept', // 'accept' | 'refuse' | 'deferred'
    abortResolve: null,
  }

  const ports = {
    Now: () => state.now,
    Schedule: (milliseconds, callback) => {
      let disposed = false
      const handle = {
        Dispose() {
          disposed = true
        },
        isDisposed: () => disposed,
        fire() {
          if (!disposed) callback()
        },
      }
      state.schedules.push(handle)
      return handle
    },
    Abort: (sessionId) => {
      state.abortCalls.push(sessionId)
      if (state.abortMode === 'deferred') {
        return new Promise((resolve) => {
          state.abortResolve = resolve
        })
      }
      return Promise.resolve(state.abortMode !== 'refuse')
    },
    Continue: async (sessionId, prompt) => {
      state.continueCalls.push({ sessionId, prompt })
      return ''
    },
    Managed: (sessionId) => state.managed.has(sessionId) && state.managed.get(sessionId),
    Text: (sessionId) => state.texts.get(sessionId) ?? '',
    Diagnostic: (kind, reason) => {
      state.diagnostics.push({ kind, reason })
    },
  }

  return { ports, state }
}

const tick = () => new Promise((resolve) => setTimeout(resolve, 0))

test('WHAT[latency-degeneration-001] 一次受管请求正常完成记入完整样本且编译类存在', async () => {
  assert.equal(typeof LatencyWatchdog, 'function', 'the compiled class must exist')

  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now,
    ports.Schedule,
    ports.Abort,
    ports.Continue,
    ports.Managed,
    ports.Text,
    ports.Diagnostic,
    detector,
  )

  state.managed.set('sess-1', true)
  state.now = 1000
  watchdog.Begin('sess-1', 'run-1')
  state.now = 4000
  watchdog.Complete('sess-1', 'run-1')

  assert.equal(detector.completed().length, 1)
  assert.equal(detector.completed()[0].sessionId, 'sess-1')
  assert.equal(detector.completed()[0].durationMs, 3000)
  assert.equal(detector.censored().length, 0)
  assert.equal(state.abortCalls.length, 0)
})

test('WHAT[latency-degeneration-001] 同一 provider run 的重复 start 事件保持原起算点（幂等）', async () => {
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )

  state.managed.set('sess-dup', true)
  state.now = 100
  watchdog.Begin('sess-dup', 'run-dup')
  state.now = 200
  // Streaming updates re-emit the started state with the same run id.
  watchdog.Begin('sess-dup', 'run-dup')

  assert.equal(state.schedules.length, 1, 'duplicate start must not re-arm a timer')
  state.now = 500
  watchdog.Complete('sess-dup', 'run-dup')

  assert.equal(detector.completed().length, 1)
  assert.equal(detector.completed()[0].durationMs, 400, 'duration is measured from the first start')
})

test('WHAT[latency-degeneration-001] 同 parentID 的旧完成事件晚于新请求启动不得归入新请求', async () => {
  // Regression: In the Host, multiple provider steps of the same turn share the
  // exact same user message id (assistant parentID). If an old request's
  // terminal arrives late after a new attempt has already started, pairing by
  // parentID would wrongly complete the new attempt with the old one's duration.
  // Pairing by provider run id ensures the foreign event is ignored and the
  // in-flight measurement stays intact.
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )

  state.managed.set('sess-late', true)
  state.now = 0
  watchdog.Begin('sess-late', 'run-1')

  state.now = 100
  // Step 2 starts before step 1 completed (e.g. step 1 was aborted or lost its terminal).
  watchdog.Begin('sess-late', 'run-2')
  assert.equal(state.schedules.length, 2, 'run-2 arms its own timer')
  assert.equal(state.schedules[0].isDisposed(), true, 'run-1 timer was cancelled at supersede')
  assert.equal(
    state.diagnostics.some((d) => d.kind === 'latency-degeneration-sample-dropped'),
    true,
    'superseding an un-terminated measurement records a dropped-sample diagnostic',
  )

  state.now = 300
  // Late terminal for run-1 arrives now. Both run-1 and run-2 share parentID in reality.
  watchdog.Complete('sess-late', 'run-1')
  assert.equal(detector.completed().length, 0, 'run-1 terminal must not be attributed')

  // run-2 measurement is still in flight and armed.
  const snap = watchdog.Snapshot()
  assert.equal(snap.length, 1)
  assert.equal(snap[0].SessionId, 'sess-late')
  assert.equal(snap[0].Armed, true)

  state.now = 600
  watchdog.Complete('sess-late', 'run-2')
  assert.equal(detector.completed().length, 1)
  assert.equal(detector.completed()[0].sessionId, 'sess-late')
  assert.equal(detector.completed()[0].durationMs, 500, 'duration is measured from run-2 start (600 - 100)')
  assert.equal(detector.censored().length, 0)
  assert.equal(state.abortCalls.length, 0)
})

test('WHAT[latency-degeneration-001] 缺失请求身份时放弃样本（fail closed，不得归入任何请求）', async () => {
  // Regression: Neither side falling back to a session-level match can prove
  // uniqueness. A start without identity must not measure; a terminal without
  // identity must drop the measurement without a sample.
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )

  state.managed.set('sess-miss', true)

  // 1) Start without identity -> no measurement created.
  watchdog.Begin('sess-miss', undefined)
  assert.equal(state.schedules.length, 0, 'a start without identity arms no timer')
  watchdog.Complete('sess-miss', undefined)
  assert.equal(detector.completed().length, 0)

  // 2) Terminal without identity -> drops measurement without sample.
  watchdog.Begin('sess-miss', 'run-a')
  assert.equal(state.schedules.length, 1)
  watchdog.Complete('sess-miss', undefined)
  assert.equal(detector.completed().length, 0, 'unattributable terminal records no sample')
  assert.equal(detector.censored().length, 0)
  assert.equal(
    state.diagnostics.some((d) => d.kind === 'latency-degeneration-sample-dropped'),
    true,
  )

  // The measurement was dropped to prevent an armed timer outliving a completed
  // request; a subsequent terminal for run-a now finds nothing.
  watchdog.Complete('sess-miss', 'run-a')
  assert.equal(detector.completed().length, 0)
})

test('WHAT[latency-degeneration-001] Abort 与完成竞态只记一个完整样本（不双重记账）', async () => {
  // Regression: While Abort is in flight asynchronously, the host may deliver
  // the assistant terminal event. When Abort settles true afterwards, it must
  // re-check that the measurement is still this attempt's; because Complete
  // already removed it, no censored sample and no continuation may be issued.
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )

  const threshold = detector.ThresholdMs('sess-race')
  state.managed.set('sess-race', true)
  state.abortMode = 'deferred'
  state.now = 0
  watchdog.Begin('sess-race', 'run-race')

  state.now = threshold + 100
  state.schedules[0].fire()
  await tick()
  assert.ok(state.abortResolve, 'Abort must be in flight')

  // Complete arrives while Abort is pending.
  state.now = threshold + 150
  watchdog.Complete('sess-race', 'run-race')
  assert.equal(detector.completed().length, 1, 'the real completion is recorded')

  // Abort resolves true now.
  state.abortResolve(true)
  await tick()

  assert.equal(detector.completed().length, 1, 'still exactly one complete sample')
  assert.equal(detector.censored().length, 0, 'censored sample must not be double-counted')
  assert.equal(state.continueCalls.length, 0, 'no continuation prompt for an already-completed request')
  assert.equal(state.abortCalls.length, 1)
})

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

  // Verify diagnostic Snapshot contract before fire.
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
  assert.ok(
    detector.censored()[0].censoredMs >= threshold,
    'censored sample must be at least the threshold',
  )
  assert.equal(state.abortCalls.length, 1)
  assert.equal(state.continueCalls.length, 1)
  assert.equal(state.continueCalls[0].sessionId, 'sess-2')
  assert.equal(state.continueCalls[0].prompt, 'CONTINUE-PROSE')
  assert.equal(detector.completed().length, 0)
  assert.equal(watchdog.Snapshot().length, 0, 'measurement was removed after interrupt')
})

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
  assert.equal(
    state.diagnostics.some((d) => d.kind === 'latency-degeneration-abort-refused'),
    true,
  )

  // Kept in-flight, unarmed (no interrupt storm).
  const snap = watchdog.Snapshot()
  assert.equal(snap.length, 1)
  assert.equal(snap[0].Armed, false)

  state.now = threshold + 1200
  watchdog.Complete('sess-3', 'run-3')
  assert.equal(detector.completed().length, 1)
  assert.equal(detector.completed()[0].durationMs, threshold + 1200)
})

test('WHAT[latency-degeneration-004] Complete 幂等，不同 run 的完成事件不破坏在途测量', async () => {
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )

  // Non-existent session is a no-op.
  watchdog.Complete('sess-none', 'run-x')
  assert.equal(detector.completed().length, 0)

  state.managed.set('sess-4', true)
  state.now = 100
  watchdog.Begin('sess-4', 'run-a')

  // A foreign run terminal is ignored.
  state.now = 500
  watchdog.Complete('sess-4', 'run-b')
  assert.equal(detector.completed().length, 0)
  assert.equal(watchdog.Snapshot().length, 1, 'in-flight measurement stays armed')

  // The matching run completes normally.
  watchdog.Complete('sess-4', 'run-a')
  assert.equal(detector.completed().length, 1)

  // A replayed terminal for the same run records nothing.
  watchdog.Complete('sess-4', 'run-a')
  assert.equal(detector.completed().length, 1)
})

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
  assert.equal(
    detector.calls.some((c) => c.kind === 'forget' && c.sessionId === 'sess-5'),
    true,
  )

  watchdog.Cancel('sess-5')
  watchdog.OnIdle('sess-6')

  assert.equal(detector.completed().length, 0)
  assert.equal(detector.censored().length, 0)
  assert.equal(
    state.diagnostics.some((d) => d.kind === 'latency-degeneration-idle-without-completion'),
    true,
  )
  assert.equal(state.schedules[0].isDisposed(), true)
  assert.equal(state.schedules[1].isDisposed(), true)
  assert.equal(watchdog.Snapshot().length, 0)
})

test('WHAT[latency-degeneration-006] 非受管不启动，Dispose 幂等且之后 Begin 无效', async () => {
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )

  // Unmanaged session is completely inert.
  watchdog.Begin('sess-unmanaged', 'run-u')
  assert.equal(state.schedules.length, 0)
  watchdog.Complete('sess-unmanaged', 'run-u')
  assert.equal(detector.completed().length, 0)

  // Dispose idempotency.
  state.managed.set('sess-d', true)
  watchdog.Begin('sess-d', 'run-d')
  assert.equal(state.schedules.length, 1)

  watchdog.Dispose()
  watchdog.Dispose()
  assert.equal(state.schedules[0].isDisposed(), true)

  // Begin after dispose is inert.
  watchdog.Begin('sess-d', 'run-d2')
  assert.equal(state.schedules.length, 1)
  watchdog.Complete('sess-d', 'run-d2')
  assert.equal(detector.completed().length, 0)
})


// ---------------------------------------------------------------------------
// 物理接线与真实事件解码验证 (Physical Wiring & Event Codec Oracles)
// ---------------------------------------------------------------------------

function wireHostEvents(watchdog, activeCompactingSessions = new Set()) {
  return function settleLatencyWatchdog(raw) {
    const start = HostSignalSurface.tryDecodeExactProviderStart(raw)
    if (start) {
      const sessionId = start.sessionId
      if (!activeCompactingSessions.has(sessionId)) {
        watchdog.Begin(sessionId, start.providerRun)
      }
    }

    const terminal = HostSignalSurface.tryDecodeExactProviderTerminal(raw)
    if (terminal) {
      if (terminal.disposition === 'Completed') {
        watchdog.Complete(terminal.sessionId, terminal.providerRun)
      }
    }

    const signal = HostSignalSurface.tryDecode(raw)
    if (signal && signal.case === 'SessionIdle') {
      watchdog.OnIdle(signal.fields[0])
    }
  }
}

test('WHAT[latency-degeneration-001] 物理接线验证：HostEventCodec 真实时序与首 token 前永久挂死漏检物理反例', async () => {
  // Oracle: tryDecodeExactProviderStart requires an assistant message.updated event
  // with time.created. If the provider hangs before emitting its first token,
  // the host emits no assistant message.updated.
  // This test proves via real HostSignalSurface wiring that watchdog.Begin is NEVER
  // invoked in this scenario, so the hung request is missed (漏检).
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )
  const settleLatencyWatchdog = wireHostEvents(watchdog)

  state.managed.set('sess-hang', true)
  state.now = 100

  // 1. User submits prompt -> host emits user message.updated
  const userEvent = {
    type: 'message.updated',
    sessionID: 'sess-hang',
    properties: {
      info: {
        sessionID: 'sess-hang',
        role: 'user',
        id: 'usr-1',
        time: { created: 100 },
      },
    },
  }
  settleLatencyWatchdog(userEvent)

  // 2. Provider request is sent over network, but permanently hangs before first token.
  // Host emits NO assistant message.updated event.
  // Advance time far past the threshold (e.g. 35000 ms).
  state.now = 35100

  // 3. Physical wiring assertion: watchdog was never armed, no timer scheduled, no abort.
  assert.equal(
    watchdog.Snapshot().length,
    0,
    'watchdog was never started because tryDecodeExactProviderStart requires assistant message.updated',
  )
  assert.equal(state.schedules.length, 0, 'no timeout timer was ever armed')
  assert.equal(state.abortCalls.length, 0, 'no abort was ever issued for request hung before first token')
  assert.equal(detector.censored().length, 0, 'no censored sample recorded for hung request')
})

test('WHAT[latency-degeneration-001] 物理接线验证：相同 parentID 不同 assistant id 迟到终态事件精确配对', async () => {
  // Physical wiring test using real HostSignalSurface.tryDecodeExactProviderStart
  // and HostSignalSurface.tryDecodeExactProviderTerminal.
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )
  const settleLatencyWatchdog = wireHostEvents(watchdog)

  state.managed.set('sess-wire', true)
  state.now = 100

  // Step 1 start: assistant ast-1 for user prompt usr-1
  settleLatencyWatchdog({
    type: 'message.updated',
    sessionID: 'sess-wire',
    properties: {
      info: {
        sessionID: 'sess-wire',
        role: 'assistant',
        id: 'ast-1',
        parentID: 'usr-1',
        time: { created: 100 },
      },
    },
  })
  assert.equal(state.schedules.length, 1)

  state.now = 200
  // Step 2 start: assistant ast-2 for same user prompt usr-1 (retry/superseded)
  settleLatencyWatchdog({
    type: 'message.updated',
    sessionID: 'sess-wire',
    properties: {
      info: {
        sessionID: 'sess-wire',
        role: 'assistant',
        id: 'ast-2',
        parentID: 'usr-1',
        time: { created: 200 },
      },
    },
  })
  assert.equal(state.schedules.length, 2)
  assert.equal(state.schedules[0].isDisposed(), true)

  state.now = 400
  // Late terminal for ast-1 arrives now
  settleLatencyWatchdog({
    type: 'message.updated',
    sessionID: 'sess-wire',
    properties: {
      info: {
        sessionID: 'sess-wire',
        role: 'assistant',
        id: 'ast-1',
        parentID: 'usr-1',
        time: { created: 100, completed: 400 },
        finish: 'stop',
      },
    },
  })
  assert.equal(detector.completed().length, 0, 'late terminal for ast-1 must not complete ast-2')
  assert.equal(watchdog.Snapshot().length, 1, 'ast-2 is still armed in-flight')

  state.now = 700
  // Terminal for ast-2 arrives
  settleLatencyWatchdog({
    type: 'message.updated',
    sessionID: 'sess-wire',
    properties: {
      info: {
        sessionID: 'sess-wire',
        role: 'assistant',
        id: 'ast-2',
        parentID: 'usr-1',
        time: { created: 200, completed: 700 },
        finish: 'stop',
      },
    },
  })
  assert.equal(detector.completed().length, 1)
  assert.equal(detector.completed()[0].durationMs, 500, 'duration is 700 - 200 = 500 ms')
})

test('WHAT[latency-degeneration-001] 物理接线验证：缺 id 或缺 parentID 事件真实解码为 null（fail closed 不误归属）', async () => {
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )
  const settleLatencyWatchdog = wireHostEvents(watchdog)

  state.managed.set('sess-failclosed', true)

  // 1. Missing assistant id
  const missingIdEvt = {
    type: 'message.updated',
    sessionID: 'sess-failclosed',
    properties: {
      info: {
        sessionID: 'sess-failclosed',
        role: 'assistant',
        parentID: 'usr-1',
        time: { created: 100 },
      },
    },
  }
  assert.equal(HostSignalSurface.tryDecodeExactProviderStart(missingIdEvt), null)
  settleLatencyWatchdog(missingIdEvt)
  assert.equal(state.schedules.length, 0)

  // 2. Missing parentID
  const missingParentEvt = {
    type: 'message.updated',
    sessionID: 'sess-failclosed',
    properties: {
      info: {
        sessionID: 'sess-failclosed',
        role: 'assistant',
        id: 'ast-1',
        time: { created: 100 },
      },
    },
  }
  assert.equal(HostSignalSurface.tryDecodeExactProviderStart(missingParentEvt), null)
  settleLatencyWatchdog(missingParentEvt)
  assert.equal(state.schedules.length, 0)

  // 3. Missing finish on terminal
  const incompleteTerminal = {
    type: 'message.updated',
    sessionID: 'sess-failclosed',
    properties: {
      info: {
        sessionID: 'sess-failclosed',
        role: 'assistant',
        id: 'ast-1',
        parentID: 'usr-1',
        time: { created: 100, completed: 500 },
      },
    },
  }
  assert.equal(HostSignalSurface.tryDecodeExactProviderTerminal(incompleteTerminal), null)
  settleLatencyWatchdog(incompleteTerminal)
  assert.equal(detector.completed().length, 0)
})

test('WHAT[latency-degeneration-001] 物理接线验证：真实事件下的 Abort 与 Terminal 竞态单次结算', async () => {
  const detector = fakeDetector()
  const { ports, state } = makePorts()
  const watchdog = createLatencyWatchdog(
    ports.Now, ports.Schedule, ports.Abort, ports.Continue, ports.Managed,
    ports.Text, ports.Diagnostic, detector,
  )
  const settleLatencyWatchdog = wireHostEvents(watchdog)

  const threshold = detector.ThresholdMs('sess-race-phys')
  state.managed.set('sess-race-phys', true)
  state.abortMode = 'deferred'
  state.now = 0

  settleLatencyWatchdog({
    type: 'message.updated',
    sessionID: 'sess-race-phys',
    properties: {
      info: {
        sessionID: 'sess-race-phys',
        role: 'assistant',
        id: 'ast-race',
        parentID: 'usr-race',
        time: { created: 0 },
      },
    },
  })

  // Timer fires at threshold + 100
  state.now = threshold + 100
  state.schedules[0].fire()
  await tick()
  assert.ok(state.abortResolve, 'Abort is pending')

  // Real terminal arrives while Abort is in flight
  state.now = threshold + 150
  settleLatencyWatchdog({
    type: 'message.updated',
    sessionID: 'sess-race-phys',
    properties: {
      info: {
        sessionID: 'sess-race-phys',
        role: 'assistant',
        id: 'ast-race',
        parentID: 'usr-race',
        time: { created: 0, completed: threshold + 150 },
        finish: 'stop',
      },
    },
  })
  assert.equal(detector.completed().length, 1, 'terminal completed the measurement')

  // Abort now completes true
  state.abortResolve(true)
  await tick()

  assert.equal(detector.completed().length, 1, 'still exactly one complete sample')
  assert.equal(detector.censored().length, 0, 'no double counting of censored sample')
  assert.equal(state.continueCalls.length, 0, 'no continuation prompt sent')
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
  // Wire ports.Text directly to the real ProviderLanguageSurface
  ports.Text = (sessionId) => {
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

  // Trigger timeout on sess-zh
  const threshold = detector.ThresholdMs('sess-zh')
  state.now = 0
  settleLatencyWatchdog({
    type: 'message.updated',
    sessionID: 'sess-zh',
    properties: {
      info: {
        sessionID: 'sess-zh',
        role: 'assistant',
        id: 'ast-zh',
        parentID: 'usr-zh',
        time: { created: 0 },
      },
    },
  })
  state.now = threshold + 500
  state.schedules[0].fire()
  await tick()

  assert.equal(state.continueCalls.length, 1)
  assert.equal(state.continueCalls[0].sessionId, 'sess-zh')
  assert.equal(state.continueCalls[0].prompt, zhText)
})
