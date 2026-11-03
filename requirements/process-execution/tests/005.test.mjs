import test from 'node:test'

{
  const {fixture,assert,caseName,existsSync} = await import('../../repository-programming/tests/support/command-program.fixture.mjs')
  test('WHAT[process-execution-005] call refuses non-string commands without a physical spawn', async t=>{
    const h=fixture(t,'proc005','DevOps')
    for(const [index,value] of ['0','-5','NaN','Infinity','null','{}'].entries()) {
      const result=await h.run('invalid-'+index,'return await this.call('+value+');')
      assert.equal(caseName(result),'Failed')
    }
    assert.equal(existsSync(h.file('spawned')),false)
  })
}
