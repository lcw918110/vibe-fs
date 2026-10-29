import assert from 'node:assert/strict'
import test from 'node:test'
import { ToolRuntimeScopeSurface } from '../../../../dist/OpenCode/Tools/ToolRuntimeScopeSurface.js'
import * as dispatcher from '../../../../dist/Interaction/Dispatch/DispatchSurface.js'
import {
  withJournal, hostPort, prepareAcceptanceObserver, acceptObservedPhysical,
} from '../../../dispatch-protocol/tests/support/authority.mjs'

const pause = ms => new Promise(resolve => setTimeout(resolve, ms))
const makeScope = ToolRuntimeScopeSurface.createAdvisorSender

test('WHAT[advisor-005] real ToolRuntimeScope cannot settle final steer before PhysicalAccepted', async t => {
  await withJournal('advisor-runtime-physical', async handle => {
    const context = await prepareAcceptanceObserver(handle, 'continuation', 'advisor-runtime-physical')
    let resolveDispatched
    const dispatched = new Promise(resolve => { resolveDispatched = resolve })
    let key
    const sessionPort = hostPort(async (_session, _text, options) => {
      key = options.Metadata.wanxiangshu_prompt_key
      assert.equal(options.Metadata.wanxiangshu_consultation_steer, true)
      resolveDispatched()
      return dispatcher.admittedWithReceipt('submitted-not-accepted')
    })
    const runtime = makeScope(handle, sessionPort)
    t.after(() => runtime.dispose())
    let completed = false
    const sending = runtime.send(
      context.session, 'review this scope work', 'advisor:receipt', () => true,
    ).then(outcome => { completed = true; return outcome })
    try {
      await dispatched
      await pause(25)
      assert.equal(completed, false, 'the product API may not call Finished on a Submitted receipt')
      const actual = await acceptObservedPhysical(handle, context, key, 'advisor-runtime-physical-landed')
      assert.equal(actual.ok, true, actual.error)
      const outcome = await sending
      assert.deepEqual(outcome, { ok: true, error: null })
      assert.equal(dispatcher.pendingClaimCount(handle, context.session), 0)
    } finally {
      if (!completed && key) await acceptObservedPhysical(handle, context, key, 'advisor-runtime-cleanup')
      await sending
    }
  })
})

test('WHAT[advisor-005] Source Scope retiring during pending send refuses success even after physical acceptance', async t => {
  await withJournal('advisor-runtime-epoch', async handle => {
    const context = await prepareAcceptanceObserver(handle, 'continuation', 'advisor-runtime-epoch')
    let key
    let reportSend
    const dispatched = new Promise(resolve => { reportSend = resolve })
    let current = true
    const port = hostPort(async (_sid, _text, options) => {
      key = options.Metadata.wanxiangshu_prompt_key
      reportSend()
      return dispatcher.admittedWithReceipt('queue-only')
    })
    const runtime = makeScope(handle, port)
    t.after(() => runtime.dispose())
    const sending = runtime.send(
      context.session, 'stale final advice', 'advisor:old', () => current,
    )
    await dispatched
    current = false
    const actual = await acceptObservedPhysical(handle, context, key, 'old-scope-physical-landed')
    assert.equal(actual.ok, true, actual.error)
    const result = await sending
    assert.equal(result.ok, false, 'old Scope cannot be confirmed by the final Advisor flow')
    assert.match(result.error, /Scope advanced before physical steer confirmation/)
  })
})

test('WHAT[advisor-005] unknown physical delivery from product runtime is not a successful steer', async t => {
  await withJournal('advisor-runtime-unknown', async handle => {
    const context = await prepareAcceptanceObserver(handle, 'continuation', 'advisor-runtime-unknown')
    let calls = 0
    const runtime = makeScope(handle, hostPort(async () => {
      calls++
      return dispatcher.acceptanceUnknown('receipt alone cannot prove landing')
    }))
    t.after(() => runtime.dispose())
    const result = await runtime.send(
      context.session, 'unverified final advice', 'advisor:unknown', () => true,
    )
    assert.equal(result.ok, false)
    assert.match(result.error, /Acceptance unknown/)
    assert.equal(calls, 1)
    assert.equal(dispatcher.pendingClaimCount(handle, context.session), 1)
  })
})
