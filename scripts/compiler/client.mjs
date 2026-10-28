import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import net from 'node:net'
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'
import { digest, toolchain } from './toolchain.mjs'
import { readOwner, ownerAlive } from './ownership.mjs'

export function locations(root) {
  const state = path.join(root, '.fable-build/compiler/service')
  const directory = path.join(process.platform === 'win32' ? os.tmpdir() : '/tmp', `wanxiangshu-checker-${process.getuid?.() ?? 'user'}`)
  const name = digest(root).slice(0, 32)
  return { state, directory, socket: process.platform === 'win32' ? `\\\\.\\pipe\\wanxiangshu-checker-${name}` : path.join(directory, name + '.sock') }
}

function exchange(socketPath, request, output) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(socketPath)
    let connected = false
    let completed = false
    let buffer = ''
    const finish = (error, result) => {
      if (completed) return
      completed = true
      clearTimeout(timer)
      socket.destroy()
      if (error) reject(error)
      else resolve(result)
    }
    const timer = setTimeout(() => finish(new Error('Compiler request timed out; run the build again')), 600000)
    socket.setEncoding('utf8')
    socket.on('connect', () => {
      connected = true
      socket.write(JSON.stringify(request) + '\n')
    })
    socket.on('data', text => {
      buffer += text
      let end
      while ((end = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, end)
        buffer = buffer.slice(end + 1)
        try {
          const message = JSON.parse(line)
          if (message.type === 'log') output(message)
          else if (message.type === 'result') finish(null, message.result)
          else throw new Error('Unknown compiler response')
        } catch (error) { finish(error) }
      }
    })
    socket.on('error', error => {
      error.unavailable = !connected && ['ENOENT', 'ECONNREFUSED'].includes(error.code)
      finish(error)
    })
    socket.on('close', () => finish(new Error('Compiler disconnected before reporting a result; run the build again')))
  })
}

export async function requestCompiler(directory, command, output = () => {}) {
  const root = fs.realpathSync(directory)
  const places = locations(root)
  const identity = command.op === 'compile' ? toolchain(root) : null
  const deadline = Date.now() + 30000
  let lastStart = 0
  while (Date.now() < deadline) {
    try {
      const result = await exchange(places.socket, { ...command, root, key: identity?.key, protocol: 1 }, output)
      if (!result.retry) return result
    } catch (error) {
      if (!error.unavailable) throw error
      if (!ownerAlive(readOwner(places.state))) {
        if (command.op !== 'compile') return { ok: true, running: false }
        if (Date.now() - lastStart > 500) {
          fs.mkdirSync(places.state, { recursive: true })
          const log = fs.openSync(path.join(places.state, 'service.log'), 'a')
          try {
            const { NODE_OPTIONS, NODE_PATH, ...env } = process.env
            const child = spawn(process.execPath, [fileURLToPath(new URL('./service.mjs', import.meta.url)), root, identity.key],
              { cwd: root, env, detached: true, stdio: ['ignore', log, log] })
            child.on('error', () => {})
            child.unref()
          } finally { fs.closeSync(log) }
          lastStart = Date.now()
        }
      }
    }
    await delay(40)
  }
  throw new Error(`Compiler service did not become ready; see ${path.join(places.state, 'service.log')}`)
}

export function residentSpawn(root) {
  return async (command, args, options) => {
    if (command !== 'dotnet' || args.slice(0, 4).join(' ') !== 'tool run fable --') throw new Error('Expected a Fable compile request')
    let stdout = ''
    let stderr = ''
    const result = await requestCompiler(root, { op: 'compile', project: args[4], output: args[args.indexOf('-o') + 1],
      noRestore: args.includes('--noRestore'), noCache: args.includes('--noCache') }, message => {
      if (message.stream === 'stdout') stdout += message.text
      else stderr += message.text
      if (options.stdio === 'inherit') process[message.stream].write(message.text)
    })
    if (options.stdio === 'inherit' && result.pid) console.log(`[checker] pid=${result.pid} reused=${result.reused} retired=${result.retired}`)
    if (!result.ok) {
      const text = (result.errors ?? [result.error]).join('\n') + '\n'
      stderr += text
      if (options.stdio === 'inherit') process.stderr.write(text)
    }
    return { code: result.ok ? 0 : 1, stdout, stderr }
  }
}
