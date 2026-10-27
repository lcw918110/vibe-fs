import test from 'node:test'
import assert from 'node:assert/strict'
import fc from 'fast-check'
import {LatencyWatchdogFactory_createObservedLatencyWatchdog as create} from '../../../dist/OpenCode/Host/LatencyWatchdog.js'

const microtasks=async()=>{for(let i=0;i<16;i++)await Promise.resolve()}
const failed=reason=>Promise.reject(new Error(reason))
const pending=()=>new Promise(()=>{})

function rig({interrupt,send,ack}){
  let clock=0,accepted,lateRefused
  const timers=[],facts=[]
  const abort=()=>{
    if(interrupt==='accept')return Promise.resolve(true)
    if(interrupt==='refuse')return Promise.resolve(false)
    if(interrupt==='reject')return failed('interrupt rejected')
    if(interrupt==='throw')throw Error('interrupt threw')
    return pending()
  }
  const continueObserved=(_sid,_text,physical,onRefused)=>{
    lateRefused=onRefused
    facts.push('send-invoked')
    accepted=physical
    if(send==='refuse')return Promise.resolve('physical send refused')
    if(send==='reject')return failed('send failed')
    if(send==='throw')throw Error('send threw')
    return send==='pending'?pending():Promise.resolve('')
  }
  const watchdog=create(
    ()=>clock,
    (ms,fire)=>{
      const timer={ms,fire,disposed:false,Dispose(){this.disposed=true}}
      timers.push(timer);return timer
    },
    abort,continueObserved,
    ()=>true,()=> 'Continue this unfinished work.',
    (name,reason)=>facts.push(name),
    (_sid,reason)=>facts.push('returned:'+reason),
    (_sid)=>facts.push('observer-released'),
    {ThresholdMs:()=>10,ObserveCensored:()=>facts.push('censored'),
      ObserveCompleted:()=>facts.push('completed'),Forget:()=>{}}
  )
  const start=run=>watchdog.Begin('engineer',run)
  const fire=()=>{clock=12;timers.at(-1).fire()}
  const physical=()=>{if(accepted)accepted()}
  const refuseLater=()=>{if(lateRefused)lateRefused('host definitively refused this queued prompt')}
  return {watchdog,timers,facts,start,fire,physical,refuseLater}
}

test('WHAT[latency-degeneration-009] fast-check: each causal resolution settles by real physical resume or explicit return, never a pending code await',async()=>{
  await fc.assert(fc.asyncProperty(
    fc.constantFrom('accept','refuse','reject','throw'),
    fc.constantFrom('accepted','refuse','reject','throw'),
    fc.boolean(),
    async(interrupt,send,ackBeforeResult)=>{
      const r=rig({interrupt,send,ack:ackBeforeResult})
      try{
        r.start('run-1')
        r.fire()
        // No timers or time-bounded Promise.race resolve the recovery.
        // The scheduler is simply allowed to flush already-runnable callbacks.
        await microtasks()
        if(interrupt!=='accept'){
          assert.equal(r.facts.includes('send-invoked'),false)
          if(interrupt==='refuse'){
            assert.ok(r.facts.includes('latency-degeneration-abort-refused'))
            assert.equal(r.watchdog.Snapshot().length,1,
              'host refused to interrupt, so the original Provider remains responsible')
          }else{
            assert.ok(r.facts.some(x=>x.startsWith('returned:')),
              'definite interrupt failures are returned to parent immediately')
            assert.equal(r.watchdog.Snapshot().length,0)
          }
        } else if(send==='accepted'){
          assert.ok(r.facts.includes('send-invoked'))
          assert.equal(r.facts.includes('latency-degeneration-resumed'),false,
            'queued is not physically accepted')
          r.physical()
          await microtasks()
          assert.equal(r.facts.filter(x=>x==='latency-degeneration-resumed').length,1)
          assert.equal(r.facts.some(x=>x.startsWith('returned:')),false)
        } else{
          assert.equal(r.facts.filter(x=>x.startsWith('returned:')).length,1)
          assert.equal(r.facts.includes('latency-degeneration-resumed'),false)
        }
        r.timers[0].fire()
        await microtasks()
        assert.ok(r.facts.filter(x=>x==='latency-degeneration-resumed').length<=1)
        assert.ok(r.facts.filter(x=>x.startsWith('returned:')).length<=1)
      }finally{r.watchdog.Dispose()}
    }),
    {seed:0xD36E2026,numRuns:200}
  )
})

test('WHAT[latency-degeneration-009] fast-check: queued send cannot fake success; real later physical acceptance resumes once',async()=>{
  await fc.assert(fc.asyncProperty(fc.boolean(),async(secondRun)=>{
    const r=rig({interrupt:'accept',send:'pending'})
    try{
      r.start('old')
      r.fire()
      await microtasks()
      assert.ok(r.facts.includes('send-invoked'))
      assert.ok(!r.facts.includes('latency-degeneration-resumed'))
      assert.ok(!r.facts.some(x=>x.startsWith('returned:')))
      if(secondRun){
        r.start('new')
        r.physical()
        await microtasks()
        assert.ok(!r.facts.includes('latency-degeneration-resumed'),
          'late observer from old interrupted run cannot complete a newer Provider run')
      }else{
        r.physical()
        r.physical()
        await microtasks()
        assert.equal(r.facts.filter(x=>x==='latency-degeneration-resumed').length,1)
      }
    }finally{r.watchdog.Dispose()}
  }),{seed:0x11AD2026,numRuns:70})
})

test('WHAT[latency-degeneration-009] pending external abort never blocks a new Provider attempt or session cancellation',async()=>{
  const r=rig({interrupt:'pending',send:'pending'})
  r.start('old')
  r.fire()
  await microtasks()
  r.start('new')
  assert.equal(r.watchdog.Snapshot().length,1)
  r.watchdog.Discard('engineer','new')
  assert.equal(r.watchdog.Snapshot().length,0)
  assert.equal(r.facts.includes('send-invoked'),false,
    'an unconfirmed interrupt may not be treated as accepted')
  r.watchdog.Cancel('engineer')
  r.watchdog.Dispose()
})


test('WHAT[latency-degeneration-009] fast-check: delayed Host Refused and physical acceptance settle exactly once in either order',async()=>{
  await fc.assert(fc.asyncProperty(fc.boolean(),async(physicalFirst)=>{
    const r=rig({interrupt:'accept',send:'pending'})
    try{
      r.start('exact-run')
      r.fire()
      await microtasks()
      assert.ok(r.facts.includes('send-invoked'))
      if(physicalFirst){r.physical();r.refuseLater()}
      else{r.refuseLater();r.physical()}
      await microtasks()
      assert.equal(r.facts.filter(x=>x==='latency-degeneration-resumed').length+(r.facts.filter(x=>x.startsWith('returned:')).length),1)
      assert.equal(r.facts.includes('latency-degeneration-resumed'),physicalFirst)
    }finally{r.watchdog.Dispose()}
  }),{seed:0xD36E0926,numRuns:70})
})


test('WHAT[latency-degeneration-009] idle during a refused interrupt returns explicitly instead of silently forgetting the work',async()=>{
  const outcomes=[]
  let concludeAbort
  const cancelled=[]
  const watchdog=create(
    ()=>12,
    (_ms,trigger)=>{cancelled.push(trigger);return {Dispose(){}}},
    ()=>new Promise(resolve=>{concludeAbort=resolve}),
    ()=>{assert.fail('refused interrupt must not send a second prompt')},
    ()=>true,()=> 'continue',
    kind=>outcomes.push(kind),
    (_sid,reason)=>outcomes.push('returned:'+reason),
    ()=>{},
    {ThresholdMs:()=>10,ObserveCensored:()=>{},ObserveCompleted:()=>{},Forget:()=>{}}
  )
  watchdog.Begin('engineer','run')
  cancelled[0]()
  watchdog.OnIdle('engineer')
  assert.equal(typeof concludeAbort,'function')
  concludeAbort(false)
  await microtasks()
  assert.equal(outcomes.filter(x=>x.startsWith('returned:')).length,1,
    'idle plus a definite rejected abort means there is no running attempt left to resume')
  assert.equal(watchdog.Snapshot().length,0)
  watchdog.Dispose()
})


test('WHAT[latency-degeneration-009] fast-check: both orderings of rejected interrupt and idle return abandoned child',async()=>{
  await fc.assert(fc.asyncProperty(fc.boolean(),async(idleFirst)=>{
    let settleAbort
    let fired
    const events=[]
    const watchdog=create(
      ()=>12,
      (_ms,fire)=>{fired=fire;return {Dispose(){}}},
      ()=>new Promise(resolve=>{settleAbort=resolve}),
      ()=>{assert.fail('definite rejected interrupt must never send continuation')},
      ()=>true,()=> 'continue',
      kind=>events.push(kind),
      (_sid,reason)=>events.push('returned:'+reason),
      ()=>{},
      {ThresholdMs:()=>10,ObserveCensored:()=>{},ObserveCompleted:()=>{},Forget:()=>{}},
    )
    try{
      watchdog.Begin('engineer','run')
      fired()
      if(idleFirst)watchdog.OnIdle('engineer')
      settleAbort(false)
      await microtasks()
      if(!idleFirst)watchdog.OnIdle('engineer')
      await microtasks()
      assert.equal(events.filter(x=>x.startsWith('returned:')).length,1,
        'rejected interrupt with an idle child must return regardless of callback order')
      assert.equal(watchdog.Snapshot().length,0)
    }finally{watchdog.Dispose()}
  }),{seed:0x1D1E2026,numRuns:65})
})


test('WHAT[latency-degeneration-009] fast-check: session owner releases pending detached observer on cancel, supersession and disposal',async()=>{
  await fc.assert(fc.asyncProperty(
    fc.constantFrom('cancel','new-run','dispose'),
    fc.boolean(),
    async(action,latePhysical)=>{
      const r=rig({interrupt:'accept',send:'pending'})
      r.start('old-provider')
      r.fire()
      await microtasks()
      assert.ok(r.facts.includes('send-invoked'))
      if(action==='cancel') r.watchdog.Cancel('engineer')
      else if(action==='dispose') r.watchdog.Dispose()
      else r.start('fresh-provider')
      assert.equal(r.facts.filter(x=>x==='observer-released').length,1)
      if(latePhysical)r.physical()
      r.refuseLater()
      await microtasks()
      assert.equal(r.facts.some(x=>x==='latency-degeneration-resumed'),false)
      assert.equal(r.facts.some(x=>x.startsWith('returned:')),false)
      r.watchdog.Dispose()
    }
  ),{seed:0xD36ECA4C,numRuns:100})
})
