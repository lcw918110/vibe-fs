#!/usr/bin/env node
/**
 * Warm the local opencode binary once.
 *
 * First launch on a machine (or fresh CI runner) pays package resolution /
 * native binary extract / OS page-cache costs. Later ProcessHost.serve starts
 * then compete with per-test timeouts. The integration orchestrator invokes
 * this once before any integration child; the release sink never duplicates it.
 *
 * Exit 0 on success; non-zero if the binary is missing or version fails.
 */

import { spawnSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { ensureNativeOpencode } from './lib/ensure-native-opencode.mjs'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

ensureNativeOpencode({ root })

function resolveBin() {
  if (process.env.OPENCODE_BIN) return process.env.OPENCODE_BIN
  const local = path.join(root, 'node_modules', '.bin', 'opencode')
  if (fs.existsSync(local)) return local
  return 'opencode'
}

const bin = resolveBin()
const started = Date.now()
console.error(`[warmup-opencode] bin=${bin}`)

const warmupHome = fs.mkdtempSync(path.join(os.tmpdir(), 'opencode-warmup-'))
const warmupConfig = path.join(warmupHome, '.config')
fs.mkdirSync(warmupConfig, { recursive: true })
const warmupEnv = {
  ...process.env,
  HOME: warmupHome,
  USERPROFILE: warmupHome,
  XDG_CONFIG_HOME: warmupConfig,
}

let version
try {
  version = spawnSync(bin, ['serve', '--version'], {
    cwd: root,
    encoding: 'utf8',
    env: warmupEnv,
    timeout: 120_000,
  })
} catch (error) {
  try { fs.rmSync(warmupHome, { recursive: true, force: true }) } catch {}
  throw error
}

try { fs.rmSync(warmupHome, { recursive: true, force: true }) } catch {}

if (version.error || version.status !== 0) {
  console.error(
    `[warmup-opencode] serve --version failed status=${version.status} ` +
      `error=${version.error?.message || ''} stderr=${(version.stderr || '').slice(0, 500)}`,
  )
  process.exit(version.status === null ? 1 : version.status)
}

const ver = String(version.stdout || version.stderr || '').trim().split('\n')[0] || '(unknown)'
console.error(`[warmup-opencode] ready in ${Date.now() - started}ms total`)
