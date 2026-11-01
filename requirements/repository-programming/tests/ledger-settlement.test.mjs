import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { setImmediate } from 'node:timers/promises'
import { typedRole } from '../../../dist/Repository/Programming/Js/GeneratorSurface.js'
import { JsToolWorkflow_run } from '../../../dist/Repository/Programming/Js/ToolWorkflow.js'
import { closePrograms } from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import {
  JsFailureModule_code,
  JsFailureModule_reason,
  JsTransactionAppendFailure,
  JsTransactionAppendPhase,
} from '../../../dist/Repository/Programming/Js/Failure.js'
import { EventIdModule_create } from '../../../dist/Foundation/Identity.js'
import { AppendError } from '../../../dist/Persistence/EventStore/StoreTypes.js'
import { FSharpResult$2 } from '../../../dist/fable_modules/fable-library-js.5.13.0/Result.js'

const deferred = () => {
  let resolve
  let reject
  const promise = new Promise((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

const ok = () => new FSharpResult$2(0, [EventIdModule_create('settled-event')])
const appendError = (call) => new FSharpResult$2(1, [new JsTransactionAppendFailure(
  call.phase,
  call.transactionId,
  EventIdModule_create('failed-event'),
  new AppendError(2, ['record refused']),
)])

const program = `class Js extends JsProgram {
  async run() {
    this.write('rewrite.txt', 'after');
    this.write('created.txt', 'created');
    this.mv('source.txt', 'destination.txt');
    this.rm('removed.txt');
    return { done: true };
  }
}`

const scenarios = [
  {
    name: 'ordinary Promise rejection',
    first: 'reject',
    rejection: () => new Error('record rejected'),
    code: 'PROGRAM_FAILED',
    reason: 'program threw: record rejected',
  },
  {
    name: 'Promise rejection with an explicit code and reason',
    first: 'reject',
    rejection: () => Object.assign(new Error('fallback message'), {
      code: 'LEDGER_UNAVAILABLE',
      reason: 'record service unavailable',
    }),
    code: 'LEDGER_UNAVAILABLE',
    reason: 'record service unavailable',
  },
  {
    name: 'Result error before a later rejection',
    first: 'result',
    second: 'reject',
    rejection: () => new Error('later rejection'),
    code: 'TRANSACTION_PREPARE_FAILED',
  },
  {
    name: 'rejection before a later Result error',
    first: 'reject',
    second: 'result',
    rejection: () => new Error('first rejection'),
    code: 'PROGRAM_FAILED',
    reason: 'program threw: first rejection',
  },
]

for (const scenario of scenarios) {
  test(`file ledger waits for every record and retains effects: ${scenario.name}`, { timeout: 10_000 }, async () => {
    const root = mkdtempSync(join(tmpdir(), 'wxs-ledger-settlement-'))
    const calls = []
    const appended = deferred()
    const append = (phase, transactionId) => {
      const pending = deferred()
      calls.push({ ...pending, phase, transactionId })
      if (calls.length === 8) appended.resolve()
      return pending.promise
    }
    const persistence = {
      AppendPrepared: prepared => append(JsTransactionAppendPhase.Prepared, prepared.TransactionId),
      AppendCommitted: transactionId => append(JsTransactionAppendPhase.Committed, transactionId),
    }
    let running
    try {
      writeFileSync(join(root, 'rewrite.txt'), 'before')
      writeFileSync(join(root, 'source.txt'), 'moved bytes')
      writeFileSync(join(root, 'removed.txt'), 'removed bytes')
      const surface = typedRole('Engineer', 'en')
      assert.ok(surface)
      running = JsToolWorkflow_run(
        surface.Capabilities, root, surface.BaseClassSource, program,
        3000, BigInt(Date.now() + 60_000), 1 << 20, persistence,
      )
      let finished = false
      running.then(() => { finished = true }, () => { finished = true })
      await Promise.race([
        appended.promise,
        running.then(() => { throw new Error('program returned before all file records were issued') }),
      ])

      const assertFiles = () => {
        assert.equal(readFileSync(join(root, 'rewrite.txt'), 'utf8'), 'after')
        assert.equal(readFileSync(join(root, 'created.txt'), 'utf8'), 'created')
        assert.equal(readFileSync(join(root, 'destination.txt'), 'utf8'), 'moved bytes')
        assert.equal(existsSync(join(root, 'source.txt')), false)
        assert.equal(existsSync(join(root, 'removed.txt')), false)
      }
      assertFiles()

      // Complete later records first. The final record stays pending until
      // after the first failure; completion order must not choose the error.
      for (let index = calls.length - 2; index >= 1; index--) {
        if (index === 1 && scenario.second === 'reject') calls[index].reject(scenario.rejection())
        else if (index === 1 && scenario.second === 'result') calls[index].resolve(appendError(calls[index]))
        else calls[index].resolve(ok())
      }
      if (scenario.first === 'result') calls[0].resolve(appendError(calls[0]))
      else calls[0].reject(scenario.rejection())
      await setImmediate()
      assert.equal(finished, false, 'a failed record must not abandon a later pending append')
      calls.at(-1).resolve(ok())

      const outcome = await running
      assert.equal(outcome.cases()[outcome.tag], 'Failed')
      const [failure, rewritten, created, removed, moved] = outcome.fields
      assert.equal(JsFailureModule_code(failure), scenario.code)
      const reason = scenario.first === 'result'
        ? `transaction ${calls[0].transactionId.fields[0]} event failed-event: append failed: record refused`
        : scenario.reason
      assert.equal(JsFailureModule_reason(failure), reason)
      assert.deepEqual(Array.from(rewritten), ['rewrite.txt'])
      assert.deepEqual(Array.from(created), ['created.txt'])
      assert.deepEqual(Array.from(removed), ['removed.txt'])
      assert.deepEqual(Array.from(moved), [['source.txt', 'destination.txt']])
      assert.equal(calls.length, 8, 'each physical mutation has one Prepared/Committed pair')
      assertFiles()
    } finally {
      for (const call of calls) call.resolve(ok())
      if (running) await running.catch(() => {})
      await closePrograms()
      rmSync(root, { recursive: true, force: true })
    }
  })
}
