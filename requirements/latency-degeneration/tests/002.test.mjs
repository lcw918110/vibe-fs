import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { interruptAttemptAdapterProbe } from '../../../dist/OpenCode/Host/SessionsSurface.js'

test('WHAT[latency-degeneration-007] interrupting a Provider attempt retains a managed child session for its next continuation',async()=>{
  const physical=await interruptAttemptAdapterProbe()
  assert.equal(physical.created,true)
  assert.equal(physical.childInterrupted,true)
  assert.equal(physical.childStillManagedAfterInterrupt,true,
    'the continuation must address the original still-managed Session, not a session terminated by AbortSession')
  assert.equal(physical.rootRejected,true,
    'the external operator owns interruptions of a user-facing root Session')
})

test('WHAT[latency-degeneration-007] production latency watchdog uses the attempt interruption port, not session termination',()=>{
  const source=readFileSync(new URL('../../../src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs',import.meta.url),'utf8')
  const beginning=source.indexOf('let latencyPorts: LatencyWatchdogPorts')
  const end=source.indexOf('let latencyWatchdog =',beginning)
  assert.ok(beginning>0&&end>beginning)
  const wiring=source.slice(beginning,end)
  assert.match(wiring,/Abort = fun sessionId ->[\s\S]*?sessionPort\.InterruptAttempt/)
  assert.doesNotMatch(wiring,/sessionPort\.AbortSession/,
    'a watchdog must not tear down the session it needs to resume')
  assert.match(wiring,/HostSessionNudge\.sendContinuationDetachedObservedResult/,
    'the interruption must send the existing managed continuation, not only record censored latency')
  const sender=readFileSync(new URL('../../../src/Wanxiangshu/Interaction/Dispatch/SessionNudge.fs',import.meta.url),'utf8')
  assert.match(sender,/PromptDispatcher\.AwaitMode\.Detached/,
    'the Host sender must dispatch without blocking on physical acceptance')
  assert.match(wiring,/Notify = fun _ ->/, 'physical event callback must be installed')
  assert.match(wiring,/accepted \(\)/, 'acceptance notification must settle continuation')
  assert.match(wiring,/onLateRefused/, 'a late definite Host refusal must reach the failure-return port')
  assert.match(wiring,/\n\s+acceptance\n/,
    'the Host sender must receive a physical acceptance observer, not merely an SDK receipt')
})

test('WHAT[latency-degeneration-007] post-interrupt continuation waits for true PhysicalAccepted, not a queued SDK receipt', async()=>{
  const dispatcher=await import('../../../dist/Interaction/Dispatch/DispatchSurface.js')
  const {withJournal, hostPort, prepareAcceptanceObserver, acceptObservedPhysical, ownAcceptanceRegistration}=
    await import('../../dispatch-protocol/tests/support/authority.mjs')
  await withJournal('degeneration-physical-007',async handle=>{
    const context=await prepareAcceptanceObserver(handle,'continuation','latency-resume')
    let key
    let letSend
    const submitted=new Promise(resolve=>{letSend=resolve})
    let completed=false
    const landed=[]
    const sending=dispatcher.sendContinuationWithAcceptance(
      hostPort(async (_sid,_prose,options)=>{
        key=options.Metadata.wanxiangshu_prompt_key
        letSend()
        return dispatcher.admittedWithReceipt('sdk-abort-restart-queued')
      }),
      handle,context.session,'resume after degeneration interruption',
      'ProviderRetryAttempt',context.profile,'Await',
      physical=>landed.push(physical),
      registration=>ownAcceptanceRegistration(handle,registration),
    ).then(result=>{completed=true;return result})
    try{
      await submitted
      await new Promise(resolve=>setTimeout(resolve,25))
      assert.equal(completed,false,'queuing a continuation after abort is not yet model resumption')
      assert.deepEqual(landed,[])
      const observed=await acceptObservedPhysical(handle,context,key,'physical-resumed-007')
      assert.equal(observed.ok,true,observed.error)
      const result=await sending
      assert.equal(result.ok,true,result.error)
      assert.deepEqual(landed,['physical-resumed-007'])
    }finally{
      if(!completed&&key)await acceptObservedPhysical(handle,context,key,'physical-cleanup-007')
      await sending
    }
  })
})


test('WHAT[latency-degeneration-009] real detached sender returns without waiting but only emits resumed on genuine PhysicalAccepted',async()=>{
  const dispatcher=await import('../../../dist/Interaction/Dispatch/DispatchSurface.js')
  const {withJournal,hostPort,prepareAcceptanceObserver,acceptObservedPhysical,ownAcceptanceRegistration}=
    await import('../../dispatch-protocol/tests/support/authority.mjs')
  await withJournal('degeneration-detached-009',async handle=>{
    const context=await prepareAcceptanceObserver(handle,'continuation','detached-physical')
    const received=[]
    let key
    const sent=await dispatcher.sendContinuationWithAcceptance(
      hostPort(async (_sid,_message,options)=>{
        key=options.Metadata.wanxiangshu_prompt_key
        return dispatcher.admittedWithReceipt('sdk-queued')
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
