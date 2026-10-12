import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import { reclaimRecordedProcess } from './normal-exit-residual-support.mjs'

export function registerNormalExitResidualTest() {
  test('WHAT[verification-system-006] a complete passing native ledger rejects and reclaims its actual same-group residue while preserving a foreign group', {
    skip: !['linux', 'darwin'].includes(process.platform),
  }, async t => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'normal-exit-residual-'))
    const support = new URL('./normal-exit-residual-support.mjs', import.meta.url)
    const fixture = path.join(directory, 'normal-exit-residual.fixture.mjs')
    let launcher
    let launcherClosed = false
    let launcherTerminal
    let failure
    let receipt
    const cleanupErrors = []
    let output = ''
    try {
      fs.writeFileSync(fixture, `import test from 'node:test'
import { leaveSameGroupResource } from ${JSON.stringify(support.href)}
test('actual same-group resource outlives its normally passing leaf', () => leaveSameGroupResource(${JSON.stringify(directory)}))
`)
      const env = { ...process.env, TMPDIR: directory }
      delete env.NODE_TEST_CONTEXT
      delete env.WXS_VERIFICATION_WORKER_DIAGNOSTICS
      launcher = spawn(process.execPath, [fileURLToPath(support), 'supervise', directory, fixture], {
        env, stdio: ['ignore', 'pipe', 'pipe'],
      })
      launcherTerminal = new Promise(resolve => {
        let spawnError
        launcher.once('error', error => { spawnError = error })
        launcher.once('close', (code, signal) => {
          launcherClosed = true
          resolve({ code, signal, spawnError })
        })
      })
      launcher.stdout.setEncoding('utf8').on('data', chunk => { output += chunk })
      launcher.stderr.setEncoding('utf8').on('data', chunk => { output += chunk })
      const terminal = await launcherTerminal
      assert.equal(terminal.spawnError, undefined, output)
      assert.equal(terminal.code, 1, output)
      assert.equal(terminal.signal, null, output)
      const caught = JSON.parse(fs.readFileSync(path.join(directory, 'caught.json'), 'utf8'))
      assert.match(caught.error.message, /supervised suite failed \(exit 1\)/)
      assert.equal(caught.error.exitCode, 1)
      assert.equal(fs.readFileSync(path.join(directory, 'leaf-returned'), 'utf8'), 'actual leaf completed\n')
      assert.match(output, /\[test-summary\] 1 file\(s\), 1 passed, 0 failed/)
      assert.match(output, /runner: 1 passed, 0 failed; 1\/1 planned file\(s\) completed/)
      assert.match(output, new RegExp(`residual process group ${caught.owned.inner.pid} after inner exit; surviving pids: ${caught.owned.resource.pid}(?:\\n|\\r)`))
      assert.match(output, new RegExp(`process group ${caught.owned.inner.pid} reclaimed; the run still fails`))
      assert.match(output, new RegExp(`post-exit group verification/reclamation: pid=${caught.owned.inner.pid};.*accepted=false`))
      assert.doesNotMatch(output, /WATCHDOG|physical backstop|inner runner exited|inner runner died|exit was not observed|no authoritative summary|partial evidence|pending proof/)
      const costs = output.split('\n').filter(line => line.startsWith('runner: worker cost '))
        .map(line => JSON.parse(line.slice('runner: worker cost '.length)))
      assert.equal(costs.length, 1, output)
      assert.equal(costs[0].status, 'complete', output)
      assert.equal(costs[0].exit.exitCode, 0)
      assert.equal(costs[0].pid, caught.owned.worker.pid)
      assert.equal(caught.owned.worker.parentPid, caught.owned.inner.pid)
      assert.equal(caught.owned.resource.parentPid, caught.owned.worker.pid)
      assert.equal(caught.owned.resource.pgid, caught.owned.inner.pid)
      assert.deepEqual(caught.ownedMembers, [])
      assert.equal(caught.homeExists, false)
      assert.ok(caught.foreignCurrent)
      assert.equal(caught.foreignCurrent.pid, caught.foreign.pid)
      assert.equal(caught.foreignCurrent.pgid, caught.foreign.pgid)
      assert.equal(caught.foreignCurrent.started, caught.foreign.started)
      const foreignCleanup = JSON.parse(fs.readFileSync(path.join(directory, 'foreign-cleanup.json'), 'utf8'))
      assert.equal(foreignCleanup.pid, caught.foreign.pid)
      assert.deepEqual(foreignCleanup.members, [])
      receipt = {
        caught,
        foreignCleanup,
        workerCost: costs[0],
        supervisorLines: output.split('\n').filter(line =>
          line.includes('[test-summary]') ||
          line.includes('runner: 1 passed, 0 failed; 1/1 planned file(s) completed') ||
          line.includes('residual process group ') ||
          line.includes('reclaimed; the run still fails') ||
          line.includes('post-exit group verification/reclamation:')),
      }
    } catch (error) { failure = { error } }
    finally {
      if (launcher && !launcherClosed) {
        try {
          launcher.kill('SIGKILL')
          const terminal = await launcherTerminal
          if (terminal.spawnError) throw terminal.spawnError
        } catch (error) { cleanupErrors.push(error) }
      }
      for (const [file, key] of [['owned.json', 'resource'], ['foreign.json', null]]) {
        try {
          const receipt = path.join(directory, file)
          if (!fs.existsSync(receipt)) continue
          const captured = JSON.parse(fs.readFileSync(receipt, 'utf8'))
          await reclaimRecordedProcess(key ? captured[key] : captured)
        } catch (error) { cleanupErrors.push(error) }
      }
      if (cleanupErrors.length === 0) {
        try { fs.rmSync(directory, { recursive: true, force: true }) }
        catch (error) { cleanupErrors.push(error) }
      }
    }
    if (cleanupErrors.length > 0) throw new AggregateError([
      ...(failure ? [failure.error] : []), ...cleanupErrors,
    ], `Native residual proof or owned cleanup failed; evidence=${directory}`, { cause: failure?.error })
    if (failure) throw failure.error
    t.diagnostic(JSON.stringify(receipt))
  })
}
