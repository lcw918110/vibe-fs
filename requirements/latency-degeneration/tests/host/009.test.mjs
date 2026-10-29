import test from 'node:test'
import assert from 'node:assert/strict'
import {
  withJournal, prepareAcceptanceObserver, acceptObservedPhysical, ownAcceptanceRegistration,
} from '../../../dispatch-protocol/tests/support/authority.mjs'
import * as dispatch from '../../../../dist/Interaction/Dispatch/DispatchSurface.js'

test('WHAT[latency-degeneration-009] real detached sender returns without waiting but only emits resumed on genuine PhysicalAccepted',async()=>{
  const {hostPort}=await import('../../../dispatch-protocol/tests/support/authority.mjs')
  await withJournal('degeneration-detached-009',async handle=>{
    const context=await prepareAcceptanceObserver(handle,'continuation','detached-physical')
    const received=[]
    let key
    const sent=await dispatch.sendContinuationWithAcceptance(
      hostPort(async (_sid,_message,options)=>{
        key=options.Metadata.wanxiangshu_prompt_key
        return dispatch.admittedWithReceipt('sdk-queued')
      }),
      handle,context.session,'Continue without waiting for physical receipt',
      'ProviderRetryAttempt',context.profile,'Detached',
      physical=>received.push(physical),
      subscription=>ownAcceptanceRegistration(handle,subscription),
    )
    assert.equal(sent.ok,true,sent.error)
    assert.ok(key)
    assert.deepEqual(received,[],'claim and queued SDK receipt are NOT resumption')
    const accepted=await acceptObservedPhysical(handle,context,key,'physical-resume-009')
    assert.equal(accepted.ok,true,accepted.error)
    assert.deepEqual(received,['physical-resume-009'])
    assert.equal((await acceptObservedPhysical(handle,context,key,'physical-resume-009-duplicate')).ok,false,
      'the same prompt key cannot be admitted for two physical messages')
  })
})

const flush=async()=>{for(let i=0;i<24;i++)await Promise.resolve()}
const port=send=>({
  SendPrompt:send,
  SubscribeTerminal:()=>({Dispose(){}}),
  SubscribeFutureTerminal:()=>({Dispose(){}}),
  IsManagedChild:()=>true,
})
const returned=result=>{
  assert.equal(result?.ok,true,result.error)
  return result.key
}

test('WHAT[latency-degeneration-009] the production HostSessionNudge forwards a real late Refused after a detached claim',async()=>{
  await withJournal('degeneration-late-refused-009',async handle=>{
    const context=await prepareAcceptanceObserver(handle,'continuation','late-refusal')
    let late
    const resumed=[],rejected=[]
    const key=returned(await dispatch.sendContinuationDetachedObserved(
      port(async(_sid,_text,opts)=>{
        late=opts.DetachedListener
        return dispatch.admittedWithReceipt('host-queued-no-physical')
      }),
      handle,context.session,
      'Continue interrupted Engineer work',
      'ProviderRetryAttempt',id=>resumed.push(id),
      registration=>ownAcceptanceRegistration(handle,registration),
      reason=>rejected.push(reason),
    ))
    assert.ok(key)
    assert.equal(resumed.length,0)
    assert.equal(rejected.length,0)
    assert.equal(typeof late,'function','real port must install the Host late receipt listener')
    await dispatch.deliverDetachedVerdict(late, 'Refused', 'host did not accept prompt')
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
    const claim=await dispatch.sendContinuationDetachedObserved(
      port(async(_sid,_text,opts)=>{
        key=opts.Metadata.wanxiangshu_prompt_key
        return dispatch.admittedWithReceipt('queued-only')
      }),
      handle,context.session,
      'Continue interrupted Engineer work',
      'ProviderRetryAttempt',id=>resumed.push(id),
      registration=>ownAcceptanceRegistration(handle,registration),
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
  await withJournal('degeneration-direct-refused-009',async handle=>{
    const context=await prepareAcceptanceObserver(handle,'continuation','direct-refused')
    const resumed=[],rejected=[]
    const result=await dispatch.sendContinuationDetachedObserved(
      port(async()=>dispatch.retryable('initial Host admission refused')),
      handle,context.session,
      'Continue interrupted Engineer work','ProviderRetryAttempt',id=>resumed.push(id),
      registration=>ownAcceptanceRegistration(handle,registration),
      reason=>rejected.push(reason),
    )
    returned(result)
    await flush()
    assert.deepEqual(rejected,['initial Host admission refused'])
    assert.deepEqual(resumed,[])
  })
})

test('WHAT[latency-degeneration-009] ambiguous SDK delivery is not a definite refusal or fabricated PhysicalAccepted',async()=>{
  await withJournal('degeneration-outcome-unknown-009',async handle=>{
    const context=await prepareAcceptanceObserver(handle,'continuation','unknown')
    const resumed=[],rejected=[]
    const result=await dispatch.sendContinuationDetachedObserved(
      port(async()=>dispatch.acceptanceUnknown('provider never supplied acceptance evidence')),
      handle,context.session,
      'Continue interrupted Engineer work','ProviderRetryAttempt',id=>resumed.push(id),
      registration=>ownAcceptanceRegistration(handle,registration),
      reason=>rejected.push(reason),
    )
    returned(result)
    await flush()
    assert.deepEqual(rejected,[],'unknown external admission cannot be turned into a definite refusal')
    assert.deepEqual(resumed,[],'unknown external admission cannot be fabricated as a successful resumption')
  })
})
