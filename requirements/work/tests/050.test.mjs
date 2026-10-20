import assert from 'node:assert/strict'
import test from 'node:test'
import { withExecutablePlugin, acceptAuthorityRoot, openIncumbency } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { userQuestionObservation } from '../../../dist/Interaction/Dispatch/DispatchSurface.js'

const lifecycleSurface = await import('../../../dist/Mission/Lifecycle/Surface.js')
const journalSurface = await import('../../../dist/Persistence/Journal/Surface.js')
const workspaceEventStoreSurface = await import('../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js')

const program = (body) => `class Js extends JsProgram {
  async run() {
${body}
  }
}`

const context = (session, call = 'plan-call', run = 'plan-run') => ({
  sessionID: session,
  agent: 'plan',
  callID: call,
  messageID: run,
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

const withPlanSession = async (body, session = `jsask-${Math.random().toString(36).slice(2, 8)}`) => {
  await withExecutablePlugin(async (hooks, directory, created, runtime) => {
    await acceptAuthorityRoot(runtime, session, 'plan', 'user-root')
    await humanInput(hooks, runtime, session, 'user-root', 'Deliver the requested behavior.')
    await openIncumbency(runtime, session, `inc-${session}`, 'S1')
    await body({ hooks, directory, runtime, session, created })
  })
}

const handleOf = (doc) => {
  const match = doc.match(/handle = "([^"]+)"/)
  assert.ok(match, `parked document must carry a handle: ${doc}`)
  return match[1]
}

test('WHAT[work-050] js.ask in an S1 plan program records the question and parks the program', async () => {
  await withPlanSession(async ({ hooks, runtime, session }) => {
    const notices = []
    runtime.client.tui = { showToast: async ({ body }) => { notices.push(body); return {} } }

    const parked = await hooks.tool['todowrite-for-plan'].execute(
      { program: program(`    const answer = await js.ask('Which deployment environment should this target?');\n    return { answer };`) },
      context(session),
    )

    assert.match(parked, /# parked/)
    assert.equal(notices.length, 1, 'the user is notified once')
    assert.match(notices[0].message, /Which deployment environment/)
    assert.equal(userQuestionObservation(runtime.journal, session).length, 1, 'one durable question is recorded')

    // 等待门禁要求真实用户输入先到达；此后 unpark 注入的值即 await 的值。
    await humanInput(hooks, runtime, session, 'user-reply', 'staging')
    const resumed = await hooks.tool['todowrite-for-plan'].execute(
      { unpark: { handle: handleOf(parked), value: 'staging' } },
      context(session, 'plan-call-reply', 'plan-run-reply'),
    )
    assert.match(resumed, /# ok/)
    assert.match(resumed, /answer = "staging"/, 'the await yields the injected answer')
  })
})

test('WHAT[work-050] js.ask outside S1 is refused with ASK_REFUSED', async () => {
  await withPlanSession(async ({ hooks, runtime, session }) => {
    const store = workspaceEventStoreSurface.acquire(await journalSurface.JournalSurface_initializeStateRoot(runtime.directory))
    const promoted = await lifecycleSurface.seedReviewAs(store, session, 'Promoted', 'reviewer')
    assert.equal(promoted.reviewed, true, 'the S1 tenure is promoted into S2')
    runtime.client.tui = { showToast: async () => ({}) }

    const output = await hooks.tool['todowrite-for-plan'].execute(
      { program: program(`    await js.ask('Out of stage?');\n    return { unreachable: true };`) },
      context(session, 'plan-call-s2', 'plan-run-s2'),
    )

    assert.match(output, /# failed/)
    assert.match(output, /ASK_REFUSED/)
    assert.match(output, /only in the S1 planning stage/)
    assert.equal(userQuestionObservation(runtime.journal, session).length, 0, 'no question is recorded outside S1')
  })
})

test('WHAT[work-050] a pending question blocks every new plan call until the user replies', async () => {
  await withPlanSession(async ({ hooks, runtime, session }) => {
    runtime.client.tui = { showToast: async () => ({}) }
    const parked = await hooks.tool['todowrite-for-plan'].execute(
      { program: program(`    await js.ask('Still waiting?');\n    return { done: true };`) },
      context(session),
    )
    assert.match(parked, /# parked/)

    const blocked = await hooks.tool['todowrite-for-plan'].execute(
      { abandon: { handle: handleOf(parked) } },
      context(session, 'plan-call-abandon', 'plan-run-abandon'),
    )
    assert.match(String(blocked), /等待|waiting|不能/)
  })
})
