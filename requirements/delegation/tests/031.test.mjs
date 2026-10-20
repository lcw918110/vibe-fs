import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as sync from '../../../dist/Execution/Delegation/SyncDelegate/Surface.js'

const directories = new Set()
const live = async (owner) => {
  const directory = await mkdtemp(join(tmpdir(), 'wxs-deleg031-'))
  directories.add(directory)
  return sync.create(directory, [{ sessionId: owner, agent: 'manager' }])
}
test.afterEach(async () => {
  for (const directory of directories) await rm(directory, { recursive: true, force: true })
  directories.clear()
})

const waitForPromptCount = async (h, owner, role, count) => {
  await sync.awaitPromptCount(h, owner, role, count)
  assert.equal(sync.acceptPrompt(h, owner, role, count - 1), true)
}

const settle = async (h, owner, role, answer, run = 'run-1') => sync.settle(h, owner, role, answer, run)

const remainsPending = async (promise) =>
  Promise.race([
    promise.then((value) => ({ kind: 'resolved', value })),
    new Promise((resolve) => setImmediate(() => resolve({ kind: 'pending' }))),
  ])

test('WHAT[delegation-031] successive completed work reuses the child and delivers each bounded view', async () => {
  const owner = 'owner-deleg031-duplicate'
  const h = await live(owner)
  try {
    const first = sync.invoke(h, owner, 'Engineer', 'FIRST')
    await waitForPromptCount(h, owner, 'Engineer', 1)
    assert.equal(await settle(h, owner, 'Engineer', 'FIRST-ANSWER', 'run-first'), true)
    assert.equal((await first).ok, true)

    const second = sync.invoke(h, owner, 'Engineer', 'SECOND')
    await waitForPromptCount(h, owner, 'Engineer', 2)
    assert.equal(await settle(h, owner, 'Engineer', 'SECOND-ANSWER', 'run-second'), true)
    const secondResult = await second
    assert.equal(secondResult.ok, true)
    assert.match(secondResult.value, /SECOND-ANSWER/)
    assert.doesNotMatch(secondResult.value, /FIRST-ANSWER/)
    assert.equal(sync.childCount(h), 1)
  } finally {
    sync.dispose(h)
  }
})

test('WHAT[delegation-031] DELEG_031_stale_authority_completion_cannot_claim_a_new_call', async () => {
  const owner = 'owner-deleg031-stale'
  const h = await live(owner)
  try {
    const first = sync.invoke(h, owner, 'Engineer', 'FIRST')
    await waitForPromptCount(h, owner, 'Engineer', 1)
    assert.equal(await settle(h, owner, 'Engineer', 'FIRST-ANSWER', 'run-first'), true)
    assert.equal((await first).ok, true)

    const second = sync.invoke(h, owner, 'Engineer', 'SECOND')
    await waitForPromptCount(h, owner, 'Engineer', 2)

    // A stale completion under a superseded authority root is rejected: the
    // pending call stays pending and no checkpoint advances for it.
    assert.equal(
      await sync.settleWithAuthorityRoot(h, owner, 'Engineer', 'STALE-ANSWER', 'run-stale', 'old-authority-root'),
      false,
    )
    assert.deepEqual(await remainsPending(second), { kind: 'pending' })

    assert.equal(await settle(h, owner, 'Engineer', 'SECOND-ANSWER', 'run-second'), true)
    const secondResult = await second
    assert.equal(secondResult.ok, true)
    assert.match(secondResult.value, /SECOND-ANSWER/)
    assert.doesNotMatch(secondResult.value, /STALE-ANSWER/)
  } finally {
    sync.dispose(h)
  }
})

test('WHAT[delegation-031] DELEG_031_parent_supersede_leaves_no_orphan_completion_claim', async () => {
  const owner = 'owner-deleg031-supersede'
  const h = await live(owner)
  try {
    const pending = sync.invoke(h, owner, 'Engineer', 'SUPERSEDED-CHARGE')
    await waitForPromptCount(h, owner, 'Engineer', 1)

    // Parent supersedes the call before its completion arrives.
    assert.equal(sync.abandonPendingCall(h, owner, 'Engineer'), true)

    // The late completion afterwards cannot claim the abandoned call: the
    // call is gone, so settle finds no live call and reports false; the
    // invocation itself already failed with the supersede reason, not with
    // a stale success.
    assert.equal(await settle(h, owner, 'Engineer', 'LATE-ANSWER', 'run-late'), false)
    const result = await pending
    assert.equal(result.ok, false)
  } finally {
    sync.dispose(h)
  }
})

test('WHAT[delegation-031] DELEG_031_completed_and_delete_in_both_orders_settle_exactly_once', async () => {
  for (const order of ['complete-then-delete', 'delete-then-complete']) {
    const owner = `owner-deleg031-order-${order}`
    const h = await live(owner)
    try {
      const pending = sync.invoke(h, owner, 'Engineer', `CHARGE-${order}`)
      await waitForPromptCount(h, owner, 'Engineer', 1)

      if (order === 'complete-then-delete') {
        assert.equal(await settle(h, owner, 'Engineer', `ANSWER-${order}`, 'run-order'), true)
        assert.equal((await pending).ok, true)
        // Graceful scope close stages the delegate; the staged binding is
        // retired exactly once.
        assert.equal(sync.stageDeletedDelegate(h, owner), true)
        // After staging, the live binding is retired: scope-close resolves
        // the staged (deleted) delegate, and the live child lookup is gone.
        assert.equal(await sync.child(h, owner, 'Engineer'), null)
        assert.match(String(sync.scopeCloseChild(h, owner, 'Engineer')), /child-1/)
      } else {
        // Delete first: the pending call is cancelled before its completion.
        sync.cancelSession(h, owner)
        const result = await pending
        assert.equal(result.ok, false)
      }
      assert.equal(sync.childCount(h), 1)
    } finally {
      sync.dispose(h)
    }
  }
})
