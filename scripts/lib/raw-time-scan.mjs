/**
 * raw-time-scan.mjs — ambient-time physical boundary scanner.
 *
 * Domain / Application / Session code must never read a wall clock directly;
 * the only legal ambient-time owners are the exact physical adapter files in
 * RAW_TIME_ALLOWLIST. time-capability-004's ward is the unit test at
 * requirements/time-capability/tests/004.test.mjs — this is
 * a library, not a gate: fail-closed behavior lives in the tests and in
 * collectRawTimeScanEntries' missing-root throw.
 */

import { existsSync, readFileSync } from 'node:fs'
import { join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { walk } from './walk.mjs'

export const ROOT = fileURLToPath(new URL('../..', import.meta.url))
export const PRODUCTION_ROOT_REL = 'src/Wanxiangshu'

/**
 * Scan the complete production tree. Physical adapters are removed only by the
 * exact-file allowlist below; a new sibling never inherits an exemption.
 */
export const RAW_TIME_SCAN_ROOTS = Object.freeze(['.'])

/**
 * Forbidden raw-time tokens in Domain / Application / Session.
 * Includes Date.now, the JS interop twin of UtcNow.
 */
export const RAW_TIME_TOKENS = Object.freeze([
  'DateTimeOffset.UtcNow',
  'DateTime.Now',
  'DateTime.UtcNow',
  'Date.now',
  'setTimeout',
  'timerTask',
])

/**
 * Exact physical adapter files allowed to own ambient time. Directory prefixes
 * are forbidden: every new adapter must be reviewed and added explicitly.
 */
export const RAW_TIME_ALLOWLIST = Object.freeze([
  'Change/Host/Host.fs',
  'Execution/Delegation/Fork/OpenCode/JoinResultRenderer.fs',
  'Execution/Delegation/Fork/OpenCode/JoinTool.fs',
  'Execution/Delegation/Fork/OpenCode/ToolSurface.fs',
  'Execution/Delegation/SyncDelegate/Surface.fs',
  'Interaction/Dispatch/PhysicalAcceptance.fs',
  'OpenCode/Host/PairProgrammingThoughtSurface.fs',
  'OpenCode/Host/PluginHost.fs',
  'OpenCode/Tools/ToolRegistry.fs',
  'Persistence/EventStore/ProcessEventLog.fs',
  'Persistence/Journal/EventStoreJournalWriter.fs',
  'Process/JsSandbox.fs',
  'Process/JsCallLoop.fs',
  'Process/NodeProcessWait.fs',
  'Process/ProcessRunner.fs',
  'Process/Pty.fs',
  'Process/PtySupervisor.fs',
  'Process/NodeTiming.fs',
  'Repository/Investigation/Semble/Stdio.fs',
  'Repository/Programming/Js/OpenCode/BookkeeperTool.fs',
  'Repository/Programming/Js/OpenCode/ToolHost.fs',
  'Repository/Programming/Js/OpenCode/ToolWorkflow.fs',
  'OpenCode/Host/PluginSessionScope.fs',
])

const norm = (p) => p.replace(/\\/g, '/')

/**
 * @param {string} file
 * @param {readonly string[]} [allowlist]
 */
export const isRawTimeAllowlisted = (file, allowlist = RAW_TIME_ALLOWLIST) => {
  const rel = norm(file)
  const candidates = [
    rel,
    rel.startsWith(`${PRODUCTION_ROOT_REL}/`)
      ? rel.slice(PRODUCTION_ROOT_REL.length + 1)
      : rel,
  ]
  for (const entry of allowlist) {
    const a = norm(entry)
    for (const c of candidates) {
      if (c === a) return true
    }
  }
  return false
}

/**
 * Scan text entries for raw-time tokens. Skips allowlisted paths.
 *
 * @param {{ file: string, text: string }[]} entries
 * @param {{ allowlist?: readonly string[], tokens?: readonly string[] }} [opts]
 * @returns {{ kind: 'raw-time', file: string, line: number, token: string, text: string, message: string }[]}
 */
export const scanRawTimeEntries = (entries, opts = {}) => {
  const allowlist = opts.allowlist ?? RAW_TIME_ALLOWLIST
  const tokens = opts.tokens ?? RAW_TIME_TOKENS
  const violations = []
  for (const { file, text } of entries) {
    const fileNorm = norm(file)
    if (isRawTimeAllowlisted(fileNorm, allowlist)) continue
    const lines = text.split('\n')
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i]
      // Skip pure documentation / comments naming the forbidden tokens.
      const code = line.replace(/\/\/.*/, '')
      for (const token of tokens) {
        if (code.includes(token)) {
          violations.push({
            kind: 'raw-time',
            file: fileNorm,
            line: i + 1,
            token,
            text: line.trim(),
            message: `${fileNorm}:${i + 1} raw time '${token}' — ${line.trim().slice(0, 120)}`,
          })
        }
      }
    }
  }
  return violations
}

/**
 * Collect every source entry under the declared production scan roots.
 * @param {string} productionRoot absolute path to src/Wanxiangshu
 * @param {readonly string[]} [roots]
 */
export const collectRawTimeScanEntries = (
  productionRoot,
  roots = RAW_TIME_SCAN_ROOTS,
) => {
  if (!existsSync(productionRoot)) {
    throw new Error(`raw-time scan root does not exist: ${productionRoot}`)
  }

  const entries = []
  for (const root of roots) {
    const dir = resolve(productionRoot, root)
    const relativeRoot = norm(relative(productionRoot, dir))
    if (relativeRoot.startsWith('../') || relativeRoot === '..') {
      throw new Error(`raw-time scan root escapes production tree: ${root}`)
    }
    if (!existsSync(dir)) {
      throw new Error(`raw-time scan root does not exist: ${dir}`)
    }
    for (const abs of walk(dir, ['.fs', '.mjs', '.js', '.ts'])) {
      entries.push({
        file: norm(relative(productionRoot, abs) || abs),
        text: readFileSync(abs, 'utf8'),
      })
    }
  }
  return entries
}

/**
 * Raw-time scan of the production tree (injectable root for fixtures).
 * @param {string} [repoRoot]
 * @param {{ allowlist?: readonly string[] }} [opts]
 */
export const scanRawTimeProduction = (repoRoot = ROOT, opts = {}) => {
  const productionRoot = join(repoRoot, PRODUCTION_ROOT_REL)
  return scanRawTimeEntries(collectRawTimeScanEntries(productionRoot), {
    allowlist: opts.allowlist ?? RAW_TIME_ALLOWLIST,
  })
}
