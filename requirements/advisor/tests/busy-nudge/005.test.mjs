import assert from 'node:assert/strict'
import test from 'node:test'
import { ToolRuntimeScopeSurface } from '../../../../dist/OpenCode/Tools/ToolRuntimeScopeSurface.js'
import * as dispatcher from '../../../../dist/Interaction/Dispatch/DispatchSurface.js'
import {
  withJournal, hostPort, prepareAcceptanceObserver, acceptObservedPhysical,
} from '../../../dispatch-protocol/tests/support/authority.mjs'

const pause = ms => new Promise(resolve => setTimeout(resolve, ms))
const setup = async (handle, suffix) => {
  const context = await prepareAcceptanceObserver(handle, 'continuation', suffix)
  assert.ok(context.profile, 'the production authority projection must own a real active logical run')
  return { context, session: context.session }
}

test('WHAT[advisor-005] actual BusyNudge Await wrapper waits for durable PhysicalAccepted', async () => {
  await withJournal('advisor-host-awaiter', async handle => {
    const { context, session } = await setup(handle, 'advisor-host-await')
    let promptKey
    let notified
    let resolveSubmitted
    const submitted = new Promise(resolve => { resolveSubmitted = resolve })
    const observer = {
      Notify: message => { notified = message },
      AttachDisposable: () => {},
    }
    let completed = false
    const sending = ToolRuntimeScopeSurface.sendBusyNudgeAwait(handle,
      hostPort(async (_session, _text, options) => {
        promptKey = options.Metadata.wanxiangshu_prompt_key
        resolveSubmitted()
        return dispatcher.admittedWithReceipt('sdk-queue-only')
      }),
      session, 'advisor final steer', observer,
    ).then(result => { completed = true; return result })
    try {
      await submitted
      await pause(30)
      assert.equal(completed, false, 'Host SDK queued receipt is not a physical landing')
      assert.equal(dispatcher.pendingClaimCount(handle, context.session), 1)
      assert.equal(notified, undefined)
      const accepted = await acceptObservedPhysical(handle, context, promptKey, 'real-advisor-physical')
      assert.equal(accepted.ok, true, accepted.error)
      const result = await sending
      assert.deepEqual(result, { ok: true, error: null })
      assert.ok(notified, 'the physical acceptance observer must run on durable landing')
      assert.equal(dispatcher.pendingClaimCount(handle, context.session), 0)
    } finally {
      if (!completed && promptKey) await acceptObservedPhysical(handle, context, promptKey, 'cleanup-physical')
      await sending
    }
  })
})

test('WHAT[advisor-005] accepted observation budget is honored instead of the 10 second admission default', async () => {
  await withJournal('advisor-host-budget', async handle => {
    const { context, session } = await setup(handle, 'advisor-host-budget')
    let promptKey
    let resolveSubmitted
    const submitted = new Promise(resolve => { resolveSubmitted = resolve })
    const observer = {
      Notify: () => {},
      AttachDisposable: () => {},
      Budget: 1200,
    }
    let completed = false
    const sending = ToolRuntimeScopeSurface.sendBusyNudgeAwait(handle,
      hostPort(async (_session, _text, options) => {
        promptKey = options.Metadata.wanxiangshu_prompt_key
        resolveSubmitted()
        return dispatcher.admittedWithReceipt('sdk-queue-only')
      }),
      session, 'advisor final steer', observer,
    ).then(result => { completed = true; return result })
    try {
      await submitted
      await pause(30)
      assert.equal(completed, false, 'a queued SDK receipt is still not a physical landing')
      const accepted = await acceptObservedPhysical(handle, context, promptKey, 'late-advisor-physical')
      assert.equal(accepted.ok, true, accepted.error)
      const result = await sending
      assert.deepEqual(result, { ok: true, error: null })
    } finally {
      if (!completed && promptKey) await acceptObservedPhysical(handle, context, promptKey, 'cleanup-budget-physical')
      await sending
    }
  })
})

test('WHAT[advisor-005] actual BusyNudge Await wrapper preserves unknown landing and no fabricated confirmation', async () => {
  await withJournal('advisor-host-unknown', async handle => {
    const { context, session } = await setup(handle, 'advisor-host-unknown')
    let notified = false
    const result = await ToolRuntimeScopeSurface.sendBusyNudgeAwait(handle,
      hostPort(async () => dispatcher.acceptanceUnknown('host failed after taking the request')),
      session, 'advisor final steer', {
        Notify: () => { notified = true },
        AttachDisposable: () => {},
      },
    )
    assert.equal(result.ok, false)
    assert.match(result.error, /Acceptance unknown/)
    assert.equal(notified, false)
    assert.equal(dispatcher.pendingClaimCount(handle, context.session), 1)
  })
})

test('WHAT[advisor-005] an acceptance that arrives after the stated budget is reported Unknown, never Completed', async () => {
  await withJournal('advisor-host-budget-expired', async handle => {
    const { context, session } = await setup(handle, 'advisor-host-expired')
    let promptKey
    let resolveSubmitted
    const submitted = new Promise(resolve => { resolveSubmitted = resolve })
    const result = await ToolRuntimeScopeSurface.sendBusyNudgeAwait(handle,
      hostPort(async (_session, _text, options) => {
        promptKey = options.Metadata.wanxiangshu_prompt_key
        resolveSubmitted()
        return dispatcher.admittedWithReceipt('sdk-queue-only')
      }),
      session, 'advisor final steer', {
        Notify: () => {},
        AttachDisposable: () => {},
        Budget: 60,
      },
    )
    await submitted
    assert.equal(result.ok, false, 'a late landing cannot be reported as a completed steer')
    assert.match(result.error, /Acceptance unknown/)
    assert.equal(dispatcher.pendingClaimCount(handle, context.session), 1,
      'an unproven landing keeps its pending claim instead of releasing it')
    const accepted = await acceptObservedPhysical(handle, context, promptKey, 'too-late-physical')
    assert.equal(accepted.ok, true,
      'a landing that really happened is still accepted by the durable claim')
    assert.equal(dispatcher.pendingClaimCount(handle, context.session), 0,
      'the late landing settles the claim without turning the expired wait into a Completed steer')
  })
})
