import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const child = await import("../../../dist/Execution/Delegation/Fork/ChildRecoverySurface.js");

const aborted = ['aborted:host abort']

test('WHAT[effect-accounting-007] P0_RECOVERY_JOIN_001_aborted_alone_is_not_terminal', () => {
  assert.equal(child.resolve('active', 'missing', aborted, '').result, 'RecoveryIncomplete')
})
test('WHAT[effect-accounting-007] P0_RECOVERY_JOIN_001_aborted_observed_never_joinable', () => {
  const result = child.resolve('active', 'active', ['aborted:signal only'], '')
  assert.notEqual(result.result, 'RecoveredTerminal')
  assert.equal(result.result, 'RecoveryIncomplete')
})
test('WHAT[effect-accounting-007] P0_RECOVERY_JOIN_001_aborted_with_session_active_is_recovered_active', () => {
  assert.equal(child.resolve('active', 'active', ['aborted:stale abort', 'active'], '').result, 'RecoveredActive')
})
test('WHAT[effect-accounting-007] P0_RECOVERY_JOIN_001_mid_turn_snapshot_active_with_session_active_is_recovered_active', () => {
  assert.equal(child.resolve('active', 'active', ['active'], '').result, 'RecoveredActive')
})
test('WHAT[effect-accounting-007] P0_RECOVERY_JOIN_001_true_unreadable_is_recovery_incomplete', () => {
  assert.equal(child.resolve('active', 'unreadable', ['active'], '').result, 'RecoveryIncomplete')
})
test('WHAT[effect-accounting-007] P0_RECOVERY_JOIN_001_tryFromProvenTerminal_rejects_empty_body', () => {
  assert.equal(child.provenTerminal('').ok, false)
})
test('WHAT[effect-accounting-007] P0_RECOVERY_JOIN_001_proven_terminal_then_joinable', () => {
  assert.deepEqual(child.provenTerminal('{"status":"ok"}'), {
    ok: true,
    finality: 'Succeeded',
    body: '{"status":"ok"}',
  })
  assert.equal(child.resolve('active', 'terminal', ['aborted:prior abort'], 'body-ok').result, 'RecoveredTerminal')
})
test('WHAT[effect-accounting-007] P0_RECOVERY_JOIN_001_durable_completed_awaiting_join_is_joinable', () => {
  assert.equal(child.resolve('completed', 'missing', ['aborted:noise'], 'body-ok').result, 'RecoveredTerminal')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const clean = await import("../../../dist/Execution/Delegation/Fork/CleanBreakSurface.js");
const join = await import("../../../dist/Execution/Delegation/Fork/OpenCode/JoinSurface.js");
const handles = await import("../../../dist/Execution/Delegation/Handle/Surface.js");

const legacy = () => clean.legacyBody('run-legacy-abort')

test('WHAT[effect-accounting-007] P0_CLEAN_BREAK_agent_join_wire_never_renders_aborted', () => {
  const wire = clean.joinWire('coder', 'host abort was observation, not finality')
  assert.ok(!wire.includes('status = "aborted"'))
  assert.match(wire, /# coder could not complete the charge\./)
  assert.match(wire, /host abort was observation, not finality/)
  assert.ok(!/\bstatus\s*=/.test(wire))
})
test('WHAT[effect-accounting-007] P0_CLEAN_BREAK_tryFromDurableCompleted_refuses_send_failure_aborted_body', () => {
  const result = clean.tryDecode('h-false-abort', legacy())
  assert.equal(result.ok, false)
  assert.match(result.error, /legacy false abort|not a joinable completion/i)
})
test('WHAT[effect-accounting-007] P0_CLEAN_BREAK_legacy_aborted_blob_decodes_without_run_completion', () => {
  assert.deepEqual(clean.decode(legacy()), { case: 'LegacyFalseAbort' })
  assert.equal(clean.tryDecode('h-false-abort', legacy()).ok, false)
})
test('WHAT[effect-accounting-007] P0_CLEAN_BREAK_v2_terminal_decodes_as_joinable_completion', () => {
  const body = JSON.stringify({
    schemaVersion: 2,
    finality: 'completed',
    run_id: 'run-2',
    view: 'ok',
    child_session_id: 'child',
    authority_root: 'root',
    provider_run: 'provider',
    directory: '',
  })
  assert.deepEqual(clean.decode(body), { case: 'Current' })
  assert.equal(clean.tryDecode('h-v2', body).ok, true)
})
test('WHAT[effect-accounting-007] P0_CLEAN_BREAK_retired_legacy_abort_never_surfaces_aborted', () => {
  const wire = join.renderBatch('english', [{ kind: 'abandoned', agentId: 'a1', agentName: 'engineer', role: 'Engineer', reason: 'legacy abort' }])
  assert.match(wire, /did not return/)
  assert.ok(!wire.includes('aborted'))
  assert.ok(!wire.includes('status ='))
})
test('WHAT[effect-accounting-007] P0_CLEAN_BREAK_fold_replay_keeps_retired_terminal_tombstone', () => {
  const state = handles.crashScenario('replayed-retired')
  assert.equal(state.lifecycle, 'Retired')
  assert.equal(state.retired, true)
  assert.equal(state.joinable, 0)
})
test('WHAT[effect-accounting-007] P0_CLEAN_BREAK_invalid_blob_keeps_join_waiting', () => {
  assert.deepEqual(clean.decode('{not-json'), { case: 'Invalid' })
  assert.equal(clean.tryDecode('h-invalid', '{not-json').ok, false)
})
}
