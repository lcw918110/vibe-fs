#!/usr/bin/env node
// Sequential focused checks: in-process static scan context.

import { dirname, join, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { createCheckContext } from './lib/check-context.mjs'
import { registrationGap } from './checks/clean-break-symbols.mjs'

const root = join(dirname(fileURLToPath(import.meta.url)))

export const checks = [
  join(root, 'checks/architecture.mjs'),
  join(root, 'checks/aggregate-retired.mjs'),
  join(root, 'checks/participant-identity-boundary.mjs'),
  join(root, 'checks/provider-projection-boundary.mjs'),
  join(root, 'checks/subsystems.mjs'),
  join(root, 'checks/fatal-inventory-gate.mjs'),
  join(root, 'checks/fsharp-control-pyramid.mjs'),
  join(root, 'checks/retry-owner.mjs'),
  join(root, 'checks/hook-policy.mjs'),
  join(root, 'checks/causal-wait-boundary.mjs'),
  join(root, 'checks/unified-store-gate.mjs'),
  join(root, 'checks/tool-referential-integrity.mjs'),
  join(root, 'checks/provider-leak-gate.mjs'),
  join(root, 'checks/llm-facing-format-gate.mjs'),
  join(root, 'checks/language-parity-gate.mjs'),
  join(root, 'checks/js-boundary-gate.mjs'),
  join(root, 'checks/shard-edge-audit.mjs'),
  join(root, 'checks/ablation-manifest.mjs'),
  join(root, 'checks/clean-break-symbols.mjs'),
]

export async function runChecks(options = {}) {
  const checkList = options.checks ?? checks
  if (options.checks == null) {
    const gap = registrationGap(checkList)
    if (gap) throw new Error(gap.message)
  }
  const ctx = options.context ?? createCheckContext({ root: options.root })
  const allIssues = []

  for (const script of checkList) {
    const fileUrl = pathToFileURL(resolve(script)).href
    const mod = await import(fileUrl)
    if (typeof mod.check !== 'function') {
      throw new Error(`Gate does not export check(context): ${script}`)
    }
    const result = await mod.check(ctx)
    const issues = [...(result?.issues ?? [])]
    if (script.replace(/\\/g, '/').endsWith('checks/clean-break-symbols.mjs') && result?.ran !== true) {
      issues.push({
        code: 'scan-not-run',
        path: script,
        message: 'clean-break symbol scan did not run',
      })
    }
    if (issues.length > 0) {
      allIssues.push({ script, issues })
    }
  }

  return {
    issues: allIssues.flatMap((entry) => entry.issues),
    byGate: allIssues,
  }
}

export async function main(argv = process.argv.slice(2), { checkList = checks, rootDir } = {}) {
  if (argv.includes('--list')) {
    console.log('Registered checks:')
    for (let i = 0; i < checkList.length; i++) {
      console.log(`  ${i + 1}. ${checkList[i]}`)
    }
    return 0
  }

  try {
    const { issues, byGate } = await runChecks({ checks: checkList, root: rootDir })
    if (issues.length === 0) {
      return 0
    }

    console.error(`check.mjs: FAILED with ${issues.length} issue(s) across ${byGate.length} gate(s)\n`)
    for (const { script, issues: gateIssues } of byGate) {
      console.error(`Gate: ${script} (${gateIssues.length} issue(s))`)
      for (const issue of gateIssues) {
        const loc = issue.path ? `${issue.path}${issue.line ? `:${issue.line}` : ''} ` : ''
        console.error(`  - ${loc}[${issue.code ?? 'error'}] ${issue.message}`)
      }
      console.error('')
    }
    return 1
  } catch (err) {
    console.error(`check.mjs: fatal error: ${err.message}`)
    return 1
  }
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href
if (isMain) {
  main(process.argv.slice(2)).then((code) => {
    process.exit(code)
  }).catch((err) => {
    console.error(`check.mjs unhandled: ${err}`)
    process.exit(1)
  })
}
