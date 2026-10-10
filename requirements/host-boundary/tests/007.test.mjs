import assert from 'node:assert/strict'
import test from 'node:test'
import * as HostSignalSurface from '../../../dist/OpenCode/Host/HostSignalSurface.js'
import * as CompactionPolicySurface from '../../../dist/Host/Contract/CompactionPolicySurface.js'

const requiredSettings = CompactionPolicySurface.requiredSettings()

const judgeFirstTurn = (pseudoRuns) => CompactionPolicySurface.judgeFirstTurn('ses_probe', pseudoRuns)

test('WHAT[host-boundary-007] HOST_006_prevention_requires_compaction_settings_off_and_autocontinue_off', () => {
  assert.deepEqual(requiredSettings.map((s) => s.path), ['compaction.auto', 'compaction.prune', 'compaction.autocontinue'])
  assert.deepEqual(requiredSettings.map((s) => s.required), [false, false, false])
  assert.equal(CompactionPolicySurface.autoContinueEnabled(), false)
})

test('WHAT[host-boundary-007] HOST_006_first_turn_probe_is_the_only_startup_verdict', () => {
  assert.equal(judgeFirstTurn(0).kind, 'Satisfied')
  assert.equal(judgeFirstTurn(1).kind, 'CompactedDespiteSettings')
})

test('WHAT[host-boundary-007] HOST_006_containment_folds_observation_and_reanchors_newest_unhandled_once', () => {
  assert.equal(CompactionPolicySurface.isContainableCompaction(true), true)
  assert.equal(CompactionPolicySurface.nextReanchor(['run_8'], () => false), 'run_8')
  assert.equal(CompactionPolicySurface.nextReanchor(['run_8'], () => true), null)
})

test('WHAT[host-boundary-007] HOST_006_first_turn_window_ends_at_the_first_completed_assistant', () => {
  const window = (messages) => CompactionPolicySurface.firstTurnCompactionRuns(messages)

  // A compaction before the first completed assistant refuses startup.
  assert.equal(
    window([
      { completedAssistant: false, compaction: false },
      { completedAssistant: false, compaction: true },
      { completedAssistant: true, compaction: false },
    ]),
    1,
  )

  // The boundary message itself counts: the compaction is the first completion seen.
  assert.equal(window([{ completedAssistant: true, compaction: true }]), 1)
  assert.equal(judgeFirstTurn(window([{ completedAssistant: true, compaction: true }])).kind, 'CompactedDespiteSettings')

  // A user /compact or later-round compaction after the window is containment work.
  assert.equal(
    window([
      { completedAssistant: true, compaction: false },
      { completedAssistant: true, compaction: true },
    ]),
    0,
  )
  assert.equal(
    judgeFirstTurn(
      window([
        { completedAssistant: true, compaction: false },
        { completedAssistant: true, compaction: true },
      ]),
    ).kind,
    'Satisfied',
  )

  // The first turn is still open: there is no window to judge yet.
  assert.equal(window([{ completedAssistant: false, compaction: true }]), null)
})
