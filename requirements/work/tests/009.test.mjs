import assert from 'node:assert/strict'
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { Work } from 'wanxiangli/work'
import { withReview, scores } from './support/plugin.mjs'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as scopeMod from '../../../dist/OpenCode/Tools/ToolRuntimeScopeSurface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'

const life = lifecycle.LifecycleSurface ?? lifecycle
const verifyOwnership =
  scopeMod.verifyOwnershipRetirementContract ||
  (scopeMod.ToolRuntimeScopeSurface && scopeMod.ToolRuntimeScopeSurface.verifyOwnershipRetirementContract)

test('WHAT[work-009] a live owned resource blocks completion without retiring', () => {
  const work = new Work('work-009', undefined)
  const step = (id, updates, blockers) =>
    work.execute(id, {
      type: 'Review',
      office: 'Manager',
      providerRunId: 'run',
      toolCallId: id,
      updates,
      ...(blockers ? { blockers } : {}),
    })
  step('r1', [{ key: 'job', value: 'do' }])
  const relayed = step('r2', [{ key: 'job', value: null }])
  assert.equal(relayed.result.kind, 'Relayed')
  work.execute('confirm-relay', { type: 'Confirm', effectId: relayed.result.effect.id, outcome: 'Sent' })
  const advanced = step('r3', [])
  assert.equal(advanced.result.milestone, '收尾')
  work.execute('confirm-advance', { type: 'Confirm', effectId: advanced.result.effect.id, outcome: 'Sent' })
  const before = work.records().length
  const blocked = step('r4', [], ['pty'])
  assert.equal(blocked.status, 'Observed')
  assert.equal(blocked.result.kind, 'Blocked')
  assert.equal(work.records().length, before)
  assert.equal(work.history().flatMap((frame) => frame.events).some((event) => event.type === 'IncumbencyRetired' && event.outcome === 'Completed'), false)
})

test('WHAT[work-009] deliver reports the live blocker instead of reporting an end', async () => {
  await withReview(async ({ hooks, runtime, session, execute }) => {
    await execute(scores('REVISE'))
    // 派工是编程面成员：`this.fork(payload, extra = [])`，宿主不再注册 fork 工具。
    const forked = await hooks.tool['todowrite-for-manager'].execute(
      {
        program:
          'class Js extends JsProgram { async run() { return await this.fork({ calling: "engineer", name: "Ada", charge: "implement" }, []); } }',
        name: 'fork-ada',
        intent: 'Wait for the manager fork receipt before checking the live delivery blocker.',
        remainingScopeTurnsEstimate: 3,
      },
      { sessionID: session, agent: 'manager' },
    )
    assert.match(String(forked), /ok = true/)
    assert.match(String(forked), /name = "Ada"/)
    assert.match(String(forked), /outcome = "created"/)
    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(runtime.directory)
    const relayed = await life.submitReview(
      commonDir,
      session,
      'relay-1',
      'Manager',
      'run-relay',
      'call-relay',
      [{ key: 'target-gap', value: null }],
      null,
      '',
    )
    assert.equal(relayed.kind, 'Relayed', relayed.reason)
    const advanced = await life.submitReview(
      commonDir,
      session,
      'advance-1',
      'Manager',
      'run-advance',
      'call-advance',
      [],
      null,
      '',
    )
    assert.equal(advanced.kind, 'Advanced', advanced.reason)
    assert.equal(advanced.milestone, '收尾')
    const before = life.inspected(commonDir, session)
    const beforeRetirements = Array.from(before.Retirements ?? [])
    const standalone = await hooks.tool.review.execute(
      { findings: [] },
      { sessionID: session, agent: 'manager', callID: 'standalone-close', messageID: 'standalone-run' },
    )
    assert.match(String(standalone), /Blocked/,
      'the registered review tool must observe the same live child as the program member')
    // 收尾的空 review 遇到仍在运行的 fork，记为 Blocked，不写退休。
    const blocked = await execute([], { call: 'close-call', run: 'close-run' })
    assert.match(String(blocked), /Blocked/)
    assert.doesNotMatch(String(blocked), /Finished\. Do not continue work\./)
    const after = life.inspected(commonDir, session)
    const retirements = Array.from(after.Retirements ?? [])
    assert.equal(retirements.length, beforeRetirements.length)
    assert.equal(retirements.some((item) => item.Outcome === 'Completed' || item.Outcome === 'Delivered'), false)
    assert.equal(after.Delivered, false)
    assert.equal(after.Stage, '收尾')
    assert.ok(after.ActiveId)
  }, { session: 'blocked-second-deliver' })
})

test('WHAT[work-009] ToolRuntimeScope ownership tracks tool-owned-work and PTY leases, keeps blockers across Cancel/Drain, and clears only on Ended', async () => {
  assert.equal(typeof verifyOwnership, 'function', 'verifyOwnershipRetirementContract must be exported')
  const res = await verifyOwnership({
    managerSessionId: 'manager-road-009',
    incumbentId: 'tenure-009',
    devopsSessionId: 'devops-009',
    engineerSessionId: 'engineer-009',
  })

  assert.equal(res.admittedWork, true)
  assert.equal(
    res.blockersWithWorkAndPty.some((b) => b.startsWith('tool-owned-work:')),
    true,
    'active tool-owned-work lease must appear in RetirementBlockersFor',
  )
  assert.equal(
    res.blockersWithWorkAndPty.includes('fork:engineer-009:pty:eng-pty-1'),
    true,
    'active engineer PTY lease must appear in RetirementBlockersFor',
  )
  assert.equal(
    res.blockersWithWorkAndPty.some((b) => b.includes('devops-pty-1')),
    false,
    'fixed DevOps PTY belongs to DevOps scope and must not block Manager tenure retirement',
  )

  // Cancel request does not decrement or clear unsettled blockers
  assert.deepEqual(
    res.blockersAfterCancelRequest,
    res.blockersWithWorkAndPty,
    'requesting cancellation on a PTY lease must not remove it from blockers before physical exit',
  )

  // Tenure freeze uses Ownership Drain and clears on Unfreeze
  assert.equal(res.frozeFirst, true)
  assert.equal(res.frozenWhileDraining, true)
  assert.equal(res.frozenAfterUnfreeze, false)

  // Ending owned work removes only the tool-owned-work lease
  assert.equal(
    res.blockersAfterWorkEnded.some((b) => b.startsWith('tool-owned-work:')),
    false,
    'settled tool-owned-work lease must be removed from blockers',
  )
  assert.equal(
    res.blockersAfterWorkEnded.includes('fork:engineer-009:pty:eng-pty-1'),
    true,
    'unsettled engineer PTY must remain a blocker until its own Ended',
  )

  // Ending engineer PTY clears the remaining tenure blocker while DevOps PTY survives
  assert.deepEqual(res.blockersAfterEngineerPtyEnded, [])
  assert.deepEqual(res.devopsPtysSurviving, ['devops-pty-1'])
})

test('WHAT[work-009] the standalone review can complete cleanup when no owned resource remains', async () => {
  await withReview(async ({ hooks, runtime, session, directory }) => {
    const review = async (id, findings) => String(await hooks.tool.review.execute(
      { findings },
      { sessionID: session, agent: 'manager', callID: id, messageID: `run-${id}` },
    ))
    assert.match(await review('clear', [{ key: 'target-gap', value: null }]), /kind = "Relayed"/)
    assert.match(await review('advance', []), /kind = "Advanced"/)
    const before = runtime.abortedIds.filter(id => id === session).length
    assert.match(await review('finish', []), /kind = "Completed"/)
    assert.equal(runtime.abortedIds.filter(id => id === session).length, before + 1)
    const root = await journalSurface.JournalSurface_initializeStateRoot(directory)
    assert.equal(life.inspected(root, session).Delivered, true)
  }, { session: 'standalone-cleanup-009' })
})

test('WHAT[work-009] durable ownership host commits Reserve before granting start permission and retains blockers across Cancel and Drain', () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-ownership-009-'))
  const commonDir = join(root, '.Herdchestra')
  mkdirSync(commonDir, { recursive: true })
  try {
    const reg = 'work-009'
    const openWork = life.executeOwnershipDurable(commonDir, reg, 'open-root', {
      type: 'OpenScope',
      scopeId: reg,
      parentId: null,
    })
    assert.equal(openWork.Status, 'Committed')

    const reserve = life.executeOwnershipDurable(commonDir, reg, 'reserve-pty-1', {
      type: 'Reserve',
      resourceId: 'pty-1',
      scopeId: reg,
      kind: 'process',
    })
    const lease = life.mayStartResource(reserve)
    assert.ok(lease, 'first Committed Reserve must yield a start lease')

    const replayReserve = life.executeOwnershipDurable(commonDir, reg, 'reserve-pty-1', {
      type: 'Reserve',
      resourceId: 'pty-1',
      scopeId: reg,
      kind: 'process',
    })
    assert.equal(replayReserve.Status, 'Replayed')
    assert.equal(life.mayStartResource(replayReserve), undefined, 'Replayed Reserve must not grant a second physical start')

    const started = life.executeOwnershipDurable(commonDir, reg, 'start-pty-1', {
      type: 'Started',
      lease: { registryId: lease.RegistryId, resourceId: lease.ResourceId, token: lease.Token },
      binding: { externalId: 'pid-101', incarnationId: 'inc-1' },
    })
    assert.equal(started.Status, 'Committed')

    // Cancel and Drain must not clear unsettled blockers
    life.executeOwnershipDurable(commonDir, reg, 'cancel-pty-1', {
      type: 'Cancel',
      lease: { registryId: lease.RegistryId, resourceId: lease.ResourceId, token: lease.Token },
    })
    life.executeOwnershipDurable(commonDir, reg, 'drain-root', {
      type: 'Drain',
      scopeId: reg,
    })
    const blockersWhileDraining = life.ownershipBlockersDurable(commonDir, reg, reg)
    assert.equal(blockersWhileDraining.length, 1)
    assert.equal(blockersWhileDraining[0].CancellationRequested, true)

    // Physical Ended clears the blocker
    const ended = life.executeOwnershipDurable(commonDir, reg, 'end-pty-1', {
      type: 'Ended',
      lease: { registryId: lease.RegistryId, resourceId: lease.ResourceId, token: lease.Token },
      binding: { externalId: 'pid-101', incarnationId: 'inc-1' },
      outcome: 'Cancelled',
    })
    assert.equal(ended.Status, 'Committed')
    assert.equal(life.ownershipBlockersDurable(commonDir, reg, reg).length, 0)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

