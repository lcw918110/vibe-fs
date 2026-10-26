import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync } from 'node:fs'
import { join } from 'node:path'
import { sandbox, runProgram, caseName, render, nodeCommand } from './support/executor.mjs'

const { contextDecode, contextView } = await import('../../../dist/OpenCode/Codec/ToolHostSurface.js')

// Host-native `run` tool is retired; `this.call` supersedes it (repository-programming [050]-[052]).

test('WHAT[process-execution-011] RUN_host_context_codec_exposes_plain_snapshot', () => {
  const decoded = contextDecode({ sessionID: 'ses-exec', agent: 'devops' })
  assert.deepEqual(contextView(decoded), {
    sessionId: 'ses-exec',
    agent: 'devops',
    toolCallId: null,
    providerRunId: null,
    promptText: null,
  })
})

test('WHAT[process-execution-011] command descriptors and retired Callable classes cannot spawn commands',async t=>{
  const {fixture,failureCode}=await import('../../repository-programming/tests/support/named-program.fixture.mjs')
  const h=fixture(t,'proc011','DevOps')
  for(const [name,body]of [
    ['descriptor','return await this.call({command:"touch spawned"});'],
    ['legacy','return await this.call(class {next(){return "touch spawned";}});'],
  ]) {
    const result=await h.run(name,body)
    assert.equal(caseName(result),'Failed')
    assert.equal(failureCode(result),'CALL_COMMAND_UNSUPPORTED')
    assert.equal(existsSync(h.file('spawned')),false)
  }
  const shape=await h.run('no-callable','return {retired:typeof this.Callable};')
  assert.match(render(shape),/retired = "undefined"/)
})

test('WHAT[process-execution-011] deadline parks without terminating the active command; explicit abandon confirms cleanup',{timeout:12000},async t=>{
  const {fixture,data,barrier,stopped,until}=await import('../../repository-programming/tests/support/command-program.fixture.mjs')
  const h=fixture(t,'proc011-expiry','DevOps'),cmd=barrier(h,'held')
  const output=await h.run('held','return await this.call('+JSON.stringify(cmd)+');',{intent:'w'.repeat(20)})
  assert.equal(caseName(output),'Parked')
  await until(()=>existsSync(h.file('held.pid')))
  const closed=data(await h.abandon('held'))
  assert.equal(closed.state,'abandoned')
  await stopped(h,'held')
})
