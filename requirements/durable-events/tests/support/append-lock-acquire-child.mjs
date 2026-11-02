import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire, syncBuiltinESMExports } from 'node:module'

// 沿主体实际消费的公开入口解析，注入与锁库使用同一份 CommonJS 依赖。
const requireFromLibrary = createRequire(import.meta.resolve('wanxiangli/file-lock'))
const lockfilePath = requireFromLibrary.resolve('proper-lockfile')
const lockfile = requireFromLibrary('proper-lockfile')

const [mode, commonDir, writerId, scenario, requestJson] = process.argv.slice(2)
const request = JSON.parse(requestJson)
assert.ok(['measure', 'cold'].includes(mode))
assert.ok(['mkdir-eacces', 'mkdir-eio', 'busy-success', 'busy-eacces'].includes(scenario))
const target = path.join(commonDir, 'wanxiangshu')
const lockPath = `${target}.lock`
const sourceFile = path.join(target, 'events', `${request.sourceWriterId}-0000.ndjson`)
const writerFile = path.join(target, 'events', `${writerId}-0000.ndjson`)
const views = (store, handle) => [request.seed, request.incoming].map(event => ({
  event: store.read(handle, event.id),
  head: store.head(handle, event.stream),
  heads: store.heads(handle, event.stream),
}))

async function cold() {
  assert.notEqual(writerId, request.sourceWriterId)
  const store = await import('../../../../dist/Persistence/EventStore/Surface.js')
  const handle = store.create(commonDir, writerId)
  try {
    assert.equal(fs.existsSync(writerFile), false)
    assert.equal(fs.existsSync(lockPath), false)
    return {
      pid: process.pid, views: views(store, handle), writerCreated: false,
      bytesBase64: fs.readFileSync(sourceFile, 'base64'),
    }
  } finally {
    store.dispose(handle)
  }
}

async function measure() {
  assert.equal(writerId, request.sourceWriterId)
  const requireFromLockfile = createRequire(lockfilePath)
  const retry = requireFromLockfile('retry')
  const originalOperation = retry.operation
  const originals = {
    mkdirSync: fs.mkdirSync, appendFileSync: fs.appendFileSync, openSync: fs.openSync,
    fsyncSync: fs.fsyncSync, closeSync: fs.closeSync, rmSync: fs.rmSync,
  }
  const injected = Object.assign(new Error(`Controlled one-hop ${scenario} mkdir failure`), {
    code: scenario === 'mkdir-eio' ? 'EIO' : 'EACCES', syscall: 'mkdir', path: lockPath,
  })
  const observed = {
    mkdirAttempts: 0, legalAfterFailure: 0, injected: 0, busy: 0,
    append: 0, fsync: 0, close: 0, release: 0,
  }
  const descriptors = new Set()
  const busy = scenario.startsWith('busy-')
  let recording = false
  let failNextMkdir = !busy
  let failedMkdir = false
  let releaseHeld
  let releaseFinished = !busy
  let busyBeforeRelease = null
  let appendBeforeRelease = false
  let settleBusy
  const busyObserved = new Promise(resolve => { settleBusy = resolve })
  const owns = (value, ownedPath) => typeof value === 'string' && path.resolve(value) === ownedPath
  let store
  let handle
  let output
  let operationFailure
  const cleanupFailures = []
  try {
    store = await import('../../../../dist/Persistence/EventStore/Surface.js')
    handle = store.create(commonDir, writerId)
    assert.deepEqual(await store.append(handle, [request.seed]), { ok: true, cuts: [] })
    const beforeBase64 = fs.readFileSync(sourceFile, 'base64')
    if (busy) {
      releaseHeld = lockfile.lockSync(target, { realpath: false, retries: 0, stale: 5000, update: 1500 })
      assert.equal(fs.existsSync(lockPath), true)
    }
    retry.operation = function (...args) {
      const operation = Reflect.apply(originalOperation, this, args)
      const originalRetry = operation.retry
      operation.retry = function (error) {
        const actualBusy = recording && error?.code === 'ELOCKED' && error.file === target
        const decision = Reflect.apply(originalRetry, this, [error])
        if (actualBusy) {
          observed.busy += 1
          queueMicrotask(settleBusy)
        }
        return decision
      }
      return operation
    }
    fs.mkdirSync = function (...args) {
      if (recording && owns(args[0], lockPath)) {
        observed.mkdirAttempts += 1
        if (failNextMkdir && !failedMkdir) {
          failedMkdir = true
          observed.injected += 1
          throw injected
        }
        if (failedMkdir) observed.legalAfterFailure += 1
      }
      return Reflect.apply(originals.mkdirSync, this, args)
    }
    fs.appendFileSync = function (...args) {
      const result = Reflect.apply(originals.appendFileSync, this, args)
      if (recording && owns(args[0], sourceFile)) {
        observed.append += 1
        if (!releaseFinished) appendBeforeRelease = true
      }
      return result
    }
    fs.openSync = function (...args) {
      const descriptor = Reflect.apply(originals.openSync, this, args)
      if (recording && owns(args[0], sourceFile) && args[1] === 'r+') descriptors.add(descriptor)
      return descriptor
    }
    fs.fsyncSync = function (...args) {
      const result = Reflect.apply(originals.fsyncSync, this, args)
      if (recording && descriptors.has(args[0])) observed.fsync += 1
      return result
    }
    fs.closeSync = function (...args) {
      const result = Reflect.apply(originals.closeSync, this, args)
      if (descriptors.delete(args[0])) observed.close += 1
      return result
    }
    fs.rmSync = function (...args) {
      if (recording && owns(args[0], lockPath)) observed.release += 1
      return Reflect.apply(originals.rmSync, this, args)
    }
    syncBuiltinESMExports()
    let settled = false
    recording = true
    const pending = store.append(handle, [request.incoming]).then(result => {
      settled = true
      return result
    }, error => {
      settled = true
      throw error
    })
    if (busy) {
      const reached = await Promise.race([
        busyObserved.then(() => 'busy'),
        pending.then(() => 'settled'),
      ])
      assert.equal(reached, 'busy', 'The original proper-lockfile must report actual ELOCKED')
      busyBeforeRelease = {
        settled, append: observed.append, lockExists: fs.existsSync(lockPath),
        bytesBase64: fs.readFileSync(sourceFile, 'base64'),
      }
      failNextMkdir = scenario === 'busy-eacces'
      releaseHeld()
      releaseHeld = undefined
      releaseFinished = true
      assert.equal(fs.existsSync(lockPath), false)
    }
    const result = await pending
    recording = false
    assert.equal(descriptors.size, 0)
    assert.equal(fs.existsSync(lockPath), false)
    let normalized = result
    if (!result.ok) {
      const { cause, ...fields } = result.error
      normalized = { ok: false, error: { ...fields, causeIsInjected: cause === injected,
        causeCode: cause?.code, causeSyscall: cause?.syscall, causePath: cause?.path } }
    }
    output = {
      pid: process.pid, observed, beforeBase64, bytesBase64: fs.readFileSync(sourceFile, 'base64'),
      views: views(store, handle), result: normalized, busyBeforeRelease, appendBeforeRelease,
    }
  } catch (error) {
    operationFailure = { error }
  } finally {
    recording = false
    retry.operation = originalOperation
    Object.assign(fs, originals)
    syncBuiltinESMExports()
    try {
      if (releaseHeld !== undefined) releaseHeld()
    } catch (error) {
      cleanupFailures.push(error)
    }
    try {
      if (handle !== undefined) store.dispose(handle)
    } catch (error) {
      cleanupFailures.push(error)
    }
  }
  if (cleanupFailures.length > 0) {
    throw new AggregateError(operationFailure === undefined ? cleanupFailures : [operationFailure.error, ...cleanupFailures],
      'append lock fixture cleanup failed', { cause: operationFailure?.error })
  }
  if (operationFailure !== undefined) throw operationFailure.error
  return output
}

process.stdout.write(JSON.stringify(mode === 'measure' ? await measure() : await cold()))
