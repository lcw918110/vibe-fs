import test from 'node:test'
import assert from 'node:assert/strict'
import { rig, flush } from './support/observed-watchdog.mjs'

test('WHAT[latency-degeneration-014] novice protection: an infinite threshold arms no timer but still samples', async () => {
  const r = rig(Number.POSITIVE_INFINITY)
  r.watchdog.Begin('engineer', 'run-1')
  assert.equal(r.timers.length, 0, 'no timer is scheduled with an infinite threshold')
  r.at(7)
  r.watchdog.Complete('engineer', 'run-1')
  assert.deepEqual(r.events, ['completed:7'])
  r.watchdog.Begin('engineer', 'run-2')
  r.watchdog.OnIdle('engineer')
  await flush()
  assert.equal(r.events.some(e => e.startsWith('returned:')), false, 'idle of a never-armed request returns nothing')
  r.watchdog.Dispose()
})
