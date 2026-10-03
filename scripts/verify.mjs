#!/usr/bin/env node
// verify.mjs — the fixed verification pipeline. `verify:daily` is the developer
// entry; `verify:release` adds release-only proofs (compiler-boundary canary,
// repo-wide envelope oracle, Long Stroke, real package) after the shared
// format/check/build/unit phases.

import { spawn } from 'node:child_process'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import fs from 'node:fs'
import { collectVerificationInputs, computeDigest, diffVerificationInputs } from './lib/build-state.mjs'

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

// 确保在沙箱和 CI 容器中，WIREIT_PARALLEL 始终有合理的正整数基准值，防止 WorkerPool got 0
if (!process.env.WIREIT_PARALLEL || Number(process.env.WIREIT_PARALLEL) < 1) {
  process.env.WIREIT_PARALLEL = String(Math.max(1, os.cpus()?.length || 4))
}

export function getTestEnv({ verbose = false, hostEnv = process.env, extra = {} } = {}) {
  const { TESTS_MJS_FILES, ...cleanEnv } = hostEnv
  return {
    ...cleanEnv,
    ...(verbose ? { NODE_TEST_VERBOSE: '1' } : {}),
    WXS_E2E_QUIET: '1',
    ...extra,
  }
}

export function verificationSteps({ root = ROOT, release = false, verbose = false, env: hostEnv = process.env } = {}) {
  const steps = [
    {
      label: 'format:check',
      cmd: 'npm',
      argv: ['run', 'format:check'],
      env: hostEnv,
    },
    {
      label: 'check',
      cmd: 'npm',
      argv: ['run', 'check'],
      env: hostEnv,
    },
    {
      label: 'build',
      cmd: process.execPath,
      argv: [path.join(root, 'scripts/build.mjs'), ...(release ? ['--clean'] : [])],
      env: hostEnv,
    },
    {
      label: 'unit',
      cmd: process.execPath,
      argv: [path.join(root, 'requirements/verification-system/tests/run.mjs')],
      env: getTestEnv({
        verbose,
        hostEnv,
        extra: {
          ...(process.env.UNIT_VERDICT_SILENCE_MS ? { UNIT_VERDICT_SILENCE_MS: process.env.UNIT_VERDICT_SILENCE_MS } : {}),
        },
      }),
    },
    {
      label: 'integration',
      cmd: process.execPath,
      argv: [path.join(root, 'requirements/verification-system/tests/integration/run.mjs')],
      timeoutMs: release ? 1_200_000 : 600_000,
      env: getTestEnv({ verbose, hostEnv, extra: release ? { WXS_RELEASE: '1' } : {} }),
    },
  ]

  if (release) {
    steps.push(
      {
        label: 'e2e',
        cmd: process.execPath,
        argv: [path.join(root, 'requirements/verification-system/tests/014.test.mjs')],
        timeoutMs: 1_500_000,
        env: getTestEnv({ verbose, hostEnv, extra: { WXS_TIER_RELEASE: '1' } }),
      },
      {
        label: 'package',
        cmd: process.execPath,
        argv: [path.join(root, 'scripts/verify-package.mjs')],
        timeoutMs: 600_000,
        env: getTestEnv({ verbose, hostEnv }),
      },
    )
  }

  return steps
}

function defaultRunStepFactory(root, verbose, output) {
  return async function defaultRunStep({
    label,
    cmd = process.execPath,
    argv,
    env,
    timeoutMs = 600_000,
    logDir,
    cwd = root,
  }) {
    const startedAt = Date.now()
    const stepLog = path.join(logDir, `${label.replace(/:/g, '-')}.log`)
    const logStream = fs.createWriteStream(stepLog, { flags: 'w' })

    let killed = false
    const timer = setTimeout(() => {
      killed = true
      child.kill('SIGKILL')
    }, timeoutMs)
    timer.unref()

    let child
    try {
      const childEnv = env ? { ...env } : (() => {
        const { TESTS_MJS_FILES, ...cleanEnv } = process.env
        return cleanEnv
      })()
      if (childEnv.WXS_RELEASE === undefined) {
        childEnv.WXS_RELEASE = '0'
      }
      if (!childEnv.WIREIT_PARALLEL || Number(childEnv.WIREIT_PARALLEL) < 1) {
        childEnv.WIREIT_PARALLEL = String(Math.max(1, os.cpus()?.length || 4))
      }
      child = spawn(cmd, argv, {
        cwd,
        stdio: ['ignore', 'pipe', 'pipe'],
        env: childEnv,
      })
    } catch (spawnError) {
      clearTimeout(timer)
      logStream.end()
      return {
        label,
        exitCode: 1,
        signal: null,
        killed: false,
        durationMs: Date.now() - startedAt,
        logPath: path.relative(root, stepLog),
        ok: false,
        error: spawnError,
      }
    }

    child.stdout.on('data', (chunk) => {
      logStream.write(chunk)
      if (verbose) output.write(chunk)
    })
    child.stderr.on('data', (chunk) => {
      logStream.write(chunk)
      process.stderr.write(chunk)
    })

    const exit = await new Promise((resolveExit) => {
      child.on('error', (error) => resolveExit({ code: 1, signal: null, error }))
      child.on('close', (code, signal) => resolveExit({ code: code ?? 1, signal }))
    })

    clearTimeout(timer)
    logStream.end()

    const ok = exit.code === 0 && !exit.signal && !killed

    if (!ok) {
      // 无论 verbose 与否，失败时自动 Dump 诊断日志尾部到控制台，消除 CI 无控制台输出的信息黑洞
      try {
        const content = fs.readFileSync(stepLog, 'utf8')
        const lines = content.trim().split('\n')
        const tailLines = lines.slice(-60).join('\n')
        if (tailLines) {
          process.stderr.write(`\n--- [${label}] 错误诊断日志转储 (最后 ${Math.min(60, lines.length)} 行) ---\n`)
          process.stderr.write(tailLines + '\n')
          process.stderr.write(`--- [${label}] 完整日志见: ${path.relative(root, stepLog)} ---\n\n`)
        }
      } catch {}
    }

    return {
      label,
      exitCode: exit.code ?? 1,
      signal: exit.signal,
      killed,
      durationMs: Date.now() - startedAt,
      logPath: path.relative(root, stepLog),
      ok,
      ...(exit.error ? { error: exit.error } : {}),
    }
  }
}

function allocateRunLogDir(baseLogDir) {
  fs.mkdirSync(baseLogDir, { recursive: true })
  const baseStamp = new Date().toISOString().replace(/[:.]/g, '-')
  let stamp = baseStamp
  let seq = 1
  while (fs.existsSync(path.join(baseLogDir, stamp))) {
    stamp = `${baseStamp}-${seq++}`
  }
  const runLogDir = path.join(baseLogDir, stamp)
  fs.mkdirSync(runLogDir, { recursive: true })

  const latestLink = path.join(baseLogDir, 'latest')
  try {
    fs.unlinkSync(latestLink)
  } catch {}
  try {
    fs.symlinkSync(stamp, latestLink, 'dir')
  } catch {}

  return runLogDir
}

export async function verify({
  root = ROOT,
  release = false,
  verbose = false,
  profile = false,
  runStep: runStepOverride,
  output = process.stdout,
  logDirectory,
  env: hostEnv = process.env,
} = {}) {
  const resolvedRoot = path.resolve(root)
  const baseLogDir = logDirectory ? path.resolve(logDirectory) : path.join(resolvedRoot, '.fable-build', 'verify-logs')
  const runLogDir = allocateRunLogDir(baseLogDir)

  const stepRunner = runStepOverride ?? defaultRunStepFactory(resolvedRoot, verbose, output)
  const plannedSteps = verificationSteps({ root: resolvedRoot, release, verbose, env: hostEnv })
  const mode = release ? 'release' : 'daily'
  const runStart = Date.now()

  let beforeInputs
  try {
    beforeInputs = collectVerificationInputs(resolvedRoot)
  } catch (err) {
    const wallMs = Date.now() - runStart
    output.write(`\n=== verify: ${mode}  inputs=error ===\n`)
    output.write(`FAIL: input collection error: ${err.message}\n`)
    return {
      mode,
      steps: plannedSteps.map((p) => ({ label: p.label, status: 'not-run' })),
      outcome: 'fail',
      failureReason: `input-collection-failed: ${err.message}`,
      wallMs,
      logDirectory: runLogDir,
      exitCode: 1,
    }
  }

  const initialDigest = computeDigest(beforeInputs).slice(0, 16)
  output.write(`\n=== verify: ${mode}  inputs=${initialDigest} ===\n`)

  const stepResults = []
  let pipelineFailed = false
  let failureReason
  let inputChanges

  for (let i = 0; i < plannedSteps.length; i++) {
    const stepPlan = plannedSteps[i]
    if (pipelineFailed) {
      stepResults.push({ label: stepPlan.label, status: 'not-run' })
      continue
    }

    let res
    try {
      res = await stepRunner({
        ...stepPlan,
        logDir: runLogDir,
        cwd: resolvedRoot,
      })
    } catch (err) {
      res = {
        label: stepPlan.label,
        ok: false,
        exitCode: 1,
        signal: null,
        durationMs: 0,
        error: err,
      }
    }

    const durationMs = res.durationMs ?? 0
    if (res.ok) {
      stepResults.push({
        label: res.label ?? stepPlan.label,
        stage: res.label ?? stepPlan.label,
        status: 'ok',
        exitCode: res.exitCode ?? 0,
        signal: res.signal ?? null,
        durationMs,
        wallMs: durationMs,
      })
      output.write(`  ${stepPlan.label.padEnd(14)} OK  ${(durationMs / 1000).toFixed(1)}s\n`)

      try {
        const currentInputs = collectVerificationInputs(resolvedRoot)
        const diff = diffVerificationInputs(beforeInputs, currentInputs)
        if (!diff.equal) {
          pipelineFailed = true
          inputChanges = diff
          failureReason = `inputs-changed:${diff.reason}`
        }
      } catch (err) {
        pipelineFailed = true
        failureReason = `after-input-collection-failed: ${err.message}`
      }
    } else {
      pipelineFailed = true
      failureReason = `step-failed:${stepPlan.label}`
      stepResults.push({
        label: res.label ?? stepPlan.label,
        stage: res.label ?? stepPlan.label,
        status: 'failed',
        exitCode: res.exitCode ?? 1,
        signal: res.signal ?? null,
        durationMs,
        wallMs: durationMs,
        ...(res.error ? { error: res.error } : {}),
      })
      output.write(`  ${stepPlan.label.padEnd(14)} FAIL(${res.exitCode ?? 1})  ${(durationMs / 1000).toFixed(1)}s\n`)
    }
  }

  let afterInputs
  if (!inputChanges) {
    try {
      afterInputs = collectVerificationInputs(resolvedRoot)
      const diff = diffVerificationInputs(beforeInputs, afterInputs)
      if (!diff.equal) {
        inputChanges = diff
        if (!pipelineFailed) {
          pipelineFailed = true
          failureReason = `inputs-changed:${diff.reason}`
        }
      }
    } catch (err) {
      if (!pipelineFailed) {
        pipelineFailed = true
        failureReason = `after-input-collection-failed: ${err.message}`
      }
    }
  }

  const wallMs = Date.now() - runStart
  if (pipelineFailed) {
    if (inputChanges) {
      output.write(`FAIL: inputs changed mid-run (${inputChanges.reason}) — re-run verify on a stable tree\n`)
    }
    output.write(`FAIL  verify ${mode}  ${(wallMs / 1000).toFixed(1)}s\n`)
    if (profile) {
      output.write(`\nFAIL verify ${mode} · 各阶段耗时:\n`)
      for (const s of stepResults) {
        output.write(`  ${s.label.padEnd(14)} ${s.wallMs ?? 0}ms\n`)
      }
      output.write(`  ${JSON.stringify(stepResults.map((s) => ({ stage: s.label, wallMs: s.wallMs ?? 0 })))}\n`)
    }
    return {
      mode,
      steps: stepResults,
      outcome: 'fail',
      ...(failureReason ? { failureReason } : {}),
      ...(inputChanges ? { inputChanges } : {}),
      wallMs,
      logDirectory: runLogDir,
      exitCode: 1,
      profile: stepResults.map((s) => ({ stage: s.label, wallMs: s.wallMs ?? 0 })),
    }
  }

  output.write(`PASS  verify ${mode}  ${(wallMs / 1000).toFixed(1)}s\n`)
  if (profile) {
    output.write(`\nPASS verify ${mode} · 各阶段耗时:\n`)
    for (const s of stepResults) {
      output.write(`  ${s.label.padEnd(14)} ${s.wallMs ?? 0}ms\n`)
    }
    output.write(`  ${JSON.stringify(stepResults.map((s) => ({ stage: s.label, wallMs: s.wallMs ?? 0 })))}\n`)
  }
  return {
    mode,
    steps: stepResults,
    outcome: 'pass',
    wallMs,
    logDirectory: runLogDir,
    exitCode: 0,
    profile: stepResults.map((s) => ({ stage: s.label, wallMs: s.wallMs ?? 0 })),
  }
}

async function main() {
  const argv = process.argv.slice(2)
  let release = false
  let verbose = false
  let profile = false

  for (const arg of argv) {
    if (arg === '--release') {
      release = true
    } else if (arg === '--verbose') {
      verbose = true
    } else if (arg === '--profile') {
      profile = true
    } else if (arg === '-h' || arg === '--help') {
      process.stdout.write('Usage: node scripts/verify.mjs [--release] [--verbose] [--profile]\n')
      process.exit(0)
    } else {
      process.stderr.write(`Unknown option: ${arg}\n`)
      process.exit(2)
    }
  }

  const result = await verify({ release, verbose, profile, env: process.env })
  process.exitCode = result.exitCode
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  await main()
}
