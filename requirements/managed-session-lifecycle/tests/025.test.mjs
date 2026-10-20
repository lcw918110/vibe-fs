import assert from 'node:assert/strict'
import test from 'node:test'
import * as PtySurface from '../../../dist/Execution/Delegation/Fork/Host/HostForkPtySurface.js'
import { withAdmittedChildren } from './support/admitted-child-work.mjs'

const devopsReturnDrain = label => withAdmittedChildren(label, 'manager-session', [
  { agentId: 'devops', sessionId: 'devops-session', role: 'devops' },
], journal => PtySurface.scenario('devops-return-drain', '', '', journal))

test('WHAT[managed-session-lifecycle-025] controlled PTY port completion clears actual HostForkRuntime ownership and name bookkeeping', async () => {
  // Scenario 1: fork a PTY, bind terminal name, assert outstanding before exit,
  // Complete the controlled backend port; this fixture starts no OS process.
  const res = await PtySurface.scenario('exit-cleanup', '', '')
  assert.equal(res.ok, true, 'exit-cleanup scenario must succeed')
  assert.equal(res.bindOk, true, 'terminal name must be bound successfully')

  // Before physical exit: PTY was tracked, owned, and findable by terminal name
  assert.ok(res.outstandingBefore.includes(res.id), 'SnapshotOutstandingPtyRuns must contain PTY before exit')
  assert.equal(res.ownedBefore, true, 'OwnsPty must be true before exit')
  assert.equal(res.byNameBefore, res.id, 'TryPtyByName must resolve to PTY id before exit')

  // After physical exit: HostForkRuntime bookkeeping is immediately cleared
  assert.equal(res.outstandingAfter.includes(res.id), false, 'SnapshotOutstandingPtyRuns must not contain PTY after exit')
  assert.equal(res.ownedAfter, false, 'OwnsPty must be false after exit')
  assert.equal(res.byNameAfter, undefined, 'TryPtyByName must be None after exit')
})

test('WHAT[managed-session-lifecycle-025] failed PTY spawn records NotStarted and leaves no phantom ownership lease', async () => {
  const res = await PtySurface.scenario('fork-error', 'broken-cmd', 'spawn failed')
  assert.equal(res.ok, false, 'fork-error scenario must report failure')
  assert.equal(res.owned, false, 'failed spawn must not leave an unsettled PTY lease in SnapshotOutstandingPtyRuns')
})

test('WHAT[managed-session-lifecycle-025] DevOps run terminal settlement closes every PTY the DevOps child owns at the port boundary', async () => {
  // Observable boundary: the PTY backend port is where owner-initiated cleanup
  // becomes a physical effect. The DevOps child's own PTY must receive that
  // terminate effect and be gone from its runtime; the engineer session keeps its
  // own PTY alive. Asserting the effect and the resulting counts — not which
  // helper issued it — keeps this test honest under internal refactors.
  const res = await devopsReturnDrain('devops-terminal-drain')
  assert.equal(res.ok, true, 'devops-return-drain scenario must succeed')

  assert.ok(res.devopsBefore.includes(res.devopsPtyId), 'DevOps must own a PTY before the run returns')
  assert.ok(res.engineerBefore.includes(res.engineerPtyId), 'Engineer must own a PTY before the DevOps run returns')

  assert.deepEqual(
    res.devopsAfter, [],
    'every PTY the DevOps child owns must be released when its run reaches its terminal settlement',
  )
  assert.ok(res.engineerAfter.includes(res.engineerPtyId), 'the engineer session PTY must survive the DevOps run settlement')

  const devopsTerminate = res.calls.filter(call => call.kind === 'signal' && call.signal === 'SIGTERM')
  assert.equal(
    devopsTerminate.length >= 1,
    true,
    'settling the DevOps run must terminate its owned PTY at the port boundary instead of leaving the process alive',
  )
})

test('WHAT[managed-session-lifecycle-025] fixed DevOps work return drains its PTY residue without affecting other runtimes', async () => {
  // Scenario 2: DevOps session holds an active PTY, another session (engineer) holds an active PTY.
  // When the DevOps run completes its terminal settlement on return, its PTYs are drained and cleared.
  // The engineer session PTY remains active and unaffected.
  const res = await devopsReturnDrain('devops-fixed-drain')
  assert.equal(res.ok, true, 'devops-return-drain scenario must succeed')

  // Before settlement: both DevOps and Engineer have outstanding PTYs
  assert.ok(res.devopsBefore.includes(res.devopsPtyId), 'DevOps must have outstanding PTY before return')
  assert.ok(res.engineerBefore.includes(res.engineerPtyId), 'Engineer must have outstanding PTY')

  // After DevOps run settlement: DevOps PTY is drained and cleared
  assert.equal(res.devopsAfter.length, 0, 'DevOps PTYs must be drained upon run return')
  assert.equal(res.devopsAfter.includes(res.devopsPtyId), false)

  // Unrelated Engineer PTY is untouched and remains alive
  assert.ok(res.engineerAfter.includes(res.engineerPtyId), 'Engineer PTY must not be affected by DevOps drain')
})

test('WHAT[managed-session-lifecycle-025] ToolRuntimeScope production wiring drains DevOps child PTYs on run complete', async () => {
  const scopeMod = await import('../../../dist/OpenCode/Tools/ToolRuntimeScopeSurface.js')
  const verify = scopeMod.verifyDevOpsReturnDrain || (scopeMod.ToolRuntimeScopeSurface && scopeMod.ToolRuntimeScopeSurface.verifyDevOpsReturnDrain)
  assert.equal(typeof verify, 'function', 'verifyDevOpsReturnDrain must be exported on ToolRuntimeScope surface')

  const res = await withAdmittedChildren('scope-devops-drain', 'manager-road-1', [
    { agentId: 'devops', sessionId: 'devops-session-1', role: 'devops' },
    { agentId: 'engineer-1', sessionId: 'engineer-session-1', role: 'engineer' },
  ], journal => verify(journal, {
    managerSessionId: 'manager-road-1',
    devopsChildSessionId: 'devops-session-1',
    engineerChildSessionId: 'engineer-session-1',
    devopsPtys: ['devops-pty-1'],
    engineerPtys: ['eng-pty-1'],
    completeRole: 'devops',
  }))

  assert.ok(res.devopsBefore.includes('devops-pty-1'), 'DevOps child must have outstanding PTY before return')
  assert.ok(res.engineerBefore.includes('eng-pty-1'), 'Engineer child must have outstanding PTY')
  assert.equal(res.devopsAfter.length, 0, 'DevOps child PTY must be drained on run complete via ToolRuntimeScope wiring')
  assert.ok(res.engineerAfter.includes('eng-pty-1'), 'Engineer child PTY must remain alive and unaffected')
})

test('WHAT[managed-session-lifecycle-025] TERM must be followed by real process exit, and escalate to KILL when the grace window elapses; the controlled PTY port starts no OS process, so real exit and the KILL escalation cannot be observed in this harness (GAP-133)', { todo: 'GAP-133: controlled PTY port starts no OS process; real process exit and KILL escalation require physical OS PTY harness' }, () => {
  // WHAT[managed-session-lifecycle-025]:
  // 固定 DevOps 每次 run 终态结算，立即收束其拥有的全部 PTY：TERM 后等待真实退出，必要时升级 KILL，随后清除记账。
  // 本 harness 使用受控 PTY port，未启动真实 OS 进程，真实 exit 与 KILL 升级证据保留 GAP-133 物理边界。
})
