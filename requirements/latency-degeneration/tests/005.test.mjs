import test from 'node:test'
import assert from 'node:assert/strict'
import {
  withJournal, prepareAcceptanceObserver, acceptObservedPhysical, ownAcceptanceRegistration,
} from '../../dispatch-protocol/tests/support/authority.mjs'
import {sendContinuationDetachedObservedResult} from '../../../dist/Interaction/Dispatch/SessionNudge.js'
import {PromptContinuationKind} from '../../../dist/Interaction/Authority/Origin.js'
import {SessionIdModule_create} from '../../../dist/Foundation/Identity.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import {JournalHandle__get_Journal} from '../../../dist/Persistence/Journal/Surface.js'

const flush=async()=>{for(let i=0;i<24;i++)await Promise.resolve()}
const port=send=>({
  SendPrompt:send,
  SubscribeTerminal:()=>({Dispose(){}}),
  SubscribeFutureTerminal:()=>({Dispose(){}}),
  IsManagedChild:()=>true,
})
const witness=(callback,handle)=>({
  Notify:callback,
  AttachDisposable:registration=>ownAcceptanceRegistration(handle,registration),
  Budget:undefined,
})
const returned=result=>{
  assert.equal(result?.tag,0,'detached claim must be accepted')
  return result.fields[0]
}

test('WHAT[latency-degeneration-009] the production HostSessionNudge forwards a real late Refused after a detached claim',async()=>{
  await withJournal('degeneration-late-refused-009',async handle=>{
    const context=await prepareAcceptanceObserver(handle,'continuation','late-refusal')
    let late
    const resumed=[],rejected=[]
    const key=returned(await sendContinuationDetachedObservedResult(
      port(async(_sid,_text,opts)=>{
        late=opts.DetachedListener
        return dispatch.admittedWithReceipt('host-queued-no-physical')
      }),
      {TryRead:()=>undefined},SessionIdModule_create(context.session),
      'Continue interrupted Engineer work',
      PromptContinuationKind.ProviderRetryAttempt,
      undefined,JournalHandle__get_Journal(handle),witness(id=>resumed.push(id),handle),
      reason=>rejected.push(reason),
    ))
    assert.ok(key)
    assert.equal(resumed.length,0)
    assert.equal(rejected.length,0)
    assert.equal(typeof late,'function','real port must install the Host late receipt listener')
    const {DetachedSendVerdict}=await import('../../../dist/Foundation/HostSendContract.js')
    await late(new DetachedSendVerdict(1,['host did not accept prompt']))
    await flush()
    assert.deepEqual(rejected,['host did not accept prompt'])
    assert.deepEqual(resumed,[])
  })
})

test('WHAT[latency-degeneration-009] the real detached sender receives physical acceptance without blocking on its Host enqueue receipt',async()=>{
  await withJournal('degeneration-sender-accepted-009',async handle=>{
    const context=await prepareAcceptanceObserver(handle,'continuation','late-accepted')
    let key
    const resumed=[],rejected=[]
    const claim=await sendContinuationDetachedObservedResult(
      port(async(_sid,_text,opts)=>{
        key=opts.Metadata.wanxiangshu_prompt_key
        return dispatch.admittedWithReceipt('queued-only')
      }),
      {TryRead:()=>undefined},SessionIdModule_create(context.session),
      'Continue interrupted Engineer work',
      PromptContinuationKind.ProviderRetryAttempt,
      undefined,JournalHandle__get_Journal(handle),witness(id=>resumed.push(id),handle),
      reason=>rejected.push(reason),
    )
    returned(claim)
    assert.ok(key)
    assert.deepEqual(resumed,[])
    const physically=await acceptObservedPhysical(handle,context,key,'physically-resumed')
    assert.equal(physically.ok,true,physically.error)
    assert.equal(resumed.length,1)
    assert.deepEqual(rejected,[])
  })
})


test('WHAT[latency-degeneration-009] an initial definite SDK refusal returns through the real Sender without physical acceptance',async()=>{
  const {Outcome_SendOutcome}=await import('../../../dist/Foundation/Outcome.js')
  await withJournal('degeneration-direct-refused-009',async handle=>{
    const context=await prepareAcceptanceObserver(handle,'continuation','direct-refused')
    const resumed=[],rejected=[]
    const result=await sendContinuationDetachedObservedResult(
      port(async()=>new Outcome_SendOutcome(2,['initial Host admission refused'])),
      {TryRead:()=>undefined},SessionIdModule_create(context.session),
      'Continue interrupted Engineer work',PromptContinuationKind.ProviderRetryAttempt,
      undefined,JournalHandle__get_Journal(handle),witness(id=>resumed.push(id),handle),
      reason=>rejected.push(reason),
    )
    returned(result)
    await flush()
    assert.deepEqual(rejected,['initial Host admission refused'])
    assert.deepEqual(resumed,[])
  })
})

test('WHAT[latency-degeneration-009] ambiguous SDK delivery is not a definite refusal or fabricated PhysicalAccepted',async()=>{
  const {Outcome_SendOutcome}=await import('../../../dist/Foundation/Outcome.js')
  await withJournal('degeneration-outcome-unknown-009',async handle=>{
    const context=await prepareAcceptanceObserver(handle,'continuation','unknown')
    const resumed=[],rejected=[]
    const result=await sendContinuationDetachedObservedResult(
      port(async()=>new Outcome_SendOutcome(3,['provider never supplied acceptance evidence'])),
      {TryRead:()=>undefined},SessionIdModule_create(context.session),
      'Continue interrupted Engineer work',PromptContinuationKind.ProviderRetryAttempt,
      undefined,JournalHandle__get_Journal(handle),witness(id=>resumed.push(id),handle),
      reason=>rejected.push(reason),
    )
    returned(result)
    await flush()
    assert.deepEqual(rejected,[],'unknown external admission cannot be turned into a definite refusal')
    assert.deepEqual(resumed,[],'unknown external admission cannot be fabricated as a successful resumption')
  })
})
