import fs from 'node:fs'
import path from 'node:path'
import { randomUUID } from 'node:crypto'
import { spawnSync } from 'node:child_process'

export function alive(pid) {
  if (!Number.isSafeInteger(pid) || pid <= 0) return false
  try { process.kill(pid, 0); return true }
  catch (error) {
    if (error.code === 'ESRCH') return false
    throw error
  }
}

function started(pid) {
  if (!alive(pid)) return null
  const result = process.platform === 'win32'
    ? spawnSync('powershell.exe', ['-NoProfile', '-Command', `(Get-Process -Id ${pid}).StartTime.ToUniversalTime().Ticks`], { encoding: 'utf8' })
    : spawnSync('/bin/ps', ['-p', String(pid), '-o', 'lstart='], { encoding: 'utf8', env: { ...process.env, TZ: 'UTC', LC_ALL: 'C' } })
  if (result.status === 0 && result.stdout.trim()) return result.stdout.trim()
  if (!alive(pid)) return null
  throw new Error(`Cannot identify compiler process ${pid}`)
}

export function readOwner(directory) {
  try { return JSON.parse(fs.readFileSync(path.join(directory, 'active/owner.json'), 'utf8')) }
  catch (error) {
    if (error.code === 'ENOENT') return null
    throw error
  }
}

export function ownerAlive(owner) {
  return owner && alive(owner.pid) && started(owner.pid) === owner.started
}

export function release(directory, owner) {
  try { fs.renameSync(path.join(directory, 'active'), path.join(directory, 'retired', owner.token)) }
  catch (error) {
    if (!['ENOENT', 'EEXIST', 'ENOTEMPTY'].includes(error.code)) throw error
  }
}

export function claim(directory) {
  fs.mkdirSync(path.join(directory, 'retired'), { recursive: true })
  const owner = { pid: process.pid, started: started(process.pid), token: randomUUID() }
  const candidate = path.join(directory, 'candidate-' + owner.token)
  fs.mkdirSync(candidate)
  fs.writeFileSync(path.join(candidate, 'owner.json'), JSON.stringify(owner))
  try {
    for (let attempt = 0; attempt < 4; attempt++) {
      try {
        fs.renameSync(candidate, path.join(directory, 'active'))
        return owner
      } catch (error) {
        if (!['EEXIST', 'ENOTEMPTY'].includes(error.code)) throw error
      }
      const previous = readOwner(directory)
      if (ownerAlive(previous)) return null
      if (previous) release(directory, previous)
    }
    return null
  } finally { fs.rmSync(candidate, { recursive: true, force: true }) }
}
