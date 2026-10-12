import assert from 'node:assert/strict'
import { execFileSync, spawn } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { setImmediate as nextTurn } from 'node:timers/promises'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { superviseNodeTest } from '../e2e/support/supervise-node-test.mjs'
import { PROCESS_TREE_TIMEOUT_MS, SIGKILL_GRACE_MS, UNIT_VERDICT_SILENCE_MS } from '../e2e/support/time-budget.js'

const nativePath = fileURLToPath(import.meta.url)

export function processRows(timeout = PROCESS_TREE_TIMEOUT_MS) {
  const output = execFileSync('ps', ['-eo', 'pid=,ppid=,pgid=,stat=,lstart='], {
    encoding: 'utf8', timeout,
    env: { ...process.env, LC_ALL: 'C' },
  })
  return output.trim().split('\n').map(line => {
    const fields = /^\s*(\d+)\s+(\d+)\s+(\d+)\s+(\S+)\s+(.+?)\s*$/.exec(line)
    if (!fields) throw new Error(`Invalid actual process record: ${line}`)
    return { pid: Number(fields[1]), parentPid: Number(fields[2]), pgid: Number(fields[3]), state: fields[4], started: fields[5] }
  }).filter(row => !/^[ZX]/.test(row.state))
}

const sameCreation = (actual, captured) => actual?.pid === captured.pid &&
  actual.pgid === captured.pgid && actual.started === captured.started

const atomicJson = (file, value) => {
  fs.writeFileSync(`${file}.tmp`, JSON.stringify(value))
  fs.renameSync(`${file}.tmp`, file)
}

function ownChild(child) {
  let exited = false
  let closed = false
  let error
  const terminal = new Promise(resolve => {
    child.once('error', failure => { error = failure })
    child.once('exit', () => { exited = true })
    child.once('close', (code, signal) => {
      closed = true
      resolve({ code, signal, error })
    })
  })
  return { child, terminal, exited: () => exited, closed: () => closed, error: () => error }
}

function resourceReady(owner) {
  return new Promise((resolve, reject) => {
    owner.child.once('message', message => {
      try {
        assert.equal(message?.type, 'resource-ready')
        assert.equal(message.identity.pid, owner.child.pid)
        resolve(message)
      } catch (error) { reject(error) }
    })
    owner.child.once('error', reject)
    owner.child.once('exit', () => reject(new Error('Actual resource exited before ready')))
  })
}

async function stopCreatedChild(owner) {
  if (!owner.exited() && !owner.closed()) owner.child.kill('SIGKILL')
  const error = owner.error()
  if (error) throw error
  const terminal = await owner.terminal
  if (terminal.error) throw terminal.error
}

export async function reclaimRecordedProcess(identity) {
  let current = processRows().find(row => row.pid === identity.pid)
  if (!current) return
  if (!sameCreation(current, identity)) throw new Error('Captured PID no longer names its observed creation')
  try { process.kill(identity.pid, 'SIGKILL') }
  catch (error) { if (error.code !== 'ESRCH') throw error }
  const deadline = Date.now() + SIGKILL_GRACE_MS
  while (true) {
    const remaining = deadline - Date.now()
    if (remaining <= 0) throw new Error('Captured actual resource did not drain')
    current = processRows(Math.min(PROCESS_TREE_TIMEOUT_MS, remaining)).find(row => row.pid === identity.pid)
    if (!current) return
    if (!sameCreation(current, identity)) throw new Error('Captured PID changed during cleanup')
    await nextTurn()
  }
}

export async function leaveSameGroupResource(directory) {
  const env = { ...process.env }
  delete env.NODE_TEST_CONTEXT
  const owner = ownChild(spawn(process.execPath, [nativePath, 'resource', directory], {
    env, stdio: ['ignore', 'ignore', 'ignore', 'ipc'],
  }))
  let released = false
  let failure
  try {
    const ready = await resourceReady(owner)
    const rows = processRows()
    const worker = rows.find(row => row.pid === process.pid)
    const inner = rows.find(row => row.pid === process.ppid)
    const resource = rows.find(row => row.pid === ready.identity.pid)
    assert.ok(worker && inner && resource, 'All three actual processes are present before release')
    assert.equal(resource.parentPid, worker.pid)
    assert.equal(worker.parentPid, inner.pid)
    assert.equal(worker.pgid, inner.pid)
    assert.equal(resource.pgid, inner.pid)
    assert.ok(sameCreation(resource, ready.identity))
    assert.equal(ready.directory, fs.realpathSync(directory))
    atomicJson(path.join(directory, 'owned.json'), { inner, worker, resource, home: process.env.HOME })
    const disconnected = new Promise(resolve => owner.child.once('disconnect', resolve))
    await new Promise((resolve, reject) => owner.child.send({ type: 'detach-parent' }, error => error ? reject(error) : resolve()))
    await disconnected
    owner.child.unref()
    fs.writeFileSync(path.join(directory, 'leaf-returned'), 'actual leaf completed\n')
    released = true
  } catch (error) { failure = { error } }
  finally {
    if (!released) {
      try { await stopCreatedChild(owner) }
      catch (cleanupError) {
        failure = { error: failure
          ? new AggregateError([failure.error, cleanupError], 'Leaf setup and its creation cleanup failed', { cause: failure.error })
          : cleanupError }
      }
    }
  }
  if (failure) throw failure.error
}

async function holdResource(directory) {
  const watcher = fs.watch(directory, () => {})
  watcher.on('error', error => { throw error })
  process.once('exit', () => watcher.close())
  process.once('message', message => {
    assert.deepEqual(message, { type: 'detach-parent' })
    process.disconnect()
  })
  const identity = processRows().find(row => row.pid === process.pid)
  assert.ok(identity)
  await new Promise((resolve, reject) => process.send({ type: 'resource-ready', identity,
    directory: fs.realpathSync(directory) }, error => error ? reject(error) : resolve()))
}

async function superviseResidual(directory, fixture) {
  const env = { ...process.env }
  delete env.NODE_TEST_CONTEXT
  delete env.WXS_VERIFICATION_WORKER_DIAGNOSTICS
  const foreignOwner = ownChild(spawn(process.execPath, [nativePath, 'resource', directory], {
    env, detached: true, stdio: ['ignore', 'ignore', 'ignore', 'ipc'],
  }))
  let failure
  const cleanupErrors = []
  try {
    const ready = await resourceReady(foreignOwner)
    const foreign = processRows().find(row => row.pid === foreignOwner.child.pid)
    assert.ok(sameCreation(foreign, ready.identity))
    assert.equal(foreign.parentPid, process.pid)
    assert.equal(foreign.pgid, foreign.pid)
    atomicJson(path.join(directory, 'foreign.json'), foreign)
    try {
      await superviseNodeTest({ files: [fixture], label: 'normal-exit-residual',
        silenceMs: Number(process.env.UNIT_VERDICT_SILENCE_MS || UNIT_VERDICT_SILENCE_MS), throwOnFailure: true })
      throw new Error('A normally drained ledger concealed an actual surviving group member')
    } catch (error) {
      failure = { error }
      const owned = JSON.parse(fs.readFileSync(path.join(directory, 'owned.json'), 'utf8'))
      const rows = processRows()
      atomicJson(path.join(directory, 'caught.json'), {
        error: { name: error.name, message: error.message, exitCode: error.exitCode },
        owned, foreign,
        ownedMembers: rows.filter(row => row.pgid === owned.inner.pgid),
        foreignCurrent: rows.find(row => row.pid === foreign.pid) ?? null,
        homeExists: fs.existsSync(owned.home),
      })
    }
  } catch (error) {
    if (failure) cleanupErrors.push(error)
    else failure = { error }
  } finally {
    try { await stopCreatedChild(foreignOwner) }
    catch (error) { cleanupErrors.push(error) }
    try {
      atomicJson(path.join(directory, 'foreign-cleanup.json'), {
        pid: foreignOwner.child.pid,
        members: processRows().filter(row => row.pgid === foreignOwner.child.pid),
      })
    } catch (error) { cleanupErrors.push(error) }
  }
  if (cleanupErrors.length > 0) throw new AggregateError([
    ...(failure ? [failure.error] : []), ...cleanupErrors,
  ], 'Original supervisor rejection and foreign cleanup evidence', { cause: failure?.error })
  if (failure) throw failure.error
}

if (process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url) {
  const [mode, directory, fixture] = process.argv.slice(2)
  if (mode === 'resource') await holdResource(directory)
  else if (mode === 'supervise') await superviseResidual(directory, fixture)
  else throw new Error(`Unknown native residual role: ${mode}`)
}
