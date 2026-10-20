import test from 'node:test'

test.todo('WHAT[structured-workflow-003] actual retirement recovery re-enters its workflow from durable facts without a saved continuation address')

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { TaskResultListSurface_traverseM } = await import("../../../dist/Foundation/FsToolkitFableCompat.js");
const outcomeSurface = await import("../../../dist/Foundation/OutcomeSurface.js");


test('WHAT[structured-workflow-003] OutcomeSurface defines public vocabulary for outcome classification', () => {
  assert.ok(outcomeSurface.sendOutcomeKinds().includes('AdmittedWithReceipt'))
  assert.ok(outcomeSurface.isValidAgentRunResult('terminal text'))
  assert.equal(outcomeSurface.isValidAgentRunResult('   '), false)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const reconcileSurface = await import("../../../dist/Composition/Turn/ReconcileSurface.js");

const evidence = {
  snapshotError: (reason) => reconcileSurface.evidenceSnapshotError(reason),
  noTurn: () => reconcileSurface.evidenceNoTurn(),
  provisional: (outcome) => reconcileSurface.evidenceProvisional(outcome),
  unknown: () => reconcileSurface.evidenceUnknown(),
  terminal: (outcome) => reconcileSurface.evidenceTerminal(outcome),
  sessionCleared: () => reconcileSurface.evidenceSessionCleared(),
}
const wake = {
  idle: (session = 'ses-a', attemptSerial = 1) => reconcileSurface.idleWake(session, attemptSerial),
  retry: () => reconcileSurface.retryWake(),
  failure: () => reconcileSurface.failureWake(),
  abort: () => reconcileSurface.abortWake(),
}
const name = (observation, signal = wake.retry()) =>
  reconcileSurface.decisionName(reconcileSurface.decideStep(signal, observation))

test('WHAT[structured-workflow-003] RECONCILE_PROGRAM_005: TurnUnknown never crosses the stable business-turn boundary', () => {
  // HOST-004 / rabbit §7: TurnUnknown is type-unreachable for publishDecision
  // (not a TurnOutcome). IdleWake + Unknown → Publish (observation
  // handoff only); business repair lives in TurnWorkflow / InteractionRepair.
  const handoff = reconcileSurface.decideStep(wake.idle('ses-a', 1), evidence.unknown())
  assert.equal(reconcileSurface.decisionName(handoff), 'Publish')

  // The owner-level observations keep the clean-break contract without exposing
  // DU constructors or reflection metadata.
  assert.equal(reconcileSurface.isSnapshotObservation('TurnUnknown'), true)
  assert.equal(reconcileSurface.isPublishableOutcome('TurnUnknown'), false)
  assert.equal(reconcileSurface.tryOutcome('TurnUnknown').accepted, false)
  assert.equal(reconcileSurface.tryOutcome('TurnUnknown').name, undefined)
})
test('WHAT[structured-workflow-003] RECONCILE_PROGRAM_007: TurnUnknown is SnapshotObservation, not TurnOutcome', () => {
  // The five accepted JS outcome names are checked through the owner’s stable
  // acceptance result, never through DU case metadata.
  for (const outcome of [
    'TurnInProgress',
    'TurnNeedsContinuation',
    'TurnCompleted',
    'TurnAborted',
    'TurnFailed',
  ]) {
    assert.equal(reconcileSurface.tryOutcome(outcome).accepted, true)
  }
  assert.equal(reconcileSurface.isSnapshotObservation('TurnUnknown'), true)
  assert.equal(reconcileSurface.tryOutcome('TurnUnknown').accepted, false)
  assert.equal(reconcileSurface.isPublishableOutcome('TurnUnknown'), false)

  // outcomeOf must refuse TurnUnknown as a TurnOutcome. The owner returns a
  // rejected observation rather than minting Unknown or collapsing it into
  // TurnFailed.
  const refused = reconcileSurface.tryOutcome('TurnUnknown')
  assert.equal(refused.accepted, false)
  assert.equal(reconcileSurface.isPublishableOutcome('TurnFailed'), true)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { readFileSync } = await import("node:fs");
const { join } = await import("node:path");
const reconcile = await import("../../../dist/Composition/Turn/ReconcileSurface.js");
const { default: test } = await import("node:test");
const { fileURLToPath } = await import("node:url");

const ROOT = new URL('../../../', import.meta.url).pathname
const readSrc = (rel) => readFileSync(join(ROOT, rel), 'utf8')

test('WHAT[structured-workflow-003] SW_009_reconcile_domain_is_observation_stabilization_not_a_program', () => {
  // The registered ReconcileSurface is the owner contract: callers observe
  // one causal observation decision, never emitted union metadata.
  for (const n of [
    'decideStep',
    'decisionName',
    'publishDecision',
    'isTerminalOutcome',
    'consumeKey',
    'clearProvisional',
  ]) {
    assert.equal(typeof reconcile[n], 'function', `ReconcileSurface must export ${n}`)
  }

  // No second-runtime restore surface: no continuation-pointer restore, no
  // program AST, no interpreter. (RECONCILE_PROGRAM_006 pins the same
  // absence for Command/Reply/Trace exports.)
  for (const n of [
    'RestoreContinuation',
    'ResumeProgram',
    'ReplayProgram',
    'ProgramNode',
    'TraceInterpreter',
    'CommandBus',
    'StepAst',
    'materializePass',
    'interpretWith',
    'ProtocolMismatch',
  ]) {
    assert.equal(n in reconcile, false, `ReconcileSurface must not export ${n}`)
  }
})
test('WHAT[structured-workflow-003] SW_009_recovery_surface_drives_ordinary_workflow_entrypoints', () => {
  // Recovery is a permit-gated re-entry into the same named workflows the
  // live path uses (ARCH-005 / ce-temporal-ownership §15–17): the
  // SessionRecoveryWorkflow entry, the provider recovery vocabulary, and the
  // thin per-context TurnWorkflow router. None of them is a stored position.
  //
  // Source-tree proof: each workflow module defines its named entrypoint as a
  // `let` — the direct-CE contract (structured-workflow-001). Build-verification
  // (guide-contract.test.mjs) proves the emitted modules load and the
  // entrypoints are callable.
  const entrypoints = [
    ['src/Wanxiangshu/Participant/Provider/Attempt/Fallback/Workflow.fs', 'continueAfterConfirmedFailure'],
    ['src/Wanxiangshu/Participant/Provider/Attempt/Fallback/Workflow.fs', 'continueDelegateCallAfterConfirmedFailure'],
    ['src/Wanxiangshu/Composition/Turn/Workflow.fs', 'observe'],
    ['src/Wanxiangshu/Mission/Manager/Workflow.fs', 'observe'],
    ['src/Wanxiangshu/Mission/Manager/Workflow.fs', 'observeIdle'],
  ]
  const missing = []
  for (const [file, name] of entrypoints) {
    const source = readSrc(file)
    if (!new RegExp(`\\blet(?: rec)?(?: private)? ${name}\\b`).test(source)) {
      missing.push(`${file}: ${name}`)
    }
  }
  assert.deepEqual(missing, [], `recovery workflow entrypoints must exist in production source: ${missing.join('; ')}`)
})
test('WHAT[structured-workflow-003] SW_009_change_seam_has_no_recovery_control_token_dispatcher', () => {
  // OBL-002: the Change seam must not re-introduce a JobRecoveryAction
  // control-token type, a recoveryAction producer, a resumeFromDurableFacts
  // interpreter, or any equivalent NextAction dispatcher. Recovery re-enters
  // the ordinary CE workflow by matching independent durable facts directly.
  const forbidden = [
    'src/Wanxiangshu/Change/Projection.fs',
    'src/Wanxiangshu/Change/Program.fs',
    'src/Wanxiangshu/Change/Surface.fs',
  ]
  const symbols = [
    /\bJobRecoveryAction\b/,
    /\brecoveryAction\b/,
    /\bresumeFromDurableFacts\b/,
    /\brecoveryFromProgress\b/,
    /\brecoveryFor\b/,
    /\bactionObject\b/,
    /\brecoveryActionView\b/,
  ]
  const violations = []
  for (const file of forbidden) {
    const source = readSrc(file)
    for (const pattern of symbols) {
      if (pattern.test(source)) {
        violations.push(`${file}: ${pattern.source}`)
      }
    }
  }
  assert.deepEqual(violations, [], `Change seam must not re-introduce recovery control tokens: ${violations.join('; ')}`)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { existsSync, readFileSync } = await import("node:fs");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const { fileURLToPath } = await import("node:url");
const outcomeSurface = await import("../../../dist/Foundation/OutcomeSurface.js");

const ROOT = new URL('../../../', import.meta.url).pathname
const readSrc = (rel) => readFileSync(join(ROOT, rel), 'utf8')

test('WHAT[structured-workflow-003] SW_002_workflow_modules_export_no_program_counter_shaped_names', () => {
  // DSL-002 / ARCH-008: the direct workflow owner exposes story entrypoints,
  // never a stored business stage. Source-tree proof: no Stage/Phase/NextAction
  // let bindings in the workflow modules.
  const workflowFiles = [
    'src/Wanxiangshu/Mission/Manager/Workflow.fs',
    'src/Wanxiangshu/Composition/Turn/Workflow.fs',
  ]
  const programCounter = /\b(?:let|type)\s+\w*(?:Stage|Phase|NextAction|CurrentStage|RunState)\b/
  const bad = []
  for (const file of workflowFiles) {
    const source = readSrc(file)
    if (programCounter.test(source)) bad.push(file)
  }
  assert.deepEqual(bad, [], `workflow modules must not define program-counter-shaped names: ${bad.join('; ')}`)
})
test('WHAT[structured-workflow-003] SW_003_domain_flow_and_outcome_types_are_domain_facts', () => {
  // Agent errors remain domain facts. Companion has no generic context/error
  // workflow vocabulary: direct durable and transform owners carry its operations.
  const agentErrors = readSrc('src/Wanxiangshu/Execution/Agent/Errors.fs')
  assert.match(agentErrors, /\btype AgentContext\b/, 'Execution/Agent/Errors must define AgentContext')
  assert.match(agentErrors, /\btype AgentError\b/, 'Execution/Agent/Errors must define AgentError')

  assert.equal(
    existsSync(join(ROOT, 'src/Wanxiangshu/Context/Companion/Errors.fs')),
    false,
    'Context/Companion/Errors.fs must stay deleted with its generic flow facade',
  )

  const companionOwnerFiles = [
    'src/Wanxiangshu/Context/Companion/Model.fs',
    'src/Wanxiangshu/Context/Companion/Runtime.fs',
    'src/Wanxiangshu/Context/Companion/Host.fs',
    'src/Wanxiangshu/Context/Companion/JournalPort.fs',
    'src/Wanxiangshu/Context/Companion/Transform.fs',
  ]
  const companionOwners = companionOwnerFiles.map(readSrc).join('\n')
  assert.doesNotMatch(companionOwners, /\b(?:CompanionProgram|CompanionContext|CompanionError)\b/)
  assert.match(companionOwners, /\bICompanionDurablePort\b/)
  assert.match(companionOwners, /\bapplyCompanionForOrdinaryMaterial\b/)

  // AgentRunResult is the completion payload of a successful agent run
  // (EXEC-006): typed physical facts plus terminal output, validated by
  // IsValid. No transport parts, no stage latch.
  const outcome = readSrc('src/Wanxiangshu/Foundation/Outcome.fs')
  assert.match(outcome, /\btype AgentRunResult\b/, 'Foundation/Outcome must define AgentRunResult')
  assert.match(outcome, /\btype AgentRunFailure\b/, 'Foundation/Outcome must define AgentRunFailure')
  // EXEC-006: IsValid rejects empty terminal text — the JS-native surface
  // exposes this without the test touching Fable record internals.
  assert.equal(outcomeSurface.isValidAgentRunResult(''), false)
  assert.equal(outcomeSurface.isValidAgentRunResult('  '), false)
  assert.equal(outcomeSurface.isValidAgentRunResult('terminal output'), true)

  const sendOutcomeCases = outcomeSurface.sendOutcomeKinds()
  assert.deepEqual(sendOutcomeCases, [
    'AdmittedWithReceipt',
    'AdmittedWithPhysicalMessage',
    'Retryable',
    'AcceptanceUnknown',
    'Fatal',
  ])

  // SessionError names real world conditions (budget spent, prompt
  // uncertain, projection broken, inbox full), not execution steps.
  const sessionErrorCases = outcomeSurface.sessionErrorKinds()
  assert.deepEqual(sessionErrorCases, [
    'NoProgress',
    'SessionCancelled',
    'AutoRecoveryExhausted',
    'ReviewExhausted',
    'PromptUncertain',
    'ProjectionBroken',
    'InboxFull',
    'Protocol',
  ])
})
}
