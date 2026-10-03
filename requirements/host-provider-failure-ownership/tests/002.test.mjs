import assert from 'node:assert/strict'
import test from 'node:test'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

integrationTest('WHAT[host-provider-failure-ownership-002] installed Host makes one upstream request for one failed physical run with production retry configuration', { todo: 'GAP-144: installed 1.18.29 retries despite the zero configuration' }, () => {
  const result = spawnSync(process.execPath, [fileURLToPath(new URL('./support/run-host-retry-canary.mjs', import.meta.url))], { encoding: 'utf8' })
  assert.equal(result.status, 0, result.stdout + result.stderr)
  const observed = JSON.parse(result.stdout)
  assert.equal(observed.providerRequests, 1)
  assert.equal(observed.providerRuns, 1)
  assert.equal(observed.configuredRetries, 0)
  assert.equal(observed.hostErrorObserved, true)
  assert.equal(observed.terminalObserved, true)
})

test.todo('WHAT[host-provider-failure-ownership-002] full plugin recovery opens only licensed fresh runs after Host failure without duplicate upstream requests (GAP-143)')
