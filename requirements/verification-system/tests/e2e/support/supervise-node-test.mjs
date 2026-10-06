/**
 * Out-of-process supervisor for a node:test child (verification-system-006 silence criterion).
 *
 * Shared by unit / integration / package runners. The silence window is fed only by
 * test verdicts; stdout/stderr/diagnostics are background and never renew.
 */

import { execFileSync, spawn } from 'node:child_process'
import { mkdtempSync, realpathSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, relative, resolve } from 'node:path'
import { performance } from 'node:perf_hooks'
import { setTimeout as delay } from 'node:timers/promises'
import { fileURLToPath } from 'node:url'

import { Watchdog } from './watchdog.js'
import { PROCESS_TREE_TIMEOUT_MS, SIGKILL_GRACE_MS, SUITE_BACKSTOP_MS } from './time-budget.js'
import { classifyVerdict } from '../../support/verdict-feed.mjs'
import { isFileCompletionEvent, testEntryFile } from '../../support/test-run-state.mjs'
import { validateWorkerCostSnapshot } from '../../support/worker-cost-observation.mjs'

export const NODE_TEST_INNER = fileURLToPath(new URL('../../support/run-inner.mjs', import.meta.url))

export function createFileWaitTracker(files) {
  const entries = new Map(files.map((file) => [resolve(file), { phase: 'queued', lastVerdict: null }]))
  return {
    observe(event) {
      const starts = event?.type === 'runner:file-start'
      const drains = event?.type === 'runner:file-drained'
      const testStarts = event?.type === 'test:start'
      const verdict = classifyVerdict(event)
      if (!starts && !drains && !testStarts && !verdict?.blocking) return
      const entryFile = starts || drains ? event?.data?.entryFile : testEntryFile(event)
      if (typeof entryFile !== 'string' || entryFile.length === 0) {
        throw new Error('File lifecycle event requires entryFile')
      }
      const file = resolve(entryFile)
      const entry = entries.get(file)
      if (!entry) throw new Error(`File lifecycle event names unplanned entry ${file}`)
      const expected = starts ? 'queued' : 'active'
      if (entry.phase !== expected) {
        throw new Error(`File lifecycle event ${event.type} received while ${file} is ${entry.phase}`)
      }
      if (starts) entry.phase = 'active'
      else if (drains) entry.phase = 'drained'
      else if (testStarts) entry.lastStart = { ...event.data }
      else entry.lastVerdict = verdict.reason
    },
    snapshot() {
      const snapshot = { queued: [], active: [], drained: [] }
      for (const [file, entry] of entries) {
        if (entry.phase === 'active') snapshot.active.push({ file, lastVerdict: entry.lastVerdict,
          ...(entry.lastStart ? { lastStart: { ...entry.lastStart } } : {}),
        })
        else snapshot[entry.phase].push(file)
      }
      return snapshot
    },
  }
}

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

function inspectProcesses({ timeout }) {
  return execFileSync('ps', ['-eo', 'pid=,ppid=,pgid=,stat='], { encoding: 'utf8', timeout })
}

function processRecords(deadline, phase, inspect = inspectProcesses) {
  const remaining = deadline - Date.now()
  if (remaining <= 0) throw new Error('Owned termination observation deadline expired')
  const output = inspect({ phase, timeout: Math.min(PROCESS_TREE_TIMEOUT_MS, remaining) })
  if (!output.trim()) throw new Error('process inspection returned no records')
  return output.trim().split('\n').map(line => {
    const fields = /^\s*(\d+)\s+(\d+)\s+(\d+)\s+(\S+)\s*$/.exec(line)
    if (!fields) throw new Error(`unparseable process record: ${line}`)
    return { pid: Number(fields[1]), parent: Number(fields[2]), group: Number(fields[3]), state: fields[4] }
  }).filter(record => !/^[ZX]/.test(record.state))
}

function rememberDescendantGroups(pgid, records, groups) {
  const descendants = new Set(records.filter(record => record.group === pgid).map(record => record.pid))
  let added
  do {
    added = false
    for (const record of records) {
      if (descendants.has(record.parent) && !descendants.has(record.pid)) {
        descendants.add(record.pid)
        added = true
      }
    }
  } while (added)
  for (const record of records) {
    if (descendants.has(record.pid) && record.group !== pgid) groups.add(record.group)
  }
}

async function freezeAndCaptureDescendantGroups(pgid, deadline, inspect, groups) {
  try {
    process.kill(-pgid, 'SIGSTOP')
  } catch (error) {
    if (error.code === 'ESRCH') throw new Error('The owned inner group disappeared before frozen descendant capture', { cause: error })
    throw error
  }
  while (true) {
    const records = processRecords(deadline, 'freeze-confirmation', inspect)
    rememberDescendantGroups(pgid, records, groups)
    const owned = records.filter(record => record.group === pgid)
    if (owned.length === 0) throw new Error('The owned inner group was absent from the frozen descendant snapshot')
    if (owned.every(record => /^[Tt]/.test(record.state))) return
    if (Date.now() >= deadline) throw new Error(`Could not observe the frozen inner process group ${pgid}`)
    await delay(5)
  }
}

async function captureFrozenDescendantGroups(pgid, deadline, inspect, groups) {
  let initialFailure
  try {
    rememberDescendantGroups(pgid, processRecords(deadline, 'initial-capture', inspect), groups)
  } catch (error) {
    initialFailure = { error }
  }
  try {
    await freezeAndCaptureDescendantGroups(pgid, deadline, inspect, groups)
  } catch (error) {
    if (initialFailure) throw new AggregateError([initialFailure.error, error], 'Initial and frozen descendant capture failed', { cause: initialFailure.error })
    throw error
  }
  if (initialFailure) throw initialFailure.error
}

async function awaitObservedGroups(groups, deadline, inspect, phase = 'descendant-drain') {
  if (groups.size === 0) return
  while (true) {
    const survivors = processRecords(deadline, phase, inspect).filter(record => groups.has(record.group))
    if (survivors.length === 0) return
    if (Date.now() >= deadline) {
      throw new Error(`Observed descendant groups did not drain: ${survivors.map(record => `${record.pid}/${record.group}`).join(', ')}`)
    }
    await delay(10)
  }
}

async function failAfterObservedCleanup(error, groups, deadline, inspect) {
  try {
    await awaitObservedGroups(groups, deadline, inspect, 'failure-drain')
  } catch (cleanupError) {
    throw new AggregateError([error, cleanupError], 'Owned termination and remaining group observation failed', { cause: error })
  }
  throw error
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
 *   inspectProcessTree?: (request: { phase: string, timeout: number }) => string,
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
  inspectProcessTree,
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

  const ownedHome = inner === NODE_TEST_INNER
    ? realpathSync(mkdtempSync(join(tmpdir(), 'wxs-runner-home-')))
    : null
  let result
  let failure
  try {
    result = await superviseOwnedNodeTest({ files, label, silenceMs, env, logPrefix, inner, ownedHome, inspectProcessTree })
  } catch (error) {
    failure = { error }
  } finally {
    try {
      if (ownedHome !== null) rmSync(ownedHome, { recursive: true, force: true })
    } catch (error) {
      failure = { error: failure
        ? new AggregateError([failure.error, error], 'Supervised suite failed and its owned HOME could not be reclaimed', { cause: failure.error })
        : error }
    }
  }
  if (failure) {
    if (throwOnFailure) throw failure.error
    console.error(failure.error)
    process.exit(failure.error.exitCode ?? 1)
  }
  return result
}

async function superviseOwnedNodeTest({ files, label, silenceMs, env, logPrefix, inner, ownedHome, inspectProcessTree }) {
  let terminationFailure = null
  const fail = (code = 1) => {
    throw Object.assign(new Error(`${logPrefix}: supervised suite failed (exit ${code})`, {
      cause: terminationFailure ?? undefined,
    }), { exitCode: code })
  }

  console.error(`${logPrefix}: ${files.length} test file(s), ${silenceMs}ms verdict-silence window`)

  // Absolute paths: test:complete reports absolute `data.file`.
  const plannedFiles = new Set(files.map((file) => resolve(file)))
  const outstanding = new Set(plannedFiles)
  const fileWaits = createFileWaitTracker(files)
  const workerCosts = new Map()
  const workerCost = file => workerCosts.get(file) ?? {
    version: 1, entryFile: file, enabled: env.WXS_VERIFICATION_WORKER_DIAGNOSTICS !== '0',
    pid: null, preImport: null, exit: null, interval: null,
    status: env.WXS_VERIFICATION_WORKER_DIAGNOSTICS === '0' ? 'disabled' : 'missing',
    reason: env.WXS_VERIFICATION_WORKER_DIAGNOSTICS === '0' ? 'Worker diagnostics disabled' : 'Worker cost snapshot not received',
  }
  const reportFileWaits = () => {
    const waits = fileWaits.snapshot()
    console.error(`${logPrefix}: file streams: ${waits.drained.length} drained, ${waits.active.length} active, ${waits.queued.length} queued`)
    for (const { file, lastVerdict, lastStart } of waits.active) {
      console.error(`${logPrefix}: active file ${relative(process.cwd(), file)}; waiting for stream drain; last verdict: ${lastVerdict ?? 'none received'}`)
      console.error(`${logPrefix}: worker cost ${JSON.stringify(workerCost(file))}`)
      if (lastStart) console.error(`${logPrefix}: last runtime test start ${JSON.stringify(lastStart)}`)
    }
    if (waits.queued.length > 0) {
      console.error(`${logPrefix}: ${waits.queued.length} queued file(s) have not started`)
    }
  }
  let runnerSummary = null
  let drained = false
  let runnerError = null
  let child
  let silenceFired = false
  let exitDeadline = null
  let finishExit
  let termination = null
  const requestTermination = () => {
    if (termination !== null) return
    const deadline = Date.now() + SIGKILL_GRACE_MS
    if (exitDeadline === null) {
      exitDeadline = setTimeout(() => {
        console.error(`${logPrefix}: inner runner exit was not observed within ${SIGKILL_GRACE_MS}ms after termination`)
        finishExit({ code: null, signal: null, observed: false })
      }, SIGKILL_GRACE_MS)
    }
    termination = (async () => {
      const groups = new Set()
      let captureFailure
      try {
        if (child?.pid) await captureFrozenDescendantGroups(child.pid, deadline, inspectProcessTree, groups)
      } catch (error) {
        captureFailure = { error }
      } finally {
        try {
          if (child?.pid) process.kill(-child.pid, 'SIGKILL')
        } catch (error) {
          if (error.code !== 'ESRCH') {
            captureFailure = { error: captureFailure
              ? new AggregateError([captureFailure.error, error], 'Owned capture and inner termination failed', { cause: captureFailure.error })
              : error }
          }
        }
      }
      if (captureFailure) await failAfterObservedCleanup(captureFailure.error, groups, deadline, inspectProcessTree)
      try {
        await awaitObservedGroups(groups, deadline, inspectProcessTree)
      } catch (error) {
        await failAfterObservedCleanup(error, groups, deadline, inspectProcessTree)
      }
    })().catch(error => {
      terminationFailure = error
      runnerError = { message: `Could not complete owned runner termination: ${error.message}` }
      console.error(`${logPrefix}: ${runnerError.message}`)
    })
  }

  const watchdog = new Watchdog({
    timeoutMs: silenceMs,
    label,
    deps: {
      terminate() {
        silenceFired = true
        requestTermination()
      },
    },
    onTimeout: () => {
      if (outstanding.size === 0) {
        console.error(
          `${logPrefix}: all planned files completed but the child would not exit — ` +
            'the runner lifecycle is still open',
        )
      } else {
        console.error(
          `${logPrefix}: ${outstanding.size} file(s) had not reported completion`,
        )
      }
      reportFileWaits()
      console.error(runnerSummary
        ? `${logPrefix}: ${runnerSummary.passed} passed, ${runnerSummary.failed} failed before the silence`
        : `${logPrefix}: verdict counts unavailable; no authoritative summary before the silence`)
      try {
        process.stderr.write('')
      } catch {}
    },
  })

  const homeArguments = ownedHome === null ? [] : ['--owned-test-home', ownedHome]
  try {
    child = spawn(process.execPath, [inner, ...homeArguments, ...files], {
      stdio: ['ignore', 'inherit', 'inherit', 'ipc'],
      detached: true,
      env,
    })
  } catch (error) {
    watchdog.stop()
    throw error
  }

  let backstopFired = false
  const backstop = setTimeout(() => {
    backstopFired = true
    console.error(`${logPrefix}: suite exceeded the ${SUITE_BACKSTOP_MS}ms physical backstop`)
    reportFileWaits()
    requestTermination()
  }, SUITE_BACKSTOP_MS)
  backstop.unref()

  const startedAt = performance.now()
  child.on('message', (event) => {
    if (event?.type === 'runner:worker-cost') {
      const file = event.data?.entryFile
      if (typeof file !== 'string' || !plannedFiles.has(resolve(file)) || file !== resolve(file)) {
        console.error(`${logPrefix}: worker cost unavailable: unplanned entry`)
        return
      }
      try {
        const observed = validateWorkerCostSnapshot(event.data, {
          entryFile: file, parentPid: child.pid, enabled: env.WXS_VERIFICATION_WORKER_DIAGNOSTICS !== '0',
        })
        const previous = workerCosts.get(file)
        if (previous?.status === 'invalid') return
        if (previous?.preImport && JSON.stringify(previous.preImport) !== JSON.stringify(observed.preImport) ||
            previous?.exit && JSON.stringify(previous.exit) !== JSON.stringify(observed.exit)) {
          throw new TypeError('Worker cost snapshot replaced an earlier observation')
        }
        workerCosts.set(file, observed)
      } catch {
        workerCosts.set(file, { version: 1, entryFile: file, enabled: env.WXS_VERIFICATION_WORKER_DIAGNOSTICS !== '0',
          pid: null, preImport: null, exit: null, interval: null,
          status: 'invalid', reason: 'Worker cost snapshot failed validation' })
      }
      return
    }
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
    try {
      fileWaits.observe(event)
    } catch (error) {
      runnerError = { message: error.message }
      console.error(`${logPrefix}: invalid file lifecycle: ${error.message}`)
      requestTermination()
      return
    }
    if (event?.type === 'runner:file-start' || event?.type === 'runner:file-drained') {
      console.error(`${logPrefix}: file lifecycle ${JSON.stringify({ ...event.data, type: event.type, elapsedMs: performance.now() - startedAt })}`)
      if (event.type === 'runner:file-drained') console.error(`${logPrefix}: worker cost ${JSON.stringify(workerCost(resolve(event.data.entryFile)))}`)
    }
    if (isFileCompletionEvent(event)) {
      outstanding.delete(resolve(testEntryFile(event)))
    }

    const progress = classifyVerdict(event)
    if (progress !== null) watchdog.advance(progress)
  })

  const exit = await new Promise((resolveExit) => {
    let settled = false
    finishExit = (outcome) => {
      if (settled) return
      settled = true
      if (exitDeadline !== null) clearTimeout(exitDeadline)
      resolveExit(outcome)
    }
    child.on('exit', (code, signal) => finishExit({ code, signal, observed: true }))
    child.on('error', (error) => {
      console.error(`${logPrefix}: could not start the inner runner: ${error.message}`)
      finishExit({ code: 1, signal: null, observed: true })
    })
  })

  watchdog.stop()
  clearTimeout(backstop)
  if (termination !== null) await termination
  const groupVerificationStarted = performance.now()
  const processGroupClean = child.pid ? await verifyExitedGroup(child.pid, logPrefix) : false
  console.error(
    `${logPrefix}: post-exit group verification/reclamation: pid=${child.pid ?? 'none'}; ` +
      `elapsedMs=${(performance.now() - groupVerificationStarted).toFixed(3)}; accepted=${processGroupClean}`,
  )

  if (!runnerSummary && exit.observed && exit.signal === null) {
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

  if (silenceFired || !exit.observed) fail(1)

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

  if (fileWaits.snapshot().drained.length !== files.length) {
    console.error(`${logPrefix}: incomplete run; not every planned file stream drained`)
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
