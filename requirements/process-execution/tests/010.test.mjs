import test from 'node:test'

{
  const {default:assert}=await import('node:assert/strict')
  const {sandbox,runProgram,caseName,parseData}=await import('./support/executor.mjs')
  test('WHAT[process-execution-010] a completed call reports separate raw streams and actual exit code',async()=>{
    const {dir,cleanup}=sandbox('wxs-proc010-')
    try {
      const outcome=await runProgram(dir,"return await this.call(\"printf 'hello-stdout'; printf 'hello-stderr' >&2\");")
      assert.equal(caseName(outcome),'Succeeded')
      const result=parseData(outcome)
      assert.equal(result.exitCode,0);assert.equal(result.stdout,'hello-stdout');assert.equal(result.stderr,'hello-stderr')
    } finally { cleanup() }
  })
  test('WHAT[process-execution-010] nonzero shell exit remains command data',async()=>{
    const {dir,cleanup}=sandbox('wxs-proc010-nonzero-')
    try {
      const result=await runProgram(dir,'return await this.call("exit 3");')
      assert.equal(caseName(result),'Succeeded');assert.equal(parseData(result).exitCode,3)
    } finally { cleanup() }
  })
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const { renderPtyCompletion } = await import('../../../dist/Process/Surface.js')

test('WHAT[process-execution-010] EXEC_004_pty_completion_is_natural_language_plus_exit_code', () => {
  const wire = renderPtyCompletion('shell', 'pty-9', 'ended', 0)
  assert.match(wire, /# shell has ended\./)
  assert.match(wire, /exit_code = 0/)
  assert.ok(!wire.includes('pty_id'))
})
}
