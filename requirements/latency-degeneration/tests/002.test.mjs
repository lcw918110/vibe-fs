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
  assert.match(wiring,/HostSessionNudge\.sendContinuationResult/,
    'the interruption must send the existing managed continuation, not only record censored latency')
  assert.match(wiring,/PromptDispatcher\.AwaitMode\.Await/,
    'a continuation must await physical acceptance rather than only write a Detached claim')
  assert.match(wiring,/Some acceptance/,
    'the send must bind a real acceptance observer instead of treating queueing as delivery')
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
