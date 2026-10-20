import assert from 'node:assert/strict'
import test from 'node:test'
import * as sync from '../../../dist/Execution/Delegation/SyncDelegate/Surface.js'
import { withSyncRuntime } from './support/sync-runtime.mjs'

test('WHAT[delegation-023] transient failure follows the injected retry verdict before delivering success or exhaustion', async () => {
  const owner = 'owner-retry'
  await withSyncRuntime(owner, async runtime => {
    const pending = sync.invoke(runtime, owner, 'Engineer', 'retry charge')
    await sync.awaitPromptCount(runtime, owner, 'Engineer', 1)
    assert.equal(sync.acceptPrompt(runtime, owner, 'Engineer', 0), true)
    sync.scriptRetry(runtime, ['dispatched'])
    assert.equal(await sync.observeTurn(runtime, owner, 'Engineer', 'TurnFailed', '', 'run-retry-1'), true)
    assert.equal(sync.retryCalls(runtime), 1)
    assert.equal(sync.dispatchRetryAttempt(runtime, owner, 'Engineer'), true)
    assert.equal(await sync.observeTurn(runtime, owner, 'Engineer', 'TurnCompleted', 'retry delivered view', 'run-retry-2'), true)
    const result = await pending
    assert.equal(result.ok, true)
    assert.match(result.value, /retry delivered view/)
  })
  const exhausted = 'owner-exhausted'
  await withSyncRuntime(exhausted, async runtime => {
    sync.scriptRetry(runtime, ['terminal:provider retry budget exhausted'])
    const pending = sync.invoke(runtime, exhausted, 'Engineer', 'exhausted charge')
    await sync.awaitPromptCount(runtime, exhausted, 'Engineer', 1)
    assert.equal(sync.acceptPrompt(runtime, exhausted, 'Engineer', 0), true)
    assert.equal(await sync.observeTurn(runtime, exhausted, 'Engineer', 'TurnFailed', '', 'run-exhausted-1'), true)
    assert.deepEqual(await pending, { ok: false, error: 'SyncDelegate run failed: provider retry budget exhausted' })
    assert.equal(sync.retryCalls(runtime), 1)
  })
})

test.todo('WHAT[delegation-023] actual shared recovery engine exhausts all applicable paths before the parent receives final failure (GAP-153)')
