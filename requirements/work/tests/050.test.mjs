import assert from 'node:assert/strict'
import test from 'node:test'
import { withExecutablePlugin, acceptAuthorityRoot } from '../../verification-system/tests/support/plugin-fixture.mjs'
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
    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
    const surface = lifecycleSurface.LifecycleSurface ?? lifecycleSurface
    const opened = await surface.submitReview(
      commonDir,
      session,
      'open-review',
      'Plan',
      'run-open',
      'call-open',
      [{ key: 'need', value: 'a plan' }],
      null,
      '',
    )
    assert.equal(opened.kind, 'Reviewed', opened.reason)
    assert.equal(opened.milestone, '事实与取舍')
    await body({ hooks, directory, runtime, session, created, commonDir, surface, opened })
  })
}

const nameOf = (doc) => {
  const match = doc.match(/^name = "([^"]+)"/m)
  assert.ok(match, `parked document must carry its program name: ${doc}`)
  return match[1]
}

test('WHAT[work-050] this.ask records the question and parks the program', async () => {
  await withPlanSession(async ({ hooks, runtime, session }) => {
    const notices = []
    runtime.client.tui = { showToast: async ({ body }) => { notices.push(body); return {} } }

    const parked = await hooks.tool['todowrite-for-plan'].execute(
      { program: program(`    const answer = await this.ask('Which deployment environment should this target?');\n    return { answer };`), name: 'ask-deploy',
        intent: 'Record the question and retain the paused program until the real user reply arrives.',
        remainingScopeTurnsEstimate: 3,
      },
      context(session),
    )

    assert.match(parked, /# parked/)
    assert.equal(notices.length, 1, 'the user is notified once')
    assert.match(notices[0].message, /Which deployment environment/)
    assert.equal(userQuestionObservation(runtime.journal, session).length, 1, 'one durable question is recorded')

    // 等待门禁要求真实用户输入先到达；此后 unpark 注入的值即 await 的值。
    await humanInput(hooks, runtime, session, 'user-reply', 'staging')
    const resumed = await hooks.tool['todowrite-for-plan'].execute(
      { name: 'resume-question', intent: 'Resume the original question with the real user reply under this new request window.', remainingScopeTurnsEstimate: 3, program: program('return await this.unpark(' + JSON.stringify(nameOf(parked)) + ', "staging");') },
      context(session, 'plan-call-reply', 'plan-run-reply'),
    )
    assert.match(resumed, /# ok/)
    assert.match(resumed, /answer = "staging"/, 'the await yields the injected answer')
  })
})

test('WHAT[work-050] ask still parks after a review, and deliver is not a promotion', async () => {
  await withPlanSession(async ({ hooks, runtime, session, directory }) => {
    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
    const surface = lifecycleSurface.LifecycleSurface ?? lifecycleSurface
    const recorded = await surface.submitReview(
      commonDir,
      session,
      'r1',
      'Plan',
      'run',
      'call-review',
      [{ key: 'need', value: 'a plan' }],
      null,
      '',
    )
    assert.equal(recorded.kind, 'Reviewed', recorded.reason)
    assert.equal(recorded.milestone, '事实与取舍')

    const store = workspaceEventStoreSurface.acquire(commonDir)
    const advanced = await surface.seedDeliver(store, session, 'run-promote', 'call-promote', 1n)
    assert.equal(advanced.ok, false, advanced.error)
    assert.notEqual(advanced.decision, 'Promoted')
    assert.match(String(advanced.error), /review is the only/)

    runtime.client.tui = { showToast: async () => ({}) }
    const output = await hooks.tool['todowrite-for-plan'].execute(
      { program: program(`    await this.ask('Which fact is still missing?');\n    return { unreachable: true };`), name: 'ask-after-review',
        intent: 'Record the question and retain the paused program until the real user reply arrives.',
        remainingScopeTurnsEstimate: 3,
      },
      context(session, 'plan-call-after', 'plan-run-after'),
    )

    assert.match(String(output), /# parked/)
    assert.doesNotMatch(String(output), /ASK_REFUSED/)
    assert.equal(userQuestionObservation(runtime.journal, session).length, 1, 'the question is recorded after review')
    const view = surface.inspected(commonDir, session)
    assert.equal(view.Stage, '事实与取舍')
    assert.equal(view.Delivered, false)
  })
})

test('WHAT[work-050] a pending question blocks every new plan call until the user replies', async () => {
  await withPlanSession(async ({ hooks, runtime, session }) => {
    runtime.client.tui = { showToast: async () => ({}) }
    const parked = await hooks.tool['todowrite-for-plan'].execute(
      { program: program(`    await this.ask('Still waiting?');\n    return { done: true };`), name: 'ask-wait',
        intent: 'Record the question and retain the paused program until the real user reply arrives.',
        remainingScopeTurnsEstimate: 3,
      },
      context(session),
    )
    assert.match(parked, /# parked/)

    const blocked = await hooks.tool['todowrite-for-plan'].execute(
      { name: 'abandon-question', intent: 'Request cleanup only if the pending-question gate admits this operation.', remainingScopeTurnsEstimate: 3, program: program('return await this.abandon(' + JSON.stringify(nameOf(parked)) + ');') },
      context(session, 'plan-call-abandon', 'plan-run-abandon'),
    )
    assert.match(String(blocked), /等待|waiting|不能/)
  })
})
