import fs from 'node:fs'
import net from 'node:net'
import path from 'node:path'
import { spawn, spawnSync } from 'node:child_process'
import { createInterface } from 'node:readline'
import { toolchain, prepare } from './toolchain.mjs'
import { claim, release, readOwner } from './ownership.mjs'
import { locations } from './client.mjs'
import { readReferences, currentReferences, referenceKey, recordReferences } from './references.mjs'
import { createUsage } from './usage.mjs'

const root = fs.realpathSync(process.argv[2])
const expected = process.argv[3]
const places = locations(root)
const usage = createUsage(path.join(root, '.fable-build/compiler/usage.json'))
const identity = claim(places.state)
if (!identity) process.exit(0)
const setting = (name, fallback) => {
  const value = Number(process.env[name] ?? fallback)
  if (!Number.isSafeInteger(value) || value < 1) throw new Error(`Invalid ${name}`)
  return value
}
let worker
let referenceState
let pending
let ready
let retirement
let active
let count = 0
let idle
let closing = false
let queue = Promise.resolve()
const sockets = new Set()
let queued = 0
const send = (socket, result) => {
  if (!socket.destroyed) socket.end(JSON.stringify({ type: 'result', result }) + '\n')
}
const log = (stream, text) => {
  if (active && !active.destroyed) active.write(JSON.stringify({ type: 'log', stream, text }) + '\n')
}
function kill(child) {
  try {
    if (process.platform === 'win32') spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'])
    else process.kill(-child.pid, 'SIGKILL')
  } catch (error) {
    if (error.code !== 'ESRCH') throw error
  }
}
async function retire() {
  if (retirement) return retirement
  if (!worker) return
  const previous = worker
  worker = undefined
  count = 0
  retirement = new Promise(resolve => {
    if (previous.exitCode !== null || previous.signalCode !== null) resolve()
    else {
      previous.once('exit', resolve)
      kill(previous)
    }
  })
  try { await retirement }
  finally { retirement = undefined }
}
async function launch(current) {
  if (worker) return
  const entry = prepare(root, current)
  const child = spawn('dotnet', [entry, String(process.pid)], { cwd: root, detached: process.platform !== 'win32', stdio: ['pipe', 'pipe', 'pipe'] })
  worker = child
  const started = new Promise((resolve, reject) => { ready = { resolve, reject } })
  createInterface({ input: child.stdout }).on('line', line => {
    if (line === '@@CHECKER_READY') {
      ready?.resolve()
      ready = undefined
    } else if (line.startsWith('@@CHECKER_RESULT ')) {
      const waiting = pending
      pending = undefined
      try { waiting?.resolve(JSON.parse(line.slice('@@CHECKER_RESULT '.length))) }
      catch (error) { waiting?.reject(error) }
    } else log('stdout', line + '\n')
  })
  child.stderr.setEncoding('utf8')
  child.stderr.on('data', text => log('stderr', text))
  const failed = error => {
    ready?.reject(error)
    ready = undefined
    pending?.reject(error)
    pending = undefined
  }
  child.on('error', failed)
  child.on('exit', (code, signal) => {
    if (worker === child) worker = undefined
    failed(new Error(`Checker exited: ${code ?? signal}`))
  })
  const timer = setTimeout(() => kill(child), 30000)
  try { await started }
  finally { clearTimeout(timer) }
}
function compile(command) {
  return new Promise((resolve, reject) => {
    pending = { resolve, reject }
    worker.stdin.write(JSON.stringify(command) + '\n', error => {
      if (error) {
        pending = undefined
        reject(error)
      }
    })
  })
}

const server = net.createServer(socket => {
  sockets.add(socket)
  socket.on('error', () => {})
  socket.on('close', () => {
    sockets.delete(socket)
    if (active === socket) void retire()
  })
  socket.setEncoding('utf8')
  let buffer = ''
  let received = false
  socket.on('data', text => {
    if (received) return
    buffer += text
    if (buffer.length > 65536) return socket.destroy()
    const end = buffer.indexOf('\n')
    if (end < 0) return
    received = true
    let command
    try {
      command = JSON.parse(buffer.slice(0, end))
      if (command.root !== root || command.protocol !== 1 || !['compile', 'status', 'stop'].includes(command.op)) throw new Error('Invalid compiler request')
      if (command.op === 'compile' && (!['project', 'output'].every(name => {
        if (typeof command[name] !== 'string') return false
        const relative = path.relative(root, path.resolve(command[name]))
        return relative && relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative)
      })
        || typeof command.noRestore !== 'boolean' || typeof command.noCache !== 'boolean')) throw new Error('Invalid compile paths or options')
    } catch (error) {
      send(socket, { ok: false, error: error.message })
      return
    }
    if (command.op !== 'status') {
      clearTimeout(idle)
      queued++
    }
    queue = queue.then(async () => {
      active = command.op === 'compile' ? socket : undefined
      let finishUsage
      let result
      let outcome = 'failure'
      try {
        if (closing) return send(socket, { retry: true })
        if (socket.destroyed) return
        if (command.op === 'stop') {
          await retire()
          send(socket, { ok: true, stopped: true })
          stop()
          return
        }
        if (command.op === 'status') {
          send(socket, { ok: true, running: true, servicePid: process.pid, workerPid: worker?.pid ?? null, completed: count })
          return
        }
        const current = toolchain(root)
        if (command.key !== expected || current.key !== expected) {
          send(socket, { retry: true })
          stop()
          return
        }
        const referenceInputs = currentReferences(readReferences(root))
        if (worker && referenceState !== referenceKey(referenceInputs)) await retire()
        await launch(current)
        const timer = setTimeout(() => { void retire() }, 540000)
        finishUsage = usage.begin({ workerPid: worker.pid, projects: 1, toolchain: current.key })
        try { result = await compile(command) }
        finally { clearTimeout(timer) }
        if (toolchain(root).key !== expected || readOwner(places.state)?.token !== identity.token) {
          throw new Error('Compiler inputs or service ownership changed during compilation; run the build again')
        }
        count++
        if (result.ok) {
          if (referenceKey(currentReferences(referenceInputs)) !== referenceKey(referenceInputs)
            || referenceKey(currentReferences(result.references)) !== referenceKey(result.references)) {
            throw new Error('Referenced assemblies changed during compilation; run the build again')
          }
          recordReferences(root, result.references)
          referenceState = referenceKey(currentReferences(readReferences(root)))
        }
        const retired = !result.ok || count >= setting('WXS_CHECKER_BUILD_LIMIT', 32)
          || result.memoryBytes >= setting('WXS_CHECKER_MEMORY_MIB', 1536) * 1048576
        if (retired) await retire()
        outcome = result.ok ? 'success' : 'failure'
        send(socket, { ...result, retired, servicePid: process.pid })
      } catch (error) {
        await retire()
        send(socket, { ok: false, error: error.message })
      } finally {
        finishUsage?.({ outcome, reuse: typeof result?.reused === 'boolean'
          ? { created: result.reused ? 0 : 1, reused: result.reused ? 1 : 0, reusedAcrossBuilds: result.reused ? 1 : 0 }
          : null, workerMemory: result?.memory ?? null, serviceMemory: process.memoryUsage(), retired: !worker })
        active = undefined
        if (command.op !== 'status') {
          queued--
          if (!closing && queued === 0) armIdle()
        }
      }
    }).catch(error => {
      console.error(error)
      stop()
    })
  })
})
function armIdle() {
  clearTimeout(idle)
  idle = setTimeout(stop, setting('WXS_CHECKER_IDLE_MS', 600000))
}
function stop() {
  if (closing) return
  closing = true
  clearTimeout(idle)
  server.close()
  void (async () => {
    await retire()
    await queue
    for (const socket of sockets) socket.destroy()
    if (readOwner(places.state)?.token === identity.token) release(places.state, identity)
  })().catch(error => {
    console.error(error)
    process.exitCode = 1
  })
}
try {
  if (toolchain(root).key !== expected) {
    release(places.state, identity)
    process.exit(0)
  }
  if (process.platform !== 'win32') {
    fs.mkdirSync(places.directory, { recursive: true, mode: 0o700 })
    if (fs.statSync(places.directory).uid !== process.getuid()) throw new Error('Compiler socket directory has another owner')
    fs.rmSync(places.socket, { force: true })
  }
  await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(places.socket, resolve)
  })
  if (process.platform !== 'win32') fs.chmodSync(places.socket, 0o600)
  process.on('SIGTERM', stop)
  process.on('SIGINT', stop)
  armIdle()
} catch (error) {
  console.error(error)
  stop()
  process.exitCode = 1
}
