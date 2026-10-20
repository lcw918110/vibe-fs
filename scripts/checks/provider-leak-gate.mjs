#!/usr/bin/env node
/**
 * ARCH-016 Gate B — Provider Leak Gate.
 * Provider-visible schema / fixed prose / renderer output must not leak internal vocabulary.
 *
 * Usage:
 *   node scripts/checks/provider-leak-gate.mjs
 */

import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { walk } from '../lib/walk.mjs'

const norm = (p) => p.replace(/\\/g, '/')

/** Provider renderer surfaces (Join / horizon / tool catalog prose). */
export const PROVIDER_SCAN_ROOTS = Object.freeze([
  'src/Wanxiangshu/Execution/Delegation/Fork/OpenCode/JoinResultRenderer.fs',
  'src/Wanxiangshu/Execution/Session/OpenCode/HorizonTool.fs',
  'src/Wanxiangshu/Execution/Delegation/Fork/OpenCode/JoinTool.fs',
  'src/Wanxiangshu/Execution/Delegation/Fork/OpenCode/Tool.fs',

  'src/Wanxiangshu/Mission/Work/OpenCode/ReviewShell.fs',
  'src/Wanxiangshu/Mission/Work/OpenCode/ReviewBackendManager.fs',
  'src/Wanxiangshu/Mission/Planning/PlanTools.fs',
  'src/Wanxiangshu/Repository/Programming/Js/OpenCode/BookkeeperTool.fs',
  'src/Wanxiangshu/OpenCode/Tools/FetchTool.fs',
  'src/Wanxiangshu/Mission/Work/OpenCode/DeliverTool.fs',
  'src/Wanxiangshu/OpenCode/Tools/BashHoneypotTool.fs',

  'src/Wanxiangshu/Execution/Fission/OpenCode/Tool.fs',
  'src/Wanxiangshu/Repository/Knowledge/Casebook/OpenCode/Tools.fs',
])

/** Lines that assemble provider-visible prose or wire fields. */
const PROVIDER_OUTPUT_LINE_RE =
  /Description\s*=|field\s+"|tomlObject|tomlObjectWithInstructions|\[\s*"error"|ToolHostCodec\.(?:TString|TTable)|\btString\s+\(|\bTString\s+"|instructions\s*=|hookSuffix|catalogDescription/

/** verification-system-005 Gate B leak vocabulary (substring tokens). */
export const FORBIDDEN_TOKENS = Object.freeze([
  'SessionId',
  'AgentId',
  'ManagerJobId',
  'PtyId',
  'FissionGroupId',
  'agent_id',
  'pty_id',
  'session_id',
  'lane_index',
])

/** Join/horizon generic DTO field names in renderer output. */
export const FORBIDDEN_DTO_PATTERNS = Object.freeze([
  { id: 'field-status', re: /\bfield\s+"status"/ },
  { id: 'field-code', re: /\bfield\s+"code"/ },
  { id: 'field-error', re: /\bfield\s+"error"/ },
  { id: 'field-ordinal', re: /\bfield\s+"ordinal"/ },
  { id: 'field-kind', re: /\bfield\s+"kind"/ },
  { id: 'field-count', re: /\bfield\s+"count"/ },
  { id: 'toml-error-dto', re: /\[\s*"error"\s*,|\[\s*"error",\s*t(?:String|Table)/ },
])

/** fast-/deep- execution binding must not appear in provider-facing prose. */
export const FAST_DEEP_BINDING_RE = /\b(?:fast|deep)-(?:student|teacher|inspector|coder|manager|orchestrator|devops|browser|inquiry|bookkeeper|reviewer)\b/

/**
 * @typedef {{ id: string, file: string, line: number, text: string }} Violation
 */

/**
 * @param {string} file
 * @param {string} text
 * @returns {Violation[]}
 */
export const scanText = (file, text) => {
  /** @type {Violation[]} */
  const violations = []
  const rel = norm(file)
  const lines = text.split('\n')

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]
    if (/^\s*\/\//.test(line) || /^\s*\/\*/.test(line) || /^\s*\*\//.test(line)) continue

    for (const token of FORBIDDEN_TOKENS) {
      if (!PROVIDER_OUTPUT_LINE_RE.test(line)) continue
      if (line.includes(token)) {
        violations.push({ id: `token:${token}`, file: rel, line: i + 1, text: line.trim() })
      }
    }

    if (PROVIDER_OUTPUT_LINE_RE.test(line) && FAST_DEEP_BINDING_RE.test(line)) {
      violations.push({ id: 'fast-deep-binding', file: rel, line: i + 1, text: line.trim() })
    }

    for (const { id, re } of FORBIDDEN_DTO_PATTERNS) {
      if (re.test(line)) {
        violations.push({ id, file: rel, line: i + 1, text: line.trim() })
      }
    }
  }

  return violations
}

/**
 * @param {{ file: string, text: string }[]} entries
 * @returns {Violation[]}
 */
export const scanEntries = (entries) => {
  /** @type {Violation[]} */
  const violations = []
  for (const { file, text } of entries) violations.push(...scanText(file, text))
  return violations
}

const collectEntries = (repoRoot) => {
  /** @type {{ file: string, text: string }[]} */
  const entries = []
  for (const root of PROVIDER_SCAN_ROOTS) {
    const abs = resolve(repoRoot, root)
    if (!existsSync(abs)) {
      throw new Error(`provider-leak-gate: scan root missing on disk: ${root}`)
    }
    if (abs.endsWith('.fs')) {
      entries.push({ file: norm(root), text: readFileSync(abs, 'utf8') })
      continue
    }
    for (const file of walk(abs, ['.fs'])) {
      entries.push({
        file: norm(file.slice(repoRoot.length + 1)),
        text: readFileSync(file, 'utf8'),
      })
    }
  }
  return entries
}

/** @param {Violation[]} violations */
export const countByFile = (violations) => {
  /** @type {Record<string, number>} */
  const counts = {}
  for (const v of violations) counts[v.file] = (counts[v.file] ?? 0) + 1
  return counts
}

/**
 * @param {string} [repoRoot]
 * @returns {{ ok: boolean, violations: Violation[], counts: Record<string, number> }}
 */
export const scanRepo = (repoRoot = process.cwd()) => {
  const violations = scanEntries(collectEntries(repoRoot))
  const counts = countByFile(violations)
  return { ok: violations.length === 0, violations, counts }
}

export function check(context) {
  const repoRoot = context?.root ?? process.cwd()
  let entries
  if (context?.readText) {
    entries = []
    for (const root of PROVIDER_SCAN_ROOTS) {
      const abs = resolve(repoRoot, root)
      if (!existsSync(abs)) throw new Error(`provider-leak-gate: scan root missing on disk: ${root}`)
      if (abs.endsWith('.fs')) {
        entries.push({ file: norm(root), text: context.readText(root) })
      } else {
        for (const file of walk(abs, ['.fs'])) {
          const rel = norm(file.slice(repoRoot.length + 1))
          entries.push({ file: rel, text: context.readText(rel) })
        }
      }
    }
  } else {
    entries = collectEntries(repoRoot)
  }
  const violations = scanEntries(entries)
  return {
    issues: violations.map((v) => ({
      code: v.id,
      path: v.file,
      line: v.line,
      message: v.text ? `${v.id} — ${v.text}` : v.id,
    })),
    violations,
    ok: violations.length === 0,
  }
}

export const runCli = () => {
  const result = check()

  if (result.ok) {
    console.log('provider-leak-gate: OK — provider renderer surfaces pass Gate B (zero violations)')
    return 0
  }

  console.error(`provider-leak-gate: ${result.issues.length} violation(s)\n`)
  for (const v of result.violations) {
    const loc = v.line ? `${v.file}:${v.line}` : v.file
    console.error(`  ${loc}: ${v.id}${v.text ? ` — ${v.text}` : ''}`)
  }
  return 1
}


const isMain =
  process.argv[1] !== undefined &&
  resolve(fileURLToPath(import.meta.url)) === resolve(process.argv[1])

if (isMain) {
  const code = runCli()
  if (code !== 0) process.exit(code)
}
