import assert from 'node:assert/strict'
import test from 'node:test'
import * as signal from '../../../dist/Foundation/TerminalSignal.js'
import { SessionIdModule_create as sessionId } from '../../../dist/Foundation/Identity.js'

test('WHAT[structured-workflow-005] 每个游标独立观察自己的中断记录', () => {
  const session = sessionId('structured-workflow-005')
  const first = signal.DegenerationInterrupts_Cursor_$ctor_ZD623056(session)
  signal.DegenerationInterrupts_record(session)
  const second = signal.DegenerationInterrupts_Cursor_$ctor_ZD623056(session)
  try {
    assert.equal(signal.DegenerationInterrupts_Cursor__IsOwnAbort(first), true)
    assert.equal(signal.DegenerationInterrupts_Cursor__IsOwnAbort(first), false)
    assert.equal(signal.DegenerationInterrupts_Cursor__IsOwnAbort(second), false)
    signal.DegenerationInterrupts_record(session)
    assert.equal(signal.DegenerationInterrupts_Cursor__IsOwnAbort(first), true)
    assert.equal(signal.DegenerationInterrupts_Cursor__IsOwnAbort(second), true)
  } finally {
    signal.DegenerationInterrupts_forget(session)
  }
})
