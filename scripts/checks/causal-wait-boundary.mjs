#!/usr/bin/env node
/**
 * Causal-wait architecture gate.
 *
 * causal-wait-003/004:
 *   - Fact and Journal carriers contain no executable causal-wait vocabulary.
 *   - Snapshot readers, registry implementation, diagnostic bridges and their
 *     file locator stay inside Execution/Session/Wait.
 *
 * Migration guards retained here:
 *   - critical migrated sites do not reintroduce bare TCS.Task awaits.
 */

import { existsSync, readFileSync, realpathSync, statSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { walk } from '../lib/walk.mjs'
import { maskFSharpTrivia } from '../lib/fsharp-source.mjs'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', '..')
const WAIT_OWNER = 'Execution/Session/Wait/'
const DIAGNOSTIC_COMPOSITION_ROOT = 'OpenCode/Plugin/PluginHostWiring.fs'
const DIAGNOSTIC_LOCATOR = 'causal-waits.json'
const normalize = (path) => path.replace(/\\/g, '/')

const DURABLE_VOCABULARY = [
  ['IWaitSnapshotReader', /\bIWaitSnapshotReader\b/],
  ['IWaitObserver', /\bIWaitObserver\b/],
  ['CausalWaitRegistry', /\bCausalWaitRegistry\b/],
  ['CausalWaitHub', /\bCausalWaitHub\b/],
  ['CausalWaitBridge', /\bCausalWaitBridge\b/],
  ['CausalWaitSurface', /\bCausalWaitSurface\b/],
  ['CausalAwait', /\bCausalAwait\b/],
  ['DiagnosticWaitSnapshot', /\bDiagnosticWaitSnapshot\b/],
  ['DiagnosticWaitExit', /\bDiagnosticWaitExit\b/],
  ['DiagnosticWait', /\bDiagnosticWait\b/],
  ['WaitKind', /\bWaitKind\b/],
  ['CausalWait', /\bCausalWait\b/],
]

const SNAPSHOT_READ_CAPABILITIES = [
  ['IWaitSnapshotReader', /\bIWaitSnapshotReader\b/],
  ['DiagnosticWaitSnapshot', /\bDiagnosticWaitSnapshot\b/],
  ['CausalWaitRegistry', /\bCausalWaitRegistry\b/],
  ['CausalWaitBridge', /\bCausalWaitBridge\b/],
  ['CausalWaitSurface', /\bCausalWaitSurface\b/],
  ['CausalWaitHub.reader', /\bCausalWaitHub\.reader\b/],
  ['CausalWaitHub.snapshot', /\bCausalWaitHub\.snapshot\b/],
  ['CausalWaitHub.read', /\bCausalWaitHub\.read\b/],
  ['CausalWaitHub.frontiers', /\bCausalWaitHub\.frontiers\b/],
  ['CausalWaitHub.writeToWorkspace', /\bCausalWaitHub\.writeToWorkspace\b/],
]

const firstToken = (rules, text) => rules.find(([, pattern]) => pattern.test(text))?.[0]

const isDurableCarrier = (relativePath) => {
  const parts = relativePath.split('/')
  const name = parts.at(-1)
  return parts.includes('Journal') || name === 'Fact.fs' || name === 'Facts.fs'
}

const durableViolation = (relativePath, token) =>
  `${relativePath}: causal-wait vocabulary "${token}" must not enter Fact/Journal`

const readViolation = (relativePath, token) =>
  `${relativePath}: diagnostics read capability "${token}" is confined to Execution/Session/Wait`

export function analyzeObservationBoundary(files) {
  if (!Array.isArray(files)) throw new TypeError('causal-wait-boundary: files must be an array')

  const violations = []
  for (const file of files) {
    if (typeof file?.rel !== 'string' || typeof file?.text !== 'string') {
      throw new TypeError('causal-wait-boundary: every file requires string rel and text fields')
    }

    const relativePath = normalize(file.rel)
    const executable = maskFSharpTrivia(file.text)

    if (isDurableCarrier(relativePath)) {
      const token = firstToken(DURABLE_VOCABULARY, executable)
      if (token !== undefined) {
        violations.push(durableViolation(relativePath, token))
        continue
      }
    }

    if (relativePath.startsWith(WAIT_OWNER)) continue

    const withoutDiagnosticInjection = relativePath === DIAGNOSTIC_COMPOSITION_ROOT
      ? executable.replace(/\bCausalWaitBridge\.target\b/g, '')
      : executable
    const capability = firstToken(SNAPSHOT_READ_CAPABILITIES, withoutDiagnosticInjection)
    if (capability !== undefined) {
      violations.push(readViolation(relativePath, capability))
      continue
    }

    if (/\bCausalWaitHub\b/.test(withoutDiagnosticInjection)) {
      violations.push(readViolation(relativePath, 'CausalWaitHub'))
      continue
    }

    if (file.text.includes(DIAGNOSTIC_LOCATOR)) {
      violations.push(readViolation(relativePath, DIAGNOSTIC_LOCATOR))
    }
  }

  return violations
}

export function collectCausalWaitBoundaryFiles(root = ROOT) {
  const sourceRoot = join(root, 'src/Wanxiangshu')
  if (!existsSync(sourceRoot) || !statSync(sourceRoot).isDirectory()) {
    throw new Error('causal-wait-boundary: required scan root missing: src/Wanxiangshu')
  }

  return [...walk(sourceRoot)]
    .filter((path) => path.endsWith('.fs'))
    .map((path) => ({
      rel: normalize(path.slice(sourceRoot.length + 1)),
      text: readFileSync(path, 'utf8'),
    }))
}

export function check(context) {
  const root = context?.root ?? ROOT
  let files
  if (context?.productionFiles) {
    const prefix = 'src/Wanxiangshu/'
    files = context.productionFiles()
      .filter(({ file }) => file.endsWith('.fs'))
      .map(({ file, text }) => ({
        rel: normalize(file.startsWith(prefix) ? file.slice(prefix.length) : file),
        text,
      }))
  } else {
    files = collectCausalWaitBoundaryFiles(root)
  }
  const problems = analyzeObservationBoundary(files)
  return {
    issues: problems.map((problem) => ({
      code: 'causal-wait-boundary-violation',
      message: problem,
    })),
    filesCount: files.length,
  }
}

export function runCli() {
  const { issues, filesCount } = check()
  if (issues.length > 0) {
    console.error('causal-wait-boundary FAILED:')
    for (const issue of issues) console.error(`  - ${issue.message}`)
    return 1
  }
  console.log(`causal-wait-boundary OK — ${filesCount} production files`)
  return 0
}

const isMainModule = (() => {
  try {
    return import.meta.url === pathToFileURL(realpathSync(process.argv[1])).href
  } catch {
    return false
  }
})()

if (isMainModule) {
  const code = runCli()
  if (code !== 0) process.exit(code)
}
