import test from 'node:test'
import {performance} from 'node:perf_hooks'
import {execute} from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import {fixture,assert,data,caseName,failureCode,parkedPayload,render,source,barrier,stopped,until,delay,existsSync,readFileSync,writeFileSync} from './support/command-program.fixture.mjs'

test('WHAT[repository-programming-052] top-level intent validates before effects and computed member budgets are rejected',async t=>{
  const h=fixture(t,'052-entry','DevOps')
  for(const intent of [undefined,'',null,42]) {
    const output=await execute(h.root,'DevOps','en',h.session,{name:'bad',program:source('this.write("bad","ran");return 1;'),intent},2000,Date.now()+60000,1<<20,null)
    assert.equal(caseName(output),'Failed');assert.equal(failureCode(output),'INVALID_ARGUMENT')
    assert.equal(existsSync(h.file('bad')),false)
  }
  const output=await h.run('bypass','const intent="x".repeat(48000);return await this.call("touch bypass",intent);')
  assert.equal(caseName(output),'Failed');assert.equal(failureCode(output),'INVALID_ARGUMENT')
  assert.equal(existsSync(h.file('bypass')),false)
})

test('WHAT[repository-programming-052] consecutive calls spend the same admission window', {timeout:15000},async t=>{
  const h=fixture(t,'052-sequence','DevOps'),first=barrier(h,'first'),second=barrier(h,'second')
  const started=performance.now()
  const pending=h.run('target','await this.call('+JSON.stringify(first)+');this.write("first.after","done");await this.call('+JSON.stringify(second)+');this.write("second.after","done");return "done";',{intent:'s'.repeat(40)})
  await until(()=>existsSync(h.file('first.pid')))
  await delay(Math.max(0,started+1000-performance.now()))
  writeFileSync(h.file('first.release'),'release')
  await until(()=>existsSync(h.file('second.pid')))
  const secondAt=performance.now(),receipt=await pending,ended=performance.now()
  assert.equal(caseName(receipt),'Parked',render(receipt))
  assert.equal(readFileSync(h.file('first.after'),'utf8'),'done')
  assert.equal(existsSync(h.file('second.after')),false)
  assert.ok(ended-started>=1930);assert.ok(ended-started<2600)
  assert.ok(ended-secondAt<1500,'second call renewed the budget')
  writeFileSync(h.file('second.release'),'release');await stopped(h,'second')
  assert.equal(existsSync(h.file('second.after')),false)
  assert.equal(data(await h.unpark('target')).state,'completed')
  for(const name of ['first','second']) assert.equal(readFileSync(h.file(name+'.starts'),'utf8'),'start\n')
})

test('WHAT[repository-programming-052] controller and target share expiry; controller recovery replays its cached target pause', {timeout:15000},async t=>{
  const h=fixture(t,'052-control','DevOps'),cmd=barrier(h,'target')
  assert.equal(caseName(await h.run('target','await this.park("ready");await this.call('+JSON.stringify(cmd)+');this.write("target.after","done");return "done";')),'Parked')
  const started=performance.now()
  const controller=await h.run('controller','const r=await this.unpark("target");this.write("controller.after",r.state);return r;',{intent:'界'.repeat(8)})
  assert.equal(caseName(controller),'Parked',render(controller))
  assert.ok(performance.now()-started>=1130,'UTF-8 byte budget was not used')
  writeFileSync(h.file('target.release'),'release');await stopped(h,'target')
  const done=data(await h.unpark('controller'))
  assert.equal(done.state,'completed');assert.equal(done.value.name,'target');assert.equal(done.value.state,'parked')
  assert.equal(existsSync(h.file('target.after')),false)
  assert.equal(readFileSync(h.file('controller.after'),'utf8'),'parked')
  assert.equal(data(await h.unpark('target')).state,'completed')
  assert.deepEqual(data(await h.unpark('controller')),done)
  assert.equal(readFileSync(h.file('target.starts'),'utf8'),'start\n')
})
