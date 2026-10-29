import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { mkdtempSync, realpathSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { Time as TimeTesting } from 'wanxiangli/runtime'
import { scopeRetention } from 'wanxiangli/scope-retention'

export const terminalEntry = (text) => ({
  type: 'message',
  id: randomUUID(),
  message: { role: 'assistant', stopReason: 'stop', content: [{ type: 'text', text }] },
})

export async function withSecondHostTime(activateWithTiming, work) {
  const directory = realpathSync(mkdtempSync(join(tmpdir(), 'wxs-second-host-time-')))
  const clock = TimeTesting.virtualClockPort()
  const hosts = []
  const timers = []
  let activatingHost = null
  let failure
  let result

  const createTimer = () => {
    assert.ok(activatingHost, 'A real Host activation owns each timer')
    const virtual = TimeTesting.virtualTimerPort()
    const notices = []
    const waiters = []
    const timer = {
      virtual,
      delays: [],
      disposeCalls: 0,
      nextDelay: () => notices.length > 0
        ? Promise.resolve(notices.shift())
        : new Promise(resolve => waiters.push(resolve)),
      port: {
        Delay(milliseconds) {
          const handle = virtual.port.Delay(milliseconds)
          let markAwaited
          const notice = {
            milliseconds,
            cancelCalls: 0,
            awaited: new Promise(resolve => { markAwaited = resolve }),
          }
          timer.delays.push(notice)
          const waiting = waiters.shift()
          if (waiting) waiting(notice)
          else notices.push(notice)
          return {
            get Delay() {
              markAwaited()
              return handle.Delay
            },
            Cancel() {
              notice.cancelCalls++
              handle.Cancel()
            },
          }
        },
        Dispose() {
          timer.disposeCalls++
          virtual.port.Dispose()
        },
      },
    }
    activatingHost.timer = timer
    timers.push(timer)
    return timer.port
  }

  const activate = async ({ registrationFailure } = {}) => {
    const events = new Map()
    const prefix = randomUUID()
    let entries = []
    const host = {
      tools: new Map(),
      errors: [],
      sent: [],
      reads: 0,
      childIds: [],
      timer: null,
      setEntries: value => { entries = value },
      async emit(name) {
        for (const handler of [...(events.get(name) ?? [])]) await handler({}, context)
      },
      shutdown: () => host.emit('session_shutdown'),
      execute(text) {
        const tool = host.tools.get('wxs_deliver')
        assert.ok(tool, JSON.stringify(host.errors))
        // Common law «Scope»: every OMP tool call carries a `scope` annotation;
        // open retention the way a prior (elided) reset would, then keepLast.
        const sessionId = context.sessionManager.getSessionId()
        const status = scopeRetention.status(sessionId)
        if (!(status?.known && status.remaining !== null && status.remaining > 0)) {
          scopeRetention.ensureEpoch(sessionId, status?.epoch == null || status.epoch === '' ? '0' : String(status.epoch))
          scopeRetention.seed(sessionId, 48)
        }
        return tool.execute(prefix + '-call', { text, scope: { kind: 'keepLast' } }, null, null, context)
      },
      nextDelay: () => host.timer.nextDelay(),
    }
    const context = {
      cwd: directory,
      sessionManager: { getSessionId: () => prefix + '-parent', getEntries: () => [] },
    }
    const pi = {
      on(name, handler) {
        if (!events.has(name)) events.set(name, [])
        events.get(name).push(handler)
      },
      registerTool(tool) {
        if (registrationFailure) throw registrationFailure
        host.tools.set(tool.name, tool)
      },
      logger: { debug() {}, error: (message, detail) => host.errors.push({ message, detail }) },
      pi: {
        SessionManager: {
          create() {
            const id = prefix + '-child-' + host.childIds.length
            return {
              async newSession() { host.childIds.push(id) },
              getSessionId: () => id,
              async setSessionName() {},
              getEntries() {
                host.reads++
                return entries
              },
            }
          },
        },
        async createAgentSession({ sessionManager }) {
          return { session: {
            sessionManager,
            async prompt(text) {
              host.sent.push(text)
              return true
            },
          } }
        },
      },
    }
    hosts.push(host)
    activatingHost = host
    try {
      const before = timers.length
      await activateWithTiming(pi, clock.port, createTimer)
      assert.equal(timers.length, before, 'Wiring the extension does not allocate an activation timer')
      await host.emit('session_start')
    } finally {
      activatingHost = null
    }
    return host
  }

  try {
    execFileSync('git', ['init', '-q', directory])
    execFileSync('git', ['-C', directory, '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
      'commit', '--allow-empty', '-qm', 'time fixture'])
    result = await work({
      activate,
      timers,
      advance(milliseconds) {
        clock.advanceMs(milliseconds)
        for (const timer of timers) timer.virtual.advance(milliseconds)
      },
    })
  } catch (error) {
    failure = { error }
  } finally {
    const cleanup = await Promise.allSettled(hosts.map(host => host.shutdown()))
    try { rmSync(directory, { recursive: true, force: true }) }
    catch (reason) { cleanup.push({ status: 'rejected', reason }) }
    for (const item of cleanup) {
      if (item.status === 'rejected') failure = { error: failure
        ? new AggregateError([failure.error, item.reason], 'Second-host fixture failed and cleanup failed', { cause: failure.error })
        : item.reason }
    }
  }
  if (failure) throw failure.error
  return result
}
