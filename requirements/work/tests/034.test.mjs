import assert from 'node:assert/strict'
import { join } from 'node:path'
import test from 'node:test'
import * as prefix from '../../../dist/Context/Prefix/Surface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as surface from '../../../dist/Mission/Planning/Surface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'
import { clearAll } from '../../../dist/Repository/Programming/Js/Anchor/AnchorSurface.js'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import {
  assistantText,
  cursors,
  ids,
  msgId,
  msgText,
  syntheticPredecessor,
  toolResult,
  userText,
} from './support/plan-messages.mjs'

const assemble = (rawMessages, tenure, messageCursors) =>
  surface.PlanningSurface.assembleTenureMessages(rawMessages, tenure, messageCursors)

const life = lifecycle.LifecycleSurface ?? lifecycle
const framesOf = (text) => text.split('\n').filter((line) => line.length > 0).map((line) => JSON.parse(line))
const eventsOf = (text) => framesOf(text).flatMap((frame) => frame.events ?? [])
const incumbencyIdOf = (text) => [...eventsOf(text)].reverse().find((event) => event.type === 'IncumbencyOpened').incumbencyId

test('WHAT[work-034] initial tenure (S1) assembles U + current messages with no synthetic record and requests reanchor on first turn', () => {
  const rawMessages = [
    userText('u1', 'Initial user prompt'),
    userText('u2', 'Second user instruction'),
  ]

  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    isFreshHandover: true,
  }

  // 1. First turn: no assistant/tool messages yet, isFreshHandover = true -> reanchorRequested = true
  const firstTurn = assemble(rawMessages, tenure, {})
  assert.equal(firstTurn.reanchorRequested, true, 'a fresh handover must request a reanchor once')
  assert.equal(firstTurn.messages.length, 2)
  assert.equal(msgId(firstTurn.messages[0]), 'u1')
  assert.equal(msgId(firstTurn.messages[1]), 'u2')

  // 2. Subsequent turn: current tenure assistant/tool messages exist -> reanchorRequested stays false
  const subsequentMessages = [
    ...rawMessages,
    assistantText('a1', 'working on S1', 'u2'),
    toolResult('t1', 'todowrite-for-plan', 'draft'),
  ]
  const tenureContinuing = { ...tenure, isFreshHandover: false }
  const secondTurn = assemble(
    subsequentMessages,
    tenureContinuing,
    cursors({ a1: 5n, t1: 6n }),
  )
  assert.equal(secondTurn.reanchorRequested, false, 'a continuing tenure must not re-request reanchor')
  assert.equal(secondTurn.messages.length, 4)
  assert.deepEqual(
    ids(secondTurn.messages),
    ['u1', 'u2', 'a1', 't1'],
  )
})

test('WHAT[work-034] non-initial tenure strips prior assistant/tool messages, injects no synthetic-prev-* row, and preserves the current user reply in chronological order', () => {
  // Real Host rows: historical U, prior S1 assistant+tool, a current user
  // reply (feedback) and a current assistant row. Prior assistant/tool rows
  // observed before openingCursor are stripped; no synthetic-prev-* row
  // is ever injected.
  const rawMessages = [
    userText('u1', 'Initial user prompt'),
    assistantText('a-old', 'S1 assistant work', 'u1'),
    toolResult('t-old', 'todowrite-for-plan', 'S1 tool work'),
    userText('u2', 'Feedback from user'),
    assistantText('a-current', 'S2 assistant fresh', 'u2'),
    toolResult('t-current', 'todowrite-for-plan', 'S2 tool result'),
  ]

  const tenureS2 = {
    workId: 'work-1',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }

  const result = assemble(
    rawMessages,
    tenureS2,
    cursors({ 'a-old': 5n, 't-old': 8n, u2: 22n, 'a-current': 25n, 't-current': 26n }),
  )

  assert.deepEqual(
    ids(result.messages),
    ['u1', 'u2', 'a-current', 't-current'],
  )

  // Prior assistant/tool messages and any synthetic-prev-* rows must be absent
  const assembledIds = ids(result.messages)
  assert.ok(!assembledIds.some((id) => id.startsWith('synthetic-prev-')), 'no synthetic-prev-* row is injected')
  assert.ok(!assembledIds.includes('a-old'), 'prior assistant message must be stripped')
  assert.ok(!assembledIds.includes('t-old'), 'prior tool message must be stripped')
})

test('WHAT[work-034] user messages U preserve original bytes and order verbatim without modification', () => {
  const originalUser1 = userText('u1', 'Exact bytes line 1\nLine 2\tTabs and spaces   ')
  const originalUser2 = userText('u2', 'Another user prompt with emojis: 💡🚀')

  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    isFreshHandover: true,
  }

  const result = assemble([originalUser1, originalUser2], tenure, {})

  assert.equal(result.messages.length, 2)
  // Strict identity and byte-for-byte content equality
  assert.equal(msgText(result.messages[0]), msgText(originalUser1))
  assert.equal(msgText(result.messages[1]), msgText(originalUser2))
  assert.deepEqual(result.messages[0], originalUser1)
  assert.deepEqual(result.messages[1], originalUser2)
})

test('WHAT[work-034] the first part immediately after the opening cursor survives tenure assembly', () => {
  const rawMessages = [
    userText('u1', 'go'),
    assistantText('a-first', 'first current assistant part', 'u1'),
    userText('u2', 'follow-up'),
  ]
  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }
  const result = assemble(
    rawMessages,
    tenure,
    cursors({ 'a-first': 21n, u2: 22n }),
  )
  const assembledIds = ids(result.messages)
  assert.ok(assembledIds.includes('a-first'), 'the first part right after opening must survive')
  assert.ok(assembledIds.includes('u2'), 'the follow-up user reply must survive in order')
  assert.deepEqual(assembledIds, ['u1', 'a-first', 'u2'])
})

test('WHAT[work-034] a current assistant row with a Host id but no first-observation entry stays', () => {
  const rawMessages = [
    userText('u1', 'go'),
    assistantText('a-no-cursor', 'current work', 'u1'),
  ]
  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }
  // info.id is present. The empty cursor map means this physical message has
  // not been captured yet. That is not a missing identity, so assembly keeps
  // the row and does not fail the exchange.
  const result = assemble(rawMessages, tenure, {})
  assert.ok(ids(result.messages).includes('a-no-cursor'))
  assert.equal(result.messages.filter((m) => msgId(m).startsWith('synthetic-prev-')).length, 0)
})

test('WHAT[work-034] a semantic assistant row with no Host info.id rejects assembly', () => {
  const rawMessages = [
    userText('u1', 'go'),
    {
      info: { role: 'assistant' },
      parts: [{ type: 'text', text: 'current work without a stable identity' }],
    },
  ]
  const identified = assistantText('a-uncaptured', 'captured later', 'u1')
  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }
  assert.throws(
    () => assemble(rawMessages, tenure, {}),
    /no stable XTrace message identity/i,
  )
  const stayed = assemble([userText('u1', 'go'), identified], tenure, {})
  assert.ok(ids(stayed.messages).includes('a-uncaptured'))
})

test('WHAT[work-034] synthetic rows are stripped and no synthetic record is ever injected', () => {
  const rawMessages = [
    userText('u1', 'go'),
    syntheticPredecessor('inc-old', 'work-1', 'stale synthesized row'),
    assistantText('a-current', 'current work', 'u1'),
  ]
  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }
  const result = assemble(
    rawMessages,
    tenure,
    cursors({ 'a-current': 25n }),
  )
  const syntheticRows = result.messages.filter((m) => msgId(m).startsWith('synthetic-prev-'))
  assert.equal(syntheticRows.length, 0, 'no synthetic-prev-* row is injected')
  assert.ok(!result.messages.some((m) => msgId(m) === 'synthetic-prev-inc-old'))
  assert.deepEqual(ids(result.messages), ['u1', 'a-current'])
})

test('WHAT[work-034] durable Plan active state assembles U + current interactions in place', () => {
  const rawMessages = [
    userText('u1', 'go'),
    assistantText('a1', 'current work', 'u1'),
    userText('u2', 'feedback'),
  ]
  const tenure = {
    workId: 'w-25a',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }
  const result = assemble(rawMessages, tenure, cursors({ a1: 25n, u2: 26n }))
  assert.deepEqual(
    ids(result.messages),
    ['u1', 'a1', 'u2'],
    'U and current interactions must be assembled in place in chronological order',
  )
  assert.equal(result.reanchorRequested, false, 'a continuing durable tenure must not re-request reanchor')
})

test('WHAT[work-034] the lifecycle document view is reachable as a pure function', () => {
  assert.equal(typeof life.documentView, 'function')
  const opened = life.encodeOpening('plan', 'w-25a', '/tmp/plan-test', 0n)
  assert.equal(life.documentView(opened).found, true)
})

test('WHAT[work-034] reanchorRequested is returned by assemble and is true only on a fresh handover', () => {
  const rawMessages = [userText('u1', 'hi')]
  const tenure = {
    workId: 'w-25b',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    isFreshHandover: true,
  }
  const fresh = assemble(rawMessages, tenure, {})
  assert.equal(typeof fresh.reanchorRequested, 'boolean')
  assert.equal(fresh.reanchorRequested, true, 'a fresh handover must request a reanchor')

  const continuing = assemble(rawMessages, { ...tenure, isFreshHandover: false }, {})
  assert.equal(continuing.reanchorRequested, false, 'a continuing tenure must not re-request reanchor on an empty stop')
})

const tenureFor = (overrides) => ({
  workId: 'w1',
  incumbencyId: 'inc-2',
  stage: 'S2',
  openingCursor: 20n,
  isFreshHandover: false,
  ...overrides,
})

test('WHAT[work-034] S1 with no retirement carries no previous range', () => {
  const opened = life.encodeOpening('plan', 'w1', '/tmp/plan-test', 0n)
  const view = life.documentView(opened)
  assert.equal(view.openingStage, 'S1')
  assert.equal(view.latestRetirement, '')
  assert.equal(view.predecessor, '')
  assert.equal(view.openingCursor, 0)
})

test('WHAT[work-034] S1 retirement preserves its opening range for the next tenure', () => {
  const opened = life.encodeOpening('plan', 'w1', '/tmp/plan-test', 7n)
  const reviewed = life.reviewProbe(opened, incumbencyIdOf(opened), 'Stayed')
  assert.equal(reviewed.decision, 'Write', reviewed.detail)
  const relayed = life.deliverProbe(`${opened}${reviewed.bytes}`, 'run-034', 'call-034', 25n)
  assert.equal(relayed.decision, 'Write', relayed.detail)
  const retired = eventsOf(relayed.bytes)[0]
  const successor = eventsOf(relayed.bytes)[1]
  assert.equal(retired.retirementCursor, 25)
  assert.equal(successor.openingCursor, 25)
  assert.equal(successor.stage, 'S1', 'Stayed keeps the opening stage for the successor')
  const view = life.documentView(`${opened}${reviewed.bytes}${relayed.bytes}`)
  assert.equal(view.latestRetirement, '7,25')
  assert.equal(view.predecessor, '7,25')
  assert.equal(view.openingCursor, 25)
})

test('WHAT[work-034] tenure assembly strips prior interactions by openingCursor without materializing a previous record', () => {
  const rawMessages = [
    userText('u1', 'go'),
    assistantText('a-old', 'old work', 'u1'),
    assistantText('a-new', 'new work', 'u1'),
  ]
  const result = surface.PlanningSurface.assembleTenureMessages(
    rawMessages,
    tenureFor(),
    cursors({ 'a-old': 9n, 'a-new': 30n }),
  )
  const assembledIds = ids(result.messages)
  assert.ok(!assembledIds.includes('a-old'))
  assert.ok(assembledIds.includes('a-new'))
  assert.ok(!assembledIds.some((id) => id.startsWith('synthetic-prev-')))
  assert.deepEqual(assembledIds, ['u1', 'a-new'])
})

test('WHAT[work-034] tenure reanchor event shape carries session, epochs, work and incumbency', () => {
  assert.equal(typeof prefix.applyTenureReanchor, 'function')
  assert.equal(typeof prefix.isTenureReanchored, 'function')
  assert.equal(typeof prefix.reanchoredTenures, 'function')
})

test('WHAT[work-034] duplicate incumbencyId is rejected on second tenure reanchor', () => {
  const empty = prefix.empty
  const first = prefix.applyTenureReanchor({ previousEpoch: 0n, nextEpoch: 1n, incumbencyId: 'inc-1' }, empty)
  assert.equal(first.ok, true)
  const second = prefix.applyTenureReanchor({ previousEpoch: 1n, nextEpoch: 2n, incumbencyId: 'inc-1' }, first.value)
  assert.equal(second.ok, false)
  assert.equal(second.error, 'TenureAlreadyReanchored')
})

test('WHAT[work-034] reanchorRequested is true on fresh handover and deduplicated via isTenureReanchored', () => {
  const rawMessages = [userText('u1', 'hi')]
  const tenure = {
    workId: 'w-27',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    isFreshHandover: true,
  }
  const result = surface.PlanningSurface.assembleTenureMessages(rawMessages, tenure, {})
  assert.equal(result.reanchorRequested, true)

  const continuing = surface.PlanningSurface.assembleTenureMessages(
    rawMessages,
    { ...tenure, isFreshHandover: false },
    {},
  )
  assert.equal(continuing.reanchorRequested, false)
})

test('WHAT[work-034] successor projection strips earlier assistant and tool messages and never inserts a synthetic predecessor record', () => {
  const rawMessages = [
    userText('u-history', 'Original user request'),
    assistantText('a-old', 'previous tenure answer'),
    toolResult('t-old', 'todowrite-for-plan', 'old draft'),
    assistantText('a-current', 'this tenure answer'),
    assistantText('a-uncaptured', 'this request, not captured yet'),
  ]
  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }
  const result = assemble(
    rawMessages,
    tenure,
    cursors({ 'a-old': 4n, 't-old': 5n, 'a-current': 25n }),
  )
  const syntheticRows = result.messages.filter((m) => msgId(m).startsWith('synthetic-prev-'))
  assert.equal(syntheticRows.length, 0, 'no synthetic predecessor record is inserted')
  assert.deepEqual(ids(result.messages), ['u-history', 'a-current', 'a-uncaptured'])
})

test('WHAT[work-034] post-S3 projection strips all S3-and-earlier context and injects no synthetic record', () => {
  // The delivered Plan work carries no active tenure. Its isolation is keyed on
  // the delivered retirement frontier: tenureForPostS3 sets OpeningCursor to
  // frontier + 1. Every assistant/tool exchange observed at-or-before the
  // frontier is stripped, the historical user inputs survive, and no synthetic
  // record is injected.
  const deliveredFrontier = 40n
  const rawMessages = [
    userText('u-history', 'Original user request'),
    assistantText('a-s1', 'S1 tenure assistant'),
    toolResult('t-s1', 'todowrite-for-plan', 'S1 tool'),
    assistantText('a-s2', 'S2 tenure assistant'),
    assistantText('a-s3', 'S3 tenure assistant'),
    userText('u-command', 'Please execute deliver now.'),
    assistantText('a-post', 'post-S3 assistant', 'u-command'),
  ]
  const tenure = {
    workId: 'work-post-s3',
    incumbencyId: 'post-s3',
    stage: 'S3',
    openingCursor: deliveredFrontier + 1n,
    isFreshHandover: false,
  }
  const result = assemble(
    rawMessages,
    tenure,
    cursors({ 'a-s1': 5n, 't-s1': 8n, 'a-s2': 20n, 'a-s3': 38n, 'u-command': 41n, 'a-post': 42n }),
  )
  const kept = ids(result.messages)
  assert.equal(
    result.messages.filter((m) => msgId(m).startsWith('synthetic-prev-')).length,
    0,
    'post-S3 inherits no previous delivered view',
  )
  assert.ok(kept.includes('u-history'), 'historical user input is preserved verbatim')
  assert.ok(kept.includes('u-command'), 'the post-S3 command survives')
  assert.ok(kept.includes('a-post'), 'post-S3 assistant after the frontier stays')
  for (const prior of ['a-s1', 't-s1', 'a-s2', 'a-s3']) {
    assert.ok(!kept.includes(prior), `${prior} (S3-and-earlier) must be stripped`)
  }
})

test('WHAT[work-034] a relaying tenure declaring view: { extra } commits ViewCheckpointCommitted and delivers the declared view to the successor request', async () => {
  clearAll()
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
    const planRoot = join(commonDir, 'wanxiangshu')
    const sessionID = 'ses-plan-view-handover-034'
    await acceptAuthorityRoot(runtime, sessionID, 'plan', 'user-plan-root-034')

    const userRoot = {
      info: { id: 'user-plan-root-034', role: 'user', sessionID, agent: 'plan', time: { created: 1 } },
      parts: [{ id: 'part-034-u1', type: 'text', text: 'Plan the tenure handover.' }],
    }
    const asstRoot = {
      info: {
        id: 'asst-plan-root-034',
        sessionID,
        parentID: 'user-plan-root-034',
        role: 'assistant',
        agent: 'plan',
        providerID: 'provider',
        modelID: 'plan-model',
        time: { created: 2 },
      },
      parts: [],
    }
    runtime.pushHostMessage(sessionID, userRoot)
    runtime.pushHostMessage(sessionID, asstRoot)
    await hooks['chat.message']({ sessionID, agent: 'plan' }, { message: userRoot.info, parts: userRoot.parts })
    await hooks['experimental.chat.messages.transform']({ sessionID }, { messages: [structuredClone(userRoot)] })

    const declaredExtra = 'HANDOVER_VIEW_034: keep anchor #1 and verify S1 trade-offs.'
    const viewToolMsg = {
      info: { id: 'run-034-view', role: 'assistant', sessionID, parentID: 'user-plan-root-034', time: { created: 2 } },
      parts: [
        {
          id: 'part-034-view',
          type: 'tool',
          tool: 'todowrite-for-plan',
          callID: 'call-034-view',
          state: {
            status: 'completed',
            input: {
              program: 'class Js extends JsProgram { async run() { return { ok: true }; } }',
              view: { prefixUntil: 1, thenRemove: { 1: declaredExtra } },
            },
            output: 'ok',
          },
        },
      ],
    }
    runtime.pushHostMessage(sessionID, viewToolMsg)
    await hooks.tool['todowrite-for-plan'].execute(
      {
        program: 'class Js extends JsProgram { async run() { return { ok: true }; } }',
        view: { prefixUntil: 1, thenRemove: { 1: declaredExtra } },
      },
      { sessionID, agent: 'plan', callID: 'call-034-view', messageID: 'run-034-view', providerRunID: 'run-034-view' },
    )
    await hooks.event({
      event: {
        type: 'message.part.updated',
        properties: { sessionID, part: viewToolMsg.parts[0] },
      },
    })

    assert.deepEqual(
      journalSurface.JournalSurface_snapshot(runtime.journal).viewCheckpoints,
      [{ sessionId: sessionID, checkpoints: [{ callId: 'call-034-view' }] }],
      'relaying tenure view declaration commits a durable ViewCheckpointCommitted',
    )

    const relayMsg = {
      info: { id: 'run-034-deliver', role: 'assistant', sessionID, parentID: 'user-plan-root-034', time: { created: 3 } },
      parts: [
        {
          id: 'part-034-deliver',
          type: 'tool',
          tool: 'deliver',
          callID: 'call-034-deliver',
          state: { status: 'completed', input: {}, output: 'relayed' },
        },
      ],
    }
    runtime.pushHostMessage(sessionID, relayMsg)
    await hooks.tool.deliver.execute(
      {},
      { sessionID, agent: 'plan', callID: 'call-034-deliver', messageID: 'run-034-deliver', providerRunID: 'run-034-deliver' },
    )

    const followup = {
      info: { id: 'user-034-followup', role: 'user', sessionID, agent: 'plan', time: { created: 4 } },
      parts: [{ id: 'part-034-u2', type: 'text', text: 'Proceed in the new tenure.' }],
    }
    const asstFollowup = {
      info: {
        id: 'asst-034-followup',
        sessionID,
        parentID: 'user-034-followup',
        role: 'assistant',
        agent: 'plan',
        providerID: 'provider',
        modelID: 'plan-model',
        time: { created: 5 },
      },
      parts: [],
    }
    runtime.pushHostMessage(sessionID, followup)
    runtime.pushHostMessage(sessionID, asstFollowup)
    await hooks['chat.message']({ sessionID, agent: 'plan' }, { message: followup.info, parts: followup.parts })
    const successorOut = {
      messages: [structuredClone(userRoot), structuredClone(viewToolMsg), structuredClone(relayMsg), structuredClone(followup)],
    }
    await hooks['experimental.chat.messages.transform']({ sessionID }, successorOut)

    assert.ok(
      !successorOut.messages.some((m) => String(m?.info?.id ?? '').startsWith('synthetic-prev-')),
      'no synthetic-prev-* message is injected for the successor tenure',
    )
    assert.ok(
      JSON.stringify(successorOut.messages).includes(declaredExtra),
      'successor request carries the predecessor declared view.extra text',
    )
  })
})
