import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { default: fc } = await import("fast-check");
const ReconcileSurface = await import("../../../dist/Composition/Turn/ReconcileSurface.js");

const idleWake = ReconcileSurface.idleWake('s1', 1n)

test('WHAT[host-boundary-005] exact failure and idle evidence survive in either order', () => {
  const failure = ReconcileSurface.failureWakeFor('msg-current')

  assert.equal(ReconcileSurface.mergeWakeKind('msg-current', failure, idleWake), 'IdleFailureWake')
  assert.equal(ReconcileSurface.mergeWakeKind('msg-current', idleWake, failure), 'IdleFailureWake')
  assert.equal(
    ReconcileSurface.mergeWakeKind('', failure, ReconcileSurface.failureWake()),
    'FailureWake',
    'a coarse failure without identity cannot erase an exact failed physical',
  )
  assert.equal(
    ReconcileSurface.mergeWakeKind('', failure, idleWake),
    'FailureWake',
    'missing process-local binding is not evidence that the exact failed physical was superseded',
  )
  assert.equal(
    ReconcileSurface.mergeWakeKind('msg-next', failure, idleWake),
    'IdleWake',
    'a new physical user message must release the old failure wake',
  )
  assert.equal(ReconcileSurface.mergeWakeKind('msg-current', failure, ReconcileSurface.abortWake()), 'AbortWake')
})
test('WHAT[host-boundary-005] coarse failure without physical binding cannot publish a terminal turn', async () => {
  const result = await ReconcileSurface.unboundFailureScenario()
  assert.equal(result.snapshotReads, 0)
})
test('WHAT[host-boundary-005] EXEC_reconcile_projection_edge_drives_exactly_one_additional_idle_read', async () => {
  const result = await ReconcileSurface.idleProjectionEdgeScenario()

  assert.deepEqual(result, {
    snapshotReads: 2,
    providerRun: 'projection-edge-current-run',
    outcome: 'TurnCompleted',
    hasQuiescence: true,
  })
})
test('WHAT[host-boundary-005] physical ingress fences a held old snapshot while the next wake publishes the human turn', async () => {
  const result = await ReconcileSurface.physicalIngressDuringSnapshotScenario()

  assert.deepEqual(result, {
    snapshotReads: 2,
    publishedBeforeHumanRead: 0,
    observedSnapshots: [[
      'held-snapshot-old',
      'held-snapshot-old-run',
      'held-snapshot-human',
      'held-snapshot-human-run',
    ]],
    published: [{
      physical: 'held-snapshot-human',
      providerRun: 'held-snapshot-human-run',
      outcome: 'TurnCompleted',
      hasQuiescence: true,
    }],
  })
})
test('WHAT[host-boundary-005] EXEC_reconcile_without_projection_edge_reads_once_and_exposes_no_counter', async () => {
  const result = await ReconcileSurface.idleProvisionalWithoutProjectionEdgeScenario()
  assert.deepEqual(result, {
    snapshotReads: 1,
    observed: true,
    outcome: 'TurnInProgress',
  })

  const decision = ReconcileSurface.decideStep(
    ReconcileSurface.retryWake(),
    ReconcileSurface.evidenceProvisional('TurnInProgress'),
  )
  assert.deepEqual(decision, { name: 'StopPass' })
})
test('WHAT[host-boundary-005] EXEC_reconcile_projection_edge_delivers_the_next_failed_provider_run_to_AABB', async () => {
  const result = await ReconcileSurface.failureProjectionEdgeScenario()

  assert.deepEqual(result, {
    snapshotReads: 2,
    providerRun: 'projection-edge-current-run',
    outcome: 'TurnFailed',
    hasQuiescence: false,
  })
})
test('WHAT[host-boundary-005] EXEC_provider_failure_with_exact_current_assistant_does_not_wait_for_terminal_projection', async () => {
  const result = await ReconcileSurface.failureWitnessCurrentAssistantScenario()

  assert.deepEqual(result, {
    snapshotReads: 1,
    observed: true,
    providerRun: 'failure-witness-current-run',
    outcome: 'TurnFailed',
    reason: 'Bad Request: input_invalid',
    hasQuiescence: false,
  })
})
test('WHAT[host-boundary-005] EXEC_reconcile_snapshot_error_and_no_turn_stop_current_pass', () => {
  const error = ReconcileSurface.decideStep(
    idleWake,
    ReconcileSurface.evidenceSnapshotError('projection unavailable'),
  )
  assert.equal(ReconcileSurface.decisionName(error), 'StopPass')

  const noTurn = ReconcileSurface.decideStep(
    ReconcileSurface.failureWake(),
    ReconcileSurface.evidenceNoTurn(),
  )
  assert.equal(ReconcileSurface.decisionName(noTurn), 'StopPass')
})
test('WHAT[host-boundary-005] EXEC_only_idle_can_publish_a_nonterminal_current_assistant', () => {
  const idle = ReconcileSurface.decideStep(
    idleWake,
    ReconcileSurface.evidenceProvisional('TurnInProgress'),
  )
  assert.equal(ReconcileSurface.decisionName(idle), 'Publish')

  for (const wake of [
    ReconcileSurface.retryWake(),
    ReconcileSurface.failureWake(),
    ReconcileSurface.abortWake(),
  ]) {
    const decision = ReconcileSurface.decideStep(
      wake,
      ReconcileSurface.evidenceProvisional('TurnInProgress'),
    )
    assert.equal(ReconcileSurface.decisionName(decision), 'StopPass')
  }
})
test('WHAT[host-boundary-005] mutation_canary_terminal_evidence_still_publishes', () => {
  const decision = ReconcileSurface.decideStep(
    ReconcileSurface.failureWake(),
    ReconcileSurface.evidenceTerminal('TurnCompleted'),
  )
  assert.equal(ReconcileSurface.decisionName(decision), 'Publish')
})
test('WHAT[host-boundary-005] terminal provider failure publishes only with matching typed physical witness', () => {
  const terminal = ReconcileSurface.evidenceTerminalFor('failed-physical', 'TurnFailed')

  assert.equal(
    ReconcileSurface.decisionName(
      ReconcileSurface.decideStep(ReconcileSurface.failureWakeFor('failed-physical'), terminal),
    ),
    'Publish',
  )
  assert.equal(
    ReconcileSurface.decisionName(
      ReconcileSurface.decideStep(ReconcileSurface.failureWakeFor('other-physical'), terminal),
    ),
    'StopPass',
  )
  assert.equal(
    ReconcileSurface.decisionName(ReconcileSurface.decideStep(ReconcileSurface.retryWake(), terminal)),
    'StopPass',
  )
  assert.equal(
    ReconcileSurface.decisionName(ReconcileSurface.decideStep(idleWake, terminal)),
    'StopPass',
    'idle observed before session.error must not publish a provider failure without its typed witness',
  )
})
test('WHAT[host-boundary-005] failure witness is retained and idle evidence is preserved', () => {
  const physical = 'msg-failure-property'
  const terminal = ReconcileSurface.evidenceTerminalFor(physical, 'TurnFailed')
  const coarseWake = fc.constantFrom(ReconcileSurface.retryWake(), ReconcileSurface.idleWake('ses-failure-property'))

  fc.assert(
    fc.property(fc.array(coarseWake, { maxLength: 40 }), (incomingWakes) => {
      assert.equal(
        ReconcileSurface.decisionName(ReconcileSurface.decideStep(ReconcileSurface.failureWakeFor(physical), terminal)),
        'Publish',
      )
      for (const wake of incomingWakes) {
        assert.equal(
          ReconcileSurface.mergeWakeKind(physical, ReconcileSurface.failureWakeFor(physical), wake),
          wake.kind === 'IdleWake' ? 'IdleFailureWake' : 'FailureWake',
        )
        assert.equal(ReconcileSurface.decisionName(ReconcileSurface.decideStep(wake, terminal)), 'StopPass')
      }
    }),
    { seed: 0x484f5354, numRuns: 100 },
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const EventsSurface = await import("../../../dist/OpenCode/Host/EventsSurface.js");
const ReconcileSurface = await import("../../../dist/Composition/Turn/ReconcileSurface.js");
const HostSignalSubscribeSurface = await import("../../../dist/OpenCode/Host/HostSignalSubscribeSurface.js");

process.env.WANXIANGSHU_NO_FATAL_EXIT = '1'
const notify = (port, sessionId, outcome) => EventsSurface.notify(port, sessionId, outcome.kind, outcome.providerRun ?? '', outcome.error ?? outcome.value ?? '')
const completed = (providerRun = '') => ({ kind: 'Completed', providerRun })
const failed = (error) => ({ kind: 'Failed', error })
const idleWake = ReconcileSurface.idleWake('s1', 1n)

test('WHAT[host-boundary-005] EXEC_reconcile_error_never_self_polls', () => {
  const decision = ReconcileSurface.decideStep(
    idleWake,
    ReconcileSurface.evidenceSnapshotError('provider unavailable'),
  )
  assert.equal(ReconcileSurface.decisionName(decision), 'StopPass')
})
test('WHAT[host-boundary-005] EXEC_reconcile_idle_is_the_only_nonterminal_publish_authority', () => {
  const idle = ReconcileSurface.decideStep(idleWake, ReconcileSurface.evidenceProvisional('TurnInProgress'))
  assert.equal(ReconcileSurface.decisionName(idle), 'Publish')

  const failure = ReconcileSurface.decideStep(
    ReconcileSurface.failureWake(),
    ReconcileSurface.evidenceProvisional('TurnInProgress'),
  )
  assert.equal(ReconcileSurface.decisionName(failure), 'StopPass')
})
test('WHAT[host-boundary-005] EXEC_reconcile_idle_provisional_publishes', () => {
  const decision = ReconcileSurface.decideStep(idleWake, ReconcileSurface.evidenceProvisional('TurnInProgress'))
  assert.equal(ReconcileSurface.decisionName(decision), 'Publish')
})
test('WHAT[host-boundary-005] EXEC_reconcile_unknown_under_idle_wake_publishes', () => {
  // Unknown (finish=None) under IdleWake → Publish.
  // Under Retry/Failure/Abort wake → StopPass.
  const idleDecision = ReconcileSurface.decideStep(idleWake, ReconcileSurface.evidenceUnknown())
  assert.equal(ReconcileSurface.decisionName(idleDecision), 'Publish')

  const retryDecision = ReconcileSurface.decideStep(ReconcileSurface.retryWake(), ReconcileSurface.evidenceUnknown())
  assert.equal(ReconcileSurface.decisionName(retryDecision), 'StopPass')
})
test('WHAT[host-boundary-005] EXEC_reconcile_session_cleared_stops', () => {
  const decision = ReconcileSurface.decideStep(idleWake, ReconcileSurface.evidenceSessionCleared())
  assert.equal(ReconcileSurface.decisionName(decision), 'StopPass')
})
test('WHAT[host-boundary-005] mutation_canary_snapshot_error_must_stop_pass', () => {
  // SnapshotError must never Publish or self-authorize another read.
  const decision = ReconcileSurface.decideStep(idleWake, ReconcileSurface.evidenceSnapshotError('e'))
  assert.equal(ReconcileSurface.decisionName(decision), 'StopPass',
    'mutation guard: SnapshotError must StopPass, not Publish or Reread')
})
}
