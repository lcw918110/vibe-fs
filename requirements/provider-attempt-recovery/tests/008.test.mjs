import assert from 'node:assert/strict'
import test from 'node:test'
import * as compression from '../../../dist/Context/Companion/CompressionSurface.js'
import * as reconcile from '../../../dist/Composition/Turn/ReconcileSurface.js'
import * as turns from '../../../dist/Interaction/Repair/CompletedTurnSurface.js'

const planner = compression.attemptPlanner

test('WHAT[provider-attempt-recovery-008] idle arriving before exact empty provider error never dispatches interaction repair', async () => {
  for (const parts of [[], [{ type: 'text', text: '<tool_call>read</tool_call>' }], [{ type: 'text', text: 'partial answer' }]]) {
    const result = await reconcile.providerErrorIdleRaceScenario({
      info: {
        id: 'failed-provider-run', sessionID: 'failed-session', parentID: 'failed-physical',
        role: 'assistant', agent: 'devops', time: { created: 1, completed: 2 },
        error: { name: 'UnknownError', data: { message: 'Cloud Code Assist API error (400): input token exceeds 1048576', statusCode: 400 } },
      },
      parts,
    })
    assert.equal(result.idleDeliveries, 0, 'idle must not hand the errored turn to interaction repair')
    assert.equal(result.deliveries, 1, 'exact provider error produces one failure delivery')
    assert.equal(result.terminal.providerRun, 'failed-provider-run')
    assert.equal(result.terminal.outcome, 'TurnFailed')
    assert.equal(result.terminal.hasQuiescence, false, 'the failure carries no idle repair permission')
    assert.equal(result.failure, 'ProviderTransient')
  }
})

test('WHAT[provider-attempt-recovery-008] errored empty and XML-only terminals wait for provider recovery, never idle repair', () => {
  for (const parts of [[], [{ type: 'reasoning', text: 'unfinished thoughts' }], [{ type: 'text', text: '<tool_call>read</tool_call>' }]]) {
    const classified = turns.classifyOutcome(true, null, 'UnknownError', parts)
    assert.equal(classified.kind, 'TurnFailed')
    const evidence = reconcile.evidenceTerminalFor('failed-physical', classified.kind)
    assert.equal(reconcile.decisionName(reconcile.decideStep(reconcile.idleWake('failed-session'), evidence)), 'StopPass')
    assert.equal(reconcile.decisionName(reconcile.decideStep(reconcile.failureWakeFor('failed-physical'), evidence)), 'Publish')
  }
  for (const parts of [[], [{ type: 'text', text: '<tool_call>read</tool_call>' }]]) {
    assert.equal(turns.classifyOutcome(true, 'stop', null, parts).kind, 'TurnNeedsContinuation', 'invalid content without a provider error remains repairable')
    assert.equal(turns.classifyOutcome(true, null, 'AbortError', parts).kind, 'TurnAborted', 'operator cancellation must not become provider failure')
    assert.equal(turns.classifyOutcome(false, 'error', 'UnknownError', parts).kind, 'TurnFailed')
  }
})

test('WHAT[provider-attempt-recovery-008] terminal validity distinguishes unusable content from an ordinary answer', () => {
  assert.deepEqual(compression.terminalValidity(''), { valid: false, rejection: 'Empty' })
  assert.deepEqual(compression.terminalValidity('<tool_call>do</tool_call>'), {
    valid: false,
    rejection: 'XmlOnly',
  })
  assert.deepEqual(compression.terminalValidity('a real answer'), { valid: true, rejection: null })

})

test('WHAT[provider-attempt-recovery-008] unusable content alone cannot substitute for a confirmed provider failure', () => {
  // A confirmed provider class still terminalizes the attempt; the policy owns
  // whether that becomes a licensed retry or a terminal.
  assert.equal(reconcile.failureWitnessMintsTerminal('ProviderTransient', false), true)
  assert.equal(reconcile.failureWitnessMintsTerminal('ProviderPermanent', false), true)

  // No confirmed provider class plus unusable formal content: the witness must
  // not mint TurnFailed — the turn stays with bounded Interaction Repair.
  for (const label of [
    'ProtocolRejection',
    'LocalInvariant',
    'StreamInterruptedAfterFirstToken',
    'AuthorizationDenied',
    'UserCancelled',
    'Superseded',
  ]) {
    assert.equal(reconcile.failureWitnessMintsTerminal(label, false), false, label)
    assert.equal(reconcile.failureWitnessMintsTerminal(label, true), true, label)
  }
})
{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const turns = await import("../../../dist/Interaction/Repair/CompletedTurnSurface.js");

const text = (value) => ({ type: 'text', text: value })
const reasoning = (value) => ({ type: 'reasoning', text: value })

test('WHAT[provider-attempt-recovery-008] RECON_formal_content_gate_is_shared_with_terminal_validity', () => {
  assert.equal(turns.formalContentUnusable(null), true)
  assert.equal(turns.formalContentUnusable([]), true)
  assert.equal(turns.formalContentUnusable([reasoning('only thoughts')]), true)
  assert.equal(turns.formalContentUnusable([text('   ')]), true)
  assert.equal(turns.formalContentUnusable([text('<tool_call>read</tool_call>')]), true)
  assert.equal(turns.formalContentUnusable([text('a real answer')]), false)
  assert.equal(turns.formalContentUnusable([text('a real answer'), reasoning('and thinking')]), false)
})

test.todo('WHAT[provider-attempt-recovery-008] actual unfinished Manager and other repairable roles reach one repair without spending provider budget (GAP-139)')
}
