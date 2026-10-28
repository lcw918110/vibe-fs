import assert from 'node:assert/strict'
import test from 'node:test'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import * as dispatcher from '../../../dist/Interaction/Dispatch/Dispatcher.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as authority from '../../../dist/Interaction/Authority/RuntimeSurface.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as identity from '../../../dist/Foundation/Identity.js'
import * as adapter from '../../../dist/Composition/Durable/PromptJournalAdapter.js'
import * as personas from '../../../dist/Participant/Persona/Surface.js'

// AcceptPhysicalAgentOwnerRoot validates the owner's identity seed twice: once
// before the PromptPhysicalAccepted fact persists, and once after (the fold is
// the only writer; every read goes to the journal's projection). The window
// between the two validations is where the owner can durably close.
//
// The test drives the real dispatcher against a real journal. The wrapper
// journal delegates every member to the production PromptJournalAdapter and
// only makes one existing await observable: after the real Append of the
// child's PromptPhysicalAccepted has succeeded, it signals the test and holds
// that append's return. Inside the window the test closes the owner through
// the only legal durable closure path (lifecycle review/relay/advance/close +
// appendAuthorityLogicalRunClosed), then releases the wrapper so the persisted
// fact returns to the dispatcher and the second validation runs.
//
// Asserted:
// - the second validation rejects with OwnerAuthorityNotActive (matched by
//   name inside the describeIdentitySeedRejection text) and the root is never
//   registered: activeLogicalRun stays null and no AuthorityRootAccepted
//   exists for the child;
// - the PromptPhysicalAccepted fact itself is durable: physicalLandingCount 1
//   after a cold reopen of the same directory, with no pending claim left;
// - a cold reopen and a replayed accept both stay failed and authority-free.
//
// NOT asserted here: that a real host can interleave the closure inside this
// window. The wrapper only makes the dispatcher's existing await observable;
// host-side timing belongs to other runs. The live-owner success path of the
// same accept is already covered by interaction-authority 017.
//
// Run: node --test requirements/dispatch-protocol/tests/016.test.mjs
// (single-file command shape unverified; DevOps builds dist and runs it).

const FResult = (result) =>
  result.tag === 0 ? { ok: true, value: result.fields[0] } : { ok: false, error: result.fields[0] }

const withSharedJournal = async (label, action) => {
  const directory = mkdtempSync(join(tmpdir(), `wxs-dispatch-\${label}-`))
  execFileSync('git', ['init', '--quiet', directory])
  let handle
  const open = async () => {
    const result = await journal.JournalSurface_acquireSharedForWorkspace(directory, process.pid, '2026-10-28T00:00:00Z')
    assert.equal(result.ok, true, JSON.stringify(result.error))
    handle = result.journal
    return handle
  }
  const reopen = async () => {
    journal.JournalSurface_dispose(handle)
    handle = null
    return open()
  }
  try { return await action(await open(), reopen, directory) }
  finally {
    if (handle) journal.JournalSurface_dispose(handle)
    rmSync(directory, { recursive: true, force: true })
  }
}

const rootSelection = () => {
  const resolved = personas.resolveParticipantIdentityAtRoot('manager')
  assert.equal(resolved.ok, true, resolved.error)
  const value = resolved.identity
  return {
    kind: 'RootSelection', ownerSession: null, ownerLogicalRun: null, ownerAuthorityRoot: null,
    participantIdentity: {
      participant: value.name, role: value.role, persona: value.persona,
      personaCatalogVersion: value.catalogVersion, origin: value.origin,
    },
  }
}

const acceptOwner = async (handle, session) => {
  const result = await dispatch.acceptHumanRootSelection(handle, session, `root-\${session}`, rootSelection())
  assert.equal(result.ok, true, JSON.stringify(result.error))
  return result.profile
}

const hostPort = (send) => ({ SubscribeTerminal: () => ({ Dispose() {} }), SendPrompt: send })

// The exact legal closure of an owner's root, as interaction-authority 017
// establishes it: review -> relay -> advance -> close through the real
// lifecycle surface, then the one durable AuthorityLogicalRunClosed the
// ledger accepts.
const closeOwner = async (handle, session, directory) => {
  const surface = lifecycle.LifecycleSurface ?? lifecycle
  const step = (requestId, updates) => {
    const receipt = surface.submitReview(directory, session, requestId, 'Manager', 'run-1', requestId, updates, [], '')
    assert.equal(receipt.status, 'Committed', receipt.reason)
    return receipt
  }
  const reviewed = step('review', [{ key: 'job', value: 'do' }])
  assert.equal(reviewed.kind, 'Reviewed', reviewed.reason)
  const relayed = step('relay', [{ key: 'job', value: null }])
  assert.equal(relayed.kind, 'Relayed', relayed.reason)
  const advanced = step('advance', [])
  assert.equal(advanced.kind, 'Advanced', advanced.reason)
  const completed = step('close', [])
  assert.equal(completed.kind, 'Completed', completed.reason)
  const view = surface.inspected(directory, session)
  assert.equal(view.Delivered, true)
  const retirement = Array.from(view.Retirements ?? []).at(-1)
  assert.equal(retirement.Outcome, 'Completed')
  await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, session, retirement.RetirementId)
  assert.equal(dispatch.projectionObservation(handle, session).activeLogicalRun, null)
}

test('dispatch: a root accept re-validates its owner after PromptPhysicalAccepted persists — an owner closed in that window fails closed', async () => {
  await withSharedJournal('owner-closed-in-flight', async (handle, reopen, directory) => {
    const owner = 'owner-closed-in-flight-owner'
    const child = 'owner-closed-in-flight-child'
    const physical = 'physical-owner-closed-in-flight'

    const ownerProfile = await acceptOwner(handle, owner)
    const seed = authority.issueInheritedIdentitySeed('engineer', ownerProfile)
    assert.equal(seed.ok, true, seed.ok ? '' : seed.error)

    const sent = await dispatch.sendAgentOwnerRootAwait(
      hostPort(async () => dispatch.admittedWithReceipt('receipt-in-flight')),
      handle, child, 'dispatch the child work', seed.value,
    )
    assert.equal(sent.ok, true, sent.error)

    const landed = Promise.withResolvers()
    const gate = Promise.withResolvers()
    landed.promise.catch(() => {}) // keep an unnotified failure from warning while the test is already failing
    const inner = adapter.PromptJournalAdapter_create(handle.journal)
    const holdingJournal = {
      get RuntimeId() { return inner.RuntimeId },
      ProjectionFor: (sessionId) => inner.ProjectionFor(sessionId),
      OwnershipEvidenceFor: (sessionId) => inner.OwnershipEvidenceFor(sessionId),
      HandleForChild: (sessionId) => inner.HandleForChild(sessionId),
      ChatAcceptancePersistence: () => inner.ChatAcceptancePersistence(),
      async Append(sessionId, providerRun, fact) {
        const result = await inner.Append(sessionId, providerRun, fact)
        if (sessionId.fields[0] === child && fact.cases()[fact.tag] === 'PromptPhysicalAccepted') {
          if (result.tag === 1) {
            landed.reject(new Error('physical acceptance append failed: ' + JSON.stringify(result.fields[0])))
            return result
          }
          // The physical acceptance is durably on the journal. Hold the
          // persist's await so the second validation sees what the test does
          // next.
          held = true
          landed.resolve()
          await gate.promise
        }
        return result
      },
    }
    const runtime = dispatcher.forPrompts(holdingJournal)
    const accepting = dispatcher.Runtime__AcceptAgentOwnerRoot(
      runtime,
      new identity.PromptKey(sent.key),
      new identity.SessionId(child),
      new identity.PhysicalUserMessageId(physical),
      undefined,
    )
    // Failure exit: if the accept settles before the physical acceptance was
    // ever held (e.g. the first validation rejects), surface that real failure
    // through landed instead of hanging the test on this promise.
    let held = false
    accepting.then(
      (value) => { if (!held) landed.reject(new Error('accept settled before PromptPhysicalAccepted was held: ' + JSON.stringify(value))) },
      (error) => { if (!held) landed.reject(error) },
    )

    try {
      await landed.promise
      // Mid-window: close the owner through the only legal durable path.
      await closeOwner(handle, owner, directory)
      // Mid-window: the child has not grown an authority while the acceptance
      // is still in flight.
      assert.equal(dispatch.projectionObservation(handle, child).activeLogicalRun, null)
    } finally {
      gate.resolve()
    }

    const accepted = FResult(await accepting)
    assert.equal(accepted.ok, false, 'the second validation must run after the physical acceptance persists')
    assert.match(accepted.error, /AgentOwnerRoot identity seed rejected/)
    assert.match(accepted.error, /OwnerAuthorityNotActive/)
    assert.equal(dispatch.projectionObservation(handle, child).activeLogicalRun, null)

    // Cold reopen: the physical acceptance survived, no authority appeared,
    // no claim is left pending, and a replayed accept still cannot build one.
    const cold = await reopen()
    assert.equal(dispatch.projectionObservation(cold, owner).activeLogicalRun, null)
    assert.equal(dispatch.projectionObservation(cold, child).activeLogicalRun, null)
    const coldView = journal.JournalSurface_snapshot(cold).sessionProjections[child].promptAuthority
    assert.equal(coldView.physicalLandingCount, 1)
    assert.equal(dispatch.projectionObservation(cold, child).pendingClaims.length, 0)

    const replay = FResult(await dispatcher.Runtime__AcceptAgentOwnerRoot(
      dispatcher.forPrompts(adapter.PromptJournalAdapter_create(cold.journal)),
      new identity.PromptKey(sent.key),
      new identity.SessionId(child),
      new identity.PhysicalUserMessageId(physical),
      undefined,
    ))
    assert.equal(replay.ok, false, 'a replayed accept must fail closed once the owner is closed')
    assert.match(replay.error, /OwnerAuthorityNotActive/)
    assert.equal(dispatch.projectionObservation(cold, child).activeLogicalRun, null)
  })
})
