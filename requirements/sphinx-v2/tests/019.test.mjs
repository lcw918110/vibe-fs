import assert from 'node:assert/strict'
import { randomUUID } from 'node:crypto'
import { existsSync, mkdtempSync, readFileSync, realpathSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import { runVerificationToolProbe } from '../../../scripts/lib/verification-tool-probe.mjs'
import * as persistence from '../../../dist/Sphinx/V2/Persistence/Surface.js'
import {
  store, digest, mustOk, body, envelope, goal, createdBody, work,
  transition, accepted, graphNode, seedBodies, batch, prepare, append, current, withStore,
} from './persistence-support.mjs'
import { journalBytes } from './interpretation-support.mjs'

const text = '\n原文保留\r\n  不归一化  '

const priorSealed = JSON.parse(readFileSync(new URL('./fixtures/gen143-sealed-transitions.json', import.meta.url), 'utf8'))
const priorInterpretationBytes = readFileSync(new URL('./fixtures/gen190-sealed-interpretations.json', import.meta.url))
const priorInterpretations = JSON.parse(priorInterpretationBytes.toString('utf8'))

test('WHAT[sphinx-v2-019] an actual gen190 Pending seal retains its exact native state and bytes through canonical cold replay', async () => {
  assert.equal(digest(priorInterpretationBytes), '3e491ba0bb8ea456facbb55fb6c2adb23b71a41a442662d1a0800711125717ec')
  assert.equal(priorInterpretations.generation, 190)
  assert.equal(priorInterpretations.head, '101ff8d3522e20e0875e8c9507ba2722472ce66e')
  assert.deepEqual(priorInterpretations.freshness, {
    generation: 190,
    compilerInputDigest: '94becf38ef47b1ebb4ac3bd801697c3c72805deea595b26458969918959d920e',
    generatedInputDigest: '7e1e436680aaebd7854d037e7812a3b7d15d1ac0e547713b8e481ad4d95d014a',
    artifactInputDigest: '845bbaca212c5fcc788ba4418658d405cee89498b947218554acfddd6f29e36c',
  })
  const scenario = priorInterpretations.scenarios.find(value => value.name === 'pending')
  assert.equal(scenario.events.length, 1)
  await withStore(async ({ open, close, commonDir }) => {
    const writer = open()
    const receipt = await store.append(writer, scenario.events)
    assert.equal(receipt.ok, true, JSON.stringify(receipt.error))
    assert.deepEqual(receipt.cuts, [])
    const live = current(writer, scenario.inquiry)
    assert.deepEqual(mustOk(live), scenario.state)
    assert.equal(Object.keys(mustOk(live).interpretations[0].value).length, 7)
    assert.equal(live.stateHash, scenario.stateHash)
    assert.equal(live.stateHash, scenario.events[0].payload.postStateFingerprint)
    const bytes = Buffer.from(journalBytes(commonDir)[0][1], 'base64')
    assert.equal(digest(bytes), scenario.writerBytesSha256)
    assert.equal(bytes.toString('base64'), scenario.writerBytesBase64)
    assert.deepEqual(store.read(writer, scenario.events[0].id), scenario.events[0])
    close(writer)
    assert.deepEqual(current(open(), scenario.inquiry), live)
  })
})

test('WHAT[sphinx-v2-019] actual gen190 no-op Applied and Failed seals become explicit cuts while their original envelopes remain intact', async t => {
  for (const scenario of priorInterpretations.scenarios.filter(value => value.name !== 'pending')) {
    await t.test('WHAT[sphinx-v2-019] cuts the old ' + scenario.name + ' complete post-state fingerprint', async () => {
      assert.equal(scenario.events.length, 2)
      const originalBytes = Buffer.from(scenario.writerBytesBase64, 'base64')
      assert.equal(digest(originalBytes), scenario.writerBytesSha256)
      await withStore(async ({ open, close, commonDir }) => {
        const writer = open()
        const receipt = await store.append(writer, scenario.events)
        assert.equal(receipt.ok, true, JSON.stringify(receipt.error))
        assert.equal(receipt.cuts.length, 1)
        assert.equal(receipt.cuts[0].rule, 'SphinxV2')
        assert.equal(receipt.cuts[0].failedEventId, scenario.events[1].id)
        assert.match(receipt.cuts[0].reason, /post-state-mismatch/)
        for (const event of scenario.events) assert.deepEqual(store.read(writer, event.id), event)
        const bytes = Buffer.from(journalBytes(commonDir)[0][1], 'base64')
        assert.deepEqual(bytes.subarray(0, originalBytes.length), originalBytes, 'the old canonical history is not rewritten or resealed')
        assert.equal(store.read(writer, receipt.cuts[0].cutEventId).type, 'ProjectionCutTail')
        const refused = current(writer, scenario.inquiry)
        assert.equal(refused.ok, false)
        assert.equal(refused.error.code, 'SemanticCut')
        close(writer)
        assert.deepEqual(current(open(), scenario.inquiry), refused)
      })
    })
  }
})

test('WHAT[sphinx-v2-019] previously sealed @2 creation and undispatched work retain their exact complete state through cold replay', async t => {
  assert.equal(priorSealed.head, '45e4137e4fd58c9788f160246401d562dc0e5d29')
  assert.equal(priorSealed.generation, 143)
  for (const scenario of priorSealed.scenarios.slice(0, 2)) {
    await t.test('WHAT[sphinx-v2-019] preserves actual prior seal for ' + scenario.inquiry, async () => {
      await withStore(async ({ open, close }) => {
        const writer = open()
        const receipt = await store.append(writer, [scenario.event])
        assert.equal(receipt.ok, true)
        assert.deepEqual(receipt.cuts, [])
        const live = current(writer, scenario.inquiry)
        assert.deepEqual(mustOk(live), scenario.state)
        assert.equal(live.stateHash, scenario.event.payload.postStateFingerprint)
        close(writer)
        assert.deepEqual(current(open(), scenario.inquiry), live)
      })
    })
  }
})

test('WHAT[sphinx-v2-019] prior missing-round dispatch and aggregate reservation seals become explicit durable cuts', async t => {
  for (const scenario of priorSealed.scenarios.slice(2)) {
    await t.test('WHAT[sphinx-v2-019] refuses the prior incorrect seal for ' + scenario.inquiry, async () => {
      await withStore(async ({ open, close }) => {
        const writer = open()
        const receipt = await store.append(writer, [scenario.event])
        assert.equal(receipt.ok, true)
        assert.equal(receipt.cuts.length, 1)
        assert.match(receipt.cuts[0].reason, scenario.inquiry === 'legacy-noop-dispatch' ? /unknown-round/ : /post-state-mismatch/)
        assert.deepEqual(store.read(writer, scenario.event.id), scenario.event, 'the original history remains intact')
        const refused = current(writer, scenario.inquiry)
        assert.equal(refused.ok, false)
        assert.equal(refused.error.code, 'SemanticCut')
        close(writer)
        assert.deepEqual(current(open(), scenario.inquiry), refused)
      })
    })
  }
})

const expectCut = async (handle, event, code) => {
  const receipt = await store.append(handle, [event])
  assert.equal(receipt.ok, true, JSON.stringify(receipt.error))
  assert.equal(receipt.cuts.length, 1)
  assert.equal(receipt.cuts[0].failedEventId, event.id)
  assert.equal(receipt.cuts[0].rule, 'SphinxV2')
  assert.match(receipt.cuts[0].reason, new RegExp(code))
  assert.equal(store.read(handle, event.id).id, event.id, 'bad fact is preserved, not deleted')
  const cut = store.read(handle, receipt.cuts[0].cutEventId)
  assert.equal(cut.type, 'ProjectionCutTail')
  return receipt.cuts[0]
}

test('WHAT[sphinx-v2-019] an older answer without an observation reference is a durable strict DTO cut', async () => {
  await withStore(async ({ open, close }) => {
    const handle = open()
    const creation = await append(handle, batch('old-answer', 'create', [createdBody('answer provenance')]))
    const candidate = prepare(handle, batch('old-answer', 'answer', [
      body('WorkPlanned', { work: [work('answer-work')] }),
      transition('answer-work', 'Planned', {
        case: 'Running', fence: 'answer-work:1:logical', physicalRef: 'answer-work:host-receipt',
      }),
      accepted('answer-work', 'answer-result'),
      body('AnswerCommitted', {
        renderWorkId: 'answer-work', resultObservationId: 'answer-result',
        answerRef: 'answer-artifact', stopReason: 'model-ranked-stop',
      }),
    ], creation))
    delete candidate.payload.events.at(-1).payload.resultObservationId
    const receipt = await store.append(handle, [candidate])
    assert.equal(receipt.ok, true, JSON.stringify(receipt.error))
    assert.equal(receipt.cuts.length, 1)
    assert.match(receipt.cuts[0].reason, /INVALID_TRANSITION_DTO/)
    assert.equal(receipt.cuts[0].failedEventId, candidate.id)
    assert.deepEqual(store.read(handle, candidate.id), candidate)
    const rejected = current(handle, 'old-answer')
    assert.equal(rejected.error.code, 'SemanticCut')
    close(handle)
    assert.deepEqual(current(open(), 'old-answer'), rejected)
  })
})

test('WHAT[sphinx-v2-019] a rejected multi-body preparation never publishes its valid prefix', async () => {
  await withStore(async ({ open }) => {
    const handle = open()
    const creation = await append(handle, batch('atomic-inquiry', 'create', [createdBody(text)]))
    const before = current(handle, 'atomic-inquiry')
    const bad = batch('atomic-inquiry', 'bad', [
      body('CancelRequested', { reason: 'prefix must not publish' }),
      transition('missing-work', 'Planned', { case: 'Ready' }),
    ], creation)
    const result = persistence.prepareTransition(handle, digest, bad)
    assert.equal(result.ok, false)
    assert.match(result.error.message, /not planned/)
    assert.deepEqual(current(handle, 'atomic-inquiry'), before)
    assert.deepEqual(store.heads(handle, creation.stream), [creation.id])
    assert.equal(mustOk(before).commandReceipts.length, 1)
  })
})

test('WHAT[sphinx-v2-019] an unknown body after a valid body produces a durable semantic cut and no partial Current even after reopen', async () => {
  await withStore(async ({ open, close }) => {
    const first = open()
    const creation = await append(first, batch('bad-body', 'create', [createdBody(text)]))
    const candidate = prepare(first, batch('bad-body', 'bad', [body('CancelRequested', { reason: 'prefix' })], creation))
    candidate.payload.events.push(body('UnknownBody', { reason: 'must refuse' }))
    await expectCut(first, candidate, 'INVALID_TRANSITION_DTO')
    const rejected = current(first, 'bad-body')
    assert.equal(rejected.ok, false)
    assert.equal(rejected.error.code, 'SemanticCut')
    assert.equal(Object.hasOwn(rejected, 'value'), false)
    assert.deepEqual(store.read(first, creation.id).payload.events, [createdBody(text)])
    close(first)
    const reopened = open()
    assert.deepEqual(current(reopened, 'bad-body'), rejected)
  })
})

test('WHAT[sphinx-v2-019] internal and external parent edges must agree and foreign valid history is not silently used as the base', async () => {
  await withStore(async ({ open, close }) => {
    const handle = open()
    const own = await append(handle, batch('own-inquiry', 'own', [createdBody(text)]))
    const foreign = await append(handle, batch('foreign-inquiry', 'foreign', [createdBody('foreign goal')]))
    const foreignBefore = current(handle, 'foreign-inquiry')
    const candidate = prepare(handle, batch('own-inquiry', 'bad-parent', [body('CancelRequested', { reason: 'stop' })], own))
    candidate.payload.previousHead = foreign.id
    await expectCut(handle, candidate, 'PARENT_MISMATCH')
    assert.equal(current(handle, 'own-inquiry').error.code, 'SemanticCut')
    assert.deepEqual(current(handle, 'foreign-inquiry'), foreignBefore)
    close(handle)
    const reopened = open()
    assert.equal(current(reopened, 'own-inquiry').error.code, 'SemanticCut')
    assert.deepEqual(current(reopened, 'foreign-inquiry'), foreignBefore)
  })
})

test('WHAT[sphinx-v2-019] an absent outer parent is StorageInvalid and cannot publish any transition', async () => {
  await withStore(async ({ open, close }) => {
    const handle = open()
    const creation = await append(handle, batch('missing-parent', 'create', [createdBody(text)]))
    const before = current(handle, 'missing-parent')
    const candidate = prepare(handle, batch('missing-parent', 'bad-parent', [body('CancelRequested', { reason: 'stop' })], creation))
    candidate.parents = ['absent-parent']
    const receipt = await store.append(handle, [candidate])
    assert.equal(receipt.ok, false)
    assert.equal(receipt.error.code, 'StorageInvalid')
    assert.equal(receipt.error.error.code, 'MissingParent')
    close(handle)
    const reopened = open()
    assert.deepEqual(current(reopened, 'missing-parent'), before)
    assert.equal(store.read(reopened, candidate.id), null)
  })
})

test('WHAT[sphinx-v2-019] identical event identity with changed payload is a storage collision rather than a rewrite', async () => {
  await withStore(async ({ open, close }) => {
    const handle = open()
    const first = await append(handle, batch('collision-inquiry', 'create', [createdBody(text)]))
    const before = current(handle, 'collision-inquiry')
    const changed = prepare(handle, batch('collision-inquiry', 'create', [createdBody('changed goal')]))
    assert.equal(changed.id, first.id, 'identity must not depend on post-state')
    assert.notEqual(changed.payload.postStateFingerprint, first.payload.postStateFingerprint)
    const receipt = await store.append(handle, [changed])
    assert.equal(receipt.ok, false)
    assert.equal(receipt.error.code, 'StorageInvalid')
    assert.equal(receipt.error.error.code, 'IdentityCollision')
    close(handle)
    const reopened = open()
    assert.deepEqual(current(reopened, 'collision-inquiry'), before)
    assert.deepEqual(store.read(reopened, first.id), first)
  })
})

test('WHAT[sphinx-v2-019] legal same-parent forks preserve both writers and canonical Current refuses to pick a winner', async () => {
  for (const order of [['left', 'right'], ['right', 'left']]) {
    await withStore(async ({ open, close }) => {
      const rootWriter = open()
      const creation = await append(rootWriter, batch('fork-inquiry', 'create', [createdBody(text)]))
      close(rootWriter)
      const leftWriter = open()
      const rightWriter = open()
      const left = prepare(leftWriter, batch('fork-inquiry', 'left', [body('CancelRequested', { reason: 'left' })], creation))
      const right = prepare(rightWriter, batch('fork-inquiry', 'right', [body('InquirySuspended', { reason: 'right' })], creation))
      const writers = { left: leftWriter, right: rightWriter }
      const events = { left, right }
      for (const name of order) {
        const receipt = await store.append(writers[name], [events[name]])
        assert.equal(receipt.ok, true, JSON.stringify(receipt.error))
        assert.deepEqual(receipt.cuts, [], 'a legitimate branch is not corruption or semantic rejection')
      }
      close(leftWriter)
      close(rightWriter)
      const reopened = open()
      const conflict = current(reopened, 'fork-inquiry')
      assert.equal(conflict.ok, false)
      assert.equal(conflict.error.code, 'DomainConflict')
      assert.deepEqual(conflict.error.heads, [left.id, right.id].sort())
      assert.deepEqual(store.heads(reopened, creation.stream), [left.id, right.id].sort())
      assert.deepEqual(store.read(reopened, left.id), left)
      assert.deepEqual(store.read(reopened, right.id), right)
      assert.equal(persistence.admitCancel(reopened, { inquiry: 'fork-inquiry', commandId: 'new', commandFingerprint: digest('new'), reason: 'stop' }).error.code, 'DomainConflict')
    })
  }
})

test('WHAT[sphinx-v2-019] a later legitimate child of an accepted ancestor is not rejected by a global last-state check', async () => {
  await withStore(async ({ open, close }) => {
    const handle = open()
    const creation = await append(handle, batch('ancestor-inquiry', 'create', [createdBody(text)]))
    const first = await append(handle, batch('ancestor-inquiry', 'first', [body('CancelRequested', { reason: 'first' })], creation))
    const sibling = await append(handle, batch('ancestor-inquiry', 'sibling', [body('InquirySuspended', { reason: 'sibling' })], creation))
    assert.deepEqual(current(handle, 'ancestor-inquiry').error.heads, [first.id, sibling.id].sort())
    close(handle)
    assert.deepEqual(current(open(), 'ancestor-inquiry').error.heads, [first.id, sibling.id].sort())
  })
})

test('WHAT[sphinx-v2-019] post-state verification checks actual complete state, not array sizes or the old incomplete stateHash', async () => {
  await withStore(async ({ open, close }) => {
    const handle = open()
    const creation = await append(handle, batch('fingerprint-inquiry', 'create', [createdBody(text)]))
    // A same-length change in a formerly omitted field must invalidate the sealed state.
    const candidate = prepare(handle, batch('fingerprint-inquiry', 'invalid-hash', [body('CancelRequested', { reason: 'stop' })], creation))
    candidate.payload.postStateFingerprint = digest('not the complete state')
    await expectCut(handle, candidate, 'post-state-mismatch')
    assert.equal(current(handle, 'fingerprint-inquiry').error.code, 'SemanticCut')
    close(handle)
    assert.equal(current(open(), 'fingerprint-inquiry').error.code, 'SemanticCut')
  })
  const hashes = []
  for (const configHash of [digest('configuration-a'), digest('configuration-b')]) {
    await withStore(async ({ open }) => {
      const handle = open()
      const created = createdBody(text)
      created.payload.configHash = configHash
      await append(handle, batch('same-hash-input', 'same-command', [created]))
      hashes.push(current(handle, 'same-hash-input').stateHash)
    })
  }
  assert.notEqual(hashes[0], hashes[1], 'full materialized config is not erased by equal-sized projections')
})

test('WHAT[sphinx-v2-019] canonical replay honors the parent when a child identity sorts before it', async () => {
  await withStore(async ({ open, close }) => {
    const rootWriter = open()
    const creation = await append(rootWriter, batch('causal-inquiry', 'create', [createdBody(text)]))
    close(rootWriter)
    const childWriter = open()
    let child = null
    // Input selection only: the assertion below still reads the real canonical owner.
    for (let index = 0; index < 128 && child === null; index += 1) {
      const candidate = prepare(childWriter, batch('causal-inquiry', 'child-' + index, [body('InquirySuspended', { reason: 'causal child' })], creation))
      if (candidate.id < creation.id) child = candidate
    }
    assert.notEqual(child, null, 'fixture must contain a lexically earlier child')
    const receipt = await store.append(childWriter, [child])
    assert.equal(receipt.ok, true)
    assert.deepEqual(receipt.cuts, [])
    close(childWriter)
    const replayed = mustOk(current(open(), 'causal-inquiry'))
    assert.equal(replayed.eventHead, child.id)
    assert.equal(replayed.revision, '1')
    assert.deepEqual(replayed.status, { case: 'Suspended', reason: 'causal child' })
    assert.equal(replayed.goal.originalText, text)
  })
})

test('WHAT[sphinx-v2-019] strict ingress rejects malformed fields and arrays without silently dropping them', () => {
  const valid = batch('ingress', 'create', [createdBody(text)])
  const invalid = [
    { ...valid, revision: 0 }, { ...valid, revision: '01' }, { ...valid, revision: '9223372036854775808' },
    { ...valid, events: {} }, { ...valid, events: [] }, { ...valid, extra: true },
    { ...valid, events: [body('InquiryCreated', { ...createdBody(text).payload, resourceSpecs: {} })] },
    { ...valid, events: [body('WorkPlanned', { work: [{ ...work('work-1'), attempt: 1.5 }] })] },
    { ...valid, events: [body('WorkPlanned', { work: [{ ...work('work-1'), dependencies: ['same', 'same'] }] })] },
    { ...valid, events: [body('CancelRequested', { reason: 17 })] },
    { ...valid, events: [body('CancelRequested', { reason: 'stop', certificatePatches: [] })] },
  ]
  for (const value of invalid) {
    const result = persistence.canonicalizeTransition(value)
    assert.equal(result.ok, false)
    assert.equal(result.error.code, 'INVALID_TRANSITION_DTO')
    assert.equal(Object.hasOwn(result, 'value'), false)
  }
  assert.deepEqual(mustOk(persistence.canonicalizeTransition(valid)), valid)
})

test('WHAT[sphinx-v2-019] frozen @1 material is preserved and explicitly cut, never silently restored as an empty inquiry', async () => {
  await withStore(async ({ open, close }) => {
    const first = open()
    const old = { id: 'legacy-transition', stream: 'sphinx-v2/legacy-inquiry', type: 'sphinx/v2-transition@1',
      parents: [], payload: { frozenHistoricalMaterial: true }, payloadRefs: [] }
    await expectCut(first, old, 'LEGACY_TRANSITION_UNSUPPORTED')
    const rejected = current(first, 'legacy-inquiry')
    assert.equal(rejected.error.code, 'SemanticCut')
    assert.match(rejected.error.message, /LEGACY_TRANSITION_UNSUPPORTED/)
    close(first)
    const reopened = open()
    assert.deepEqual(store.read(reopened, old.id), old)
    assert.deepEqual(current(reopened, 'legacy-inquiry'), rejected)
  })
})

test('WHAT[sphinx-v2-019] creation and multi-body work transition append atomically at one revision and survive a new writer', async () => {
  await withStore(async ({ open, close }) => {
    const first = open()
    const creation = await append(first, batch('inquiry-1', 'create', [createdBody(text)]))
    const original = mustOk(current(first, 'inquiry-1'))
    assert.equal(original.goal.originalText, text)
    assert.equal(original.revision, '0')
    assert.equal(original.eventHead, creation.id)
    const planned = await append(first, batch('inquiry-1', 'plan', [
      body('RoundOpened', { roundId: 'round-1', scopeId: 'scope-1', expectedWork: ['work-1'] }),
      body('WorkPlanned', { work: [work('work-1')] }),
      transition('work-1', 'Planned', { case: 'Ready' }),
      transition('work-1', 'Ready', { case: 'Leased', fence: 'work-1:1:logical' }),
      transition('work-1', 'Leased', { case: 'Running', fence: 'work-1:1:logical', physicalRef: 'adapter-receipt-1' }),
    ], creation))
    const live = current(first, 'inquiry-1')
    const value = mustOk(live)
    assert.equal(value.revision, '1', 'all bodies are one atomic logical revision')
    assert.equal(value.eventHead, planned.id)
    assert.deepEqual(value.work[0].value.state, { case: 'Running', fence: 'work-1:1:logical', physicalRef: 'adapter-receipt-1' })
    assert.equal(value.work[0].value.spec.physicalRef, 'adapter-receipt-1')
    assert.equal(value.commandReceipts.length, 2)
    assert.deepEqual(value.commandReceipts.find(entry => entry.key === 'plan').value, {
      fingerprint: digest('command:plan'), revision: '1', eventId: planned.id,
    })
    assert.equal(original.work.length, 0, 'preparation/fold never mutates an earlier native snapshot')
    assert.deepEqual(mustOk(persistence.canonicalizeTransition(store.read(first, planned.id).payload)), planned.payload)
    close(first)
    const reopened = open()
    assert.deepEqual(current(reopened, 'inquiry-1'), live)
  })
})

test('WHAT[sphinx-v2-019] cancellation and a late cancel remain honest across writer close and canonical cold replay', async () => {
  await withStore(async ({ open, close }) => {
    const first = open()
    const creation = await append(first, batch('cancel-inquiry', 'create', [createdBody(text)]))
    const request = await append(first, batch('cancel-inquiry', 'cancel', [body('CancelRequested', { reason: 'user stop' })], creation))
    const cancelling = current(first, 'cancel-inquiry')
    assert.deepEqual(mustOk(cancelling).status, { case: 'Cancelling' })
    close(first)
    const second = open()
    assert.deepEqual(current(second, 'cancel-inquiry'), cancelling)
    const terminal = await append(second, batch('cancel-inquiry', 'terminal', [body('InquiryCancelled', { reason: 'settled' })], request))
    await append(second, batch('cancel-inquiry', 'late-cancel', [body('CancelRequested', { reason: 'late' })], terminal))
    const cancelled = current(second, 'cancel-inquiry')
    assert.deepEqual(mustOk(cancelled).status, { case: 'Cancelled', reason: 'settled' })
    assert.equal(mustOk(cancelled).revision, '3')
    close(second)
    assert.deepEqual(current(open(), 'cancel-inquiry'), cancelled)
  })
})

const usage = { workId: 'work-2', attempt: 1, resources: [{ key: 'calls', value: 1 }], moneyMinor: '19', usageUnresolved: false, overrun: false }
const amendedGoal = {
  ...goal(text), revision: '1', constraints: ['user constraint'],
  amendments: [{ authorizedBy: 'user', revision: '1', addedConstraints: ['user constraint'], replacedText: null }],
}
// Public @2 vocabulary, not compiler tag numbers. No-op handlers are only tested for
// faithful durable representation here, not claimed as completed business operations.
const cases = [
  createdBody(text),
  body('GoalAmended', amendedGoal),
  body('SnapshotRegistered', envelope('{}')),
  body('DecisionScopeOpened', envelope('{}')),
  body('RoundOpened', { roundId: 'round-2', scopeId: 'scope-1', expectedWork: ['work-2'] }),
  body('WorkPlanned', { work: [work('work-3')] }),
  body('RoundClosed', { roundId: 'round-1', outcome: 'complete' }),
  body('BudgetReserved', { reservation: { workId: 'work-2', attempt: 1, resources: [{ key: 'calls', value: 1 }], moneyMinor: '19' }, renderReserve: [{ key: 'calls', value: 1 }] }),
  body('UsageSettled', { usage }),
  body('ReservationReleased', { workId: 'work-2', attempt: 1 }),
  body('UsageOverrunRecorded', { usage: { ...usage, overrun: true } }),
  body('DispatchRequested', { work: work('work-2'), dispatchIntentId: 'dispatch-1', publicEnvelope: envelope('{}'), privateTicket: envelope('{}') }),
  body('DispatchReceiptRecorded', { workId: 'work-2', attempt: 1, fence: 'work-2:1:logical', dispatchIntentId: 'dispatch-1', physicalRef: 'adapter-receipt-2', receipt: envelope('{}') }),
  transition('work-2', 'Running', { case: 'Failed', attempt: 1 }),
  body('HostTerminalRecorded', { workId: 'work-2', attempt: 1, fence: 'work-2:1:logical', terminal: 'stopped', receipt: envelope('{}') }),
  accepted('work-2', 'observation-2'),
  body('InterpretationPending', { observationId: 'observation-1', workId: 'work-1', attempt: 1 }),
  body('InterpretationApplied', { observationId: 'observation-1', interpretationId: 'interpretation-1', pluginRef: 'proof-plugin', delta: envelope('{}') }),
  body('InterpretationFailed', { observationId: 'observation-1', interpretationId: 'interpretation-1', pluginRef: 'proof-plugin', reason: 'plugin failed' }),
  body('GraphPatched', { pluginRef: 'proof-plugin', patch: { upsertNodes: [graphNode('node-2')], removeNodes: [], upsertEdges: [{ id: 'edge-1', tails: ['node-1'], heads: ['node-2'], relation: 'links', payload: envelope('{}'), revision: '0' }], removeEdges: [] } }),
  body('CertificateSlotsPatched', { patches: [{ certificateId: 'certificate-1', targetRef: 'node-1', valueSpaceId: 'value-1', scopeId: 'scope-1', semanticsModelRef: 'model-1', expectedSlotRevision: '0', slot: { slot: 'summary', producer: 'proof-plugin', schema: envelope('{}').schema, canonicalPayload: '{}', revision: '1', guarantee: { case: 'EmpiricalSummary', assumptions: ['declared'] }, status: { case: 'Current' } } }] }),
  body('CertificateInvalidated', { invalidation: envelope('{}'), reason: 'goal changed' }),
  body('DecisionRecorded', { decision: envelope('{}') }),
  body('AnswerPrepared', { renderWorkId: 'work-1', draftRef: 'draft-1' }),
  body('AnswerCommitted', { renderWorkId: 'work-1', resultObservationId: 'observation-1', answerRef: 'answer-1', stopReason: 'resource-limited' }),
  body('CancelRequested', { reason: 'user stop' }),
  body('InquiryCancelled', { reason: 'settled' }),
  body('InquirySuspended', { reason: 'missing capability' }),
  body('InquiryFailed', { reason: 'failed' }),
  body('InquiryStatusChanged', { status: 'input-required', reason: 'authorization needed' }),
]

const dispatchSeedBodies = includeIntent => [
  createdBody(text),
  body('RoundOpened', { roundId: 'round-1', scopeId: 'scope-1', expectedWork: ['work-2'] }),
  body('WorkPlanned', { work: [work('work-2')] }),
  transition('work-2', 'Planned', { case: 'Ready' }),
  body('BudgetReserved', {
    reservation: { workId: 'work-2', attempt: 1, resources: [{ key: 'calls', value: 1 }], moneyMinor: null },
    renderReserve: [{ key: 'calls', value: 1 }],
  }),
  transition('work-2', 'Ready', { case: 'Leased', fence: 'work-2:1:logical' }),
  ...(includeIntent ? [cases.find(value => value.case === 'DispatchRequested')] : []),
]

const bodySeed = value => {
  if (value.case === 'InquiryCreated') return [value]
  if (value.case === 'DispatchRequested') return dispatchSeedBodies(false)
  if (value.case === 'DispatchReceiptRecorded') return dispatchSeedBodies(true)
  if (value.case === 'InterpretationApplied' || value.case === 'InterpretationFailed') {
    return [...seedBodies(text), cases.find(body => body.case === 'InterpretationPending')]
  }
  return seedBodies(text)
}

test('WHAT[sphinx-v2-019] all 30 body cases cross real encoding append and new-writer canonical replay without losing public DTO fields', async () => {
  assert.equal(cases.length, 30)
  assert.equal(new Set(cases.map(value => value.case)).size, 30)
  for (const value of cases) {
    assert.deepEqual(mustOk(persistence.canonicalizeBody(value)), value, value.case)
    await withStore(async ({ open, close }) => {
      const first = open()
      const creation = await append(first, batch('body-inquiry', 'seed', bodySeed(value)))
      const encoded = value.case === 'InquiryCreated' ? creation
        : await append(first, batch('body-inquiry', 'body', [value], creation))
      const durable = store.read(first, encoded.id)
      assert.deepEqual(durable.payload.events.at(-1), value, value.case)
      const live = current(first, 'body-inquiry')
      assert.equal(mustOk(live).eventHead, encoded.id)
      assert.equal(mustOk(live).revision, value.case === 'InquiryCreated' ? '0' : '1')
      close(first)
      const reopened = open()
      assert.deepEqual(current(reopened, 'body-inquiry'), live, value.case)
      assert.deepEqual(store.read(reopened, encoded.id), durable, value.case)
    })
  }
})

const appendCutChild = fileURLToPath(new URL('./support/append-cut-settlement-child.mjs', import.meta.url))

for (const scenario of ['valid', 'malformed', 'valid-release', 'malformed-release']) {
  test(`WHAT[sphinx-v2-019] actual store ${scenario} preserves Sphinx settlement and cold replay under explicit bad-payload defense`, async t => {
    const root = realpathSync(mkdtempSync(join(tmpdir(), 'sphinx-append-cut-')))
    const commonDir = join(root, '.git')
    const sourceWriter = randomUUID()
    const eventsDir = join(commonDir, 'wanxiangshu', 'events')
    const resolveSourceFile = () => {
      const legacy = join(eventsDir, `${sourceWriter}.ndjson`)
      if (existsSync(legacy)) return legacy
      return join(eventsDir, `${sourceWriter}-0000.ndjson`)
    }
    const raw = batch('native-cut-' + scenario, 'native-settlement-' + scenario, [createdBody(text)])
    const malformed = scenario.startsWith('malformed')
    const release = scenario.endsWith('-release')
    const env = { ...process.env }
    delete env.NODE_TEST_CONTEXT
    const probe = async (mode, writerId, request) => {
      try {
        return JSON.parse(await runVerificationToolProbe(process.execPath,
          [appendCutChild, mode, commonDir, writerId, scenario, JSON.stringify(request)],
          { cwd: dirname(commonDir), env, signal: t.signal }))
      } catch (error) {
        if (error?.stderr && typeof error.message === 'string') error.message += '\n' + error.stderr
        throw error
      }
    }
    let completed = false
    try {
      const measured = await probe('measure', sourceWriter, { raw })
      assert.notEqual(measured.pid, process.pid)
      assert.deepEqual(measured.counts, { append: 1, fsync: 1, close: 1, release: 1, injected: release ? 1 : 0 })
      assert.equal(measured.physicalPreconditions, true)
      assert.equal(measured.inquiryId, raw.inquiry)
      assert.equal(measured.commandId, raw.commandId)
      assert.equal(measured.original.payload.inquiry, raw.inquiry)
      assert.equal(measured.original.payload.commandId, raw.commandId)
      assert.equal(measured.facts.length, malformed ? 2 : 1)
      assert.equal(measured.facts[0].id, measured.original.id)
      assert.equal(measured.facts[0].stream, measured.original.stream)
      assert.deepEqual(measured.facts[0].payload, malformed ? {} : measured.original.payload)
      assert.equal(measured.kind, release ? 'CommitUnknown' : 'Committed')
      assert.equal(measured.phase, release ? 'StoreRelease' : null)
      assert.equal(measured.causeSame, release)
      assert.equal(measured.originalErrorSame, release ? true : null)
      assert.equal(measured.cutIds.length, malformed ? 1 : 0)
      assert.equal(readFileSync(resolveSourceFile(), 'base64'), measured.bytes)
      assert.equal(existsSync(join(commonDir, 'wanxiangshu.lock')), false)

      const coldWriter = randomUUID()
      const cold = await probe('cold', coldWriter, {
        sourceWriter, bytes: measured.bytes, facts: measured.facts,
        inquiryId: raw.inquiry, current: measured.current,
      })
      assert.notEqual(cold.pid, measured.pid)
      assert.notEqual(cold.pid, process.pid)
      assert.equal(cold.writerId, coldWriter)
      assert.equal(cold.preserved, true)
      assert.deepEqual(cold.current, measured.current)
      assert.equal(readFileSync(resolveSourceFile(), 'base64'), measured.bytes)
      assert.equal(existsSync(join(commonDir, 'wanxiangshu', 'events', `${coldWriter}.ndjson`)), false)
      t.diagnostic(JSON.stringify({ scenario, measurePid: measured.pid, coldPid: cold.pid,
        physicalAndCold: true, ...measured.counts, kind: measured.kind, cutIds: measured.cutIds }))
      completed = true
    } finally {
      if (completed) rmSync(root, { recursive: true, force: true })
      else t.diagnostic('SPHINX_APPEND_CUT_FAILURE_EVIDENCE: retained ' + root)
    }
  })
}
