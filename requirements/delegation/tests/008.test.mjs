import assert from 'node:assert/strict'
import test from 'node:test'
import * as sync from '../../../dist/Execution/Delegation/SyncDelegate/Surface.js'
import { withSyncRuntime } from './support/sync-runtime.mjs'

test('WHAT[delegation-008] actual batch sends once in supplied Host order despite reverse invocation arrival', async () => {
  const owner = 'owner-batch-order'
  await withSyncRuntime(owner, async runtime => {
    const order = ['call-first', 'call-second']
    const second = sync.invokeBatch(runtime, owner, 'Engineer', 'SECOND-CHARGE', 'batch-run', order[1], order)
    assert.equal(sync.promptCount(runtime, owner, 'Engineer'), 0)
    const first = sync.invokeBatch(runtime, owner, 'Engineer', 'FIRST-CHARGE', 'batch-run', order[0], order)
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 1)
    assert.equal(sync.acceptPrompt(runtime, owner, 'Engineer', 0), true)
    const prompt = sync.prompt(runtime, owner, 'Engineer', 0)
    assert.ok(prompt.indexOf('FIRST-CHARGE') >= 0)
    assert.ok(prompt.indexOf('FIRST-CHARGE') < prompt.indexOf('SECOND-CHARGE'))
    assert.equal(sync.promptCount(runtime, owner, 'Engineer'), 1)
    assert.equal(await sync.settle(runtime, owner, 'Engineer', 'batch result', 'batch-run'), true)
    assert.equal((await first).kind, 'View')
    assert.deepEqual(await second, { kind: 'MergedInto', canonical: 'call-first' })
  })
})

test.todo('WHAT[delegation-008] the real Host tool-call collection fixes complete batch membership before invocation without using arrival timing (GAP-153)')
