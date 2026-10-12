import test from 'node:test'
import assertObserved from 'node:assert/strict'
import * as syncObserved from '../../../dist/Execution/Delegation/SyncDelegate/Surface.js'
import * as dispatchObserved from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as authorityObserved from '../../../dist/Interaction/Authority/RuntimeSurface.js'
import { rootFor as authorityOwnerRoot } from '../../interaction-authority/tests/support/authority.mjs'
import { withSyncRuntime as withObservedRuntime } from '../../delegation/tests/support/sync-runtime.mjs'
import {
  withJournal, hostPort, journalBytes, prepareAcceptanceObserver, sendWithAcceptanceObserver,
  acceptObservedPhysical, acceptanceObservation,
  ownAcceptanceRegistration,
} from './support/authority.mjs'

const registerLease = (key, observer) => {
  const lease = dispatchObserved.registerPhysicalAcceptanceObserver(key, observer)
  assertObserved.ok(lease !== null && lease !== undefined,
    'the production registration owner must return an opaque lease; the old unit return lacks this new capability')
  return lease
}

const withLeaseClaim = (path, label, action) => withJournal(`lease-${path}-${label}`, async (handle, _reopen, directory) => {
  const context = await prepareAcceptanceObserver(handle, path, `lease-${label}`)
  let options
  let release
  let sends = 0
  const sdk = new Promise(resolve => { release = resolve })
  try {
    const sent = await sendWithAcceptanceObserver(path,
      hostPort((_session, _text, supplied) => { options = supplied; sends += 1; return sdk }),
      handle, context, () => {},
    )
    assertObserved.equal(sent.ok, true, sent.error)
    await action({ handle, context, directory, key: sent.key, options, sends: () => sends })
  } finally {
    if (options && dispatchObserved.pendingClaimCount(handle, context.session) > 0) {
      await dispatchObserved.deliverDetachedVerdict(options.DetachedListener, 'Refused', 'lease fixture released its unaccepted Host request')
    }
    release(dispatchObserved.admittedWithReceipt('lease-fixture-sdk-result'))
  }
})

const sendWithRegistration = (path, port, handle, context, onAccepted, attachRegistration) => path === 'root'
  ? dispatchObserved.sendAgentOwnerRootWithAcceptanceRegistration(
      port, handle, context.session, 'attach root acceptance before Host effect',
      context.seed, 'Detached', onAccepted, attachRegistration,
    )
  : dispatchObserved.sendContinuationWithAcceptanceRegistration(
      port, handle, context.session, 'attach continuation acceptance before Host effect',
      'DegenerationGuard', context.profile, 'Detached', onAccepted, attachRegistration,
    )

test('WHAT[dispatch-protocol-007] accepting another real root preserves an earlier native Unknown root claim through cold reopen', async () => {
  await withJournal('unknown-root-independent-acceptance', async (handle, reopen, directory) => {
    const context = await prepareAcceptanceObserver(handle, 'root', 'independent-unknown-root')
    const sent = []
    const port = hostPort((session, text, options) => {
      sent.push({ session, text, key: options.Metadata.wanxiangshu_prompt_key })
      return Promise.resolve(dispatchObserved.acceptanceUnknown('the native Host cannot prove this root landed'))
    })
    const first = await dispatchObserved.sendAgentOwnerRootAwait(
      port, handle, context.session, 'Original independent Unknown root A', context.seed,
    )
    const second = await dispatchObserved.sendAgentOwnerRootAwait(
      port, handle, context.session, 'Another independent Unknown root B', context.seed,
    )
    for (const result of [first, second]) {
      assertObserved.equal(result.ok, false)
      assertObserved.match(result.error, /Acceptance unknown/i)
    }
    assertObserved.equal(sent.length, 2)
    assertObserved.notEqual(sent[0].key, sent[1].key)
    assertObserved.deepEqual(sent.map(item => item.session), [context.session, context.session])
    const before = dispatchObserved.projectionObservation(handle, context.session)
    assertObserved.equal(before.activeLogicalRun, null)
    assertObserved.equal(before.pendingClaims.length, 2)
    const original = before.pendingClaims.find(claim => claim.promptKey === sent[0].key)
    assertObserved.ok(original, 'the original Host metadata key names its actual durable claim')
    assertObserved.equal(original.origin, 'AgentOwnerRoot')
    assertObserved.equal(original.logicalRun, null, 'the unlanded root does not belong to a closed prior run')
    assertObserved.equal(original.authorityRoot, null)
    assertObserved.equal(original.receipt, null)

    const physical = 'accepted-independent-root-B'
    const accepted = await acceptObservedPhysical(handle, context, sent[1].key, physical)
    assertObserved.equal(accepted.ok, true, accepted.error)
    const after = dispatchObserved.projectionObservation(handle, context.session)
    assertObserved.equal(after.activeLogicalRun.authorityRoot, physical)
    assertObserved.equal(after.activeLogicalRun.session, context.session)
    assertObserved.equal(after.pendingClaims.some(claim => claim.promptKey === sent[1].key), false)
    assertObserved.deepEqual(after.pendingClaims.find(claim => claim.promptKey === sent[0].key), original,
      'new authority acceptance cannot silently abandon another root whose native physical outcome is still unknown')
    const bytes = journalBytes(directory)
    assertObserved.equal(dispatchObserved.pendingClaimCount(handle, context.session), 1)
    assertObserved.deepEqual(journalBytes(directory), bytes, 'independent claim queries append no facts')

    const cold = await reopen()
    const recovered = dispatchObserved.projectionObservation(cold, context.session)
    assertObserved.deepEqual(recovered.pendingClaims, [original])
    assertObserved.deepEqual(recovered.activeLogicalRun, after.activeLogicalRun)
    const recoveredBytes = journalBytes(directory)
    assertObserved.equal(dispatchObserved.pendingClaimCount(cold, context.session), 1)
    assertObserved.deepEqual(journalBytes(directory), recoveredBytes, 'cold claim queries do not reconcile Unknown into abandonment')
    assertObserved.equal(sent.length, 2, 'neither acceptance nor cold recovery sends the original Unknown key again')
  })
})

test('WHAT[dispatch-protocol-007] the pure authority owner preserves an unlanded root when establishing and closing another exact run', () => {
  const hash = input => `authority-owner-hash(${input})`
  const session = 'pure-unknown-root-child'
  const issued = authorityObserved.issueInheritedIdentitySeed('engineer',
    authorityOwnerRoot('manager', 'pure-unknown-root-owner', 'pure-unknown-root-parent'))
  assertObserved.equal(issued.ok, true, issued.error)
  const origin = { kind: 'AuthorityRoot', label: 'AgentOwnerRoot' }
  const firstScope = authorityObserved.claimScopeDigest(session, null, origin, 'unknown-root-A')
  const firstKey = authorityObserved.derivePromptKey(hash, session, null, null, origin, 'unknown-root-A', 1)
  const secondKey = authorityObserved.derivePromptKey(hash, session, null, null, origin, 'accepted-root-B', 1)
  const first = authorityObserved.claimAgentOwnerRoot(firstKey, session, 'unknown-root-A', issued.value)
  const second = authorityObserved.claimAgentOwnerRoot(secondKey, session, 'accepted-root-B', issued.value)
  assertObserved.equal(first.ok, true, first.error)
  assertObserved.equal(second.ok, true, second.error)
  let state = authorityObserved.registerClaim(first.value, authorityObserved.empty)
  state = authorityObserved.registerClaim(second.value, state)
  state = authorityObserved.acceptClaim(secondKey, 'pure-physical-root-B', state)
  const built = authorityObserved.createAuthorityRoot(hash, 'pure-runtime', session,
    'AgentOwnerRoot', 'pure-physical-root-B', issued.value)
  assertObserved.equal(built.ok, true, built.error)
  const root = built.value
  state = authorityObserved.registerAuthority(root, state)
  assertObserved.deepEqual(state.pendingClaims, [first.value],
    'establishing B cannot classify A logicalRun=None as a closed prior run')
  assertObserved.equal(authorityObserved.nextClaimSequence(firstScope, state), 2)

  const continuationOrigin = authorityObserved.originForContinuation('DegenerationGuard')
  const continuationScope = authorityObserved.claimScopeDigest(session, root.logicalRun, continuationOrigin, 'B-continuation')
  const landedKey = authorityObserved.derivePromptKey(hash, session, root.logicalRun, root.authorityRoot,
    continuationOrigin, 'B-continuation', 1)
  const pendingKey = authorityObserved.derivePromptKey(hash, session, root.logicalRun, root.authorityRoot,
    continuationOrigin, 'B-continuation', 2)
  state = authorityObserved.registerClaim(
    authorityObserved.claimContinuation(landedKey, session, 'DegenerationGuard', root, 'B-continuation'), state)
  state = authorityObserved.acceptClaim(landedKey, 'pure-physical-B-continuation', state)
  state = authorityObserved.registerClaim(
    authorityObserved.claimContinuation(pendingKey, session, 'DegenerationGuard', root, 'B-continuation'), state)
  assertObserved.equal(authorityObserved.nextClaimSequence(continuationScope, state), 3)
  assertObserved.equal(authorityObserved.resolveKnownOrigin('pure-physical-B-continuation', landedKey, false, state), 'Continuation')

  // This calls the pure close owner; it does not supply a durable lifecycle witness.
  const closed = authorityObserved.closeAuthority(root.logicalRun, root.authorityRoot, state)
  assertObserved.equal(closed.ok, true, closed.error)
  assertObserved.equal(closed.value.activeLogicalRun, null)
  assertObserved.deepEqual(closed.value.pendingClaims, [first.value])
  assertObserved.equal(authorityObserved.nextClaimSequence(firstScope, closed.value), 2)
  assertObserved.equal(authorityObserved.nextClaimSequence(continuationScope, closed.value), 1)
  assertObserved.deepEqual(closed.value.acceptedContinuations, [])
  assertObserved.equal(authorityObserved.resolveKnownOrigin('', firstKey, false, closed.value), 'AuthorityRoot')
  assertObserved.equal(authorityObserved.resolveKnownOrigin('pure-physical-B-continuation', landedKey, false, closed.value), 'UnknownOrigin')
  assertObserved.equal(closed.value.physicalLandings.find(landing => landing.physical === 'pure-physical-root-B').promptKey, secondKey)
  assertObserved.equal(closed.value.physicalLandings.find(landing => landing.physical === 'pure-physical-B-continuation').promptKey, landedKey)
  assertObserved.deepEqual(authorityObserved.closeAuthority(root.logicalRun, root.authorityRoot, closed.value).value, closed.value)
})

test('WHAT[dispatch-protocol-007] the pure Manager Ledger close preserves unknown roots and consumed None scopes while clearing only Manager run resources', () => {
  const hash = input => `manager-ledger-hash(${input})`
  const rootOrigin = { kind: 'AuthorityRoot', label: 'AgentOwnerRoot' }
  for (const [kind, participant] of [['HumanRoot', 'engineer'], ['AgentOwnerRoot', 'engineer'], ['HumanRoot', 'manager']]) {
    const session = `ledger-${kind}-${participant}`
    const issued = authorityObserved.issueInheritedIdentitySeed('engineer',
      authorityOwnerRoot('manager', `ledger-owner-physical-${session}`, `ledger-owner-${session}`))
    assertObserved.equal(issued.ok, true, issued.error)
    const rootKey = (payload, sequence) => authorityObserved.derivePromptKey(
      hash, session, null, null, rootOrigin, payload, sequence)
    const unknownKey = rootKey('unknown-root', 1)
    const acceptedKey = rootKey('consumed-root-payload', 1)
    const abandonedKey = rootKey('consumed-root-payload', 2)
    const unknown = authorityObserved.claimAgentOwnerRoot(unknownKey, session, 'unknown-root', issued.value)
    const accepted = authorityObserved.claimAgentOwnerRoot(acceptedKey, session, 'consumed-root-payload', issued.value)
    const abandoned = authorityObserved.claimAgentOwnerRoot(abandonedKey, session, 'consumed-root-payload', issued.value)
    for (const claim of [unknown, accepted, abandoned]) assertObserved.equal(claim.ok, true, claim.error)
    let state = authorityObserved.registerClaim(unknown.value, authorityObserved.empty)
    state = authorityObserved.registerClaim(accepted.value, state)
    state = authorityObserved.acceptClaim(acceptedKey, `ledger-old-root-physical-${session}`, state)
    state = authorityObserved.registerClaim(abandoned.value, state)
    state = authorityObserved.abandonClaim(abandonedKey, state)
    const root = kind === 'HumanRoot'
      ? authorityOwnerRoot(participant, `ledger-active-root-${session}`, session)
      : authorityObserved.createAuthorityRoot(hash, 'ledger-runtime', session,
          kind, `ledger-active-root-${session}`, issued.value).value
    assertObserved.ok(root)
    state = authorityObserved.registerAuthority(root, state)
    const unknownScope = authorityObserved.claimScopeDigest(session, null, rootOrigin, 'unknown-root')
    const consumedScope = authorityObserved.claimScopeDigest(session, null, rootOrigin, 'consumed-root-payload')
    assertObserved.deepEqual(state.pendingClaims, [unknown.value])
    assertObserved.equal(authorityObserved.nextClaimSequence(unknownScope, state), 2)
    assertObserved.equal(authorityObserved.nextClaimSequence(consumedScope, state), 3,
      'the accepted and abandoned same-payload acts consumed this scope without leaving a Pending claim')

    const continuationOrigin = authorityObserved.originForContinuation('ManagerGuard')
    const pendingScope = authorityObserved.claimScopeDigest(session, root.logicalRun, continuationOrigin, 'pending-run-continuation')
    const acceptedScope = authorityObserved.claimScopeDigest(session, root.logicalRun, continuationOrigin, 'accepted-run-continuation')
    const pendingKey = authorityObserved.derivePromptKey(hash, session, root.logicalRun, root.authorityRoot,
      continuationOrigin, 'pending-run-continuation', 1)
    const continuationKey = authorityObserved.derivePromptKey(hash, session, root.logicalRun, root.authorityRoot,
      continuationOrigin, 'accepted-run-continuation', 1)
    state = authorityObserved.registerClaim(authorityObserved.claimContinuation(
      pendingKey, session, 'ManagerGuard', root, 'pending-run-continuation'), state)
    state = authorityObserved.registerClaim(authorityObserved.claimContinuation(
      continuationKey, session, 'ManagerGuard', root, 'accepted-run-continuation'), state)
    const continuationPhysical = `ledger-continuation-physical-${session}`
    state = authorityObserved.acceptClaim(continuationKey, continuationPhysical, state)
    assertObserved.equal(authorityObserved.nextClaimSequence(pendingScope, state), 2)
    assertObserved.equal(authorityObserved.nextClaimSequence(acceptedScope, state), 2)

    // This exercises the actual pure Ledger owner, not a durable RetirementCommitted witness.
    const closed = dispatchObserved.closeCompletedHumanRootManager(state)
    if (kind !== 'HumanRoot' || participant !== 'manager') {
      assertObserved.deepEqual(closed, state, `${kind}/${participant} is outside the Manager Ledger close contract`)
      continue
    }
    assertObserved.equal(closed.activeLogicalRun, null)
    assertObserved.deepEqual(closed.lastAuthorityProfile, root)
    assertObserved.deepEqual(closed.pendingClaims, [unknown.value])
    assertObserved.equal(authorityObserved.nextClaimSequence(unknownScope, closed), 2)
    assertObserved.equal(authorityObserved.nextClaimSequence(consumedScope, closed), 3)
    assertObserved.equal(authorityObserved.nextClaimSequence(pendingScope, closed), 1)
    assertObserved.equal(authorityObserved.nextClaimSequence(acceptedScope, closed), 1)
    assertObserved.deepEqual(closed.acceptedContinuations, [])
    assertObserved.deepEqual(closed.physicalLandings, state.physicalLandings)
    assertObserved.deepEqual(closed.acceptedDispatches, state.acceptedDispatches)
    assertObserved.equal(authorityObserved.resolveKnownOrigin('', unknownKey, false, closed), 'AuthorityRoot')
    assertObserved.equal(authorityObserved.resolveKnownOrigin(continuationPhysical, continuationKey, false, closed), 'UnknownOrigin')
    assertObserved.deepEqual(dispatchObserved.closeCompletedHumanRootManager(closed), closed)
  }
})

for (const path of ['root', 'continuation']) {
  test(`WHAT[dispatch-protocol-007] the real ${path} sender hands its exact local registration to the caller before entering the Host effect`, async () => {
    await withJournal(`handoff-${path}`, async handle => {
      const context = await prepareAcceptanceObserver(handle, path, 'effect-before-attach')
      const registrations = []
      const seen = []
      let atHost
      let options
      let release
      const sdk = new Promise(resolve => { release = resolve })
      try {
        const sent = await sendWithRegistration(path,
          hostPort((_session, _text, supplied) => {
            options = supplied
            atHost = registrations.length
            return sdk
          }), handle, context, physical => seen.push(physical), registration => {
            registrations.push(registration)
            dispatchObserved.disposePhysicalAcceptanceObserver(registration)
          },
        )
        assertObserved.equal(sent.ok, true, sent.error)
        const physical = `handoff-physical-${context.session}`
        assertObserved.equal((await acceptObservedPhysical(handle, context, sent.key, physical)).ok, true)
        assertObserved.equal(atHost, 1, 'new handoff capability: the caller must own and may release the registration before Host SendPrompt begins')
        assertObserved.equal(registrations.length, 1)
        assertObserved.deepEqual(seen, [], 'release of the real sender registration prevents later notification')
        dispatchObserved.disposePhysicalAcceptanceObserver(registrations[0])
      } finally {
        if (options && dispatchObserved.pendingClaimCount(handle, context.session) > 0) {
          await dispatchObserved.deliverDetachedVerdict(options.DetachedListener, 'Refused', 'handoff fixture released its unaccepted request')
        }
        release(dispatchObserved.admittedWithReceipt('handoff-fixture-sdk-result'))
      }
    })
  })

  test(`WHAT[dispatch-protocol-007] a real Await ${path} native Unknown result cannot reject another caller's waiter or alter its durable Pending claim`, async t => {
    await withJournal(`sender-unknown-${path}`, async (handle, _reopen, directory) => {
      const context = await prepareAcceptanceObserver(handle, path, 'await-unknown-other-waiter')
      const seen = []
      let other
      let key
      let before
      let bytes
      t.mock.timers.enable({ apis: ['setTimeout'] })
      const port = hostPort((_session, _text, options) => {
        key = options.Metadata.wanxiangshu_prompt_key
        before = dispatchObserved.projectionObservation(handle, context.session)
        bytes = journalBytes(directory)
        other = dispatchObserved.awaitPhysicalConfirmation(key, 50)
        return Promise.resolve(dispatchObserved.acceptanceUnknown('actual native Host outcome remains unknown'))
      })
      const sent = path === 'root'
        ? await dispatchObserved.sendAgentOwnerRootWithAcceptance(
            port, handle, context.session, 'Await unknown root keeps independent waiter', context.seed,
            'Await', physical => seen.push(physical),
            registration => ownAcceptanceRegistration(handle, registration),
          )
        : await dispatchObserved.sendContinuationWithAcceptance(
            port, handle, context.session, 'Await unknown successor keeps independent waiter',
            'DegenerationGuard', context.profile, 'Await', physical => seen.push(physical),
            registration => ownAcceptanceRegistration(handle, registration),
          )
      assertObserved.equal(sent.ok, false)
      assertObserved.match(sent.error, /Acceptance unknown/)
      assertObserved.equal(dispatchObserved.pendingClaimCount(handle, context.session), 1)
      assertObserved.deepEqual(dispatchObserved.projectionObservation(handle, context.session), before)
      assertObserved.deepEqual(journalBytes(directory), bytes, 'Unknown adds neither Abandoned nor another durable fact')
      const physical = `await-unknown-physical-${context.session}`
      assertObserved.equal((await acceptObservedPhysical(handle, context, key, physical)).ok, true)
      t.mock.timers.tick(50)
      assertObserved.deepEqual(await other, { kind: 'Accepted', physical, reason: null },
        'a sender returning Unknown owns only its local callback, not another caller\'s confirmation waiter')
      assertObserved.deepEqual(seen, [], 'the ended Await send releases its own callback')
    })
  })
}

for (const path of ['root', 'continuation']) {
  test(`WHAT[dispatch-protocol-007] repeated registration of the same ${path} callback gives independent leases and an old release preserves the current observer`, async () => {
    await withLeaseClaim(path, 'same-callback', async ({ handle, context, key, sends }) => {
      const seen = []
      const observer = dispatchObserved.preparePhysicalAcceptanceObserver(physical => seen.push(acceptanceObservation(handle, context, physical)))
      const first = registerLease(key, observer)
      const second = registerLease(key, observer)
      assertObserved.notEqual(first, second, 'every register of the exact same typed callback has a new opaque identity')
      dispatchObserved.disposePhysicalAcceptanceObserver(first)
      const physical = `lease-same-callback-${context.session}`
      assertObserved.equal((await acceptObservedPhysical(handle, context, key, physical)).ok, true)
      assertObserved.deepEqual(seen, [{ physical, pending: 0, accepted: true }])
      assertObserved.equal(sends(), 1)
      dispatchObserved.disposePhysicalAcceptanceObserver(second)
    })
  })

  test(`WHAT[dispatch-protocol-007] disposing an old ${path} lease twice cannot remove a different later observer`, async () => {
    await withLeaseClaim(path, 'replacement', async ({ handle, context, key }) => {
      const oldSeen = []
      const currentSeen = []
      const first = registerLease(key, dispatchObserved.preparePhysicalAcceptanceObserver(physical => oldSeen.push(physical)))
      const second = registerLease(key, dispatchObserved.preparePhysicalAcceptanceObserver(physical => currentSeen.push(physical)))
      assertObserved.notEqual(first, second)
      dispatchObserved.disposePhysicalAcceptanceObserver(first)
      dispatchObserved.disposePhysicalAcceptanceObserver(first)
      const physical = `lease-replacement-${context.session}`
      assertObserved.equal((await acceptObservedPhysical(handle, context, key, physical)).ok, true)
      assertObserved.deepEqual(oldSeen, [])
      assertObserved.deepEqual(currentSeen, [physical])
      dispatchObserved.disposePhysicalAcceptanceObserver(second)
      dispatchObserved.disposePhysicalAcceptanceObserver(second)
    })
  })

  test(`WHAT[dispatch-protocol-007] releasing a local ${path} lease after Host Unknown preserves durable Pending bytes and other actual confirmation waiters`, async t => {
    await withLeaseClaim(path, 'unknown-release', async ({ handle, context, directory, key, options, sends }) => {
      const seen = []
      const lease = registerLease(key, dispatchObserved.preparePhysicalAcceptanceObserver(physical => seen.push(physical)))
      assertObserved.deepEqual(await dispatchObserved.deliverDetachedVerdict(options.DetachedListener, 'OutcomeUnknown', 'actual Host outcome is unknown'), { delivered: true })
      const projection = dispatchObserved.projectionObservation(handle, context.session)
      const bytes = journalBytes(directory)
      assertObserved.equal(dispatchObserved.pendingClaimCount(handle, context.session), 1)
      t.mock.timers.enable({ apis: ['setTimeout'] })
      const first = dispatchObserved.awaitPhysicalConfirmation(key, 50)
      const second = dispatchObserved.awaitPhysicalConfirmation(key, 50)
      dispatchObserved.disposePhysicalAcceptanceObserver(lease)
      dispatchObserved.disposePhysicalAcceptanceObserver(lease)
      assertObserved.deepEqual(dispatchObserved.projectionObservation(handle, context.session), projection)
      assertObserved.deepEqual(journalBytes(directory), bytes, 'local release appends neither Abandoned nor any other durable fact')
      assertObserved.equal(dispatchObserved.pendingClaimCount(handle, context.session), 1)
      const physical = `lease-unknown-release-${context.session}`
      assertObserved.equal((await acceptObservedPhysical(handle, context, key, physical)).ok, true)
      t.mock.timers.tick(50)
      assertObserved.deepEqual(await first, { kind: 'Accepted', physical, reason: null })
      assertObserved.deepEqual(await second, { kind: 'Accepted', physical, reason: null })
      assertObserved.deepEqual(seen, [], 'a released callback is not notified by subsequent actual acceptance')
      assertObserved.equal(sends(), 1)
    })
  })

  test(`WHAT[dispatch-protocol-007] a current ${path} lease callback exception preserves actual Accepted for every independent waiter`, async t => {
    await withLeaseClaim(path, 'throwing-observer', async ({ handle, context, key }) => {
      const error = new Error(`leased-observer-exception-${context.session}`)
      const seen = []
      const lease = registerLease(key, dispatchObserved.preparePhysicalAcceptanceObserver(physical => {
        seen.push(acceptanceObservation(handle, context, physical))
        throw error
      }))
      t.mock.timers.enable({ apis: ['setTimeout'] })
      const first = dispatchObserved.awaitPhysicalConfirmation(key, 50)
      const second = dispatchObserved.awaitPhysicalConfirmation(key, 50)
      const physical = `lease-throwing-observer-${context.session}`
      await assertObserved.rejects(acceptObservedPhysical(handle, context, key, physical), actual => actual === error)
      assertObserved.deepEqual(seen, [{ physical, pending: 0, accepted: true }])
      t.mock.timers.tick(50)
      assertObserved.deepEqual(await first, { kind: 'Accepted', physical, reason: null })
      assertObserved.deepEqual(await second, { kind: 'Accepted', physical, reason: null })
      dispatchObserved.disposePhysicalAcceptanceObserver(lease)
      dispatchObserved.disposePhysicalAcceptanceObserver(lease)
    })
  })
}

for (const path of ['root', 'continuation']) {
  test(`WHAT[dispatch-protocol-007] one detached ${path} confirmation timeout leaves another waiter and the real acceptance observer intact`, async t => {
    await withJournal(`a2-wait-${path}`, async (handle, _reopen, directory) => {
      const context = await prepareAcceptanceObserver(handle, path, 'wait-isolation')
      const seen = []
      let sends = 0
      let release
      const sdk = new Promise(resolve => { release = resolve })
      try {
        const sent = await sendWithAcceptanceObserver(path,
          hostPort(() => { sends += 1; return sdk }),
          handle, context, physical => seen.push(acceptanceObservation(handle, context, physical)),
        )
        assertObserved.equal(sent.ok, true, sent.error)
        const before = dispatchObserved.projectionObservation(handle, context.session)
        const bytes = journalBytes(directory)
        t.mock.timers.enable({ apis: ['setTimeout'] })
        const long = dispatchObserved.awaitPhysicalConfirmation(sent.key, 1000)
        const short = dispatchObserved.awaitPhysicalConfirmation(sent.key, 10)
        t.mock.timers.tick(10)
        assertObserved.deepEqual(await short, { kind: 'Unknown', physical: null, reason: null })
        assertObserved.deepEqual(dispatchObserved.projectionObservation(handle, context.session), before)
        assertObserved.deepEqual(journalBytes(directory), bytes)
        const physical = `confirmed-${context.session}`
        assertObserved.equal((await acceptObservedPhysical(handle, context, sent.key, physical)).ok, true)
        assertObserved.deepEqual(await long, { kind: 'Accepted', physical, reason: null })
        assertObserved.deepEqual(seen, [{ physical, pending: 0, accepted: true }])
        assertObserved.equal(sends, 1)
      } finally { release(dispatchObserved.admittedWithReceipt('wait-isolation-receipt')) }
    })
  })

  test(`WHAT[dispatch-protocol-007] detached ${path} remains observable after both existing confirmation waiters time out`, async t => {
    await withJournal(`a2-wait-empty-${path}`, async (handle, _reopen, directory) => {
      const context = await prepareAcceptanceObserver(handle, path, 'all-waiters-expire')
      const seen = []
      let sends = 0
      let release
      const sdk = new Promise(resolve => { release = resolve })
      try {
        const sent = await sendWithAcceptanceObserver(path,
          hostPort(() => { sends += 1; return sdk }),
          handle, context, physical => seen.push(acceptanceObservation(handle, context, physical)),
        )
        assertObserved.equal(sent.ok, true, sent.error)
        const before = dispatchObserved.projectionObservation(handle, context.session)
        const bytes = journalBytes(directory)
        t.mock.timers.enable({ apis: ['setTimeout'] })
        const first = dispatchObserved.awaitPhysicalConfirmation(sent.key, 10)
        const second = dispatchObserved.awaitPhysicalConfirmation(sent.key, 20)
        t.mock.timers.tick(10)
        const firstResult = await first
        t.mock.timers.tick(10)
        const secondResult = await second
        assertObserved.deepEqual(dispatchObserved.projectionObservation(handle, context.session), before)
        assertObserved.deepEqual(journalBytes(directory), bytes)
        const later = dispatchObserved.awaitPhysicalConfirmation(sent.key, 1000)
        const physical = `late-confirmed-${context.session}`
        assertObserved.equal((await acceptObservedPhysical(handle, context, sent.key, physical)).ok, true)
        assertObserved.deepEqual(await later, { kind: 'Accepted', physical, reason: null })
        assertObserved.deepEqual(firstResult, { kind: 'Unknown', physical: null, reason: null })
        assertObserved.deepEqual(secondResult, { kind: 'Unknown', physical: null, reason: null })
        assertObserved.deepEqual(seen, [{ physical, pending: 0, accepted: true }])
        assertObserved.equal(sends, 1)
      } finally { release(dispatchObserved.admittedWithReceipt('all-waiters-expire-receipt')) }
    })
  })
}

for (const [kind, outcome, admissionKind, claimKind] of [
  ['AcceptanceUnknown', () => dispatchObserved.acceptanceUnknown('SAME-NATIVE-REASON'), 'Unconfirmed', 'Pending'],
  ['Fatal', () => dispatchObserved.fatal('SAME-NATIVE-REASON'), 'Refused', 'Missing'],
]) {
  test(`WHAT[dispatch-protocol-007] observed ${kind} retains its actual PromptKey and native verdict without resending or guessing from reason text`, async () => {
    assertObserved.equal(typeof syncObserved.startObserved, 'function', 'new observed API missing; capability red, not an existing business failure')
    const owner = `observed-native-${kind}`
    await withObservedRuntime(owner, async runtime => {
      const execution = syncObserved.startObserved(runtime, owner, 'NATIVE-OUTCOME')
      await syncObserved.awaitPromptCount(runtime, owner, 'Engineer', 1)
      const sent = syncObserved.promptIdentity(runtime, owner, 'Engineer', 0)
      assertObserved.equal(syncObserved.returnPromptOutcome(runtime, owner, 'Engineer', 0, outcome()), true)
      const admission = await syncObserved.observedAdmission(execution)
      assertObserved.equal(admission.kind, admissionKind)
      assertObserved.equal(admission.sessionId, sent.sessionId)
      assertObserved.equal(admission.promptKey, sent.promptKey)
      assertObserved.deepEqual(admission.hostOutcome, { kind, value: 'SAME-NATIVE-REASON' })
      assertObserved.equal((await syncObserved.observedCompletion(execution)).ok, false)
      assertObserved.equal(syncObserved.promptClaimState(runtime, owner, 'Engineer', 0).kind, claimKind)
      assertObserved.equal(syncObserved.promptCount(runtime, owner, 'Engineer'), 1)
      assertObserved.equal(syncObserved.terminalListenerCount(runtime), 0)
    })
  })
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");
const dispatch = await import("../../../dist/Interaction/Dispatch/DispatchSurface.js");

const H = (input) => `H(${input})`
const RUNTIME = 'rt_1'
const SESSION = 'ses_a'
const findClaim = (projection, key) => projection.pendingClaims.find((claim) => claim.promptKey === key)
const promptOrigin = (kind) => authority.originForContinuation(kind)
const personas = {
  engineer: 'Engineer',
  coder: 'Coder',
  manager: 'Lead',
}
const rootSelection = (participant) => {
  const role = participant === 'predictor' ? 'inspector' : participant
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant,
      role,
      selectedTier: 'deep',
      persona: personas[participant] ?? 'Unknown',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
}
const inheritedSeed = (agent, physical) => {
  const owner = authority.createAuthorityRoot(
    H,
    RUNTIME,
    SESSION,
    'HumanRoot',
    physical,
    rootSelection('manager'),
  )
  assert.equal(owner.ok, true, owner.error)
  const inherited = authority.issueInheritedIdentitySeed(agent, owner.value)
  assert.equal(inherited.ok, true, inherited.error)
  return inherited.value
}
const profileOf = () => {
  const built = authority.createAuthorityRoot(
    H,
    RUNTIME,
    SESSION,
    'HumanRoot',
    'msg_u1',
    rootSelection('engineer'),
  )
  assert.equal(built.ok, true, built.ok ? '' : built.error)
  return built.value
}

test('WHAT[dispatch-protocol-007] fresh claim projection begins with no observed runtime restarts', () => {
  const root = profileOf()
  const key = 'pk_r'
  const projection = authority.registerClaim(
    authority.claimContinuation(key, SESSION, 'ManagerGuard', root, 'pd-r'),
    authority.registerAuthority(root, authority.empty),
  )


  const claim = findClaim(projection, key)
  assert.equal(claim.claimedAtRuntimeStartCount, 0)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { execFileSync } = await import("node:child_process");
const { existsSync, mkdtempSync, readFileSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");
const dispatch = await import("../../../dist/Interaction/Dispatch/DispatchSurface.js");
const contract = await import("../../../dist/OpenCode/Host/OpenCodeContract.js");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");

const hash = (value) => `H(${value})`
const capturingPort = (captured, outcome = () => dispatch.admittedWithReceipt('accepted-007')) => ({
  SubscribeTerminal: () => ({ Dispose: () => {} }),
  SendPrompt: async (session, text, options) => {
    captured.push({ session, text, options })
    return outcome()
  },
})
const personas = {
  coder: 'Coder',
  manager: 'Lead',
  devops: 'Operator',
}
const rootSelection = (participant) => {
  const role = participant === 'predictor' ? 'inspector' : participant
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant,
      role,
      selectedTier: 'deep',
      persona: personas[participant] ?? 'Unknown',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
}
const profileFor = (session, runtime = 'rt-007c') => {
  const owner = authority.createAuthorityRoot(
    hash,
    runtime,
    `${session}_owner`,
    'HumanRoot',
    `msg-${session}-owner`,
    rootSelection('manager'),
  )
  assert.equal(owner.ok, true, owner.error)
  const seed = authority.issueInheritedIdentitySeed('engineer', owner.value)
  assert.equal(seed.ok, true, seed.error)
  const built = authority.createAuthorityRoot(hash, runtime, session, 'AgentOwnerRoot', `msg-root-${session}`, seed.value)
  assert.equal(built.ok, true, built.ok ? '' : JSON.stringify(built.error))
  return built.value
}
const acceptOwner = async (handle, session = 'ses_owner') => {
  const accepted = await dispatch.acceptHumanRootSelection(
    handle,
    session,
    `msg-${session}`,
    rootSelection('manager'),
  )
  assert.equal(accepted.ok, true, accepted.ok ? '' : accepted.error)
  return accepted.profile
}
const Verdict = contract.DetachedSendVerdict
const refused = (reason) => new Verdict(1, [reason])
const outcomeUnknown = (reason) => new Verdict(2, [reason])
const verdictPort = (captured, outcome) => ({
  SubscribeTerminal: () => ({ Dispose: () => {} }),
  SendPrompt: async (session, text, options) => {
    captured.push({ session, text, options })
    return outcome()
  },
})
const waitForSettledClaims = async (handle, session, count, message) => {
  const deadline = Date.now() + 1500
  for (;;) {
    if (dispatch.pendingClaimCount(handle, session) === count) return
    if (Date.now() >= deadline) {
      assert.equal(dispatch.pendingClaimCount(handle, session), count, message)
    }
    await new Promise((resolve) => setImmediate(resolve))
  }
}
const journalLines = (base, writerId) => {
  const eventsDir = join(base, '.git', 'wanxiangshu', 'events')
  const legacy = join(eventsDir, `${writerId}.ndjson`)
  const target = existsSync(legacy) ? legacy : join(eventsDir, `${writerId}-0000.ndjson`)
  return readFileSync(target, 'utf8')
    .trim()
    .split('\n')
}
const openGitJournal = async (base, writerId, runtimeId) => {
  execFileSync('git', ['init', '--quiet', base])
  const opened = await journal.JournalSurface_bootWithWriterId(
    join(base, '.git'),
    writerId,
    runtimeId,
    4242,
    '2026-01-01T00:00:00Z',
  )
  assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
  return opened
}
const sendDetachedRoot = async (port, handle, session, text, seed) => {
  const sent = await dispatch.sendAgentOwnerRoot(port, handle, session, text, seed)
  assert.equal(sent.ok, true, sent.ok ? '' : sent.error)
  return sent
}

test('WHAT[dispatch-protocol-007] PROMPT_007_detached_refused_abandons_send_failed_without_resend', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-prompt-007-refused-'))
  const writerId = 'writer-007-refused'
  const opened = await openGitJournal(base, writerId, 'rt-007-refused')
  try {
    const owner = await acceptOwner(opened.journal, 'ses_007_refused_owner')
    const seed = authority.issueInheritedIdentitySeed('engineer', owner).value
    const captured = []
    const sent = await sendDetachedRoot(
      verdictPort(captured, () => dispatch.retryable('host refused before accept')),
      opened.journal,
      'ses_007_refused',
      'detached refused send',
      seed,
    )
    assert.equal(typeof captured[0].options.DetachedListener, 'function', 'Detached must leave its verdict listener on the send options')
    await waitForSettledClaims(opened.journal, 'ses_007_refused', 0, 'Refused must abandon the exact claim')
    assert.equal(captured.length, 1, 'Refused must never re-emit the physical send')
    const abandoned = journalLines(base, writerId).filter((line) => line.includes('PluginPromptAbandoned'))
    assert.equal(abandoned.length, 1, 'Refused must write exactly one durable Abandoned fact')
    assert.ok(abandoned[0].includes(sent.key), 'the Abandoned fact must name the exact PromptKey')
    assert.ok(abandoned[0].includes('SendFailed'), 'the Abandoned fact must carry the SendFailed reason')
    assert.ok(abandoned[0].includes('host refused before accept'), 'the Abandoned fact must carry the refusal evidence')
  } finally {
    journal.JournalSurface_dispose(opened.journal)
    rmSync(base, { recursive: true, force: true })
  }
})
test('WHAT[dispatch-protocol-007] PROMPT_007_detached_outcome_unknown_keeps_claim_pending_never_resends', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-prompt-007-unknown-'))
  const writerId = 'writer-007-unknown'
  const opened = await openGitJournal(base, writerId, 'rt-007-unknown')
  try {
    const owner = await acceptOwner(opened.journal, 'ses_007_unknown_owner')
    const seed = authority.issueInheritedIdentitySeed('engineer', owner).value
    const captured = []
    await sendDetachedRoot(
      verdictPort(captured, () => dispatch.acceptanceUnknown('response lost after enqueue')),
      opened.journal,
      'ses_007_unknown',
      'detached unknown send',
      seed,
    )
    await waitForSettledClaims(opened.journal, 'ses_007_unknown', 1, 'OutcomeUnknown must keep the claim Pending')
    assert.equal(captured.length, 1, 'OutcomeUnknown must never auto-resend the physical send')
    assert.equal(
      journalLines(base, writerId).filter((line) => line.includes('PluginPromptAbandoned')).length,
      0,
      'OutcomeUnknown must never write an Abandoned fact',
    )
    const pending = dispatch.projectionObservation(opened.journal, 'ses_007_unknown').pendingClaims
    assert.equal(pending.length, 1, 'pending evidence must stay observable for later Host reconciliation')
    assert.ok(String(pending[0].receipt).length > 0, 'the pending claim keeps its durable Submitted receipt')
  } finally {
    journal.JournalSurface_dispose(opened.journal)
    rmSync(base, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");
const dispatch = await import("../../../dist/Interaction/Dispatch/DispatchSurface.js");
const joinGuard = await import("../../../dist/Interaction/Dispatch/JoinGuardSurface.js");

const managerRootSelection = {
  kind: 'RootSelection',
  ownerSession: null,
  ownerLogicalRun: null,
  ownerAuthorityRoot: null,
  participantIdentity: {
    participant: 'manager',
    role: 'manager',
    selectedTier: 'deep',
    persona: 'Lead',
    personaCatalogVersion: 1,
    origin: 'ResolvedAtRoot',
  },
}
const appendAuthorityRoot = async (handle, session) => {
  const owner = await dispatch.acceptHumanRootSelection(
    handle,
    `${session}-owner`,
    `msg-${session}-owner`,
    managerRootSelection,
  )
  assert.equal(owner.ok, true, owner.ok ? '' : owner.error)
  const inherited = authority.issueInheritedIdentitySeed('engineer', owner.profile)
  assert.equal(inherited.ok, true, inherited.ok ? '' : inherited.error)
  return dispatch.appendAuthorityRoot(handle, session, inherited.value)
}
const capturingPort = (captured, behaviour = {}) => ({
  SubscribeTerminal: () => ({ Dispose: () => {} }),
  SendPrompt: async (session, text, options) => {
    captured.push({ session, text, options })
    if (behaviour.failFirst && captured.length === 1) {
      return dispatch.retryable('port refused')
    }
    return dispatch.admittedWithReceipt('accepted-jg')
  },
})

test('WHAT[dispatch-protocol-007] JNGD_nudge_releases_the_key_when_send_fails_and_retries', async () => {
  const sid = 'ses_jg2'
  const dir = mkdtempSync(join(tmpdir(), 'wxs-jngd-'))
  const opened = await journal.JournalSurface_bootWithWriterId(dir, 'writer-jg2', 'rt-jg2', 4242, '2026-01-01T00:00:00Z')
  assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
  const appended = await appendAuthorityRoot(opened.journal, sid)
  assert.equal(appended.ok, true, appended.ok ? '' : JSON.stringify(appended.error))
  try {
    const captured = []
    const reservations = joinGuard.newReservations()
    const port = capturingPort(captured, { failFirst: true })

    const first = await joinGuard.nudge(port, opened.journal, reservations, sid, 'run-jg-1', null)
    assert.equal(first.outcome, 'NotSent', 'a definite pre-acceptance refusal must surface as NotSent')

    const second = await joinGuard.nudge(port, opened.journal, reservations, sid, 'run-jg-1', null)
    assert.equal(second.outcome, 'Sent', 'the key must be released after a failed send')
    assert.equal(captured.length, 2)
  } finally {
    try { journal.JournalSurface_dispose(opened.journal) } catch {}
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[dispatch-protocol-007] JNGD_join_gate_dedupes_same_terminal_but_rearms_for_fresh_terminal', async () => {
  const sid = 'ses_jg_repeat'
  const dir = mkdtempSync(join(tmpdir(), 'wxs-jngd-repeat-'))
  const opened = await journal.JournalSurface_bootWithWriterId(dir, 'writer-jg-repeat', 'rt-jg-repeat', 4242, '2026-01-01T00:00:00Z')
  assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
  assert.equal((await appendAuthorityRoot(opened.journal, sid)).ok, true)
  try {
    const captured = []
    const reservations = joinGuard.newReservations()
    const port = capturingPort(captured)

    assert.equal((await joinGuard.nudge(port, opened.journal, reservations, sid, 'run-jg-a', null)).outcome, 'Sent')
    assert.equal(
      (await joinGuard.nudge(port, opened.journal, reservations, sid, 'run-jg-a', null)).outcome,
      'AlreadyOutstanding',
      'duplicate observation of one terminal must not double-send',
    )
    assert.equal(
      (await joinGuard.nudge(port, opened.journal, reservations, sid, 'run-jg-b', null)).outcome,
      'Sent',
      'outstanding work after a fresh terminal must receive another JoinGuard reminder',
    )
    assert.equal(captured.length, 2)
  } finally {
    try { journal.JournalSurface_dispose(opened.journal) } catch {}
    rmSync(dir, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");
const dispatch = await import("../../../dist/Interaction/Dispatch/DispatchSurface.js");
const recovery = await import("../../../dist/Interaction/Dispatch/RecoverySurface.js");

const BOOT_AFTER_CLAIM = '2099-01-01T00:00:00Z'
const capturingPort = (captured) => ({
  SubscribeTerminal: () => ({ Dispose: () => {} }),
  SendPrompt: async (session, text, options) => {
    captured.push({ text, options })
    return dispatch.admittedWithReceipt('accepted-011')
  },
})
const inheritedIdentity = {
  kind: 'RootSelection',
  ownerSession: null,
  ownerLogicalRun: null,
  ownerAuthorityRoot: null,
  participantIdentity: {
    participant: 'manager',
    role: 'manager',
    selectedTier: 'deep',
    persona: 'Lead',
    personaCatalogVersion: 1,
    origin: 'ResolvedAtRoot',
  },
}
const sendAgentOwnerRoot = async (port, handle, session, text) => {
  const ownerSession = `${session}_owner`
  const owner = await dispatch.acceptHumanRootSelection(
    handle,
    ownerSession,
    `msg_${ownerSession}`,
    inheritedIdentity,
  )
  assert.equal(owner.ok, true, owner.ok ? '' : owner.error)
  const seed = authority.issueInheritedIdentitySeed('engineer', owner.profile)
  assert.equal(seed.ok, true, seed.ok ? '' : seed.error)
  return dispatch.sendAgentOwnerRoot(port, handle, session, text, seed.value)
}
const userMessageWithKey = (id, keyValue) => ({
  id,
  role: 'user',
  metadata: { wanxiangshu_prompt_key: keyValue },
})

test('WHAT[dispatch-protocol-007] opening additional journal writers does not abandon an unresolved claim', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-dp011b-'))
  try {
    const first = await journal.JournalSurface_bootWithWriterId(base, 'writer-dp011b-1', 'rt_1', 4242, '2020-01-01T00:00:00Z')
    assert.equal(first.ok, true, first.ok ? '' : JSON.stringify(first.error))
    try {
      const captured = []
      const sent = await sendAgentOwnerRoot(
        capturingPort(captured),
        first.journal,
        'ses_011b',
        'never lands',
      )
      assert.equal(sent.ok, true, sent.ok ? '' : sent.error)
      assert.equal(captured.length, 1)

      // These are new journal writers in the same process, not process crashes.
      for (let start = 2; start <= 3; start += 1) {
        const reopened = await journal.JournalSurface_bootWithWriterId(
          base,
          `writer-dp011b-${start}`,
          `rt_${start}`,
          4240 + start,
          BOOT_AFTER_CLAIM,
        )
        assert.equal(reopened.ok, true, reopened.ok ? '' : JSON.stringify(reopened.error))
        const outcomes = await recovery.reconcile(reopened.journal, [])
        assert.equal(outcomes.length, 1)
        assert.equal(outcomes[0].outcome, 'StillPending', `启动 ${start}：未超预算，保持 Pending`)
        assert.equal(captured.length, 1, '绝不重发')
        journal.JournalSurface_dispose(reopened.journal)
      }

      // A fourth writer still cannot manufacture Abandoned/GaveUp.
      const fourth = await journal.JournalSurface_bootWithWriterId(base, 'writer-dp011b-4', 'rt_4', 4244, BOOT_AFTER_CLAIM)
      assert.equal(fourth.ok, true, fourth.ok ? '' : JSON.stringify(fourth.error))
      try {
        const unresolved = await recovery.reconcile(fourth.journal, [])
        assert.equal(unresolved.length, 1)
        assert.equal(unresolved[0].outcome, 'StillPending')
        assert.equal(
          dispatch.pendingClaimCount(fourth.journal, 'ses_011b'),
          1,
          'restart does not rewrite the broken tool into an abandonment terminal',
        )
        assert.equal(captured.length, 1, '全程一次发送：unknown outcome 永不复制逻辑效果')
      } finally {
        journal.JournalSurface_dispose(fourth.journal)
      }
    } finally {
      journal.JournalSurface_dispose(first.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})
}
