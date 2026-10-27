import test from 'node:test'
import { fixture, assert, data, caseName, failureCode, parkedName, parkedPayload, parkedSource, render, created, readFileSync, writeFileSync, existsSync, pendingNames } from './support/named-program.fixture.mjs'

test('WHAT[repository-programming-033] terminal receipt replays and an ended name starts a new run', async t => {
  const h=fixture(t,'033')
  await h.run('target',"const value=await this.park({ready:true});this.write('once',String(value));return value;")
  const first=data(await h.unpark('target','first'))
  const second=data(await h.unpark('target','second'))
  assert.deepEqual(second,first)
  assert.equal(readFileSync(h.file('once'),'utf8'),'first')
  const reused=await h.run('target',"this.write('again','yes');return 2;")
  assert.equal(caseName(reused),'Succeeded')
  assert.equal(data(reused),2)
  assert.equal(readFileSync(h.file('again'),'utf8'),'yes')
  const latest=data(await h.unpark('target'))
  assert.equal(latest.name,'target');assert.equal(latest.state,'completed');assert.equal(latest.value,2)
})
test('WHAT[repository-programming-033] abandoned terminal receipt is retained and never resumes canceled code', async t => {
  const h=fixture(t,'033-abandon')
  await h.run('target',"await this.park({ready:true});this.write('unexpected','yes');return 1;")
  const abandoned=data(await h.abandon('target'))
  assert.equal(abandoned.state,'abandoned')
  assert.deepEqual(data(await h.unpark('target')),abandoned)
  assert.equal(existsSync(h.file('unexpected')),false)
})
