import assert from 'node:assert/strict'
import test from 'node:test'
import { FissionGroupLaw, FissionLaneRegistry, fissionLaneRegistry } from 'wanxiangli/fission-group'

test('WHAT[intra-participant-parallelism-009] late physical completion after FissionConverged and clearOwner is safely rejected without mutating terminal', () => {
  const law = new FissionGroupLaw()
  const registry = new FissionLaneRegistry()

  const groupId = 'group-post-convergence'
  const ownerSessionId = 'owner-engineer-1'
  const laneSessions = ['lane-subsession-0', 'lane-subsession-1']

  // 1. Initial admission and binding
  registry.bindLane(groupId, ownerSessionId, 0, 2, laneSessions[0])
  registry.bindLane(groupId, ownerSessionId, 1, 2, laneSessions[1])
  assert.equal(registry.getLaneCount(), 2)
  assert.equal(registry.logicalOwner(laneSessions[0]), ownerSessionId)

  // Law admits group
  const admitVerdict = law.judge({
    event: 'admitted',
    existing: null,
    laneCount: 2,
    sessionCount: 2,
    promptCount: 2,
    ownerActive: false,
  })
  assert.deepEqual(admitVerdict, { verdict: 'set' })

  // Lanes materialize work
  assert.deepEqual(
    law.judge({
      event: 'laneMaterialized',
      laneCount: 2,
      laneIndex: 0,
      laneSession: 'match',
      work: null,
    }),
    { verdict: 'set' }
  )
  assert.deepEqual(
    law.judge({
      event: 'laneMaterialized',
      laneCount: 2,
      laneIndex: 1,
      laneSession: 'match',
      work: null,
    }),
    { verdict: 'set' }
  )

  const doneProgress = {
    workedLanes: [0, 1],
    deliveries: [],
  }

  // 2. Converged terminal reached
  const convergeVerdict = law.judge({
    event: 'converged',
    laneCount: 2,
    terminal: 'Open',
    terminalSame: false,
    progress: doneProgress,
    takeoverLane: null,
  })
  assert.deepEqual(convergeVerdict, { verdict: 'set' })

  // 3. clearOwner cleans process-local registry
  const cleanup = registry.clearOwner(ownerSessionId)
  assert.deepEqual([...cleanup.RemovedLanes].sort(), [...laneSessions].sort())
  assert.deepEqual(cleanup.RemovedGroupIds, [groupId])
  assert.equal(registry.getLaneCount(), 0)

  // 4. Invariant Assertion: Late physical arrival of PTY or Agent completion at this instant
  // A. Mapping is gone: late callback cannot resolve lane or owner binding from memory
  assert.equal(registry.tryLane(laneSessions[0]), undefined)
  assert.equal(registry.tryOwner(laneSessions[0]), undefined)
  assert.equal(registry.logicalOwner(laneSessions[0]), laneSessions[0]) // fails to own

  // B. FissionGroupLaw strictly rejects any late completion against the converged terminal
  const lateCompletionDelivered = law.judge({
    event: 'completionDelivered',
    laneCount: 2,
    laneIndex: 0,
    captured: false, // late uncaptured completion
  })
  assert.deepEqual(lateCompletionDelivered, { verdict: 'reject', rejection: 'ConflictingGroup' })

  // C. Late turn completion attempt is rejected as TerminalAlreadySet
  const lateConverged = law.judge({
    event: 'converged',
    laneCount: 2,
    terminal: 'Converged',
    terminalSame: false,
    progress: doneProgress,
    takeoverLane: null,
  })
  assert.deepEqual(lateConverged, { verdict: 'reject', rejection: 'TerminalAlreadySet' })

  const lateFailure = law.judge({
    event: 'failed',
    laneCount: 2,
    terminal: 'Converged',
    terminalSame: false,
  })
  assert.deepEqual(lateFailure, { verdict: 'reject', rejection: 'TerminalAlreadySet' })

  // D. Settle decision for an external observation after terminal cannot yield new work
  const settleDecision = law.settle({ stage: 'takeover', observation: 'Completed' })
  assert.equal(settleDecision.decision, 'complete')
})

test('WHAT[intra-participant-parallelism-009] failed terminal similarly locks group against late physical callback revival', () => {
  const law = new FissionGroupLaw()
  const registry = new FissionLaneRegistry()

  registry.bindLane('g-failed', 'owner-eng-2', 0, 2, 'l-0')
  registry.bindLane('g-failed', 'owner-eng-2', 1, 2, 'l-1')

  // Group failed
  const failVerdict = law.judge({
    event: 'failed',
    laneCount: 2,
    terminal: 'Open',
    terminalSame: false,
  })
  assert.deepEqual(failVerdict, { verdict: 'set' })

  // Owner cleared
  registry.clearOwner('owner-eng-2')

  // Late arrival cannot revive or re-materialize
  const lateMaterialize = law.judge({
    event: 'laneMaterialized',
    laneCount: 2,
    laneIndex: 0,
    laneSession: 'match',
    work: 'different',
  })
  assert.deepEqual(lateMaterialize, { verdict: 'reject', rejection: 'ConflictingLaneWork' })

  // Cannot re-fail or converge
  assert.deepEqual(
    law.judge({ event: 'converged', laneCount: 2, terminal: 'Failed', terminalSame: false, progress: { workedLanes: [], deliveries: [] }, takeoverLane: null }),
    { verdict: 'reject', rejection: 'TerminalAlreadySet' }
  )
})
