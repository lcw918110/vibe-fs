import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import {
  ProcessEventLogModule_acquireStoreLock as acquireStoreLock,
  StoreFileGate__Release as releaseStoreLock,
} from '../../../dist/Persistence/EventStore/ProcessEventLog.js'

test('file-lock extraction: expired acquisition budget preserves the store timeout diagnosis', async () => {
  const root = mkdtempSync(join(tmpdir(), 'store-lock-timeout-'))
  const commonDir = join(root, '.Herdchestra')
  const originalNow = Date.now
  let clockReads = 0
  let acquired
  try {
    // 起点为 0，进入首次获取前已超过预算，无须实际等候三十秒。
    Date.now = () => clockReads++ === 0 ? 0 : 30001
    await assert.rejects(async () => {
      acquired = await acquireStoreLock(commonDir)
    }, error => {
      assert.equal(error.constructor.name, 'ProcessEventLogModule_StoreLockAcquireTimeout')
      assert.equal(error.attempts, 0)
      assert.equal(error.waitedMs, 30001)
      return true
    })
    assert.ok(clockReads >= 2)
    assert.equal(existsSync(join(commonDir, 'wanxiangshu.lock')), false)
  } finally {
    Date.now = originalNow
    try {
      if (acquired !== undefined) await releaseStoreLock(acquired)
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  }
})
