import assert from 'node:assert/strict'
import test from 'node:test'
import * as fission from '../../../dist/Execution/Fission/Surface.js'
import * as fissionCanaries from '../../../dist/Execution/Fission/Canaries.js'
import * as fissionHost from '../../../dist/OpenCode/Host/FissionHostSurface.js'
import { assertJsData } from '../../verification-system/tests/support/js-contract.mjs'

test('WHAT[intra-participant-parallelism-009] actual turn observers absorb a marked retired owner without continuation or terminal notification', async () => {
  const owner = 'retired-owner-workflow'
  fission.markSilentInterrupt(owner)
  try {
    const observed = await fissionHost.observeReplacedOwner(owner)
    assertJsData(observed, 'Fission host observation')
    assert.deepEqual(observed, { handled: true, continuationSent: false, terminalNotified: false })
  } finally {
    fission.clearOwner(owner)
  }
})

test('WHAT[intra-participant-parallelism-009] an explicitly kicked reconciler observes the supplied completed execution without an idle event', async () => {
  const observed = await fissionCanaries.missingIdleTerminalBridgeScenario()
  assertJsData(observed, 'reconciler observation')
  assert.equal(observed.outcome, 'TurnCompleted')
  assert.equal(observed.physicalUserMessageId, 'fission-missing-idle-user')
  assert.equal(observed.providerRun, 'fission-missing-idle-run')
  assert.ok(observed.snapshotReads >= 1)
})

test.todo('WHAT[intra-participant-parallelism-009] GAP-158: actual terminal-to-Fission bridge, takeover continuation across recovery and one final completion in the original cell')
