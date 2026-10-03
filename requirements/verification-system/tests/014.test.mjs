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
import { join } from 'node:path'
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
  retireCompanionForDeletion,
  INVESTIGATION_OUTLOOK_MARKERS,
  matchInvestigationOutlookMarker,
} from './e2e/support/long-stroke-oracles.mjs'
import { factPayloads } from './e2e/support/journal-observer.js'
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
  assert.ok(entryPath.endsWith('014.test.mjs'), 'Long Stroke sole entry must be 014.test.mjs')
})

test('WHAT[verification-system-014] matchInvestigationOutlookMarker correctly identifies outlook markers and never falls back to raw description', () => {
  // Incident regression: When both markers are missing, the oracle must return null rather than falling
  // back to the full description. Falling back to the description causes undecorated tools to be fed into
  // legacy narrative assertions, misreporting a missing decoration as a legacy narrative residue.

  // 1. Both markers missing: must return null, never the description itself
  const plainToolDescription = 'Read a file or directory from the local filesystem. If the path does not exist, an error is returned.';
  assert.equal(matchInvestigationOutlookMarker(plainToolDescription), null);
  assert.notEqual(matchInvestigationOutlookMarker(plainToolDescription), plainToolDescription);

  const emptyDescription = '';
  assert.equal(matchInvestigationOutlookMarker(emptyDescription), null);

  const nonStringInputs = [null, undefined, 12345, {}, []];
  for (const input of nonStringInputs) {
    assert.equal(matchInvestigationOutlookMarker(input), null);
  }

  // 2. English marker present: returns exact English marker string
  const enDecoratedDescription =
    'Read a file from disk.\n\n' +
    'Investigation outlook: estimated_readonly_rounds estimates the consecutive read-only investigation rounds after the current batch. Include self_note only for a positive estimate, stating what to inspect next and what finding will make the next step possible; omit the note for 0.';
  const enMarker = matchInvestigationOutlookMarker(enDecoratedDescription);
  assert.equal(enMarker, INVESTIGATION_OUTLOOK_MARKERS.en);
  assert.equal(enMarker, 'Investigation outlook:');
  const enIncremental = enDecoratedDescription.slice(enDecoratedDescription.indexOf(enMarker));
  assert.ok(enIncremental.startsWith('Investigation outlook:'));

  // 3. Chinese marker present: returns exact Chinese marker string
  const zhDecoratedDescription =
    '从本地文件系统读取文件或目录。\n\n' +
    '调查展望：estimated_readonly_rounds 估计当前整批完成后的连续只读查证轮数。只在本次估计大于 0 时填写 self_note，简述接下来查什么、查到什么即可进入下一步；估计为 0 时省略短记。';
  const zhMarker = matchInvestigationOutlookMarker(zhDecoratedDescription);
  assert.equal(zhMarker, INVESTIGATION_OUTLOOK_MARKERS.zh);
  assert.equal(zhMarker, '调查展望：');
  const zhIncremental = zhDecoratedDescription.slice(zhDecoratedDescription.indexOf(zhMarker));
  assert.ok(zhIncremental.startsWith('调查展望：'));

  // 4. Invariant: Idempotent decoration appends must not produce multiple marker blocks
  const enDoubleDecorated =
    enDecoratedDescription +
    '\n\nInvestigation outlook: estimated_readonly_rounds estimates the consecutive read-only investigation rounds after the current batch.';
  const doubleMarker = matchInvestigationOutlookMarker(enDoubleDecorated);
  assert.equal(doubleMarker, INVESTIGATION_OUTLOOK_MARKERS.en);
  // Verify that an incremental slice from the first marker can detect non-idempotent duplication
  const firstIndex = enDoubleDecorated.indexOf(doubleMarker);
  const secondIndex = enDoubleDecorated.indexOf(doubleMarker, firstIndex + doubleMarker.length);
  assert.ok(secondIndex > firstIndex, 'Controlled fixture proves second occurrence exists when duplicated');

  // And for a properly decorated tool, the marker block appears exactly once
  const singleFirstIndex = enDecoratedDescription.indexOf(enMarker);
  const singleSecondIndex = enDecoratedDescription.indexOf(enMarker, singleFirstIndex + enMarker.length);
  assert.equal(singleSecondIndex, -1, 'Properly decorated description must have exactly one marker occurrence');

  // 5. Executable Mutation Verification (counterexample showing the test goes RED if reverted to 3-tier fallback):
  // The buggy 3-tier fallback returned the full description on missing markers:
  const buggyThreeTierFallback = (desc) =>
    desc.includes('Investigation outlook:')
      ? 'Investigation outlook:'
      : desc.includes('调查展望：')
        ? '调查展望：'
        : desc; // The bug: fallback to desc

  // Demonstrating the mutation creates a distinct observable failure:
  const mutantResult = buggyThreeTierFallback(plainToolDescription);
  assert.equal(mutantResult, plainToolDescription);
  // A test asserting strict equality to null strictly rejects the mutated logic:
  assert.throws(
    () => assert.equal(mutantResult, null),
    /AssertionError/,
    'Reverting to 3-tier fallback must fail the null assertion with an AssertionError',
  );
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

  const loopTools = ['fork', 'resume', 'join', 'horizon', 'review', 'suicide']
  const managerTools = ['fork', 'resume', 'join', 'horizon', 'assume', 'suicide']
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
    "# During assessment, you may directly use the review-only read tool js-manager, or entrust read-only work to Engineer to establish facts about the predecessor's work"
  for (const [step, tool] of [[0, 'review'], [1, 'suicide']]) {
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
  assert.deepEqual(currentActions.map((entry) => entry.step), Array.from({ length: 11 }, (_, index) => index))
  assert.ok(currentActions.every((entry) => entry.optional === true))
  assert.ok(!result.scenario.must.some((id) => id.startsWith('manager-current-action.')))
  assert.ok(!result.scenario.entries.some((entry) => entry.id.startsWith('manager-repair-resume.')))
  assert.ok(!result.scenario.must.some((id) => id.startsWith('manager-join-guard.')))
  assert.deepEqual({ journal: result.scenario.setup.maxJournalEvents, sse: result.scenario.setup.maxSseEvents },
    { journal: 550, sse: 2500 }, 'the measured scenario ceilings stay fixed during migration')
})

const STRENGTH_HOST_CANARY_PROMPT =
  'STRENGTH_HOST_CANARY: inspect README.md through the real nested Replica path.'

const runPreFlowPrompt = async (scenario, lane, prompt, agent) => {
  const created = await scenario.client.createSession(agent ? { agent } : {})
  const sessionID = getSessionId(created)
  assert.ok(sessionID, `${lane} session creation failed: ${JSON.stringify(created)}`)
  if (!scenario.sessionIds.includes(sessionID)) scenario.sessionIds.push(sessionID)
  bindLaneSession(scenario.provider, sessionID, lane)

  const turn = scenario.turn.start(sessionID)
  const response = await scenario.client.request('POST', `/session/${sessionID}/prompt_async`, {
    body: {
      parts: [{ type: 'text', text: prompt }],
      ...(agent ? { agent } : {}),
    },
  })
  assert.ok(response.ok, `${lane} prompt failed: ${JSON.stringify(response.data)}`)
  await turn.awaitTerminal()
  return sessionID
}

const settlePreFlowManagerSession = async (scenario, ownerSessionId) => {
  // A manager HumanRoot canary opens Incumbency; without Accepted retirement the Host
  // continues it into the ordinary authority assessment on the same session. Abort +
  // delete so bindChild later cannot pick this session as the orch Manager childId.
  const replicaIds = factPayloads(scenario.host.workDir, 'DelegationBound')
    .map((payload) => payload?.replicaSessionId ?? payload?.replica_session_id ?? payload?.ReplicaSessionId)
    .filter((id) => typeof id === 'string' && id.length > 0)

  const linkedBlogger = factPayloads(scenario.host.workDir, 'CompanionBloggerLinked')
    .filter((payload) => JSON.stringify(payload ?? {}).includes(ownerSessionId))
  if (linkedBlogger.length > 0) {
    await retireCompanionForDeletion(scenario, ownerSessionId)
  }

  for (const sessionId of [ownerSessionId, ...replicaIds]) {
    if (!scenario.sessionIds.includes(sessionId)) scenario.sessionIds.push(sessionId)
    await scenario.client.abort(sessionId).catch(() => {})
  }
}

const preFlowCanaries = async (scenario) => {
  await CUSTOMS.bindManagerLoopSequence(scenario)

  // HumanRoot manager-loop canary first: its oracle pins absolute IncumbencyOpened /
  // RetirementCommitted / AssessmentCommitted counts (HUMANROOT_CANARY_DELTAS). The
  // Strength canary also opens a manager HumanRoot incumbency, so it must run after
  // those absolute counts are sealed.
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

  const linkedBlogger = factPayloads(scenario.host.workDir, 'CompanionBloggerLinked')
    .filter((payload) => {
      const text = JSON.stringify(payload ?? {})
      return text.includes(humanrootSessionId)
    })
  if (linkedBlogger.length > 0) {
    await retireCompanionForDeletion(scenario, humanrootSessionId)
  }
  // Keep session id in scenario.sessionIds so bindChild can exclude this canary.
  // Isolation: set WXS_SKIP_STRENGTH_CANARY=1 to prove the orch/Manager publish spine.
  if (process.env.WXS_SKIP_STRENGTH_CANARY === '1') {
    // Do not abort HumanRoot on the spine-only path — aborting was observed to
    // correlate with manager-loop seal rewrites on the orch Manager child.
    if (process.env.WXS_ABORT_HUMANROOT === '1') {
      await scenario.client.abort(humanrootSessionId).catch(() => {})
    }
    console.error('[preflow-match] skipped-strength')
    return
  }

  const strengthSessionId = await runPreFlowPrompt(
    scenario,
    'strength-canary-owner',
    STRENGTH_HOST_CANARY_PROMPT,
    'manager',
  )
  // Abort before any post-assert work: Host may already be queuing the ordinary
  // manager-authority continuation on this open incumbency.
  await settlePreFlowManagerSession(scenario, strengthSessionId)
  // With Strength present, also abort HumanRoot so two open Manager roads cannot
  // contend with the orch Manager continuum after ConflictDetected.
  await scenario.client.abort(humanrootSessionId).catch(() => {})

  const deliveryIds = Object.fromEntries(
    [
      'strength-canary-owner.0',
      'strength-canary-owner.1',
      'strength-canary-owner.2',
      'strength-canary-owner.3',
      'strength-canary-owner.4',
      'strength-canary-owner.5',
      'strength-canary-replica.0',
      'strength-canary-replica.1',
      'blogger.0',
      'blogger.1',
      'strength-canary-owner-title.0',
    ].map((id) => [id, scenario.provider.matchCount(id)]),
  )
  console.error('[preflow-match]', JSON.stringify(deliveryIds))

  assert.equal(
    scenario.provider.matchCount('strength-canary-replica.0'),
    1,
    `Strength dry-run must physically start its Replica without blocking the owner. Host stderr tail:\n${scenario.host.stderrLog.slice(-4000)}\nmatch dump=${JSON.stringify(deliveryIds)}`,
  )
  // Survey text may land at step 2 (legacy) or step 3 (after reasoning inject).
  assert.ok(
    scenario.provider.matchCount('strength-canary-owner.2') +
      scenario.provider.matchCount('strength-canary-owner.3') >=
      1,
    `Strength canary owner survey text must land at step 2 or 3. match dump=${JSON.stringify(deliveryIds)}`,
  )
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

const amendDocForStrengthSkip = (doc) => {
  // Path A: prove the orch/Manager publish spine without Strength canary or
  // recovery legs. Truncate after the main-spine wire assert and drop Strength
  // must-ids so expectSatisfied does not demand skipped deliveries.
  const cut = doc.flow.findIndex((step) => step.custom === 'assertManagerToolSurfaceOnWire')
  assert.ok(cut >= 0, 'long-stroke flow must include assertManagerToolSurfaceOnWire')
  doc.flow = [...doc.flow.slice(0, cut + 1), { expectSatisfied: true }]
  doc.must = (doc.must ?? []).filter((id) => !String(id).startsWith('strength-'))
  for (const entry of doc.entries ?? []) {
    const strength =
      String(entry.id ?? '').startsWith('strength-')
      || String(entry.turnId ?? '').startsWith('strength-')
      || String(entry.lane ?? '').startsWith('strength-')
    if (strength) {
      entry.optional = true
      entry.internal = true
    }
    // Title turns are best-effort; the Host may skip them without failing the spine.
    if (entry.kind === 'title') entry.optional = true
  }
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
    ...(process.env.WXS_SKIP_STRENGTH_CANARY === '1' ? { amendDoc: amendDocForStrengthSkip } : {}),
  })
  assert.equal(code, 0, `Long Stroke canary exited with code ${code}`)
  assert.equal(
    getOpencodeSpawnCount(),
    1,
    `G4R §2: Long Stroke must spawn opencode serve exactly once (got ${getOpencodeSpawnCount()})`,
  )
})
