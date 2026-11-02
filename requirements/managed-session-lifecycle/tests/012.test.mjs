import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const HostForkPtySurface = await import("../../../dist/Execution/Delegation/Fork/Host/HostForkPtySurface.js");


test('WHAT[managed-session-lifecycle-012] HFP_fork_pty_blank_command_is_refused', async () => {
  const result = await HostForkPtySurface.scenario('blank-fork', '   ', '')
  assert.equal(result.ok, false)
  assert.equal(result.error, 'PTY command is required')
  assert.deepEqual(result.calls, [])
})
test('WHAT[managed-session-lifecycle-012] HFP_fork_pty_tracks_registers_and_resolves_last', async () => {
  const result = await HostForkPtySurface.scenario('fork', 'ls -la', '')
  assert.equal(result.ok, true)
  assert.equal(result.owned, true)
  assert.equal(result.known, true)
})
test('WHAT[managed-session-lifecycle-012] HFP_fork_pty_port_exception_untracks_and_errors', async () => {
  const result = await HostForkPtySurface.scenario('fork-error', 'ls', 'pty spawn exploded')
  assert.equal(result.ok, false)
  assert.equal(result.error, 'pty spawn exploded')
  assert.equal(result.owned, false)
})
test('WHAT[managed-session-lifecycle-012] HFP_try_pty_unknown_string_id_is_none', async () => {
  const result = await HostForkPtySurface.scenario('lookup-unknown', 'foreign', '')
  assert.equal(result.ok, true)
  assert.equal(result.known, false)
})
test('WHAT[managed-session-lifecycle-012] HFP_send_pty_unowned_id_is_unknown', async () => {
  const result = await HostForkPtySurface.scenario('send-unowned', 'echo hi', '')
  assert.equal(result.ok, false)
  assert.equal(result.error, 'Unknown PTY id: foreign')
})
test('WHAT[managed-session-lifecycle-012] HFP_send_pty_owned_but_missing_on_port_is_unknown', async () => {
  const result = await HostForkPtySurface.scenario('send-closed', 'echo hi', '')
  assert.equal(result.ok, false)
  assert.match(result.error, /Unknown PTY id/)
})
test('WHAT[managed-session-lifecycle-012] HFP_send_pty_signal_forwards_signal_command', async () => {
  const result = await HostForkPtySurface.scenario('signal', 'INT', '')
  assert.equal(result.ok, true)
  assert.deepEqual(result.calls, [{ kind: 'signal', signal: 'SIGINT' }])
})
test('WHAT[managed-session-lifecycle-012] HFP_send_pty_write_forwards_write_command', async () => {
  const result = await HostForkPtySurface.scenario('write', 'echo hi', '')
  assert.equal(result.ok, true)
  assert.deepEqual(result.calls, [{ kind: 'write', text: 'echo hi\n' }])
})
test('WHAT[managed-session-lifecycle-012] HFP_send_pty_read_with_empty_prompt', async () => {
  const result = await HostForkPtySurface.scenario('read', '', '')
  assert.equal(result.ok, true)
  assert.equal(result.output, 'terminal text')
  assert.equal(result.closed, true)
})
test('WHAT[managed-session-lifecycle-012] HFP_send_pty_port_error_propagates', async () => {
  const result = await HostForkPtySurface.scenario('write', 'echo hi', 'pty session ended')
  assert.equal(result.ok, false)
  assert.equal(result.error, 'pty session ended')
})
test('WHAT[managed-session-lifecycle-012] HFP_track_untrack_pty_run_round_trip', async () => {
  const result = await HostForkPtySurface.scenario('track-untrack', 'tracked-1', '')
  assert.equal(result.ok, true)
  assert.equal(result.ownedBefore, true)
  assert.equal(result.ownedAfter, false)
})
}
