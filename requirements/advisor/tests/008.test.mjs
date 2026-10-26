import assert from 'node:assert/strict'
import test from 'node:test'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { resolve } from 'node:path'
import * as dispatcher from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import {
  withJournal, hostPort, prepareAcceptanceObserver,
  acceptObservedPhysical, ownAcceptanceRegistration,
} from '../../dispatch-protocol/tests/support/authority.mjs'

const pause = ms => new Promise(resolve => setTimeout(resolve, ms))
const source = file => readFileSync(resolve(fileURLToPath(new URL('../../..', import.meta.url)), file), 'utf8')

test('WHAT[advisor-008] the production Advisor steer selects Await while ordinary consultation remains Detached', () => {
  const runtime = source('src/Wanxiangshu/OpenCode/Tools/ToolRuntimeScope.fs')
  const nudge = source('src/Wanxiangshu/Execution/Delegation/Fork/Host/BusyNudge.fs')
  assert.match(runtime, /SendAdvisorSteer\([\s\S]*?sendConsultationSteer recipient answer processId \(fun _ _ -> isCurrent \(\)\) true/)
  assert.match(runtime, /HostForkBusyNudge\.sendObservedAwait sessions journal/)
  assert.match(runtime, /SendAgentOwnerRoot[\s\S]*?if awaitPhysical then PromptDispatcher\.AwaitMode\.Await else PromptDispatcher\.AwaitMode\.Detached/)
  assert.match(runtime, /sendConsultationSteer recipient answer receipt Wanxiangshu\.Context\.Scope\.Surface\.confirmNegotiationSteer false/)
  assert.match(nudge, /let sendObservedAwait[\s\S]*?PromptDispatcher\.AwaitMode\.Await/)
  assert.match(nudge, /let sendObserved[\s\S]*?PromptDispatcher\.AwaitMode\.Detached/)
})

test('WHAT[advisor-008] submitted SDK receipt does not finish Await until production dispatcher confirms PhysicalAccepted', async () => {
  await withJournal('advisor-physical-await', async handle => {
    const context = await prepareAcceptanceObserver(handle, 'continuation', 'advisor-physical-await')
    let resolveSubmitted
    const submitted = new Promise(resolve => { resolveSubmitted = resolve })
    let callId
    const physicals = []
    let completed = false
    const sending = dispatcher.sendContinuationWithAcceptance(
      hostPort(async (_session, _text, options) => {
        callId = options.Metadata.wanxiangshu_prompt_key
        resolveSubmitted()
        return dispatcher.admittedWithReceipt('queued-by-SDK-is-not-physical')
      }),
      handle, context.session, 'advisor recommendation', 'BusyAgentNudge',
      context.profile, 'Await',
      physical => physicals.push(physical),
      registration => ownAcceptanceRegistration(handle, registration),
    ).then(result => { completed = true; return result })
    try {
      await submitted
      await pause(30)
      assert.equal(completed, false, 'an SDK dispatch receipt must not satisfy the final Advisor steer')
      assert.equal(dispatcher.pendingClaimCount(handle, context.session), 1)
      assert.deepEqual(physicals, [])
      const accepted = await acceptObservedPhysical(handle, context, callId, 'advisor-physical-landed')
      assert.equal(accepted.ok, true, accepted.error)
      const sent = await sending
      assert.equal(sent.ok, true, sent.error)
      assert.deepEqual(physicals, ['advisor-physical-landed'])
      assert.equal(dispatcher.pendingClaimCount(handle, context.session), 0)
    } finally {
      if (!completed && callId) await acceptObservedPhysical(handle, context, callId, 'advisor-physical-cleanup')
      await sending
    }
  })
})

test('WHAT[advisor-008] physical send outcome Unknown does not count as a successful acceptance', async () => {
  await withJournal('advisor-physical-unknown', async handle => {
    const context = await prepareAcceptanceObserver(handle, 'continuation', 'advisor-physical-unknown')
    let calls = 0
    const seen = []
    const result = await dispatcher.sendContinuationWithAcceptance(
      hostPort(async () => {
        calls++
        return dispatcher.acceptanceUnknown('host cannot prove the steer landed')
      }),
      handle, context.session, 'unknown landing', 'BusyAgentNudge',
      context.profile, 'Await',
      physical => seen.push(physical),
      registration => ownAcceptanceRegistration(handle, registration),
    )
    assert.equal(result.ok, false)
    assert.match(result.error, /Acceptance unknown/i)
    assert.equal(calls, 1)
    assert.deepEqual(seen, [])
    assert.equal(dispatcher.pendingClaimCount(handle, context.session), 1, 'the real journal retains the unresolved claim')
  })
})
