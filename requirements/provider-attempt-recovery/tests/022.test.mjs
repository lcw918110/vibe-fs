import assert from 'node:assert/strict'
import test from 'node:test'
import { ProviderAttemptStopFence } from 'wanxiangli/failure'

const freshFence = () => new ProviderAttemptStopFence()

test('WHAT[provider-attempt-recovery-022] await without the exact observation stays pending', async () => {
  const f = freshFence()
  const pending = f.WaitForStop('ses-a', 'msg-run-1')

  assert.deepEqual(f.Snapshot(), { stopped: 0, waiting: 1, denied: 0 })

  f.SignalStop('ses-a', 'msg-run-1')

  assert.equal(await pending, 'Stopped')
  assert.deepEqual(f.Snapshot(), { stopped: 1, waiting: 0, denied: 0 })
})

test('WHAT[provider-attempt-recovery-022] another run and another session never satisfy the exact attempt', async () => {
  const f = freshFence()
  const pending = f.WaitForStop('ses-a', 'msg-run-1')

  f.SignalStop('ses-a', 'msg-run-2')
  f.SignalStop('ses-b', 'msg-run-1')

  assert.deepEqual(f.Snapshot(), { stopped: 2, waiting: 1, denied: 0 })

  f.SignalStop('ses-a', 'msg-run-1')

  assert.equal(await pending, 'Stopped')
})

test('WHAT[provider-attempt-recovery-022] observation is idempotent and a late waiter resolves immediately', async () => {
  const f = freshFence()

  f.SignalStop('ses-a', 'msg-run-1')
  f.SignalStop('ses-a', 'msg-run-1')

  assert.deepEqual(f.Snapshot(), { stopped: 1, waiting: 0, denied: 0 })
  assert.equal(await f.WaitForStop('ses-a', 'msg-run-1'), 'Stopped')
})

test('WHAT[provider-attempt-recovery-022] revocation denies the waiting attempt only', async () => {
  const f = freshFence()
  const pending = f.WaitForStop('ses-a', 'msg-run-1')

  f.Revoke('ses-a')

  assert.equal(await pending, 'Denied')
  assert.equal(await f.WaitForStop('ses-a', 'msg-run-1'), 'Denied')
  assert.deepEqual(f.Snapshot(), { stopped: 0, waiting: 0, denied: 1 })

  f.SignalStop('ses-a', 'msg-run-1')
  assert.equal(await f.WaitForStop('ses-a', 'msg-run-1'), 'Denied', 'late terminal cannot revive revoked permission')

  // A later attempt of the same session is a different exact key and stays eligible.
  const later = f.WaitForStop('ses-a', 'msg-run-2')
  assert.deepEqual(f.Snapshot(), { stopped: 1, waiting: 1, denied: 1 })

  f.SignalStop('ses-a', 'msg-run-2')
  assert.equal(await later, 'Stopped')
})

test.todo('WHAT[provider-attempt-recovery-022] actual Host terminal observation alone releases recovery while coarse error and idle cannot; fresh process inherits no stop permission (GAP-139)')
