import test from 'node:test'
import assert from 'node:assert/strict'
import {forkCall,resumeCall,withRealFork} from './support/real-fork.mjs'

test('WHAT[delegation-036] real Manager busy resume queues child guidance without interrupting active Engineer Scope',async()=>{
  await withRealFork('manager-nonpreemptive-busy-036',async({hooks,runtime,createdIds})=>{
    const first=String(await forkCall(hooks,'manager-nonpreemptive-busy-036','Ada','Implement scope safely'))
    assert.match(first,/ok = true/)
    await runtime.awaitIngress()
    const child=createdIds[0]
    assert.ok(child)
    const before=runtime.prompts.length
    const abortedBefore=runtime.abortedIds.filter(id=>id===child).length
    const result=String(await resumeCall(hooks,'manager-nonpreemptive-busy-036','Ada',
      'Additional context that must not cancel the live tool or resetScope','call-busy-036'))
    assert.match(result,/ok = true/)
    assert.match(result,/nudge|nudged/i)
    const extra=runtime.prompts.slice(before).filter(entry=>
      entry.path?.id===child &&
      entry.body?.metadata?.wanxiangshu_origin==='BusyAgentNudge')
    assert.equal(extra.length,1,'one registered PromptDispatcher BusyAgentNudge must be sent')
    assert.equal(extra[0].body.delivery,'queue',
      'an active Engineer cannot be re-steered into a new provider request in the middle of a Scope')
    assert.equal(extra[0].delivery,'queue')
    assert.ok(extra[0].body.metadata.wanxiangshu_prompt_key)
    assert.notEqual(extra[0].body.parts[0].metadata.wanxiangshu_consultation_steer,true,
      'ordinary BusyAgentNudge must not be disguised as a consultation steer')
    assert.equal(runtime.abortedIds.filter(id=>id===child).length,abortedBefore,
      'parent-provided guidance must not physically abort the child')
  })
})
