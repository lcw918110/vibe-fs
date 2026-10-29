import assert from 'node:assert/strict'
import test from 'node:test'
import * as join from '../../../../dist/Execution/Delegation/Fork/JoinSurface.js'

test('WHAT[delegation-026] HostForkRuntime integrates PendingRunStore with initial zero counts and probe observation', async () => {
  const probe = join.createJoinProbe()
  const counts = join.joinProbeCounts(probe)
  assert.equal(counts.pendingRuns, 0)
  assert.equal(counts.pendingCompletions, 0)
  assert.equal(counts.pendingPtys, 0)
  join.joinProbePulseWake(probe)
  join.joinProbeCancel(probe)
  const countsAfter = join.joinProbeCounts(probe)
  assert.equal(countsAfter.pendingRuns, 0)
})
