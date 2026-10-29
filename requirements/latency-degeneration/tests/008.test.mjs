import test from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import * as codec from '../../../dist/OpenCode/Host/HostSignalSurface.js'
import {LatencyWatchdogFactory_createLatencyWatchdog as make} from '../../../dist/OpenCode/Host/LatencyWatchdog.js'

const message=(id,created,completed,finish,error)=>({
  type:'message.updated',sessionID:'engineer',
  properties:{info:{sessionID:'engineer',role:'assistant',id,parentID:'operator-message',
    time:{created,...(completed===undefined?{}:{completed})},
    ...(finish?{finish}:{}),...(error?{error}:{})}},
})

test('WHAT[latency-degeneration-008] real scope reset tool-calls is end of LLM request, not start of a long tool timeout', async()=>{
  const source=readFileSync(new URL('../../../src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs',import.meta.url),'utf8')
  const anchor=source.slice(source.indexOf('let settleLatencyWatchdog raw ='),source.indexOf('let observeEvent raw ='))
  assert.match(anchor,/tryDecodeProviderStepEnd/,
    'actual Host watchdog wiring must consume all exact provider-step ends including tool-calls')
  const calls=[], scheduled=[], censored=[], complete=[]
  let now=0
  const wd=make(()=>now,(ms,fire)=>{
    const timer={ms,fire,cancelled:false,Dispose(){this.cancelled=true}}
    scheduled.push(timer)
    return timer
  },async sid=>{calls.push(sid);return true},async()=>'',()=>true,()=>'',()=>{},
  {ThresholdMs:()=>100,ObserveCompleted:(s,elapsed)=>complete.push(elapsed),
   ObserveCensored:(s,elapsed)=>censored.push(elapsed),Forget:()=>{}})
  const start=message('assistant-tool-reset',0)
  const toolFinish=message('assistant-tool-reset',0,10,'tool-calls')
  assert.equal(codec.tryDecodeExactProviderStart(start)?.providerRun,'assistant-tool-reset')
  assert.equal(codec.tryDecodeExactProviderTerminal(toolFinish),null,
    'ordinary conversation terminal decoder does not classify a tool-call end')
  assert.equal(codec.tryDecodeProviderStepEnd(toolFinish)?.providerRun,'assistant-tool-reset',
    'actual Host event decoder already knows this exact finished tool-call')
  wd.Begin('engineer','assistant-tool-reset')
  now=10
  wd.Complete('engineer','assistant-tool-reset')
  assert.equal(wd.Snapshot().length,0)
  assert.equal(scheduled[0].cancelled,true)
  assert.deepEqual(complete,[10])
  now=350
  if(!scheduled[0].cancelled)scheduled[0].fire()
  await new Promise(resolve=>setTimeout(resolve,0))
  assert.deepEqual(calls,[],'time consumed by resetScope and subsequent tool operations cannot abort the Engineer')
  assert.deepEqual(censored,[])
})

test('WHAT[latency-degeneration-008] exact provider error/abort drops its timer without billing a complete sample or cancelling successor',async()=>{
  const c=[],timers=[]
  let now=0
  const wd=make(()=>now,(ms,fire)=>{const t={fire,disposed:false,Dispose(){this.disposed=true}};timers.push(t);return t},
    async sid=>{c.push(sid);return true},async()=>'',()=>true,()=>'',()=>{},
    {ThresholdMs:()=>100,ObserveCompleted:()=>{throw Error('failed/cancelled request is not an OK sample')},
     ObserveCensored:()=>{},Forget:()=>{}})
  assert.equal(typeof wd.Discard,'function','watchdog needs an exact-run unsampled terminal operation')
  wd.Begin('engineer','run-before-reset')
  wd.Begin('engineer','run-after-reset')
  wd.Discard('engineer','run-before-reset')
  assert.equal(wd.Snapshot().length,1,'a delayed older terminal cannot disarm a newer attempt')
  wd.Discard('engineer','run-after-reset')
  assert.equal(wd.Snapshot().length,0)
  assert.equal(timers[1].disposed,true)
  assert.deepEqual(c,[])
})
