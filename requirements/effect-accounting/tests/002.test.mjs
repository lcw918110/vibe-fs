import assert from 'node:assert/strict'
import test from 'node:test'
import * as lifecycleModule from '../../../dist/Execution/Delegation/Fork/Host/HostForkRunLifecycleSurface.js'

const lifecycle = {
  create: lifecycleModule.HostForkRunLifecycleSurface_create,
  complete: lifecycleModule.HostForkRunLifecycleSurface_complete,
  observe: lifecycleModule.HostForkRunLifecycleSurface_observe,
  completion: lifecycleModule.HostForkRunLifecycleSurface_completion,
}

const makeRun = (agentId, childId, parentId) => lifecycle.create({ agentId, childId, parentId })

const remainsPending = async (run, message) => {
  const observed = lifecycle.observe(run)
  assert.equal(observed.finished, false, `${message} must not settle the run`)
  assert.equal(observed.pending, true, 'run must stay pending for a later proven terminal')

  await lifecycle.complete(run, { kind: 'Completed', terminalText: 'later proven completion' })
  assert.equal((await lifecycle.completion(run)).status, 'completed', 'the later proven result must settle the original completion cell')
}

test('WHAT[effect-accounting-002] EXEC_join_MissingFinalReport_Failed_keeps_run_pending_not_failed', async () => {
  const run = lifecycleModule.HostForkRunLifecycleSurface_create({ agentId: 'agent-mfr', childId: 'ses_mfr_child', parentId: 'ses_mfr_parent' })
  await lifecycleModule.HostForkRunLifecycleSurface_complete(run, { kind: 'Failed', message: 'MISSING_FINAL_REPORT' })
  await remainsPending(run, 'MISSING_FINAL_REPORT')
})

test('WHAT[effect-accounting-002] EXEC_join_empty_Completed_keeps_run_pending_not_failed', async () => {
  const run = makeRun('agent-empty', 'ses_empty_child', 'ses_empty_parent')
  await lifecycle.complete(run, { kind: 'Completed', terminalText: '' })
  await remainsPending(run, 'empty Completed')
})

test('WHAT[effect-accounting-002] EXEC_join_interaction_repair_exhausted_settles_the_run', async () => {
  const run = makeRun('agent-repair-exhausted', 'ses_repair_exhausted_child', 'ses_repair_exhausted_parent')
  await lifecycle.complete(run, { kind: 'Failed', message: 'INTERACTION_REPAIR_EXHAUSTED' })

  const observed = lifecycle.observe(run)
  assert.equal(observed.finished, true, 'bounded repair exhaustion is a terminal failure, not another repair opportunity')
  assert.equal(observed.pending, false)
  assert.deepEqual(await lifecycle.completion(run), {
    status: 'failed',
    agentId: 'agent-repair-exhausted',
    code: 'ERROR',
    message: 'INTERACTION_REPAIR_EXHAUSTED',
  })
})

test('WHAT[effect-accounting-002] EXEC_join_real_Failed_still_claims_run', async () => {
  const run = makeRun('agent-real', 'ses_real_child', 'ses_real_parent')
  await lifecycle.complete(run, { kind: 'Failed', message: 'provider timeout' })

  const observed = lifecycle.observe(run)
  assert.equal(observed.finished, true, 'real failure must still claim the run')
  assert.equal(observed.pending, false)
  assert.deepEqual(await lifecycle.completion(run), {
    status: 'failed',
    agentId: 'agent-real',
    code: 'ERROR',
    message: 'provider timeout',
  })
})

test('WHAT[effect-accounting-002] EXEC_join_first_proven_terminal_is_single_assignment', async () => {
  const failedFirst = makeRun('agent-failed-first', 'ses_failed_first_child', 'ses_failed_first_parent')
  await lifecycle.complete(failedFirst, { kind: 'Failed', message: 'providers exhausted' })
  await lifecycle.complete(failedFirst, { kind: 'Completed', terminalText: 'late success' })
  assert.deepEqual(await lifecycle.completion(failedFirst), {
    status: 'failed',
    agentId: 'agent-failed-first',
    code: 'ERROR',
    message: 'providers exhausted',
  })

  const completedFirst = makeRun('agent-completed-first', 'ses_completed_first_child', 'ses_completed_first_parent')
  await lifecycle.complete(completedFirst, { kind: 'Completed', terminalText: 'owned success' })
  await lifecycle.complete(completedFirst, { kind: 'Failed', message: 'late stale failure' })
  assert.deepEqual(await lifecycle.completion(completedFirst), {
    status: 'completed',
    agentId: 'agent-completed-first',
    view: '',
  })
})
