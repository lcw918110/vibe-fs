import assert from 'node:assert/strict'
import { fork } from 'node:child_process'
import { existsSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { store, persistence, current, mustOk, digest } from '../persistence-support.mjs'

const [mode, inputJson] = process.argv.slice(2)
const input = JSON.parse(inputJson)
const selfPath = fileURLToPath(import.meta.url)

const send = message => new Promise((resolve, reject) => {
  process.send(message, error => error ? reject(error) : resolve())
})
const waitForGo = sealed => new Promise((resolve, reject) => {
  const cleanup = () => {
    process.removeListener('message', onMessage)
    process.removeListener('disconnect', onDisconnect)
  }
  const onMessage = message => {
    cleanup()
    if (message?.phase === 'go') resolve()
    else reject(new Error('The contender received an unexpected barrier message'))
  }
  const onDisconnect = () => {
    cleanup()
    reject(new Error('The original coordinator disconnected before releasing this contender'))
  }
  process.once('message', onMessage)
  process.once('disconnect', onDisconnect)
  send(sealed).catch(error => {
    cleanup()
    reject(error)
  })
})

async function contender() {
  const handle = store.create(input.commonDir, input.writerId)
  try {
    const before = current(handle, input.inquiry)
    const event = mustOk(persistence.prepareTransition(handle, digest, input.raw))
    assert.deepEqual(current(handle, input.inquiry), before, 'Each contender independently seals without publishing')
    const eventsDir = join(input.commonDir, 'wanxiangshu', 'events')
    const resolveWriterFile = () => {
      const legacy = join(eventsDir, `${input.writerId}.ndjson`)
      if (existsSync(legacy)) return legacy
      return join(eventsDir, `${input.writerId}-0000.ndjson`)
    }
    await waitForGo({ phase: 'sealed', writerId: input.writerId, pid: process.pid,
      event, before, heads: store.heads(handle, event.stream),
      writerFileExists: existsSync(resolveWriterFile()) })
    const receipt = await store.append(handle, [event])
    await send({ phase: 'appended', writerId: input.writerId, pid: process.pid, event, receipt,
      writerBytes: readFileSync(resolveWriterFile()).toString('base64'),
      current: current(handle, input.inquiry), heads: store.heads(handle, event.stream) })
  } finally {
    store.dispose(handle)
    if (process.connected) process.disconnect()
  }
}

function spawnContender(request, trace) {
  const env = { ...process.env }
  delete env.NODE_TEST_CONTEXT
  // Both contenders remain in the selected coordinator's original owned group.
  const child = fork(selfPath, ['contender', JSON.stringify(request)], {
    execPath: process.execPath, execArgv: [], env, detached: false,
    stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
  })
  let resolveSealed
  let rejectSealed
  let resolveAppended
  let rejectAppended
  const sealed = new Promise((resolve, reject) => {
    resolveSealed = resolve
    rejectSealed = reject
  })
  const appended = new Promise((resolve, reject) => {
    resolveAppended = resolve
    rejectAppended = reject
  })
  sealed.catch(() => {})
  appended.catch(() => {})
  let phase = 'sealed'
  let output = ''
  let failure
  const fail = error => {
    failure ??= error
    phase = 'failed'
    rejectSealed(error)
    rejectAppended(error)
  }
  for (const stream of [child.stdout, child.stderr]) {
    stream.setEncoding('utf8').on('data', bytes => {
      output += bytes
      if (output.length > 65536) fail(new Error('The contender exceeded its diagnostic output boundary'))
    })
    stream.once('error', fail)
  }
  child.on('message', message => {
    if (phase === 'failed') return
    if (message?.phase !== phase || message.writerId !== request.writerId || message.pid !== child.pid) {
      fail(new Error('The original contender supplied an unexpected phase or process identity'))
      return
    }
    trace.push({ phase: message.phase, writerId: request.writerId, pid: child.pid })
    if (phase === 'sealed') {
      phase = 'appended'
      resolveSealed(message)
    } else {
      phase = 'closed'
      resolveAppended(message)
    }
  })
  const closed = new Promise(resolve => {
    child.once('error', error => {
      fail(error)
    })
    child.once('close', (code, signal) => {
      if (phase !== 'closed' && phase !== 'failed') fail(new Error(`Contender closed before completing its original IPC sequence: ${code ?? signal}\n${output}`))
      resolve({ code, signal })
    })
  })
  const release = () => new Promise((resolve, reject) => {
    trace.push({ phase: 'go', writerId: request.writerId, pid: child.pid })
    child.send({ phase: 'go' }, error => error ? reject(error) : resolve())
  })
  return {
    child, sealed, appended, closed, release,
    get failure() { return failure },
  }
}

async function pair() {
  assert.equal(input.inputs.length, 2)
  const trace = []
  const contenders = []
  let result
  let failure
  const cleanupErrors = []
  try {
    for (const request of input.inputs) contenders.push(spawnContender(request, trace))
    const sealed = await Promise.all(contenders.map(owner => owner.sealed))
    assert.deepEqual(trace.map(record => record.phase), ['sealed', 'sealed'])
    assert.deepEqual(sealed[0].before, sealed[1].before)
    assert.deepEqual(sealed[0].heads, sealed[1].heads)
    await Promise.all(contenders.map(owner => owner.release()))
    const appended = await Promise.all(contenders.map(owner => owner.appended))
    const closed = await Promise.all(contenders.map(owner => owner.closed))
    for (const owner of contenders) {
      if (owner.failure) throw owner.failure
    }
    assert.deepEqual(closed, [{ code: 0, signal: null }, { code: 0, signal: null }])
    result = { pid: process.pid, sealed, appended, closed, trace }
  } catch (error) {
    failure = { error }
  } finally {
    for (const owner of contenders) {
      try {
        if (owner.child.exitCode === null && owner.child.signalCode === null) owner.child.kill('SIGKILL')
      } catch (error) {
        cleanupErrors.push(error)
      }
    }
    for (const owner of contenders) {
      try { await owner.closed }
      catch (error) { cleanupErrors.push(error) }
    }
  }
  if (cleanupErrors.length > 0) throw new AggregateError([...(failure ? [failure.error] : []), ...cleanupErrors],
    'The original contender owner failed to finish cleanup', { cause: failure?.error })
  if (failure) throw failure.error
  process.stdout.write(JSON.stringify(result) + '\n')
}

function cold() {
  const handle = store.create(input.commonDir, input.writerId)
  try {
    process.stdout.write(JSON.stringify({
      pid: process.pid,
      events: input.ids.map(id => store.read(handle, id)),
      heads: store.heads(handle, input.stream), head: store.head(handle, input.stream),
      current: current(handle, input.inquiry),
      writerFileExists: existsSync(join(input.commonDir, 'wanxiangshu', 'events', `${input.writerId}.ndjson`)),
    }) + '\n')
  } finally {
    store.dispose(handle)
  }
}

if (mode === 'contender') await contender()
else if (mode === 'pair') await pair()
else if (mode === 'cold') cold()
else throw new Error('Unknown H0b raw append fixture mode')
