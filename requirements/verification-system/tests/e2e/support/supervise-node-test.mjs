/**
 * Out-of-process supervisor for a node:test child (verification-system-006 silence criterion).
 *
 * Shared by unit / integration / package runners. The silence window is fed only by
 * test verdicts; stdout/stderr/diagnostics are background and never renew.
 */

import { execFileSync, spawn } from 'node:child_process'
import { relative, resolve } from 'node:path'
import { performance } from 'node:perf_hooks'
import { setTimeout as delay } from 'node:timers/promises'
import { fileURLToPath } from 'node:url'

import { Watchdog } from './watchdog.js'
import { PROCESS_TREE_TIMEOUT_MS, SIGKILL_GRACE_MS, SUITE_BACKSTOP_MS } from './time-budget.js'
import { classifyVerdict } from '../../support/verdict-feed.mjs'
import { isFileCompletionEvent, testEntryFile } from '../../support/test-run-state.mjs'

export const NODE_TEST_INNER = fileURLToPath(new URL('../../support/run-inner.mjs', import.meta.url))

function liveGroupMembers(pgid, timeout = PROCESS_TREE_TIMEOUT_MS) {
  if (process.platform !== 'linux' && process.platform !== 'darwin') {
    throw new Error(`process-group verification is unsupported on ${process.platform}`)
  }
  const output = execFileSync('ps', ['-eo', 'pid=,pgid=,stat='], { encoding: 'utf8', timeout })
  if (!output.trim()) throw new Error('process inspection returned no records')
  return output.trim().split('\n').flatMap((line) => {
    const fields = /^\s*(\d+)\s+(\d+)\s+(\S+)\s*$/.exec(line)
    if (!fields) throw new Error(`unparseable process record: ${line}`)
    return Number(fields[2]) === pgid && !/^[ZX]/.test(fields[3]) ? [Number(fields[1])] : []
  })
}

async function verifyExitedGroup(pgid, logPrefix) {
  try {
    const members = liveGroupMembers(pgid)
    if (members.length === 0) return true
    console.error(`${logPrefix}: residual process group ${pgid} after inner exit; surviving pids: ${members.join(', ')}`)
  } catch (error) {
    console.error(`${logPrefix}: could not verify process group ${pgid} after inner exit: ${error.message}`)
  }

  // Only this supervisor owns the detached runner group. A clean verdict ledger
  // cannot excuse a descendant left behind, even when reclamation succeeds.
  try {
    try {
      process.kill(-pgid, 'SIGKILL')
    } catch (error) {
      if (error.code !== 'ESRCH') throw error
    }
    const deadline = Date.now() + SIGKILL_GRACE_MS
    let members = liveGroupMembers(pgid, SIGKILL_GRACE_MS)
    while (members.length > 0 && Date.now() < deadline) {
      await delay(20)
      members = liveGroupMembers(pgid, Math.max(1, Math.min(PROCESS_TREE_TIMEOUT_MS, deadline - Date.now())))
    }
    if (members.length > 0) throw new Error(`surviving pids: ${members.join(', ')}`)
    console.error(`${logPrefix}: process group ${pgid} reclaimed; the run still fails`)
  } catch (error) {
    console.error(`${logPrefix}: process group ${pgid} reclamation failed: ${error.message}`)
  }
  return false
}

/**
 * @param {{
 *   files: string[],
 *   label: string,
 *   silenceMs: number,
 *   env?: NodeJS.ProcessEnv,
 *   logPrefix?: string,
 *   inner?: string,
 * }} opts
 * @returns {Promise<void>}
 */
export async function superviseNodeTest({
  files,
  label,
  silenceMs,
  env = process.env,
  logPrefix = 'runner',
  inner = NODE_TEST_INNER,
  // When true, suite failures throw instead of process.exit, so a parent
  // orchestrator (integration/run.mjs) can print its own group-level line
  // and continue to its final summary. Default false: existing callers
  // (unit / package runners, supervision tests) keep exit semantics.
  throwOnFailure = false,
}) {
  if (!Array.isArray(files) || files.length === 0) {
    console.error(`${logPrefix}: no test files given`)
    process.exit(1)
  }
  if (!Number.isFinite(silenceMs) || silenceMs <= 0) {
    console.error(`${logPrefix}: silenceMs must be a positive number, got ${silenceMs}`)
    process.exit(1)
  }

  const fail = (code = 1) => {
    if (throwOnFailure) throw new Error(`${logPrefix}: supervised suite failed (exit ${code})`)
    process.exit(code)
  }

  console.error(`${logPrefix}: ${files.length} test file(s), ${silenceMs}ms verdict-silence window`)

  // Absolute paths: test:complete reports absolute `data.file`.
  const outstanding = new Set(files.map((file) => resolve(file)))
  let runnerSummary = null
  let drained = false
  let runnerError = null
  let child

  const watchdog = new Watchdog({
    timeoutMs: silenceMs,
    label,
    onTimeout: () => {
      if (outstanding.size === 0) {
        console.error(
          `${logPrefix}: all planned files completed but the child would not exit — ` +
            'the runner lifecycle is still open',
        )
      } else {
        console.error(
          `${logPrefix}: ${outstanding.size} file(s) had not reported completion: ` +
            `${[...outstanding].map((file) => relative(process.cwd(), file)).join(', ')}`,
        )
      }
      console.error(runnerSummary
        ? `${logPrefix}: ${runnerSummary.passed} passed, ${runnerSummary.failed} failed before the silence`
        : `${logPrefix}: verdict counts unavailable; no authoritative summary before the silence`)
      try {
        process.stderr.write('')
      } catch {}
      try {
        if (child?.pid) process.kill(-child.pid, 'SIGKILL')
      } catch {}
    },
  })

  child = spawn(process.execPath, [inner, ...files], {
    stdio: ['ignore', 'inherit', 'inherit', 'ipc'],
    detached: true,
    env,
  })

  let backstopFired = false
  const backstop = setTimeout(() => {
    backstopFired = true
    console.error(`${logPrefix}: suite exceeded the ${SUITE_BACKSTOP_MS}ms physical backstop`)
    try {
      if (child?.pid) process.kill(-child.pid, 'SIGKILL')
    } catch {}
  }, SUITE_BACKSTOP_MS)
  backstop.unref()

  child.on('message', (event) => {
    if (event?.type === 'runner:summary') {
      runnerSummary = event?.data
      return
    }
    if (event?.type === 'inner:drained') {
      drained = true
      return
    }
    if (event?.type === 'runner:error') {
      runnerError = event?.data
      return
    }
    if (isFileCompletionEvent(event)) {
      outstanding.delete(resolve(testEntryFile(event)))
    }

    const progress = classifyVerdict(event)
    if (progress !== null) watchdog.advance(progress)
  })

  const exit = await new Promise((resolveExit) => {
    child.on('exit', (code, signal) => resolveExit({ code, signal }))
    child.on('error', (error) => {
      console.error(`${logPrefix}: could not start the inner runner: ${error.message}`)
      resolveExit({ code: 1, signal: null })
    })
  })

  watchdog.stop()
  clearTimeout(backstop)
  const groupVerificationStarted = performance.now()
  const processGroupClean = child.pid ? await verifyExitedGroup(child.pid, logPrefix) : false
  console.error(
    `${logPrefix}: post-exit group verification/reclamation: pid=${child.pid ?? 'none'}; ` +
      `elapsedMs=${(performance.now() - groupVerificationStarted).toFixed(3)}; accepted=${processGroupClean}`,
  )

  if (!runnerSummary && exit.signal === null) {
    console.error(`${logPrefix}: inner runner failed to provide authoritative summary`)
  }

  const passed = runnerSummary?.passed ?? 0
  const failed = runnerSummary?.failed ?? 0
  const leafDurations = runnerSummary?.leafDurations ?? []

  reportDurationDistribution(logPrefix, leafDurations)

  console.error(
    `\n${logPrefix}: ` +
      (runnerSummary ? `${passed} passed, ${failed} failed` : 'verdict counts unavailable; no authoritative summary') +
      `; ${files.length - outstanding.size}/${files.length} planned file(s) completed`,
  )

  if (failed > 0) fail(1)

  if (backstopFired) fail(1)

  if (runnerError) {
    console.error(
      `${logPrefix}: inner runner failed with stream error: ${runnerError.message ?? runnerError.name ?? 'StreamError'}`,
    )
    fail(1)
  }

  if (exit.signal !== null) {
    console.error(
      `${logPrefix}: the inner runner died by ${exit.signal}; its verdicts describe an incomplete run`,
    )
    fail(1)
  }

  if (exit.code !== 0) {
    console.error(`${logPrefix}: inner runner exited ${exit.code}`)
    fail(exit.code ?? 1)
  }

  if (!runnerSummary) {
    fail(1)
  }

  if (!drained) {
    console.error(`${logPrefix}: the inner runner exited without draining its result stream`)
    fail(1)
  }

  if (outstanding.size > 0) {
    console.error(`${logPrefix}: incomplete run; no completion for ${[...outstanding].join(', ')}`)
    fail(1)
  }

  if (runnerSummary.cancelled > 0 || runnerSummary.containerFailures > 0) {
    console.error(`${logPrefix}: cancelled tests or failed containers prevent complete acceptance`)
    fail(1)
  }

  if (runnerSummary.skipped > 0 || runnerSummary.todo > 0) {
    console.error(`${logPrefix}: partial evidence; ${runnerSummary.skipped ?? 0} skipped, ${runnerSummary.todo ?? 0} TODO; excluded cases are not passes`)
  }

  if (runnerSummary.todo > 0) {
    if (env.WXS_ACCEPT_TODO === '1') {
      console.warn(`${logPrefix}: pending proof accepted under WXS_ACCEPT_TODO override (${runnerSummary.todo} TODO)`)
    } else {
      console.error(`${logPrefix}: pending proof prevents complete acceptance`)
      fail(1)
    }
  }

  if (!processGroupClean) fail(1)

  return { passed, failed }
}

/**
 * The tier's timing distribution, printed on every run.
 *
 * A suite that reports only pass/fail hides where its wall clock goes, and the first step of every
 * performance investigation then has to rebuild the measurement — which is why most never start.
 * Percentiles separate "uniformly slow" from "one slow tail", and the named top five say which
 * test to open.
 */
function reportDurationDistribution(logPrefix, durations) {
  if (durations.length === 0) return
  const ranked = [...durations].sort((left, right) => right.ms - left.ms)
  const total = durations.reduce((sum, entry) => sum + entry.ms, 0)
  const at = (fraction) => ranked[Math.min(ranked.length - 1, Math.floor(ranked.length * fraction))].ms
  const ms = (value) => `${Math.round(value)}ms`

  console.error(
    `${logPrefix}: ${durations.length} test(s), ${(total / 1000).toFixed(1)}s of test time; ` +
      `max ${ms(ranked[0].ms)}, p90 ${ms(at(0.1))}, median ${ms(at(0.5))}`,
  )
  // The named list only earns its lines when there is a tail worth opening: a tier whose whole
  // test time is under a second has no slowest test, it has noise.
  if (total < 1000) return
  for (const entry of ranked.slice(0, 5)) {
    console.error(`${logPrefix}:   ${ms(entry.ms).padStart(7)}  ${entry.name}`)
  }
}
