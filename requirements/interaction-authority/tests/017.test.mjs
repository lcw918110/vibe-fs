import assert from 'node:assert/strict'
import test from 'node:test'
import * as authority from '../../../dist/Interaction/Authority/RuntimeSurface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as executionStatus from '../../../dist/Execution/Session/ChatExecution/StatusSurface.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import { rootFor, rootSelection, withJournal, withSharedJournal, acceptOwner, hostPort } from './support/authority.mjs'

const completeManagerLife = async (handle, session, directory) => {
  const surface = lifecycle.LifecycleSurface ?? lifecycle
  const step = (requestId, updates) => {
    const receipt = surface.submitReview(directory, session, requestId, 'Manager', 'run-1', requestId, updates, [], '')
    assert.equal(receipt.status, 'Committed', receipt.reason)
    return receipt
  }
  const reviewed = step('review', [{ key: 'job', value: 'do' }])
  assert.equal(reviewed.kind, 'Reviewed', reviewed.reason)
  assert.equal(reviewed.milestone, '执行')
  const relayed = step('relay', [{ key: 'job', value: null }])
  assert.equal(relayed.kind, 'Relayed', relayed.reason)
  const advanced = step('advance', [])
  assert.equal(advanced.kind, 'Advanced', advanced.reason)
  assert.equal(advanced.milestone, '收尾')
  const completed = step('close', [])
  assert.equal(completed.kind, 'Completed', completed.reason)
  assert.equal(completed.milestone, '已完成')
  const view = surface.inspected(directory, session)
  assert.equal(view.Delivered, true)
  assert.equal(view.ActiveId ?? null, null)
  const retirement = Array.from(view.Retirements ?? []).at(-1)
  assert.equal(retirement.Outcome, 'Completed')
  await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, session, retirement.RetirementId)
  assert.equal(dispatch.projectionObservation(handle, session).activeLogicalRun, null)
}

const replaceManagerRoot = async (handle, session, original) => {
  const replacement = await dispatch.acceptHumanRootSelection(handle, session, `replacement-${session}`, rootSelection())
  assert.equal(replacement.ok, true, replacement.error)
  assert.notEqual(replacement.profile.logicalRun, original.logicalRun)
  assert.notEqual(replacement.profile.authorityRoot, original.authorityRoot)
  assert.deepEqual(replacement.profile.identitySeed, original.identitySeed)
  return replacement.profile
}

test('WHAT[interaction-authority-017] real pure close removes previously accepted continuation lookup', () => {
  const root = rootFor()
  let state = authority.registerAuthority(root, authority.empty)
  state = authority.registerClaim(authority.claimContinuation('claimed', root.session, 'ManagerGuard', root, 'digest'), state)
  state = authority.acceptClaim('claimed', 'accepted-continuation', state)
  assert.equal(authority.resolveKnownOrigin('accepted-continuation', '', false, state), 'Continuation')
  const closed = authority.closeAuthority(root.logicalRun, root.authorityRoot, state)
  assert.equal(closed.ok, true)
  assert.equal(closed.value.lastAuthorityProfile.logicalRun, root.logicalRun)
  assert.equal(authority.resolveKnownOrigin('accepted-continuation', 'claimed', false, closed.value), 'UnknownOrigin')
})

test('WHAT[interaction-authority-017] dispatcher rejects a continuation with no active run at its target', async () => {
  await withJournal('no-active-target', async (handle) => {
    const owner = await acceptOwner(handle)
    let sends = 0
    const result = await dispatch.sendContinuation(hostPort(async () => {
      sends += 1
      return dispatch.admittedWithReceipt('receipt')
    }), handle, 'never-active-target', 'continue', 'ManagerGuard', owner, 'Await')
    assert.equal(result.ok, false)
    assert.equal(result.error, 'No active authority profile')
    // A rejected target must never reach the Host transport and must leave no
    // durable claim behind: an externally supplied profile is not authority.
    assert.equal(sends, 0)
    assert.equal(result.observation, null)
    assert.equal(dispatch.projectionObservation(handle, 'never-active-target').pendingClaims.length, 0)
  })
})

test('WHAT[interaction-authority-017] dispatcher rejects a continuation whose supplied profile names another logical run', async () => {
  await withJournal('wrong-run-target', async (handle) => {
    const owner = await acceptOwner(handle, 'wrong-run-target')
    const wrongRun = { ...owner, logicalRun: `${owner.logicalRun}-other` }
    let sends = 0
    const result = await dispatch.sendContinuation(hostPort(async () => {
      sends += 1
      return dispatch.admittedWithReceipt('receipt')
    }), handle, 'wrong-run-target', 'continue', 'ManagerGuard', wrongRun, 'Await')
    // The target has an active run, but the supplied profile does not name it:
    // a profile is a claim, not authority, so it is refused before any claim
    // is written or the Host transport is reached.
    assert.equal(result.ok, false)
    assert.match(result.error, /does not match the active logical run/)
    assert.match(result.error, new RegExp(`${owner.logicalRun}-other`))
    assert.equal(sends, 0)
    assert.equal(result.observation, null)
    assert.equal(dispatch.projectionObservation(handle, 'wrong-run-target').pendingClaims.length, 0)
    assert.equal(dispatch.projectionObservation(handle, 'wrong-run-target').claimSequences.length, 0)
  })
})

test('WHAT[interaction-authority-017] dispatcher rejects a continuation whose supplied profile names another authority root', async () => {
  await withJournal('wrong-root-target', async (handle) => {
    const owner = await acceptOwner(handle, 'wrong-root-target')
    const wrongRoot = { ...owner, authorityRoot: `${owner.authorityRoot}-other` }
    let sends = 0
    const result = await dispatch.sendContinuation(hostPort(async () => {
      sends += 1
      return dispatch.admittedWithReceipt('receipt')
    }), handle, 'wrong-root-target', 'continue', 'ManagerGuard', wrongRoot, 'Await')
    assert.equal(result.ok, false)
    assert.match(result.error, /does not match the active logical run/)
    assert.equal(sends, 0)
    assert.equal(result.observation, null)
    assert.equal(dispatch.projectionObservation(handle, 'wrong-root-target').pendingClaims.length, 0)
  })
})

test('WHAT[interaction-authority-017] dispatcher delivers a continuation to a target that has an active run', async () => {
  await withJournal('active-target', async (handle) => {
    const owner = await acceptOwner(handle)
    const seed = authority.issueInheritedIdentitySeed('engineer', owner)
    assert.equal(seed.ok, true, seed.ok ? '' : seed.error)
    const sentKeys = []
    const port = hostPort(async (session, text, options) => {
      sentKeys.push(options.Metadata.wanxiangshu_prompt_key)
      return dispatch.admittedWithReceipt('accepted-active')
    })
    const root = await dispatch.sendAgentOwnerRootAwait(port, handle, 'active-target', 'bounded assignment', seed.value)
    assert.equal(root.ok, true, root.error)
    const rootClaim = dispatch.projectionObservation(handle, 'active-target').pendingClaims.find(claim => claim.promptKey === root.key)
    assert.ok(rootClaim)
    const accepted = await dispatch.acceptAgentOwnerRoot(handle, 'active-target', root.key, 'physical-root-active')
    assert.equal(accepted.ok, true, accepted.error)
    const continuation = await dispatch.sendContinuation(port, handle, 'active-target', 'continue same work', 'ManagerGuard', accepted.profile, 'Await')
    // The same gate must admit the legitimate continuation: a sender that
    // rejected everything would satisfy the test above and lose real work.
    assert.equal(continuation.ok, true, continuation.error)
    assert.deepEqual(sentKeys, [root.key, continuation.key])
    const continuationClaim = dispatch.projectionObservation(handle, 'active-target').pendingClaims.find(claim => claim.promptKey === continuation.key)
    assert.ok(continuationClaim)
    // PROMPT-002/004: a transport receipt is only `Submitted`; the claim leaves
    // the pending map only when a real physical message id resolves it, same as
    // the root claim was resolved by acceptAgentOwnerRoot above.
    const landed = await dispatch.acceptManagedPromptClaim(handle, 'active-target', 'physical-continuation-active', continuation.key, 'engineer')
    assert.equal(landed.ok, true, landed.error)
    const observation = dispatch.projectionObservation(handle, 'active-target')
    assert.equal(observation.pendingClaims.length, 0)
    // The unlanded Root scope belongs to the session; only the continuation
    // counter belongs to the active logical run.
    const rootScope = authority.claimScopeDigest('active-target', null, { kind: 'AuthorityRoot', label: 'AgentOwnerRoot' }, rootClaim.payloadDigest)
    const continuationScope = authority.claimScopeDigest('active-target', accepted.profile.logicalRun, authority.originForContinuation('ManagerGuard'), continuationClaim.payloadDigest)
    assert.notEqual(rootScope, continuationScope)
    assert.deepEqual(new Map(observation.claimSequences.map(({ scope, count }) => [scope, count])), new Map([[rootScope, 1], [continuationScope, 1]]))
  })
})

test('WHAT[interaction-authority-017] dispatcher rejects a durably closed Manager run while its archived profile remains readable', async () => {
  await withSharedJournal('closed-manager-target', async (handle, reopen, directory) => {
    const session = 'closed-manager-target'
    const original = await acceptOwner(handle, session)
    await completeManagerLife(handle, session, directory)
    const archived = journal.JournalSurface_snapshot(handle).sessionProjections[session].promptAuthority.lastAuthorityProfile
    assert.equal(archived.logicalRun, original.logicalRun)
    assert.equal(archived.authorityRoot, original.authorityRoot)
    let sends = 0
    const result = await dispatch.sendContinuation(hostPort(async () => {
      sends += 1
      return dispatch.admittedWithReceipt('must-not-send-closed-work')
    }), handle, session, 'late guidance', 'BusyAgentNudge', original, 'Await')
    assert.equal(result.ok, false)
    assert.equal(sends, 0)
    assert.equal(result.observation, null)
    assert.equal(dispatch.projectionObservation(handle, session).pendingClaims.length, 0)
    const cold = await reopen()
    assert.equal(dispatch.projectionObservation(cold, session).activeLogicalRun, null)
    assert.equal(dispatch.projectionObservation(cold, session).pendingClaims.length, 0)
  })
})

for (const transition of ['active', 'closed', 'replaced']) {
  test(`WHAT[interaction-authority-017] a real durable continuation claim ${transition === 'active' ? 'sends while its original run stays active' : `does not send after its original run is ${transition}`}`, async () => {
    const openJournal = transition === 'active' ? withJournal : withSharedJournal
    await openJournal(`claimed-${transition}`, async (handle, reopen, directory) => {
      const session = `claimed-${transition}`
      const original = await acceptOwner(handle, session)
      const claimed = Promise.withResolvers()
      const held = Promise.withResolvers()
      const texts = []
      const sending = dispatch.sendContinuationAfterClaim(hostPort(async (target, text) => {
        assert.equal(target, session)
        texts.push(text)
        return dispatch.admittedWithReceipt('receipt-original-guidance')
      }), handle, session, 'guidance for the original work', 'BusyAgentNudge', original, 'Await', async key => {
        claimed.resolve(key)
        await held.promise
      })

      try {
        const key = await claimed.promise
        assert.deepEqual(texts, [])
        const persisted = dispatch.projectionObservation(handle, session).pendingClaims
        assert.equal(persisted.length, 1)
        assert.equal(persisted[0].promptKey, key)
        assert.equal(persisted[0].logicalRun, original.logicalRun)
        assert.equal(persisted[0].authorityRoot, original.authorityRoot)
        if (transition !== 'active') await completeManagerLife(handle, session, directory)
        if (transition === 'replaced') await replaceManagerRoot(handle, session, original)
        const current = dispatch.projectionObservation(handle, session).activeLogicalRun
        held.resolve()
        const result = await sending
        assert.equal(result.ok, transition === 'active')
        assert.deepEqual(texts, transition === 'active' ? ['guidance for the original work'] : [])
        if (transition !== 'active') {
          assert.equal(result.observation, null)
          assert.equal(dispatch.projectionObservation(handle, session).pendingClaims.length, 0)
        }
        assert.deepEqual(dispatch.projectionObservation(handle, session).activeLogicalRun, current)
        const cold = await reopen()
        assert.deepEqual(dispatch.projectionObservation(cold, session).activeLogicalRun, current)
        if (transition !== 'active') assert.equal(dispatch.projectionObservation(cold, session).pendingClaims.length, 0)
      } finally {
        held.resolve()
        await sending
      }
    })
  })

  test(`WHAT[interaction-authority-017] a frozen ingress decision ${transition === 'active' ? 'accepts its original active run' : `cannot accept after its original run is ${transition}`}`, async () => {
    const openJournal = transition === 'active' ? withJournal : withSharedJournal
    await openJournal(`ingress-${transition}`, async (handle, reopen, directory) => {
      const session = `ingress-${transition}`
      const physical = `physical-guidance-${transition}`
      const original = await acceptOwner(handle, session)
      let prepared
      const sent = await dispatch.sendContinuation(hostPort(async (target, text, options) => {
        assert.equal(target, session)
        assert.equal(text, 'guidance whose ingress is still preparing')
        prepared = dispatch.prepareManagedPromptAcceptance(handle, session, physical, options.Metadata.wanxiangshu_prompt_key, 'manager')
        return dispatch.admittedWithPhysicalMessage(physical)
      }), handle, session, 'guidance whose ingress is still preparing', 'BusyAgentNudge', original, 'Await')
      assert.equal(sent.ok, true, sent.error)
      assert.equal(typeof prepared, 'function')
      assert.equal(dispatch.projectionObservation(handle, session).pendingClaims.length, 0)
      assert.equal(executionStatus.query(handle, session, physical).accepted, false)
      if (transition !== 'active') await completeManagerLife(handle, session, directory)
      if (transition === 'replaced') await replaceManagerRoot(handle, session, original)
      const current = dispatch.projectionObservation(handle, session).activeLogicalRun
      const accepted = await prepared()
      assert.equal(accepted.ok, transition === 'active')
      if (transition !== 'active') assert.equal(accepted.error, 'IntentRejected')
      assert.equal(executionStatus.query(handle, session, physical).accepted, transition === 'active')
      assert.deepEqual(dispatch.projectionObservation(handle, session).activeLogicalRun, current)
      const cold = await reopen()
      assert.equal(executionStatus.query(cold, session, physical).accepted, transition === 'active')
      assert.deepEqual(dispatch.projectionObservation(cold, session).activeLogicalRun, current)
    })
  })

  test(`WHAT[interaction-authority-017] durable physical acceptance ${transition === 'active' ? 'establishes managed execution for its original active run' : `does not establish managed execution after its original run is ${transition}`}`, async () => {
    const openJournal = transition === 'active' ? withJournal : withSharedJournal
    await openJournal(`physical-${transition}`, async (handle, reopen, directory) => {
      const session = `physical-${transition}`
      const physical = `physical-before-managed-${transition}`
      const original = await acceptOwner(handle, session)
      const sent = await dispatch.sendContinuation(hostPort(async () => dispatch.admittedWithReceipt('receipt-before-physical')), handle, session, 'guidance waiting for managed acceptance', 'BusyAgentNudge', original, 'Await')
      assert.equal(sent.ok, true, sent.error)
      assert.equal(dispatch.projectionObservation(handle, session).pendingClaims.length, 1)
      const landed = Promise.withResolvers()
      const held = Promise.withResolvers()
      const prepared = dispatch.prepareManagedPromptAcceptanceAfterPhysical(handle, session, physical, sent.key, 'manager', async key => {
        landed.resolve(key)
        await held.promise
      })
      const accepting = prepared()

      try {
        assert.equal(await landed.promise, sent.key)
        assert.equal(dispatch.projectionObservation(handle, session).pendingClaims.length, 0)
        assert.equal(journal.JournalSurface_snapshot(handle).sessionProjections[session].promptAuthority.physicalLandingCount, 1)
        assert.equal(executionStatus.query(handle, session, physical).accepted, false)
        if (transition !== 'active') await completeManagerLife(handle, session, directory)
        if (transition === 'replaced') await replaceManagerRoot(handle, session, original)
        const current = dispatch.projectionObservation(handle, session).activeLogicalRun
        held.resolve()
        const accepted = await accepting
        assert.equal(accepted.ok, transition === 'active')
        if (transition !== 'active') assert.equal(accepted.error, 'IntentRejected')
        assert.equal(executionStatus.query(handle, session, physical).accepted, transition === 'active')
        assert.deepEqual(dispatch.projectionObservation(handle, session).activeLogicalRun, current)
        const cold = await reopen()
        assert.equal(executionStatus.query(cold, session, physical).accepted, transition === 'active')
        assert.deepEqual(dispatch.projectionObservation(cold, session).activeLogicalRun, current)
      } finally {
        held.resolve()
        await accepting
      }
    })
  })
}
