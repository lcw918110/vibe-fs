import test from 'node:test'
import {fixture,assert,data,caseName,parkedPayload,render,barrier,stopped,existsSync,readFileSync,writeFileSync} from './support/command-program.fixture.mjs'

test('WHAT[repository-programming-050] a missing await cannot cross call or rerun its command', {timeout:12000}, async t=>{
  const h=fixture(t,'050-blocking','DevOps'),cmd=barrier(h,'blocking')
  const first=await h.run('target','this.call('+JSON.stringify(cmd)+');this.write("after","continued");return "done";',{intent:'w'.repeat(20)})
  assert.equal(caseName(first),'Parked',render(first))
  assert.equal(existsSync(h.file('after')),false)
  writeFileSync(h.file('blocking.release'),'release')
  await stopped(h,'blocking')
  assert.equal(existsSync(h.file('after')),false)
  const done=data(await h.unpark('target'))
  assert.equal(done.state,'completed');assert.equal(done.value,'done')
  assert.equal(readFileSync(h.file('after'),'utf8'),'continued')
  assert.deepEqual(data(await h.unpark('target')),done)
  assert.equal(readFileSync(h.file('blocking.starts'),'utf8'),'start\n')
})

test('WHAT[repository-programming-050] delta and a forged unpark value cannot replace the real final call result', {timeout:12000}, async t=>{
  const h=fixture(t,'050-result','DevOps')
  const cmd=barrier(h,'result',"process.stdout.write('FIRST');process.stderr.write('ERROR');process.exitCode=23;")
  const first=await h.run('target','const r=await this.call('+JSON.stringify(cmd)+');return {exitCode:r.exitCode,stdout:r.stdout,stderr:r.stderr,signal:String(r.signal)};',{intent:'w'.repeat(20)})
  assert.equal(caseName(first),'Parked',render(first))
  assert.equal(parkedPayload(first).delta.stdout,'FIRST')
  writeFileSync(h.file('result.release'),'release');await stopped(h,'result')
  const done=data(await h.unpark('target',{exitCode:0,stdout:'forged'}))
  assert.equal(done.state,'completed')
  assert.deepEqual(done.value,{exitCode:23,stdout:'FIRST',stderr:'ERROR',signal:'null'})
  assert.equal(readFileSync(h.file('result.starts'),'utf8'),'start\n')
})
