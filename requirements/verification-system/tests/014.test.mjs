/**
 * The Long Stroke — sole top-level E2E entry & verification-system 014 test
 * WHAT[verification-system-014] Long Stroke 真实物理验收环境.
 *
 * Scenario: e2e/scenarios/long-stroke.toml
 * Oracles:  e2e/support/long-stroke-oracles.mjs
 *
 * NOT registered under cases/ — G4R-0 freeze forbids growing the multi-canary
 * ceiling; this is the required-exactly-one-when-present cutover path
 * (g4r-freeze gate retired 2026-08-14; the e2e-watchdog-feed gate keeps the
 * sole-entry scope).
 *
 * PHYSICAL CONTRACTS (verification-system [002]/[014]): this file is the sole Long Stroke
 * because it depends on Host facts Pure/Temporal/Adapter cannot simulate:
 *   1. OpenCode process lifetime — spawn count === 1, one serve, one journal writer
 *   2. Host-assigned assistant messageID persisted before transform, then
 *      ToolContext.messageID at execute (HOST-010 共时)
 *   3. Host ToolPart / idle / abort / child-session physical threading
 * Repeat-until-pass is forbidden; semantic branches stay at Pure/Temporal/Adapter.
 *
 * G4R §2 / Exit: one continuous OpenCode lifetime — spawn count must be exactly 1.
 *
 * The Manager tool surface is proven on the wire the sole serve lifetime really sent:
 * the independent `assume` and Host-native `todowrite` entries are advertised
 * together with the Manager spine.
 */
import assert from 'node:assert/strict'
import test from 'node:test'
import { readFileSync } from 'node:fs'
import { compileScenario } from './e2e/support/scenario-schema.js'
import { resolveEntry } from './e2e/support/runtime-key.js'
import { fileURLToPath } from 'node:url'
import './e2e/support/env-pin.mjs'
import { runCanary } from './e2e/support/scenario-driver.mjs'
import { bindLaneSession } from './e2e/support/lane.mjs'
import { getSessionId } from './e2e/support/scenario-http.js'
import { runStaticGate } from './e2e/support/index.js'
import { SOLE_ENTRY } from './e2e/support/watchdog-feed-scan.mjs'
import { releaseTest } from './support/tier-gate.mjs'
import {
  CUSTOMS,
  HUMANROOT_MANAGER_LOOP_CANARY_PROMPT,
  assertHumanRootManagerLoop,
} from './e2e/support/long-stroke-oracles.mjs'
import { WAIT_FACT_WINDOW_MS } from './e2e/support/time-budget.js'
import {
  getOpencodeSpawnCount,
  resetOpencodeSpawnCount,
} from './e2e/support/process-host-utils.js'
import {
  assertManagerToolSurface,
  collectManagerProviderToolEvidence,
} from './e2e/support/manager-tool-surface-evidence.mjs'

test('WHAT[verification-system-014] the Long Stroke entry satisfies the watchdog-feed guard', () => {
  // VERIFICATION-SYSTEM-014 requires the Layer 4 Long Stroke environment to be driven
  // by exactly one sole E2E entry executing under a single process lifecycle.
  const here = fileURLToPath(import.meta.url)
  const entryPath = here

  const gateResult = runStaticGate([entryPath])
  assert.equal(gateResult.passed, true, 'Long Stroke sole entry must satisfy static entry gate')

  // Verify that the entry defines the single physical server and single lifecycle contract
  assert.ok(entryPath.endsWith(SOLE_ENTRY), `Long Stroke sole entry must be ${SOLE_ENTRY}`)
})

test('WHAT[verification-system-014] the compiled Long Stroke scenario preserves its Manager and consecutive-failure cases', () => {
  const source = readFileSync(new URL('./e2e/scenarios/long-stroke.toml', import.meta.url), 'utf8')
  const result = compileScenario(source, { name: 'long-stroke.toml' })
  assert.equal(result.ok, true, result.ok ? '' : result.problems.join('\n'))
  const byId = new Map(result.scenario.entries.map((entry) => [entry.id, entry]))
  const ordinary = byId.get('manager-loop.2')
  assert.deepEqual({ optional: ordinary?.optional, lane: ordinary?.lane, step: ordinary?.step },
    { optional: false, lane: 'manager', step: 2 })
  assert.deepEqual(result.scenario.faults.filter((fault) => fault.kind === 'provider-error' && fault.status === 400)
    .map((fault) => fault.entryId), ['manager-loop.2', 'continue.0'])

  const loopTools = ['fork', 'resume', 'join', 'horizon', 'review', 'deliver']
  const managerTools = ['fork', 'resume', 'join', 'horizon', 'assume', 'deliver']
  const request = (turn, step, tools) => ({
    messages: [{ role: 'user', content: turn },
      ...Array.from({ length: step }, (_, index) => ({ role: 'assistant', content: `reply-${index}` }))],
    tools: tools.map((name) => ({ name })),
  })
  const bindings = new Map([['manager', 'ses_manager']])
  const context = { sessionId: 'ses_manager' }
  assert.equal(resolveEntry(request('# Delegated work you sent out has not come back yet. Continue the work.', 1, managerTools), result.scenario.entries, bindings, context).matched?.id,
    'manager-join-guard.0')
  const assessUser =
    '# You are the 2 Manager taking over this mission. A predecessor may already have done\n' +
    '# part of the work, or may already have finished it; investigate the actual workspace before you\n' +
    "# act on either assumption. The predecessor's work is the object you must assess; the shared workspace is the actual state it left behind: check it directly\n" +
    '# rather than trusting any inherited claim.\n' +
    '#\n' +
    "# During assessment, you may directly use the review-only read tool todowrite-for-manager, or entrust read-only work to Engineer to establish facts about the predecessor's work"
  for (const [step, tool] of [[0, 'review'], [1, 'deliver']]) {
    const id = `manager-reopened-loop.${step}`
    const entry = byId.get(id)
    assert.deepEqual({ step: entry?.step, optional: entry?.optional, internal: entry?.internal, tool: entry?.respond?.tool },
      { step, optional: true, internal: true, tool })
    assert.equal(resolveEntry(request(assessUser, step, loopTools), result.scenario.entries, bindings, context).matched?.id, id)
  }
  assert.equal(result.scenario.flow.filter((step) => step.waitAny).length, 0)
  for (let index = 0; index <= 2; index += 1) {
    const id = `manager-loop.${index}`
    assert.ok(result.scenario.must.includes(id), `${id} must be an exact must step`)
    assert.deepEqual(byId.get(id)?.tools, loopTools)
    assert.equal(byId.get(id)?.internal, false)
  }
  assert.ok(!result.scenario.entries.some((entry) => entry.id.startsWith('manager-resume.')))
  const currentActions = result.scenario.entries.filter((entry) => entry.turnId === 'manager-current-action')
  // The Manager idle-nudge cursor is bounded only by the run (work/manager-continue),
  // so the reusable ladder keeps one declared close attempt per bounded cursor.
  assert.deepEqual(currentActions.map((entry) => entry.step), Array.from({ length: 24 }, (_, index) => index))
  assert.ok(currentActions.every((entry) => entry.optional === true))
  assert.ok(!result.scenario.must.some((id) => id.startsWith('manager-current-action.')))
  assert.ok(!result.scenario.entries.some((entry) => entry.id.startsWith('manager-repair-resume.')))
  assert.ok(!result.scenario.must.some((id) => id.startsWith('manager-join-guard.')))
  assert.deepEqual({ journal: result.scenario.setup.maxJournalEvents, sse: result.scenario.setup.maxSseEvents },
    { journal: 699, sse: 3351 }, 'the measured scenario ceilings stay fixed during migration')
})

const preFlowCanaries = async (scenario) => {
  await CUSTOMS.bindManagerLoopSequence(scenario)

  const humanrootCreated = await scenario.client.createSession({ agent: 'manager' })
  const humanrootSessionId = getSessionId(humanrootCreated)
  assert.ok(humanrootSessionId, `humanroot-manager session creation failed: ${JSON.stringify(humanrootCreated)}`)
  if (!scenario.sessionIds.includes(humanrootSessionId)) scenario.sessionIds.push(humanrootSessionId)
  bindLaneSession(scenario.provider, humanrootSessionId, 'humanroot-manager')

  const humanrootPrompt = await scenario.client.request('POST', `/session/${humanrootSessionId}/prompt_async`, {
    body: {
      parts: [{ type: 'text', text: HUMANROOT_MANAGER_LOOP_CANARY_PROMPT }],
      agent: 'manager',
    },
  })
  assert.ok(humanrootPrompt.ok, `humanroot-manager prompt failed: ${JSON.stringify(humanrootPrompt.data)}`)

  // The successor iteration appends the owner-controlled assess resource, so its two
  // deliveries are answered by the assess-resource family instead of the reusable
  // authority-turn family. One delivery of each step is what the two iterations produce.
  for (const id of ['humanroot-loop.0', 'humanroot-loop.1', 'manager-reopened-loop.0', 'manager-reopened-loop.1']) {
    await scenario.provider.waitForExpectationAttempt(id, 1, WAIT_FACT_WINDOW_MS)
  }
  await assertHumanRootManagerLoop(scenario, humanrootSessionId)
}

const awaitManagerJoinRunning = async (scenario, ctx) => {
  const managerSessionId = ctx?.childId
  assert.ok(managerSessionId, 'Long Stroke join-running barrier requires the bound Manager session')

  const isRunningJoin = (event) =>
    event?.type === 'message.part.updated'
    && event?.sessionID === managerSessionId
    && event?.toolName === 'join'
    && event?.toolStatus === 'running'

  await scenario.events.awaitEvent(isRunningJoin, null)
}

const assertManagerToolSurfaceOnWire = async (scenario, ctx) => {
  const managerProviderWire = collectManagerProviderToolEvidence(scenario, {
    childSessionId: ctx?.childId ?? null,
  })
  const result = assertManagerToolSurface({ managerProviderWire })
  console.log(`[manager-surface] ok tools=${result.unionTools.join(',')} requests=${result.requestCount}`)
}

releaseTest('WHAT[verification-system-014] Long Stroke 真实物理验收环境', async () => {
  resetOpencodeSpawnCount()
  const code = await runCanary('long-stroke', {
    preFlow: preFlowCanaries,
    customs: {
      ...CUSTOMS,
      awaitManagerJoinRunning,
      assertManagerToolSurfaceOnWire,
    },
  })
  assert.equal(code, 0, `Long Stroke canary exited with code ${code}`)
  assert.equal(
    getOpencodeSpawnCount(),
    1,
    `G4R §2: Long Stroke must spawn opencode serve exactly once (got ${getOpencodeSpawnCount()})`,
  )
})
