import test from 'node:test'
import assert from 'node:assert/strict'
import fc from 'fast-check'
import * as signal from '../../../dist/OpenCode/Host/HostSignalSubscribeSurface.js'
import {readFileSync} from 'node:fs'

// [012] SyncDelegate fails fast on Aborted. The latency watchdog's own
// interrupt is a Host abort that it continues (or returns Failed): the
// consumer ignores exactly one Aborted per interrupt sent after it armed.
test('WHAT[latency-degeneration-012] fast-check: a cursor excuses exactly one Aborted per interrupt recorded after it opened', () => {
  fc.assert(fc.property(fc.nat(3), fc.array(fc.constantFrom('record', 'refused', 'aborted'), {maxLength: 12}), (before, steps) => {
    const s = 'session-' + Math.random()
    for (let i = 0; i < before; i++) signal.recordInterrupt(s)
    const cursor = signal.interruptCursor(s)
    let pending = 0
    for (const step of steps) {
      if (step === 'record') {signal.recordInterrupt(s); pending++}
      else if (step === 'refused' && pending > 0) {signal.refuseInterrupt(s); pending--}
      else if (step === 'aborted') {
        const own = cursor()
        assert.equal(own, pending > 0)
        if (own) pending--
      }
    }
    signal.forgetInterrupts(s)
  }), {seed: 0xDE6E012, numRuns: 300})
})

test('WHAT[latency-degeneration-012] SyncDelegate consults the cursor before failing on Aborted; the Host records before it aborts', () => {
  const sync = readFileSync(new URL('../../../src/Wanxiangshu/Execution/Delegation/SyncDelegate/Workflow.fs', import.meta.url), 'utf8')
  const hooks = readFileSync(new URL('../../../src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs', import.meta.url), 'utf8')
  assert.match(sync, /TerminalOutcome\.Aborted _ when degeneration\.IsOwnAbort\(\) -> \(\)/)
  assert.ok(hooks.indexOf('DegenerationInterrupts.record sid') < hooks.indexOf('sessionPort.InterruptAttempt sid'))
})
