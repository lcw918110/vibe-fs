import test from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as recovery from '../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js'

test('reload reads each active session once and skips terminal sessions', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-recovery-transcript-'))
  const host = await recovery.bootRecoveryHost(directory, 'absent')
  try {
    await recovery.seedAccepted(host, 'active', 'one')
    await recovery.seedAccepted(host, 'active', 'two')
    await recovery.seedProviderStarted(host, 'finished', 'three', 'provider')
    await recovery.finalizeCompleted(host, 'finished', 'three', 'provider')
    assert.deepEqual(await recovery.signalReload(host), ['active'])
    assert.equal(recovery.executionStatus(host, 'finished', 'three').phase, 'Terminal')
  } finally {
    recovery.disposeRecoveryHost(host)
    rmSync(directory, { recursive: true, force: true })
  }
})
