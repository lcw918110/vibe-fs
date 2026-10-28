import test from 'node:test'
import assert from 'node:assert/strict'
import fc from 'fast-check'
import * as signal from '../../../dist/Foundation/TerminalSignal.js'
import {SessionIdModule_create as sid} from '../../../dist/Foundation/Identity.js'
import {readFileSync} from 'node:fs'

// [012] Advisor and SyncDelegate fail fast on Aborted. The latency watchdog's
// own interrupt is a Host abort that it continues (or returns Failed): those
// consumers ignore exactly one Aborted per interrupt sent after they armed.
test('WHAT[latency-degeneration-012] fast-check: a cursor excuses exactly one Aborted per interrupt recorded after it opened', () => {
  fc.assert(fc.property(fc.nat(3), fc.array(fc.constantFrom('record', 'refused', 'aborted'), {maxLength: 12}), (before, steps) => {
    const s = sid('session-' + Math.random())
    for (let i = 0; i < before; i++) signal.DegenerationInterrupts_record(s)
    const cursor = signal.DegenerationInterrupts_Cursor_$ctor_ZD623056(s)
    let pending = 0
    for (const step of steps) {
      if (step === 'record') {signal.DegenerationInterrupts_record(s); pending++}
      else if (step === 'refused' && pending > 0) {signal.DegenerationInterrupts_unrecord(s); pending--}
      else if (step === 'aborted') {
        const own = signal.DegenerationInterrupts_Cursor__IsOwnAbort(cursor)
        assert.equal(own, pending > 0)
        if (own) pending--
      }
    }
    signal.DegenerationInterrupts_forget(s)
  }), {seed: 0xDE6E012, numRuns: 300})
})

test('WHAT[latency-degeneration-012] Advisor and SyncDelegate consult the cursor before failing on Aborted; the Host records before it aborts', () => {
  const seat = readFileSync(new URL('../../../src/Wanxiangshu/Execution/Delegation/Advisor/Seat.fs', import.meta.url), 'utf8')
  const sync = readFileSync(new URL('../../../src/Wanxiangshu/Execution/Delegation/SyncDelegate/Workflow.fs', import.meta.url), 'utf8')
  const hooks = readFileSync(new URL('../../../src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs', import.meta.url), 'utf8')
  assert.match(seat, /Aborted _ when armed\.Value && degeneration\.IsOwnAbort\(\) -> \(\)/)
  assert.match(sync, /TerminalOutcome\.Aborted _ when degeneration\.IsOwnAbort\(\) -> \(\)/)
  assert.ok(hooks.indexOf('DegenerationInterrupts.record sid') < hooks.indexOf('sessionPort.InterruptAttempt sid'))
})
