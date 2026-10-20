import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const policy = await import("../../../dist/Host/Contract/CompactionPolicySurface.js");

const settings = policy.requiredSettings()
const nextReanchor = (observed, reanchored) => {
  const handled = new Set(reanchored)
  return policy.nextReanchor(observed, (runId) => handled.has(runId))
}

test('WHAT[context-compression-002] HOST_006_prevention_layer_names_every_setting_that_must_be_off', () => {
  // The whole set is asserted, not each item's presence. A missing entry is a path by
  // which the Host can still rewrite context on its own, and dropping one turns no
  // test red unless the set itself is the assertion.
  assert.deepEqual(settings.map((setting) => setting.path), ['compaction.auto', 'compaction.prune', 'compaction.autocontinue'])

  for (const setting of settings) {
    assert.equal(setting.required, false, `${setting.path} must be required off`)
    assert.ok(setting.reason.length > 0, `${setting.path} must state why it matters`)
  }
})
test('WHAT[context-compression-002] COMPANION_009_prune_is_listed_and_says_why_containment_cannot_save_it', () => {
  // `prune` was not in the frozen clause; X0 found it while reading the Host source.
  // It bypasses the transform boundary and deletes persisted rows, and containment
  // cannot repair that: a deleted row is not a voided index, it is absent.
  const prune = settings.find((s) => s.path === 'compaction.prune')

  assert.equal(prune.clause, 'COMPANION-009')
  assert.match(prune.reason, /cannot be reanchored/)
})
test('WHAT[context-compression-002] HOST_006_autocontinue_is_answered_false_rather_than_left_to_the_default', () => {
  // `auto = false` already makes the replay branch unreachable, so this is belt and
  // braces. Answering explicitly matters because the hook is the only vetoable
  // synthetic-turn injection point; staying silent relies on an upstream default.
  assert.equal(policy.autoContinueEnabled(), false)
})
test('WHAT[context-compression-002] HOST_006_a_setting_that_cannot_be_written_fails_startup_with_its_reason', () => {
  const verdict = policy.judgeFirstTurnWithUnavailable('compaction.auto', 'ses_x', 0)

  assert.equal(verdict.kind, 'SettingUnavailable')
  assert.match(verdict.message, /HostContractUnsupported/)
  assert.match(verdict.message, /compaction\.auto/)

  // The reason travels with the message, because an operator reading
  // "compaction.auto could not be disabled" needs to know what breaks.
  assert.match(verdict.message, /overflow\.ts:28/)
})
test('WHAT[context-compression-002] HOST_006_startup_probe_passes_when_settings_are_off_and_the_first_turn_is_clean', () => {
  const verdict = policy.judgeFirstTurn('ses_x', 0)

  assert.equal(verdict.kind, 'Satisfied')
})
test('WHAT[context-compression-002] HOST_006_a_compaction_on_the_first_turn_means_a_second_implementation', () => {
  // A first turn is necessarily far below any threshold, so an automatic compaction
  // there cannot be legitimate.
  const verdict = policy.judgeFirstTurn('ses_probe', 1)

  assert.equal(verdict.kind, 'CompactedDespiteSettings')
  assert.match(verdict.message, /ses_probe/)
  assert.match(verdict.message, /first turn/)

  // Why refuse startup when containment exists: an unpreventable automatic compaction
  // grinds the mechanism into uselessness — reanchoring every few rounds means probe
  // coverage never accumulates, while everything looks normal from outside.
  assert.match(verdict.message, /no visible symptom/)
})
test('WHAT[context-compression-002] HOST_006_the_setting_check_takes_precedence_over_the_turn_observation', () => {
  // When both fail, report the unavailable setting: that is the root cause, and the
  // pseudo-run is its consequence. The other order sends an operator looking for a
  // second implementation that does not exist.
  const verdict = policy.judgeFirstTurnWithUnavailable('compaction.auto', 'ses_x', 3)

  assert.equal(verdict.kind, 'SettingUnavailable')
})
test('WHAT[context-compression-002] HOST_006_containment_keys_on_the_folded_predicate_not_raw_fields', () => {
  // The three raw fields (agent / mode / summary) are already folded into
  // `IsCompaction` at the snapshot boundary. Only the folded answer is accepted here:
  // re-deriving it would be a second definition of the observation the entire
  // containment layer keys on.
  assert.equal(policy.isContainableCompaction(true), true)
  assert.equal(policy.isContainableCompaction(false), false)
})
test('WHAT[context-compression-002] HOST_006_the_newest_unhandled_compaction_is_the_one_to_reanchor', () => {
  // The newest, because it is the one whose numbering the current transcript reflects.
  assert.equal(nextReanchor(['msg_c1', 'msg_c2', 'msg_c3'], []), 'msg_c3')
})
test('WHAT[context-compression-002] HOST_006_at_most_one_reanchor_is_emitted_per_observation', () => {
  const single = nextReanchor(['msg_c1', 'msg_c2', 'msg_c3', 'msg_c4'], [])

  assert.equal(typeof single, 'string')
  assert.equal(single, 'msg_c4')
})
test('WHAT[context-compression-002] HOST_006_an_already_reanchored_compaction_is_not_reanchored_again', () => {
  // Two observations of one pseudo-run must produce one retirement. This is the first
  // of two guards; the second is PrefixEpochProjection's epoch check (see
  // prefix-epoch.test.mjs).
  assert.equal(nextReanchor(['msg_c1'], ['msg_c1']), null)
  assert.equal(nextReanchor(['msg_c1', 'msg_c2'], ['msg_c2']), 'msg_c1')
  assert.equal(nextReanchor(['msg_c1', 'msg_c2'], ['msg_c1', 'msg_c2']), null)
})
test('WHAT[context-compression-002] HOST_006_no_observed_compaction_means_nothing_to_do', () => {
  assert.equal(nextReanchor([], []), null)
  assert.equal(nextReanchor([], ['msg_c1']), null)
})
test('WHAT[context-compression-002] HOST_006_a_new_compaction_after_a_handled_one_is_still_caught', () => {
  // A session that was already reanchored once, then genuinely compacted again. It has
  // to be recognised, or a second manual /compact would silently disable probes for
  // the rest of the session's life.
  assert.equal(nextReanchor(['msg_c1', 'msg_c2'], ['msg_c1']), 'msg_c2')
})
}
