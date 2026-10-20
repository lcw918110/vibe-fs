import assert from 'node:assert/strict'
import { join } from 'node:path'
import test from 'node:test'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as surface from '../../../dist/Mission/Planning/Surface.js'
import { isAllowed } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'
import { clearAll } from '../../../dist/Repository/Programming/Js/Anchor/AnchorSurface.js'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import {
  assistantText,
  cursors,
  ids,
  msgId,
  msgRole,
  msgText,
  syntheticPredecessor,
  userText,
} from './support/plan-messages.mjs'

// WHAT[work-020] fixes the closed input set a Plan runner receives:
// U (verbatim, original order), current-tenure interactions, the predecessor's
// declared ANCHOR view (durable ViewCheckpointCommitted + view.thenRemove), and P
// (the only writable surface, reached through todowrite-for-plan only); no
// synthetic predecessor message is ever injected.

const assemble = (rawMessages, tenure, messageCursors) =>
  surface.PlanningSurface.assembleTenureMessages(rawMessages, tenure, messageCursors)

test('WHAT[work-020] U messages are preserved verbatim in original order', () => {
  const rawMessages = [
    userText('u-1', '设计缓存架构\n第二行'),
    assistantText('a-old', 'previous tenure work', 'u-1'),
    userText('u-2', '补充约束: no cache invalidation'),
    assistantText('a-new', 'current tenure work', 'u-2'),
  ]
  const tenure = {
    workId: 'w-001-inputs',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }
  const result = assemble(
    rawMessages,
    tenure,
    cursors({ 'a-old': 15n, 'a-new': 30n }),
  )
  const userContents = result.messages
    .filter((m) => msgRole(m) === 'user')
    .map(msgText)
  assert.deepEqual(userContents, ['设计缓存架构\n第二行', '补充约束: no cache invalidation'])
})

test('WHAT[work-020] neither initial nor later tenures ever inject a synthetic-prev-* message', () => {
  const rawMessages = [
    userText('u-1', 'go'),
    assistantText('a-old', 'prior work', 'u-1'),
    assistantText('a-new', 'current work', 'u-1'),
  ]
  const firstTenure = {
    workId: 'w-001-inputs',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    isFreshHandover: true,
  }
  const laterTenure = {
    workId: 'w-001-inputs',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: true,
  }
  const firstResult = assemble([userText('u-1', 'go')], firstTenure, {})
  const laterResult = assemble(rawMessages, laterTenure, cursors({ 'a-old': 10n, 'a-new': 25n }))
  assert.ok(
    !firstResult.messages.some((m) => msgId(m).startsWith('synthetic-prev-') || m.info.synthetic === true),
    'first tenure must not carry any synthetic-prev-* message',
  )
  assert.ok(
    !laterResult.messages.some((m) => msgId(m).startsWith('synthetic-prev-') || m.info.synthetic === true),
    'later tenure must not carry any synthetic-prev-* message',
  )
  assert.deepEqual(ids(firstResult.messages), ['u-1'])
  assert.deepEqual(ids(laterResult.messages), ['u-1', 'a-new'])
})

test('WHAT[work-020] assembled messages come only from U and current-tenure interactions', () => {
  const rawMessages = [
    userText('u-1', 'go'),
    assistantText('a-1', 'current work', 'u-1'),
  ]
  const tenure = {
    workId: 'w-001-inputs',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }
  const result = assemble(
    rawMessages,
    tenure,
    cursors({ 'a-1': 30n }),
  )
  const inputIds = new Set(rawMessages.map(msgId))
  for (const m of result.messages) {
    assert.ok(
      inputIds.has(msgId(m)) && !msgId(m).startsWith('synthetic-prev-'),
      `assembled message id ${msgId(m)} must originate from the physical input messages`,
    )
  }
  // The draft P is reachable only through the todowrite-for-plan tool surface; the
  // assembled request never carries its content as an injected message.
  assert.equal(result.messages.length, rawMessages.length)
})

test('WHAT[work-020] the plan runner holds no write capability beyond P', () => {
  for (const perm of ['Write', 'Edit', 'Move', 'Remove', 'Exec', 'Pty']) {
    assert.equal(isAllowed('plan', perm), false, `plan must be denied ${perm}`)
  }
  // P itself is reached through the todowrite-for-plan programming surface.
  assert.equal(isAllowed('plan', 'JsPlan'), true)
})

test('WHAT[work-020] a negative openingCursor rejects assembly fail-closed', () => {
  const rawMessages = [userText('u-1', 'go')]
  const tenure = {
    workId: 'w-001-inputs',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: -1n,
    isFreshHandover: false,
  }
  assert.throws(
    () => assemble(rawMessages, tenure, {}),
    /nonnegative bigint|cannot be negative/i,
  )
})

test('WHAT[work-020] synthetic rows are stripped and never re-injected during assembly', () => {
  const rawMessages = [
    userText('u-1', 'go'),
    syntheticPredecessor('inc-old', 'w-001', 'old synthesized previous row'),
    assistantText('a-1', 'current work', 'u-1'),
  ]
  const tenure = {
    workId: 'w-001-inputs',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }
  const result = assemble(
    rawMessages,
    tenure,
    cursors({ 'a-1': 30n }),
  )
  const syntheticRows = result.messages.filter((m) => msgId(m).startsWith('synthetic-prev-'))
  assert.equal(syntheticRows.length, 0, 'no synthetic-prev-* row is ever present')
  assert.ok(!result.messages.some((m) => msgId(m) === 'synthetic-prev-inc-old'))
  assert.deepEqual(ids(result.messages), ['u-1', 'a-1'])
})

test('WHAT[work-020] a relaying tenure declaring view: { prefixUntil, thenRemove } commits ViewCheckpointCommitted and carries the declared handover summary into the successor request', async () => {
  clearAll()
  const life = lifecycle.LifecycleSurface ?? lifecycle
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
    const planRoot = join(commonDir, 'wanxiangshu')
    const sessionID = 'ses-plan-view-handover-020'
    await acceptAuthorityRoot(runtime, sessionID, 'plan', 'user-plan-root')

    const userRoot = {
      info: { id: 'user-plan-root', role: 'user', sessionID, agent: 'plan', time: { created: 1 } },
      parts: [{ id: 'part-u1', type: 'text', text: 'Design a resilient caching layer.' }],
    }
    const asstRoot = {
      info: {
        id: 'asst-plan-root',
        sessionID,
        parentID: 'user-plan-root',
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
    const firstOut = { messages: [structuredClone(userRoot)] }
    await hooks['experimental.chat.messages.transform']({ sessionID }, firstOut)

    // Predecessor tenure calls todowrite-for-plan with a view declaration before relaying
    const handoverProse = 'HANDOVER_VIEW_020: S1 root-cause verified; successor must audit eviction trade-offs.'
    const planCallMsg = {
      info: { id: 'run-plan-view-1', role: 'assistant', sessionID, parentID: 'user-plan-root', time: { created: 2 } },
      parts: [
        {
          id: 'part-plan-view-1',
          type: 'tool',
          tool: 'todowrite-for-plan',
          callID: 'call-plan-view-1',
          state: {
            status: 'completed',
            input: {
              program: 'class Js extends JsProgram { async run() { return { ok: true }; } }',
              view: { prefixUntil: 1, thenRemove: { 1: handoverProse } },
            },
            output: 'ok',
          },
        },
      ],
    }
    runtime.pushHostMessage(sessionID, planCallMsg)
    await hooks.tool['todowrite-for-plan'].execute(
      {
        program: 'class Js extends JsProgram { async run() { return { ok: true }; } }',
        view: { prefixUntil: 1, thenRemove: { 1: handoverProse } },
      },
      { sessionID, agent: 'plan', callID: 'call-plan-view-1', messageID: 'run-plan-view-1', providerRunID: 'run-plan-view-1' },
    )
    await hooks.event({
      event: {
        type: 'message.part.updated',
        properties: {
          sessionID,
          part: planCallMsg.parts[0],
        },
      },
    })

    // Durable ViewCheckpointCommitted is recorded for the relaying tenure's view declaration
    assert.deepEqual(
      journalSurface.JournalSurface_snapshot(runtime.journal).viewCheckpoints,
      [{ sessionId: sessionID, checkpoints: [{ callId: 'call-plan-view-1' }] }],
      'declaring view on todowrite-for-plan must commit a durable ViewCheckpointCommitted fact',
    )

    // Relay to successor tenure
    const beforeRelay = life.inspected(commonDir, sessionID)
    const relayMsg = {
      info: { id: 'run-plan-deliver-1', role: 'assistant', sessionID, parentID: 'user-plan-root', time: { created: 3 } },
      parts: [
        {
          id: 'part-plan-deliver-1',
          type: 'tool',
          tool: 'deliver',
          callID: 'call-plan-deliver-1',
          state: { status: 'completed', input: {}, output: 'relayed' },
        },
      ],
    }
    runtime.pushHostMessage(sessionID, relayMsg)
    const relayReply = await hooks.tool.deliver.execute(
      {},
      { sessionID, agent: 'plan', callID: 'call-plan-deliver-1', messageID: 'run-plan-deliver-1', providerRunID: 'run-plan-deliver-1' },
    )
    assert.match(String(relayReply), /relayed|incumbency/i)
    const afterRelay = life.inspected(commonDir, sessionID)
    assert.notEqual(afterRelay.ActiveId, beforeRelay.ActiveId, 'deliver must open the successor tenure')

    // Successor request transform runs under the new tenure: strips predecessor assistant/tool,
    // injects no synthetic-prev-*, and carries the predecessor's declared thenRemove summary.
    const successorFollowup = {
      info: { id: 'user-plan-followup', role: 'user', sessionID, agent: 'plan', time: { created: 4 } },
      parts: [{ id: 'part-u2', type: 'text', text: 'Continue with the next leg.' }],
    }
    const asstSuccessor = {
      info: {
        id: 'asst-plan-successor',
        sessionID,
        parentID: 'user-plan-followup',
        role: 'assistant',
        agent: 'plan',
        providerID: 'provider',
        modelID: 'plan-model',
        time: { created: 5 },
      },
      parts: [],
    }
    runtime.pushHostMessage(sessionID, successorFollowup)
    runtime.pushHostMessage(sessionID, asstSuccessor)
    await hooks['chat.message']({ sessionID, agent: 'plan' }, { message: successorFollowup.info, parts: successorFollowup.parts })
    const successorOut = {
      messages: [structuredClone(userRoot), structuredClone(planCallMsg), structuredClone(relayMsg), structuredClone(successorFollowup)],
    }
    await hooks['experimental.chat.messages.transform']({ sessionID }, successorOut)

    const wireIds = successorOut.messages.map((m) => m?.info?.id ?? '')
    assert.ok(!wireIds.some((id) => id.startsWith('synthetic-prev-')), 'successor request must never contain a synthetic-prev-* message')
    assert.ok(!wireIds.includes('user-plan-root'), 'the view-covered anchor is replaced by its summary')
    assert.ok(wireIds.includes('user-plan-root#summary'), 'the replaced anchor stands in as a synthetic summary row')
    assert.ok(wireIds.includes('run-plan-view-1'), 'history outside the view boundary is retained by the relay')
    assert.ok(wireIds.includes('run-plan-deliver-1'), 'history outside the view boundary is retained by the relay')
    const serialized = JSON.stringify(successorOut.messages)
    assert.ok(serialized.includes(handoverProse), 'successor request must carry the predecessor declared thenRemove summary')
  })
})