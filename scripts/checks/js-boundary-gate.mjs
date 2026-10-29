#!/usr/bin/env node
// JS semantic boundary gate (single implementation; test-boundary merged).
//
// The retired test-boundary gate's `dist/fable_modules` line check is subsumed
// by scanAll's `fable-modules` rule: same requirements file set (every .mjs/.js
// under requirements lives under a tests/ directory), strictly broader pattern
// (`fable_modules` matches every `dist/fable_modules` line and more), same
// BUILD_VERIFICATION_FILES allowlist. Explicit raw Host canaries may inspect
// the representation they verify; neighboring semantic tests and helpers may not.
// Run the single sweep; any A-D debt is RED.

import { join, resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import { scanAll } from '../lib/test-surface-scan.mjs'

export function check(context) {
  const root = context?.root ?? process.cwd()
  const scanned = scanAll(join(root, 'requirements'))
  const files = Object.keys(scanned).sort()
  const issues = []
  for (const file of files) {
    for (const hit of scanned[file]) {
      issues.push({
        code: hit.rule,
        path: hit.file,
        line: hit.line,
        message: `${hit.file}:${hit.line} [${hit.rule}] ${hit.text}`,
      })
    }
  }
  return {
    issues,
    debt: issues.length,
    fileCount: files.length,
  }
}

export function runCli(argv, root) {
  return run({ root })
}

/** Execute the gate; returns a process status for the CLI and tests. */
export const run = ({ root = process.cwd() } = {}) => {
  const scanned = scanAll(join(root, 'requirements'))
  const files = Object.keys(scanned).sort()
  const debt = files.reduce((sum, file) => sum + scanned[file].length, 0)

  if (debt > 0) {
    console.error(`js-boundary-gate: ${debt} violation(s) across ${files.length} file(s)`)
    for (const file of files) {
      for (const hit of scanned[file]) console.error(`  ${hit.file}:${hit.line} [${hit.rule}] ${hit.text}`)
    }
    return 1
  }

  console.log('js-boundary-gate: OK — 0 debt line(s) across 0 file(s)')
  return 0
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href
if (isMain) {
  const code = run()
  if (code !== 0) process.exit(code)
}
