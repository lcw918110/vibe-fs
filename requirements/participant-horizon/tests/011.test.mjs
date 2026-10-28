import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as forkTool from '../../../dist/Execution/Delegation/Fork/OpenCode/ToolSurface.js'
import * as horizonCalibration from '../../../dist/Execution/Session/OpenCode/HorizonCalibrationSurface.js'
import { placeEngineer, withForkRuntime } from './support/fork-runtime.mjs'

process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'

test('WHAT[participant-horizon-011] a participant calibration states the four numbers from the authored resource', () => {
  const cases = [
    ['English', 'en.md', 'Ada', 4, 6, 125000, '2 minutes 5 seconds', '3 minutes 7 seconds'],
    ['SimplifiedChinese', 'zh-CN.md', 'Ada', 4, 6, 125000, '2 分钟 5 秒', '3 分钟 7 秒'],
    ['English', 'en.md', 'Ada', 0, 2, 90000, '1 minute 30 seconds', '3 minutes 0 seconds'],
  ]

  for (const [language, file, label, used, left, elapsedMs, timeUsed, timeLeft] of cases) {
    const rendered = horizonCalibration.render(language, label, used, left, elapsedMs)
    const resource = readFileSync(
      new URL(`../../../resources/provider/tool/horizon/calibration/${file}`, import.meta.url),
      'utf8',
    )
    const expected = resource
      .trim()
      .replaceAll('{{label}}', label)
      .replaceAll('{{turns_used}}', String(used))
      .replaceAll('{{turns_left}}', String(left))
      .replaceAll('{{time_used}}', timeUsed)
      .replaceAll('{{time_left}}', timeLeft)
    assert.equal(rendered, expected, `${language}: ${used}/${left} turns at ${elapsedMs}ms`)
  }
})

test('WHAT[participant-horizon-011] real cancellation keeps an accepted child and its undelivered consequence visible', async (context) => {
  await withForkRuntime(async ({ runtime, owner }) => {
    await context.test('accepted child is visible without its physical identity', async () => {
      const child = await placeEngineer(runtime, owner, 'Ada')
      const active = await forkTool.executeHorizon(runtime, owner)
      assert.match(active, /Ada.*still away/)
      assert.ok(!active.includes(child), 'physical child identity stays private')
    })
    await context.test('cancellation keeps the undelivered consequence visible', async () => {
      await forkTool.cancelOwnerChildren(runtime, owner)
      assert.match(await forkTool.executeHorizon(runtime, owner), /Ada.*did not return/)
      assert.equal(forkTool.abortCount(runtime), 1)
    })
  })
})

test('WHAT[participant-horizon-011] a restarted process voids the previous process pending child work and reports it once', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-horizon-restart-voided-'))
  const owner = 'manager-horizon-restart-voided'
  const first = await forkTool.createRuntime(directory, [{ sessionId: owner, agent: 'manager' }])

  try {
    // The child work stays pending: it is accepted and visible, never cancelled.
    await placeEngineer(first, owner, 'Ada')
    assert.equal(forkTool.durableLifecycleByname(first, owner, 'Ada'), 'Active')
  } finally {
    forkTool.disposeRuntime(first)
  }

  // Restart: the pending child work can no longer be delivered, so load
  // settlement voids it. A void is not a completion, but it settles the
  // outstanding obligation: it stays visible until it is reported once.
  const restarted = await forkTool.createRuntime(directory, [{ sessionId: owner, agent: 'manager' }])

  try {
    const horizonReceipt = await forkTool.executeHorizonReceipt(restarted, owner)
    assert.equal(horizonReceipt.ok, true)
    const voidedRow = horizonReceipt.agents.find((row) => row.label === 'Ada')
    assert.ok(voidedRow, 'the voided work keeps a row in the horizon roster')
    assert.equal(voidedRow.status, 'voided', 'the voided work is reported as voided, not as still away')

    const joinPromise = forkTool.executeJoin(restarted, owner)
    const timeout = new Promise((_, reject) => setTimeout(() => reject(new Error('JOIN_HANG_DETECTED')), 1500))
    const joinResult = await Promise.race([joinPromise, timeout])
    assert.match(joinResult, /Ada/)
    assert.match(joinResult, /was voided|已作废|已撤销/)
    assert.doesNotMatch(joinResult, /\[\[data\.records\]\]/)


    // Reported once: the work leaves the roster and the handle retires.
    assert.doesNotMatch(await forkTool.executeHorizon(restarted, owner), /Ada/)
    assert.equal(forkTool.durableLifecycleByname(restarted, owner, 'Ada'), 'Retired')
  } finally {
    forkTool.disposeRuntime(restarted)
  }

  // A further process on the same directory does not report the void again.
  const third = await forkTool.createRuntime(directory, [{ sessionId: owner, agent: 'manager' }])

  try {
    const thirdJoinPromise = forkTool.executeJoin(third, owner)
    const thirdTimeout = new Promise((_, reject) => setTimeout(() => reject(new Error('JOIN_HANG_DETECTED')), 1500))
    const thirdJoin = await Promise.race([thirdJoinPromise, thirdTimeout])
    assert.match(thirdJoin, /NothingToJoin|无可等待|没有|nothing away to receive/i)
    assert.doesNotMatch(await forkTool.executeHorizon(third, owner), /Ada/)
  } finally {
    forkTool.disposeRuntime(third)
    rmSync(directory, { recursive: true, force: true })
  }
})

test.todo('WHAT[participant-horizon-011] Join delivers the abandoned consequence and retires the handle; real scenario stalls at Join after successful cancellation, root cause still under investigation (GAP-080)')

test('WHAT[participant-horizon-011] the same roster reaches a program as this.horizon data with no pace when no delegated estimate exists', async () => {
  await withForkRuntime(async ({ runtime, owner }) => {
    await placeEngineer(runtime, owner, 'Ada')
    const receipt = await forkTool.executeHorizonReceipt(runtime, owner)

    assert.equal(receipt.ok, true, 'the roster crosses as a data envelope')
    assert.ok(Array.isArray(receipt.agents), 'the envelope carries the agents roster array')
    assert.ok(Array.isArray(receipt.ptys), 'the envelope carries the ptys roster array')

    const participant = receipt.agents.find((row) => row.label === 'Ada')
    assert.ok(participant, 'the accepted child carries a row under her Byname')
    assert.deepEqual(
      participant,
      { label: 'Ada', status: 'still-away' },
      'a running charge with no delegated estimate carries no pace, never a row of zeros, and no id, target or report body',
    )
  })
})

test('WHAT[participant-horizon-011] the fixed DevOps at rest is idle, never returned or still away', async () => {
  await withForkRuntime(async ({ runtime, owner }) => {
    const roster = await forkTool.executeHorizon(runtime, owner)
    assert.match(roster, /devops is at hand and idle/)
    assert.doesNotMatch(roster, /devops (has returned|is still away)/)
    assert.doesNotMatch(roster, /work record/, 'the roster no longer pads every row with an empty work record line')
    const receipt = await forkTool.executeHorizonReceipt(runtime, owner)
    assert.deepEqual(receipt.agents, [{ label: 'devops', status: 'idle' }])
    assert.match(await forkTool.executeJoin(runtime, owner), /NothingToJoin|nothing away to receive/i, 'idle is nothing join would hand over')
  })
})

// Field regression (wx, 2026-10-28, Manager ses_e7ed7cdb…): horizon said
// "恢复缺口核实 已归来" 37 seconds after join had consumed that report, and
// "devops 已归来" for a devops whose report join had already handed over. The
// old roster read a process-local "completion cell settled" flag instead of the
// ledger join drains. The roster now answers through join's own cut.
test('WHAT[participant-horizon-011] returned means join hands it over; after join it is gone, and a fresh charge reads still away, never returned', async () => {
  const realFork = await import('../../delegation/tests/support/real-fork.mjs')
  const owner = 'manager-horizon-join-ledger'
  await realFork.withRealFork(owner, async ({ hooks, createdIds, runtime }) => {
    const terminals = await realFork.observeTerminals(runtime)
    const horizon = async () => String(await hooks.tool.horizon.execute(
      {},
      { sessionID: owner, agent: 'manager', callID: `call-horizon-${Math.random()}`, messageID: 'msg-horizon' },
    ))
    try {
      await realFork.forkCall(hooks, owner, 'Ada', 'FIRST-CHARGE')
      const child = createdIds[0]
      assert.match(await horizon(), /Ada is still away/)

      await realFork.childTurn(hooks, runtime, child, `run-${child}-1`, 'FIRST ANSWER', 'FIRST ANSWER', terminals.awaitRun)
      const returned = await horizon()
      assert.match(returned, /Ada has returned/)
      assert.doesNotMatch(returned, /FIRST ANSWER/, 'horizon does not hand over the report; join does')

      assert.match(await realFork.joinCall(hooks, owner), /FIRST ANSWER/)
      assert.doesNotMatch(await horizon(), /Ada/, 'a consumed report never reappears as returned')

      await realFork.resumeCall(hooks, owner, 'Ada', 'SECOND-CHARGE')
      const second = await horizon()
      assert.match(second, /Ada is still away/)
      assert.doesNotMatch(second, /Ada has returned/, 'a stale settled run of the previous charge is not a return')

      // The field devops case: its report already went through join, yet the
      // old roster still said "devops has returned" from the settled run cell.
      const { injectAcceptedAssessment } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
      await injectAcceptedAssessment(runtime, owner)
      const devopsReceipt = String(await realFork.resumeCall(hooks, owner, 'devops', 'DEVOPS-CHARGE'))
      assert.match(devopsReceipt, /ok = true/, devopsReceipt)
      const devops = createdIds.find((id) => id !== child)
      assert.ok(devops, `the devops child session is known: ${JSON.stringify(createdIds)}`)
      assert.match(await horizon(), /devops is still away/)
      await realFork.childTurn(hooks, runtime, devops, `run-${devops}-1`, 'DEVOPS ANSWER', 'DEVOPS ANSWER', terminals.awaitRun, 'devops')
      assert.match(await horizon(), /devops has returned/)
      assert.match(await realFork.joinCall(hooks, owner), /DEVOPS ANSWER/)
      const after = await horizon()
      assert.doesNotMatch(after, /devops has returned/, 'a report join already handed over is not a return')
      assert.match(after, /devops is at hand and idle/)
    } finally {
      terminals.dispose()
    }
  })
})

test('WHAT[participant-horizon-011] the roster reads join\'s ledger, never a process-local completion cell', () => {
  const source = readFileSync(new URL('../../../src/Wanxiangshu/Execution/Session/HorizonRoster.fs', import.meta.url), 'utf8')
  assert.match(source, /JoinDrain\.orderedCandidates/, 'returned / did-not-return / voided come from the list join drains')
  assert.match(source, /HostForkJoin\.currentProcessHandle/, 'the same process cut join uses')
  assert.match(source, /HostForkJoin\.hasPendingRun/, 'still away means join would wait for a running charge')
  assert.doesNotMatch(source, /CompletionCellSettled/, 'a settled in-process cell is not a durable return')
})
