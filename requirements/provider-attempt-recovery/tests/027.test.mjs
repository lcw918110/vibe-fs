import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import * as Observation from '../../../dist/Composition/Turn/Observation.js'
import * as identity from '../../../dist/Foundation/Identity.js'
import * as roles from '../../../dist/Foundation/Roles.js'
import * as owner from '../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js'
import { providerAttemptStopFence } from 'wanxiangli/failure'

await routing.initialize()

// The fence registers its waiter only after the recovery's journal I/O has
// settled; polling the read-only snapshot makes the Revoke/SignalStop
// interleave deterministic instead of racing the microtask chain.
const waitForPendingStop = async (timeoutMs = 2000) => {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (providerAttemptStopFence.Snapshot().waiting >= 1) return
    await new Promise((resolve) => setTimeout(resolve, 5))
  }
  throw new Error('the stop fence never registered a pending waiter')
}

// An observable session port: every continuation send is recorded, so the
// tests can tell a failure notification from a continuation nudge.
const observableSessionPort = (prompts) => ({
  SendPrompt: async (...args) => {
    prompts.push(args)
    return dispatch.admittedWithReceipt('027-stub-receipt')
  },
})

test('WHAT[provider-attempt-recovery-027] a late failure of a durably closed run stays silent while its stop was already observed', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-recovery-027-closed-'))
  const created = await journal.JournalSurface_acquireSharedForWorkspace(directory, 1, '2026-01-01T00:00:00Z')
  assert.equal(created.ok, true, created.ok ? '' : JSON.stringify(created.error))
  try {
    const accepted = await owner.acceptHumanRoot(created.journal, 'ses-027-closed', 'msg-027-root', 'manager')
    assert.equal(accepted.ok, true)

    // The run closes durably (exact Manager-life retirement) while the
    // failed attempt's stop has already been observed on the fence.
    await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, 'ses-027-closed', 'retire-027-closed')
    providerAttemptStopFence.SignalStop('ses-027-closed', 'run-027-closed')

    const EventsSurface = await import('../../../dist/OpenCode/Host/EventsSurface.js')
    const Workflow = await import('../../../dist/Participant/Provider/Attempt/Fallback/Workflow.js')

    const eventPort = EventsSurface.create()
    const terminals = []
    EventsSurface.subscribe(eventPort, (sessionId, outcome) => terminals.push({ sessionId, outcome }))
    const prompts = []
    const sessionPort = observableSessionPort(prompts)

    const turn = new Observation.ReconciledTurn(
      identity.SessionIdModule_create('ses-027-closed'),
      identity.PhysicalUserMessageIdModule_create('msg-027-physical'),
      identity.AuthorityRootUserMessageIdModule_create('msg-027-root'),
      identity.ProviderRunIdentityModule_create('run-027-closed'),
      roles.Role.Manager,
      null, [] , null, null, null, null, null,
    )

    await Workflow.continueAfterConfirmedFailure(
      sessionPort, null, eventPort, journal.JournalHandle__get_Journal(created.journal), turn,
      { tag: 6 },
      'late provider error',
      'probe continuation prompt',
    )

    // Same run durably closed: neither a failure notification nor a
    // continuation send may reach the session.
    assert.equal(terminals.length, 0, JSON.stringify(terminals))
    assert.equal(prompts.length, 0, JSON.stringify(prompts))
  } finally {
    journal.JournalSurface_dispose(created.journal)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[provider-attempt-recovery-027] after the same-run closure a different root stays the terminal verdict, not a revival', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-recovery-027-afterclose-'))
  const created = await journal.JournalSurface_acquireSharedForWorkspace(directory, 1, '2026-01-01T00:00:00Z')
  assert.equal(created.ok, true, created.ok ? '' : JSON.stringify(created.error))
  try {
    const accepted = await owner.acceptHumanRoot(created.journal, 'ses-027-afterclose', 'msg-027-root-a', 'manager')
    assert.equal(accepted.ok, true)
    await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, 'ses-027-afterclose', 'retire-027-afterclose')

    const verdict = await owner.continueSameProviderRecoveryVerdict(created.journal, 'ses-027-afterclose', 'msg-027-root-b', 'run-027')
    assert.equal(verdict, 'Terminal')
  } finally {
    journal.JournalSurface_dispose(created.journal)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[provider-attempt-recovery-027] a late failure of a run closed before recovery starts stays silent on the entrance guard', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-recovery-027-chain-'))
  const created = await journal.JournalSurface_acquireSharedForWorkspace(directory, 1, '2026-01-01T00:00:00Z')
  assert.equal(created.ok, true, created.ok ? '' : JSON.stringify(created.error))
  try {
    const accepted = await owner.acceptHumanRoot(created.journal, 'ses-027-chain', 'msg-027-chain-root', 'manager')
    assert.equal(accepted.ok, true)
    const established = await owner.establishProviderRun(created.journal, 'ses-027-chain', 'msg-027-chain-physical', 'run-027-chain')
    if (established && established.ok === false) throw new Error('establishProviderRun rejected: ' + JSON.stringify(established))
    const recorded = await owner.recordConfirmedFailure(created.journal, 10, 'ses-027-chain', 'run-027-chain', 'provider_error')
    if (!recorded || recorded.ok !== true || recorded.outcome !== 'RetryAuthorized') throw new Error('recordConfirmedFailure did not durably establish the confirmed failure record: ' + JSON.stringify(recorded))

    // The run closes durably before the late failure's recovery begins, and
    // the fence has already observed the stop when the retry would look.
    await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, 'ses-027-chain', 'retire-027-chain')
    providerAttemptStopFence.SignalStop('ses-027-chain', 'run-027-chain')

    const EventsSurface = await import('../../../dist/OpenCode/Host/EventsSurface.js')
    const Workflow = await import('../../../dist/Participant/Provider/Attempt/Fallback/Workflow.js')
    const eventPort = EventsSurface.create()
    const terminals = []
    EventsSurface.subscribe(eventPort, (sessionId, outcome) => terminals.push({ sessionId, outcome }))
    const prompts = []
    const sessionPort = observableSessionPort(prompts)

    const turn = new Observation.ReconciledTurn(
      identity.SessionIdModule_create('ses-027-chain'),
      identity.PhysicalUserMessageIdModule_create('msg-027-chain-physical'),
      identity.AuthorityRootUserMessageIdModule_create('msg-027-chain-root'),
      identity.ProviderRunIdentityModule_create('run-027-chain'),
      roles.Role.Manager,
      null, [], null, null, null, null, null,
    )

    await Workflow.continueAfterConfirmedFailure(
      sessionPort, null, eventPort, journal.JournalHandle__get_Journal(created.journal), turn,
      { tag: 6 },
      'late provider error',
      'probe continuation prompt',
    )

    // Same run durably closed: neither a failure notification nor a
    // continuation send may reach the session.
    assert.equal(terminals.length, 0, JSON.stringify(terminals))
    assert.equal(prompts.length, 0, JSON.stringify(prompts))
  } finally {
    journal.JournalSurface_dispose(created.journal)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[provider-attempt-recovery-027] without the closure fact the same late failure stays a real terminal notification', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-recovery-027-open-'))
  const created = await journal.JournalSurface_acquireSharedForWorkspace(directory, 1, '2026-01-01T00:00:00Z')
  assert.equal(created.ok, true, created.ok ? '' : JSON.stringify(created.error))
  try {
    const accepted = await owner.acceptHumanRoot(created.journal, 'ses-027-open', 'msg-027-open-root', 'manager')
    assert.equal(accepted.ok, true)
    const established = await owner.establishProviderRun(created.journal, 'ses-027-open', 'msg-027-open-physical', 'run-027-open')
    if (established && established.ok === false) throw new Error('establishProviderRun rejected: ' + JSON.stringify(established))
    const recorded = await owner.recordConfirmedFailure(created.journal, 10, 'ses-027-open', 'run-027-open', 'provider_error')
    if (!recorded || recorded.ok !== true || recorded.outcome !== 'RetryAuthorized') throw new Error('recordConfirmedFailure did not durably establish the confirmed failure record: ' + JSON.stringify(recorded))

    // No closure fact: the run is still open, so the late failure must stay
    // real. The fence waiter is confirmed registered before the revoke, so
    // the wait is denied deterministically, without the 30s timeout.
    const EventsSurface = await import('../../../dist/OpenCode/Host/EventsSurface.js')
    const Workflow = await import('../../../dist/Participant/Provider/Attempt/Fallback/Workflow.js')
    const eventPort = EventsSurface.create()
    const terminals = []
    EventsSurface.subscribe(eventPort, (sessionId, outcome) => terminals.push({ sessionId, outcome }))
    const prompts = []
    const sessionPort = observableSessionPort(prompts)

    const turn = new Observation.ReconciledTurn(
      identity.SessionIdModule_create('ses-027-open'),
      identity.PhysicalUserMessageIdModule_create('msg-027-open-physical'),
      identity.AuthorityRootUserMessageIdModule_create('msg-027-open-root'),
      identity.ProviderRunIdentityModule_create('run-027-open'),
      roles.Role.Manager,
      null, [], null, null, null, null, null,
    )

    const running = Workflow.continueAfterConfirmedFailure(
      sessionPort, null, eventPort, journal.JournalHandle__get_Journal(created.journal), turn,
      { tag: 6 },
      'late provider error',
      'probe continuation prompt',
    )
    await waitForPendingStop()
    providerAttemptStopFence.Revoke('ses-027-open')
    await running

    assert.equal(terminals.length, 1, JSON.stringify(terminals))
    assert.equal(terminals[0].outcome.kind, 'Failed')
    // The real failure surfaces as a terminal, never converted into a nudge.
    assert.equal(prompts.length, 0, JSON.stringify(prompts))
  } finally {
    journal.JournalSurface_dispose(created.journal)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[provider-attempt-recovery-027] a run closed while the retry waits on the stop fence stays silent at the stopped branch', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-recovery-027-during-'))
  const created = await journal.JournalSurface_acquireSharedForWorkspace(directory, 1, '2026-01-01T00:00:00Z')
  assert.equal(created.ok, true, created.ok ? '' : JSON.stringify(created.error))
  try {
    const accepted = await owner.acceptHumanRoot(created.journal, 'ses-027-during', 'msg-027-during-root', 'manager')
    assert.equal(accepted.ok, true)
    const established = await owner.establishProviderRun(created.journal, 'ses-027-during', 'msg-027-during-physical', 'run-027-during')
    if (established && established.ok === false) throw new Error('establishProviderRun rejected: ' + JSON.stringify(established))
    const recorded = await owner.recordConfirmedFailure(created.journal, 10, 'ses-027-during', 'run-027-during', 'provider_error')
    if (!recorded || recorded.ok !== true || recorded.outcome !== 'RetryAuthorized') throw new Error('recordConfirmedFailure did not durably establish the confirmed failure record: ' + JSON.stringify(recorded))

    const EventsSurface = await import('../../../dist/OpenCode/Host/EventsSurface.js')
    const Workflow = await import('../../../dist/Participant/Provider/Attempt/Fallback/Workflow.js')
    const eventPort = EventsSurface.create()
    const terminals = []
    EventsSurface.subscribe(eventPort, (sessionId, outcome) => terminals.push({ sessionId, outcome }))
    const prompts = []
    const sessionPort = observableSessionPort(prompts)

    const turn = new Observation.ReconciledTurn(
      identity.SessionIdModule_create('ses-027-during'),
      identity.PhysicalUserMessageIdModule_create('msg-027-during-physical'),
      identity.AuthorityRootUserMessageIdModule_create('msg-027-during-root'),
      identity.ProviderRunIdentityModule_create('run-027-during'),
      roles.Role.Manager,
      null, [], null, null, null, null, null,
    )

    // The closure lands while the recovery's stop wait is pending: the
    // waiter is confirmed registered first, then the run closes and the
    // fence is signaled. The stopped branch must stay silent.
    const running = Workflow.continueAfterConfirmedFailure(
      sessionPort, null, eventPort, journal.JournalHandle__get_Journal(created.journal), turn,
      { tag: 6 },
      'late provider error',
      'probe continuation prompt',
    )
    await waitForPendingStop()
    await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, 'ses-027-during', 'retire-027-during')
    providerAttemptStopFence.SignalStop('ses-027-during', 'run-027-during')
    await running

    assert.equal(terminals.length, 0, JSON.stringify(terminals))
    assert.equal(prompts.length, 0, JSON.stringify(prompts))
  } finally {
    journal.JournalSurface_dispose(created.journal)
    rmSync(directory, { recursive: true, force: true })
  }
})
