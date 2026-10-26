import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { sandbox, runProgram, caseName, parseData } from './support/executor.mjs'

// Host-native `run` tool and its 10-second `deadline_seconds` policy (along with
// the four PTY tools `open-terminal`, `send-terminal`, `read-terminal`, `signal-terminal`)
// are retired and superseded by `this.call` with a required top-level request deadline
// (repository-programming [050]-[052]).

const root = join(dirname(fileURLToPath(import.meta.url)), '../../..')

test('WHAT[process-execution-017] retired host run and PTY tool resources and ExecutorTool are absent', () => {
  assert.equal(existsSync(join(root, 'src/Wanxiangshu/OpenCode/Tools/ExecutorTool.fs')), false)
  assert.equal(existsSync(join(root, 'src/Wanxiangshu/OpenCode/Tools/ExecutorToolSurface.fs')), false)
  assert.equal(existsSync(join(root, 'resources/provider/tool/run')), false)
})

test('WHAT[process-execution-017] a long top-level budget has no hidden ten-second cap',async t=>{
  const {fixture,parkedPayload}=await import('../../repository-programming/tests/support/named-program.fixture.mjs')
  const h=fixture(t,'proc017','DevOps')
  const output=await h.run('large-window','await this.park("inspect");return 1;',{intent:'x'.repeat(1200)})
  assert.equal(caseName(output),'Parked')
  assert.ok(parkedPayload(output).remainingWaitMs>50000,'the admitted 60-second window was secretly capped')
})
