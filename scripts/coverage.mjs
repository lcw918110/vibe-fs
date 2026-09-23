#!/usr/bin/env node
// scripts/coverage.mjs — dedicated coverage runner using c8 over existing unit runner.
//
// 1. assertBuildFresh()
// 2. create .fable-build/coverage/<run-id>/{raw,report}
// 3. spawn unit runner with NODE_V8_COVERAGE pointing to raw directory
// 4. run c8 report --all --src dist/ with json and text reporters into report directory
// 5. verify coverage denominator against production modules (dist/**/*.js minus fable_modules)
// 6. verify input/output freshness has not drifted (INPUT_CHANGED check)
// 7. fail if test runner failed, or if coverage was corrupted/drifted

import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, readdirSync, realpathSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  assertBuildFresh,
  collectCompilerInputs,
  collectGeneratedInputs,
  collectArtifactInputs,
  computeDigest,
} from './lib/build-state.mjs'
import { walk } from './lib/walk.mjs'
import {
  selectProductionModules,
  verifyCoverageDenominator,
} from '../requirements/verification-system/tests/support/coverage-policy.mjs'

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

export async function runCoverage(options = {}) {
  const resolvedRoot = path.resolve(options.root ?? REPO_ROOT)
  const root = existsSync(resolvedRoot) ? realpathSync(resolvedRoot) : resolvedRoot
  const distDir = path.resolve(root, options.distDir ?? 'dist')
  const unitRunnerScript = options.unitRunnerScript ?? path.join(root, 'requirements/verification-system/tests/run.mjs')
  const c8Bin = options.c8Bin ?? path.join(root, 'node_modules/c8/bin/c8.js')
  const runId = options.runId ?? `${Date.now()}-${process.pid}`
  const baseCoverageDir = options.coverageDir ?? path.join(root, '.fable-build/coverage')
  const runDir = path.join(baseCoverageDir, runId)
  const rawDir = path.join(runDir, 'raw')
  const reportDir = path.join(runDir, 'report')
  const env = options.env ?? process.env

  // 1. Freshness check before starting
  let initialFreshness
  try {
    if (options.assertBuildFreshFn) {
      initialFreshness = options.assertBuildFreshFn({ root })
    } else {
      initialFreshness = assertBuildFresh({ root })
    }
  } catch (err) {
    console.error(`coverage: build is not fresh: ${err.message}`)
    return { ok: false, code: 'BUILD_NOT_FRESH', error: err }
  }

  // Record initial input digests for INPUT_CHANGED check
  const collectInputs = options.collectInputsFn ?? (() => ({
    compiler: computeDigest(collectCompilerInputs(root)),
    generated: computeDigest(collectGeneratedInputs(root)),
    artifact: computeDigest(collectArtifactInputs(root)),
  }))
  const initialDigests = collectInputs()

  mkdirSync(rawDir, { recursive: true })
  mkdirSync(reportDir, { recursive: true })

  // 2. Spawn unit runner with NODE_V8_COVERAGE=rawDir
  console.log(`coverage: running tests with NODE_V8_COVERAGE in ${rawDir}...`)
  const runnerArgs = options.runnerArgs ?? [unitRunnerScript]
  const runnerResult = spawnSync(process.execPath, runnerArgs, {
    cwd: root,
    stdio: options.silent ? 'pipe' : 'inherit',
    env: {
      UNIT_VERDICT_SILENCE_MS: process.env.UNIT_VERDICT_SILENCE_MS ?? '20000',
      ...env,
      NODE_V8_COVERAGE: rawDir,
    },
  })

  // Check if runner crashed before starting or exited non-zero
  if (runnerResult.error) {
    console.error(`coverage: test runner failed to execute: ${runnerResult.error.message}`)
    return { ok: false, code: 'COVERAGE_RUNNER_ERROR', error: runnerResult.error }
  }

  const rawEntries = existsSync(rawDir) ? readdirSync(rawDir) : []
  if (rawEntries.length === 0) {
    console.error(`coverage: no raw coverage data collected in ${rawDir}`)
    return { ok: false, code: 'RAW_COVERAGE_MISSING', status: runnerResult.status ?? 1 }
  }

  // Verify that all raw coverage JSON files are valid JSON and not empty/corrupted
  for (const file of rawEntries) {
    try {
      JSON.parse(readFileSync(path.join(rawDir, file), 'utf8'))
    } catch (err) {
      console.error(`coverage: raw coverage file is corrupted: ${file} (${err.message})`)
      return { ok: false, code: 'RAW_COVERAGE_CORRUPT', file, error: err }
    }
  }

  // 3. Run c8 report
  // c8 report --all --src dist/ --reporter=json --reporter=text --report-dir <reportDir> --temp-directory <rawDir>
  const srcRel = path.relative(root, distDir)
  const c8Args = [
    c8Bin,
    'report',
    '--all',
    '--src',
    srcRel,
    '--reporter=json',
    '--reporter=text',
    '--report-dir',
    reportDir,
    '--temp-directory',
    rawDir,
    '--exclude',
    'resources/**',
    '--exclude',
    '**/fable_modules/**',
    '--exclude',
    '**/tests/**',
    '--exclude',
    '**/scripts/**',
    '--exclude',
    'test.js',
    '--exclude',
    'child.js',
    '--exclude',
    '**/node_modules/**',
  ]

  const c8Result = spawnSync(process.execPath, c8Args, {
    cwd: root,
    stdio: options.silent ? 'pipe' : 'inherit',
    env,
  })

  if (c8Result.error || (c8Result.status !== 0 && c8Result.status !== null)) {
    console.error(`coverage: c8 report generation failed`)
    return { ok: false, code: 'C8_REPORT_ERROR', error: c8Result.error, status: c8Result.status }
  }

  // 4. Verify denominator against dist files
  const coverageJsonPath = path.join(reportDir, 'coverage-final.json')
  if (!existsSync(coverageJsonPath)) {
    console.error(`coverage: coverage-final.json not found at ${coverageJsonPath}`)
    return { ok: false, code: 'COVERAGE_JSON_MISSING' }
  }

  let coverageJson
  try {
    coverageJson = JSON.parse(readFileSync(coverageJsonPath, 'utf8'))
  } catch (err) {
    console.error(`coverage: coverage-final.json is corrupt: ${err.message}`)
    return { ok: false, code: 'COVERAGE_JSON_CORRUPT', error: err }
  }

  const reportedAbsFiles = Object.keys(coverageJson)
  const reportedRelFiles = reportedAbsFiles.map((p) => {
    const realP = existsSync(p) ? realpathSync(p) : p
    return path.relative(root, realP).replace(/\\/g, '/')
  })

  // Verify denominator matches build receipt outputs
  let manifest
  try {
    const mPath = path.join(root, '.fable-build/build-manifest.json')
    if (existsSync(mPath)) {
      manifest = JSON.parse(readFileSync(mPath, 'utf8'))
    }
  } catch {}
  if (manifest && manifest.outputs) {
    const receiptProdModules = selectProductionModules(
      Object.keys(manifest.outputs).map((k) => path.join('dist', k).replace(/\\/g, '/')),
    )
    const receiptCheck = verifyCoverageDenominator(reportedRelFiles, receiptProdModules)
    if (!receiptCheck.ok) {
      console.error(`coverage: reported files do not match build receipt outputs`)
      return { ok: false, code: 'RECEIPT_DENOMINATOR_MISMATCH', details: receiptCheck }
    }
  }

  const allDistJs = existsSync(distDir) ? walk(distDir, ['.js']) : []
  const expectedProdModules = selectProductionModules(allDistJs).map((p) =>
    path.relative(root, p).replace(/\\/g, '/'),
  )

  const denominatorCheck = verifyCoverageDenominator(reportedRelFiles, expectedProdModules)
  if (!denominatorCheck.ok) {
    console.error(`coverage: production denominator mismatch!`)
    if (denominatorCheck.missing.length > 0) {
      console.error(`  missing from coverage: ${denominatorCheck.missing.slice(0, 10).join(', ')}`)
    }
    if (denominatorCheck.extra.length > 0) {
      console.error(`  extra in coverage: ${denominatorCheck.extra.slice(0, 10).join(', ')}`)
    }
    return { ok: false, code: 'DENOMINATOR_MISMATCH', details: denominatorCheck }
  }

  // 5. INPUT_CHANGED check: verify production files were not modified during run
  const currentDigests = collectInputs()
  if (
    currentDigests.compiler !== initialDigests.compiler ||
    currentDigests.generated !== initialDigests.generated ||
    currentDigests.artifact !== initialDigests.artifact
  ) {
    console.error(`coverage: input files changed during coverage run (INPUT_CHANGED):`)
    if (currentDigests.compiler !== initialDigests.compiler)
      console.error(`  compiler: ${initialDigests.compiler} -> ${currentDigests.compiler}`)
    if (currentDigests.generated !== initialDigests.generated)
      console.error(`  generated: ${initialDigests.generated} -> ${currentDigests.generated}`)
    if (currentDigests.artifact !== initialDigests.artifact)
      console.error(`  artifact: ${initialDigests.artifact} -> ${currentDigests.artifact}`)
    return { ok: false, code: 'INPUT_CHANGED' }
  }

  // 6. Test runner status check: if test failed, overall coverage fails (exit non-zero)
  if (runnerResult.status !== 0) {
    console.error(`coverage: unit tests failed (status ${runnerResult.status})`)
    return { ok: false, code: 'TESTS_FAILED', status: runnerResult.status }
  }

  console.log(`\ncoverage: OK (${expectedProdModules.length} production modules covered, report in ${reportDir})`)
  return {
    ok: true,
    runId,
    reportDir,
    rawDir,
    productionModulesCount: expectedProdModules.length,
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const result = await runCoverage()
  if (!result.ok) {
    process.exit(1)
  }
}
