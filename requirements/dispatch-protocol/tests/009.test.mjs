import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as authority from '../../../dist/Interaction/Authority/RuntimeSurface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as contract from '../../../dist/OpenCode/Host/OpenCodeContract.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import {
  withJournal, hostPort, prepareAcceptanceObserver, sendWithAcceptanceObserver,
  acceptObservedPhysical, acceptanceObservation,
} from './support/authority.mjs'

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
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant,
      role: participant,
      selectedTier: 'deep',
      persona: personas[participant] ?? 'Unknown',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
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
  const eventsDir = join(base, '.Herdchestra', 'wanxiangshu', 'events')
  const legacy = join(eventsDir, `${writerId}.ndjson`)
  const target = existsSync(legacy) ? legacy : join(eventsDir, `${writerId}-0000.ndjson`)
  return readFileSync(target, 'utf8')
    .trim()
    .split('\n')
}

const openGitJournal = async (base, writerId, runtimeId) => {
  execFileSync('git', ['init', '--quiet', base])
  const opened = await journal.JournalSurface_bootWithWriterId(
    join(base, '.Herdchestra'),
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

test('WHAT[dispatch-protocol-009] PROMPT_007_detached_claims_and_persists_without_physical_accepted', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-prompt-007-'))
  try {
    const opened = await journal.JournalSurface_bootWithWriterId(base, 'writer-007', 'rt-007', 4242, '2026-01-01T00:00:00Z')
    assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
    try {
      const owner = await acceptOwner(opened.journal, 'ses_007_owner')
      const seed = authority.issueInheritedIdentitySeed('engineer', owner).value
      const captured = []
      const sent = await dispatch.sendAgentOwnerRoot(
        capturingPort(captured),
        opened.journal,
        'ses_007',
        'detached dispatch',
        seed,
      )
      assert.equal(sent.ok, true, sent.ok ? '' : sent.error)
      assert.equal(typeof sent.key, 'string', 'Detached still returns PromptKey')
      assert.equal(captured.length, 1, 'SendPrompt must be reached')
      assert.equal(dispatch.pendingClaimCount(opened.journal, 'ses_007'), 1, 'Detached must claim/submit (PendingClaims = 1)')
    } finally {
      journal.JournalSurface_dispose(opened.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})

test('WHAT[dispatch-protocol-009] PROMPT_007_detached_sdk_physical_id_does_not_race_chat_message_acceptance', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-prompt-007-physical-'))
  try {
    const opened = await journal.JournalSurface_bootWithWriterId(base, 'writer-007-physical', 'rt-007-physical', 4242, '2026-01-01T00:00:00Z')
    assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
    try {
      const owner = await acceptOwner(opened.journal, 'ses_007_physical_owner')
      const seed = authority.issueInheritedIdentitySeed('engineer', owner).value
      const port = capturingPort([], () => dispatch.admittedWithPhysicalMessage('msg-sdk-early-007'))
      const sent = await dispatch.sendAgentOwnerRoot(
        port,
        opened.journal,
        'ses_007_physical',
        'detached sdk physical return',
        seed,
      )
      assert.equal(sent.ok, true, sent.ok ? '' : sent.error)
      await Promise.resolve()
      assert.equal(
        dispatch.pendingClaimCount(opened.journal, 'ses_007_physical'),
        1,
        'Detached leaves PhysicalAccepted to the real chat.message ingress even if SDK returns an id early',
      )
    } finally {
      journal.JournalSurface_dispose(opened.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})

test('WHAT[dispatch-protocol-009] PROMPT_007_detached_returns_even_when_session_send_task_never_settles', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-prompt-007-never-'))
  let release
  try {
    const opened = await journal.JournalSurface_bootWithWriterId(base, 'writer-007-never', 'rt-007-never', 4242, '2026-01-01T00:00:00Z')
    assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
    try {
      const owner = await acceptOwner(opened.journal, 'ses_007_never_owner')
      const seed = authority.issueInheritedIdentitySeed('devops', owner).value
      let invoked = 0
      const never = new Promise((resolve) => { release = resolve })
      const port = {
        SubscribeTerminal: () => ({ Dispose: () => {} }),
        SendPrompt: () => {
          invoked += 1
          return never
        },
      }

      const pending = dispatch.sendAgentOwnerRoot(
        port,
        opened.journal,
        'ses_007_never',
        'detached must hand control back after invocation',
        seed,
      )
      const result = await pending
      assert.equal(result.ok, true, result.ok ? '' : result.error)
      assert.equal(invoked, 1, 'Detached still invokes Host enqueue exactly once')
      assert.equal(dispatch.pendingClaimCount(opened.journal, 'ses_007_never'), 1)
    } finally {
      release?.(dispatch.admittedWithReceipt('accepted-late-007'))
      journal.JournalSurface_dispose(opened.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})

test('WHAT[dispatch-protocol-009] PROMPT_007_detached_continuation_same_claim_path', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-prompt-007c-'))
  try {
    const opened = await journal.JournalSurface_bootWithWriterId(base, 'writer-007c', 'rt-007c', 4242, '2026-01-01T00:00:00Z')
    assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
    try {
      const owner = await acceptOwner(opened.journal, 'ses_007c_owner')
      const seed = authority.issueInheritedIdentitySeed('engineer', owner).value
      const captured = []
      const port = capturingPort(captured)
      const root = await dispatch.sendAgentOwnerRoot(
        port,
        opened.journal,
        'ses_007c',
        'root for continuation',
        seed,
      )
      assert.equal(root.ok, true, root.ok ? '' : root.error)

      const accepted = await dispatch.acceptAgentOwnerRoot(opened.journal, 'ses_007c', root.key, 'msg-root-ses_007c')
      assert.equal(accepted.ok, true, accepted.error)

      const cont = await dispatch.sendContinuation(
        port,
        opened.journal,
        'ses_007c',
        'busy nudge text',
        'BusyAgentNudge',
        accepted.profile,
        'Detached',
      )
      assert.equal(cont.ok, true, cont.ok ? '' : cont.error)
      assert.equal(captured.length, 2)
      assert.ok(dispatch.projectionObservation(opened.journal, 'ses_007c').pendingClaims.length >= 1, 'continuation claim must persist')
    } finally {
      journal.JournalSurface_dispose(opened.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})

test('WHAT[dispatch-protocol-009] PROMPT_007_detached_late_listener_verdict_routes_to_exact_owner', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-prompt-007-late-'))
  const writerId = 'writer-007-late'
  const opened = await openGitJournal(base, writerId, 'rt-007-late')
  try {
    const owner = await acceptOwner(opened.journal, 'ses_007_late_owner')
    const seed = authority.issueInheritedIdentitySeed('engineer', owner).value
    const captured = []
    const sent = await sendDetachedRoot(
      verdictPort(captured, () => dispatch.admittedWithReceipt('accepted-007-late')),
      opened.journal,
      'ses_007_late',
      'detached late verdict send',
      seed,
    )
    const listener = captured[0].options.DetachedListener
    assert.equal(typeof listener, 'function', 'Detached must leave its verdict listener on the send options')
    await waitForSettledClaims(opened.journal, 'ses_007_late', 1, 'admission keeps the claim Pending')
    await listener(refused('late transport refusal after admission'))
    await waitForSettledClaims(opened.journal, 'ses_007_late', 0, 'late Refused must abandon the exact claim')
    assert.equal(captured.length, 1, 'late Refused must not re-emit the physical send')
    const abandoned = journalLines(base, writerId).filter((line) => line.includes('PluginPromptAbandoned'))
    assert.equal(abandoned.length, 1, 'late Refused must write exactly one durable Abandoned fact')
    assert.ok(abandoned[0].includes(sent.key), 'the late Abandoned fact must name the exact PromptKey')
    assert.ok(abandoned[0].includes('SendFailed'), 'the late Abandoned fact must carry the SendFailed reason')
  } finally {
    journal.JournalSurface_dispose(opened.journal)
    rmSync(base, { recursive: true, force: true })
  }
})

test('WHAT[dispatch-protocol-009] PROMPT_007_detached_late_unknown_keeps_claim_then_refused_abandons', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-prompt-007-dup-'))
  const writerId = 'writer-007-dup'
  const opened = await openGitJournal(base, writerId, 'rt-007-dup')
  try {
    const owner = await acceptOwner(opened.journal, 'ses_007_dup_owner')
    const seed = authority.issueInheritedIdentitySeed('engineer', owner).value
    const captured = []
    await sendDetachedRoot(
      verdictPort(captured, () => dispatch.admittedWithReceipt('accepted-007-dup')),
      opened.journal,
      'ses_007_dup',
      'detached duplicate verdict send',
      seed,
    )
    const listener = captured[0].options.DetachedListener
    await listener(outcomeUnknown('first late verdict loses the response'))
    await waitForSettledClaims(opened.journal, 'ses_007_dup', 1, 'late OutcomeUnknown must keep the claim Pending')
    await listener(refused('second late verdict refuses after unknown'))
    await waitForSettledClaims(opened.journal, 'ses_007_dup', 0, 'the later Refused still abandons the exact claim')
    assert.equal(captured.length, 1, 'late verdicts must never re-emit the physical send')
    assert.equal(
      journalLines(base, writerId).filter((line) => line.includes('PluginPromptAbandoned')).length,
      1,
      'unknown-then-refused must settle the durable claim exactly once',
    )
  } finally {
    journal.JournalSurface_dispose(opened.journal)
    rmSync(base, { recursive: true, force: true })
  }
})

test('WHAT[dispatch-protocol-009] PROMPT_007_detached_owned_settled_late_delivery_writes_no_fact', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-prompt-007-owned-'))
  const writerId = 'writer-007-owned'
  const opened = await openGitJournal(base, writerId, 'rt-007-owned')
  try {
    const owner = await acceptOwner(opened.journal, 'ses_007_owned_owner')
    const seed = authority.issueInheritedIdentitySeed('engineer', owner).value
    const captured = []
    await sendDetachedRoot(
      verdictPort(captured, () => dispatch.admittedWithReceipt('accepted-007-owned')),
      opened.journal,
      'ses_007_owned',
      'detached owned settled send',
      seed,
    )
    const listener = captured[0].options.DetachedListener
    await listener(Verdict.OwnedSettled)
    await waitForSettledClaims(opened.journal, 'ses_007_owned', 1, 'OwnedSettled must leave the Pending claim untouched')
    assert.equal(captured.length, 1, 'OwnedSettled late delivery must never re-emit the physical send')
    assert.equal(
      journalLines(base, writerId).filter((line) => line.includes('PluginPromptAbandoned')).length,
      0,
      'OwnedSettled late delivery must write no durable fact',
    )
  } finally {
    journal.JournalSurface_dispose(opened.journal)
    rmSync(base, { recursive: true, force: true })
  }
})

const deliverHostVerdict = async (options, kind, reason = null) => {
  assert.equal(typeof options.DetachedListener, 'function')
  assert.deepEqual(await dispatch.deliverDetachedVerdict(options.DetachedListener, kind, reason), { delivered: true })
}

for (const path of ['root', 'continuation']) {
  test(`WHAT[dispatch-protocol-009] definite refusal of a detached ${path} still cancels all confirmation waits for the abandoned key`, async () => {
    await withJournal(`a2-refusal-wait-${path}`, async handle => {
      const context = await prepareAcceptanceObserver(handle, path, 'refusal-waiters')
      const seen = []
      let options
      const sent = await sendWithAcceptanceObserver(path,
        hostPort(async (_session, _text, supplied) => {
          options = supplied
          return dispatch.admittedWithReceipt('refusal-waiters-receipt')
        }), handle, context, physical => seen.push(physical),
      )
      assert.equal(sent.ok, true, sent.error)
      const first = dispatch.awaitPhysicalConfirmation(sent.key, 1000)
      const second = dispatch.awaitPhysicalConfirmation(sent.key, 1000)
      await deliverHostVerdict(options, 'Refused', 'Host proves this request was never sent')
      assert.deepEqual(await first, { kind: 'Rejected', physical: null, reason: 'Cancelled' })
      assert.deepEqual(await second, { kind: 'Rejected', physical: null, reason: 'Cancelled' })
      assert.equal(dispatch.pendingClaimCount(handle, context.session), 0)
      assert.equal((await acceptObservedPhysical(handle, context, sent.key, `invalid-${context.session}`)).ok, false)
      assert.deepEqual(seen, [])
    })
  })

  test(`WHAT[dispatch-protocol-009] detached ${path} returns while SDK is unresolved and observes actual managed acceptance`, async () => {
    await withJournal(`d0-${path}-pending-sdk`, async handle => {
      const context = await prepareAcceptanceObserver(handle, path, 'pending-sdk')
      const seen = []
      let release
      let sends = 0
      const sdk = new Promise(resolve => { release = resolve })
      try {
        const sent = await sendWithAcceptanceObserver(path,
          hostPort(() => { sends += 1; return sdk }), handle, context,
          physical => seen.push(acceptanceObservation(handle, context, physical)),
        )
        assert.equal(sent.ok, true, sent.error)
        assert.equal(sends, 1)
        assert.equal(dispatch.pendingClaimCount(handle, context.session), 1)
        assert.deepEqual(seen, [])
        const physical = `actual-${context.session}`
        const accepted = await acceptObservedPhysical(handle, context, sent.key, physical)
        assert.equal(accepted.ok, true, accepted.error)
        assert.deepEqual(seen, [{ physical, pending: 0, accepted: true }])
        assert.equal(sends, 1)
      } finally { release(dispatch.admittedWithReceipt(`late-${context.session}`)) }
    })
  })

  test(`WHAT[dispatch-protocol-009] detached ${path} OwnedSettled delivery retains its observer without proving physical acceptance`, async () => {
    await withJournal(`d0-${path}-owned`, async handle => {
      const context = await prepareAcceptanceObserver(handle, path, 'owned')
      const seen = []
      let options
      let release
      const sdk = new Promise(resolve => { release = resolve })
      try {
        const sent = await sendWithAcceptanceObserver(path,
          hostPort((_session, _text, supplied) => { options = supplied; return sdk }),
          handle, context, physical => seen.push(acceptanceObservation(handle, context, physical)),
        )
        assert.equal(sent.ok, true, sent.error)
        // This awaits the real Host listener's no-op verdict, not the still
        // unresolved SDK task or its detached observer.
        await deliverHostVerdict(options, 'OwnedSettled')
        assert.deepEqual(seen, [])
        assert.equal(dispatch.pendingClaimCount(handle, context.session), 1)
        const physical = `ingress-${context.session}`
        assert.equal((await acceptObservedPhysical(handle, context, sent.key, physical)).ok, true)
        assert.deepEqual(seen, [{ physical, pending: 0, accepted: true }])
      } finally { release(dispatch.admittedWithReceipt('late-owned-sdk-result')) }
    })
  })

  test(`WHAT[dispatch-protocol-009] detached ${path} retains its observer after a real Host Unknown delivery until actual physical evidence`, async () => {
    await withJournal(`d0-${path}-unknown`, async handle => {
      const context = await prepareAcceptanceObserver(handle, path, 'unknown')
      const seen = []
      let options
      let release
      let sends = 0
      const sdk = new Promise(resolve => { release = resolve })
      try {
        const sent = await sendWithAcceptanceObserver(path,
          hostPort((_session, _text, supplied) => { sends += 1; options = supplied; return sdk }),
          handle, context, physical => seen.push(acceptanceObservation(handle, context, physical)),
        )
        assert.equal(sent.ok, true, sent.error)
        await deliverHostVerdict(options, 'OutcomeUnknown', 'the eventual response cannot be read')
        assert.equal(dispatch.pendingClaimCount(handle, context.session), 1)
        assert.deepEqual(seen, [])
        const physical = `proven-after-unknown-${context.session}`
        assert.equal((await acceptObservedPhysical(handle, context, sent.key, physical)).ok, true)
        assert.deepEqual(seen, [{ physical, pending: 0, accepted: true }])
        assert.equal(sends, 1)
      } finally { release(dispatch.admittedWithReceipt('late-unknown-sdk-result')) }
    })
  })

  test(`WHAT[dispatch-protocol-009] detached ${path} definite Host refusal rejects later acceptance of the abandoned key without notifying`, async () => {
    await withJournal(`d0-${path}-refused`, async handle => {
      const context = await prepareAcceptanceObserver(handle, path, 'refused')
      const seen = []
      let options
      let release
      const sdk = new Promise(resolve => { release = resolve })
      try {
        const sent = await sendWithAcceptanceObserver(path,
          hostPort((_session, _text, supplied) => { options = supplied; return sdk }),
          handle, context, physical => seen.push(acceptanceObservation(handle, context, physical)),
        )
        assert.equal(sent.ok, true, sent.error)
        await deliverHostVerdict(options, 'Refused', 'Host proved this enqueue was never accepted')
        assert.equal(dispatch.pendingClaimCount(handle, context.session), 0)
        const rejected = await acceptObservedPhysical(handle, context, sent.key, `invalid-after-refusal-${context.session}`)
        assert.equal(rejected.ok, false)
        assert.deepEqual(seen, [])
      } finally { release(dispatch.admittedWithReceipt('late-refused-sdk-result')) }
    })
  })
}
