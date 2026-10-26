import assert from 'node:assert/strict'
import test from 'node:test'
import { withExecutablePlugin, acceptAuthorityRoot, openIncumbency } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { userQuestionObservation } from '../../../dist/Interaction/Dispatch/DispatchSurface.js'

const program = (body) => `class Js extends JsProgram {
  async run() {
${body}
  }
}`

const context = (session, call = 'ask-call', run = 'ask-run') => ({
  sessionID: session,
  agent: 'plan',
  callID: call,
  messageID: run,
  providerRunID: run,
})

const humanInput = async (hooks, runtime, session, id, text) => {
  const message = {
    id,
    role: 'user',
    agent: 'plan',
    sessionID: session,
    model: { providerID: 'provider', modelID: 'plan-model' },
  }
  runtime.pushHostMessage(session, { info: { ...message, time: { created: 20 } }, parts: [{ type: 'text', text }] })
  await hooks['chat.message']({ sessionID: session, agent: 'plan', messageID: id }, {
    message,
    parts: [{ type: 'text', text }],
  })
}

const withPlanSession = async (body) => {
  const session = `ask049-${Math.random().toString(36).slice(2, 8)}`
  await withExecutablePlugin(async (hooks, directory, created, runtime) => {
    await acceptAuthorityRoot(runtime, session, 'plan', 'user-root')
    await humanInput(hooks, runtime, session, 'user-root', 'Need one fact before continuing.')
    await openIncumbency(runtime, session, `inc-${session}`, 'S1')
    await body({ hooks, directory, runtime, session })
  })
}

const nameOf = (doc) => {
  const match = String(doc).match(/^name = "([^"]+)"/m)
  assert.ok(match, `parked document must carry its program name: ${doc}`)
  return match[1]
}

const completePart = async (hooks, session, call, run) => {
  // WHAT[work-049]: the interrupt fires on the Host's confirmation of this very
  // ask tool result — `message.part.updated` with a completed tool part.
  // The plugin registers the `event` hook, so the envelope must be the real
  // Host event shape.
  await hooks.event({
    event: {
      type: 'message.part.updated',
      properties: {
        sessionID: session,
        part: {
          type: 'tool',
          tool: 'todowrite-for-plan',
          callID: call,
          messageID: run,
          state: { status: 'completed', input: {} },
        },
      },
    },
  })
}

test('WHAT[work-049] ask parks and records the question before the attempt interrupt', async () => {
  await withPlanSession(async ({ hooks, runtime, session }) => {
    const notices = []
    runtime.client.tui = { showToast: async ({ body }) => { notices.push(body); return {} } }
    const parked = await hooks.tool['todowrite-for-plan'].execute(
      { program: program(`    await this.ask('Which environment?');\n    return { done: true };`), name: 'ask-plan',
          intent: 'Record the question and retain the paused program until the real user reply arrives.',
          remainingScopeTurnsEstimate: 3,
        },
      context(session),
    )
    assert.match(String(parked), /# parked/)
    assert.equal(userQuestionObservation(runtime.journal, session).length, 1)
    assert.equal(notices.length, 1)
    const before = runtime.abortedIds.length
    await completePart(hooks, session, 'ask-call', 'ask-run')
    assert.equal(
      runtime.abortedIds.length,
      before + 1,
      'the confirmed ask tool result interrupts this attempt exactly once',
    )
    assert.equal(runtime.abortedIds.at(-1), session, 'the interrupt targets the asking session')
  })
})

test('WHAT[work-049] a repeated completed event does not interrupt twice', async () => {
  await withPlanSession(async ({ hooks, runtime, session }) => {
    runtime.client.tui = { showToast: async () => ({}) }
    await hooks.tool['todowrite-for-plan'].execute(
      { program: program(`    await this.ask('Repeat?');\n    return { done: true };`), name: 'repeat-ask',
          intent: 'Record the question and retain the paused program until the real user reply arrives.',
          remainingScopeTurnsEstimate: 3,
        },
      context(session, 'repeat-call', 'repeat-run'),
    )
    await completePart(hooks, session, 'repeat-call', 'repeat-run')
    const afterFirst = runtime.abortedIds.length
    assert.equal(afterFirst, 1, 'the first confirmed terminal event interrupts exactly once')
    await completePart(hooks, session, 'repeat-call', 'repeat-run')
    assert.equal(runtime.abortedIds.length, afterFirst, 'a repeated terminal event does not interrupt twice')
  })
})

test('WHAT[work-049] an unrelated call and an old human input do not unlock the wait', async () => {
  await withPlanSession(async ({ hooks, runtime, session }) => {
    runtime.client.tui = { showToast: async () => ({}) }
    const parked = await hooks.tool['todowrite-for-plan'].execute(
      { program: program(`    await this.ask('Still waiting?');\n    return { done: true };`), name: 'wait-ask',
          intent: 'Record the question and retain the paused program until the real user reply arrives.',
          remainingScopeTurnsEstimate: 3,
        },
      context(session),
    )
    await humanInput(hooks, runtime, session, 'user-root', 'Need one fact before continuing.')
    const blocked = await hooks.tool['todowrite-for-plan'].execute(
      { name: 'abandon-question', intent: 'Request cleanup only if the pending-question gate admits this operation.', program: program('return await this.abandon(' + JSON.stringify(nameOf(parked)) + ');'), remainingScopeTurnsEstimate: 3 },
      context(session, 'other-call', 'other-run'),
    )
    assert.match(String(blocked), /等待|waiting|不能/)
    assert.equal(userQuestionObservation(runtime.journal, session).length, 1)
  })
})

test('WHAT[work-049] a new human reply after the question unlocks the same task', async () => {
  await withPlanSession(async ({ hooks, runtime, session }) => {
    runtime.client.tui = { showToast: async () => ({}) }
    const parked = await hooks.tool['todowrite-for-plan'].execute(
      { program: program(`    const answer = await this.ask('Which environment?');\n    return { answer };`), name: 'reply-ask',
          intent: 'Record the question and retain the paused program until the real user reply arrives.',
          remainingScopeTurnsEstimate: 3,
        },
      context(session),
    )
    await humanInput(hooks, runtime, session, 'user-reply', 'staging')
    const resumed = await hooks.tool['todowrite-for-plan'].execute(
      { name: 'resume-question', intent: 'Resume the original question with the real user reply under this new request window.', program: program('return await this.unpark(' + JSON.stringify(nameOf(parked)) + ', "staging");'), remainingScopeTurnsEstimate: 3 },
      context(session, 'reply-call', 'reply-run'),
    )
    assert.match(String(resumed), /# ok/)
  })
})
