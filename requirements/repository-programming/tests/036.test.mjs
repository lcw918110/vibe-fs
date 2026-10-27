import test from 'node:test'
import { fixture, assert, data, caseName, failureCode, parkedName, parkedPayload, parkedSource, render, created, readFileSync, writeFileSync, existsSync, pendingNames } from './support/named-program.fixture.mjs'

test('WHAT[repository-programming-036] names are per-session and there is no inherited eight-site ceiling', async t => {
  const h=fixture(t,'036')
  for(let i=0;i<10;i++){
    const result=await h.run('target-'+i,'await this.park({index:'+i+'}); return '+i+';')
    assert.equal(caseName(result),'Parked');assert.equal(parkedName(result),'target-'+i)
  }
  assert.equal(Array.from(pendingNames(h.session)).length,10)
  const other=fixture(t,'036-other')
  assert.equal(caseName(await other.run('target-0',"await this.park({other:true});return 1;")),'Parked')
  await h.abandon('target-0')
  assert.equal(Array.from(pendingNames(h.session)).length,9)
  assert.deepEqual(Array.from(pendingNames(other.session)),['target-0'])
  assert.equal(failureCode(await h.run('target-1','return 2;')),'NAME_TAKEN')
  const reused=await h.run('target-0','return 2;')
  assert.equal(caseName(reused),'Succeeded');assert.equal(data(reused),2)
  assert.equal(Array.from(pendingNames(h.session)).length,9)
})
