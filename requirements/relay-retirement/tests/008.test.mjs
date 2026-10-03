import assert from 'node:assert/strict'
import test from 'node:test'
import {withReview, scores} from '../../relay-assessment/tests/support/plugin.mjs'
import {withSuccessor, messageId} from '../../relay-context-projection/tests/support/cut.mjs'
import { withExecutablePlugin, acceptAuthorityRoot } from '../../verification-system/tests/support/plugin-fixture.mjs'

test('WHAT[relay-retirement-008] actual Accepted suicide returns without abort and the next transform stops the retired attempt', async () => {
  await withReview(async ({execute, hooks, runtime, session}) => {
    assert.match(await execute(scores('PERFECT')), /recorded = true/)
    const result = await hooks.tool.suicide.execute({}, {sessionID: session, callID: 'suicide-call', messageID: 'retirement-run', agent: 'manager'})
    assert.match(result, /finished = true/)
    assert.deepEqual(runtime.abortedIds, [])
    const output = {messages: [
      {info: {id: 'user-root', role: 'user', sessionID: session, model: {providerID: 'provider', modelID: 'manager-model'}}, parts: [{type: 'text', text: 'Deliver the requested behavior and verification.'}]},
      {info: {id: 'retirement-run', role: 'assistant'}, parts: [{type: 'text', text: 'Old closing tail'}]},
    ]}
    await hooks['experimental.chat.messages.transform']({sessionID: session}, output)
    assert.deepEqual(output.messages, [])
    assert.deepEqual(runtime.abortedIds, [session])
    assert.equal(runtime.prompts.length, 0)
  })
})

test('WHAT[relay-retirement-008] actual Continue starts one same-session successor whose transform retains the old physical history without another abort', async () => {
  await withSuccessor(async ({hooks, runtime, session, history, gate}) => {
    const output = {messages: [...structuredClone(history), gate]}
    await hooks['experimental.chat.messages.transform']({sessionID: session}, output)
    for (const prior of history) {
      assert.ok(output.messages.some(message => messageId(message) === messageId(prior)), `lost physical message ${messageId(prior)}`)
    }
    assert.ok(output.messages.some(message => messageId(message) === messageId(gate)))
    assert.deepEqual(runtime.abortedIds, [session])
    assert.equal(runtime.prompts.length, 1)
  })
})

test('WHAT[relay-retirement-008] physical prompt after Accepted suicide invalidates certificate and unblocks successor manager tools', async () => {
  const { withExecutablePlugin, acceptAuthorityRoot } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
  await withExecutablePlugin(async (hooks, _directory, _children, runtime) => {
    const sessionID = 'ses-accepted-human-successor'
    const rootID = `root-${sessionID}`
    const root = {
      id: rootID,
      role: 'user',
      parts: [{ type: 'text', text: 'Design the capability reuse resolver.' }],
    }
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    runtime.pushHostMessage(sessionID, root)
    await hooks['chat.message'](
      { sessionID, messageID: rootID, agent: 'manager' },
      { message: root, parts: root.parts },
    )
    const user = { info: { id: rootID, role: 'user', sessionID }, parts: root.parts }
    await hooks['experimental.chat.messages.transform']({ sessionID }, { messages: [user] })

    // Step 1: Submit PERFECT review
    const scores = Object.fromEntries([
      'language_algorithms', 'simplicity', 'structure', 'granularity',
      'tests_evidence', 'logic_reliability_boundaries', 'caller_ergonomics', 'completeness',
    ].map((name) => [name, 'PERFECT']))
    const review = {
      id: 'run-review', role: 'assistant', parentID: rootID,
      parts: [
        { type: 'text', text: 'All criteria perfect.' },
        { type: 'tool', tool: 'review', callID: 'call-review', state: { status: 'pending', input: scores } },
      ],
    }
    runtime.pushHostMessage(sessionID, review)
    const context = (callID, messageID) => ({ sessionID, agent: 'manager', callID, messageID })
    const reviewResult = await hooks.tool.review.execute(scores, context('call-review', review.id))
    assert.match(reviewResult, /recorded = true/)

    // Step 2: Suicide commits Accepted retirement
    const retiredRun = {
      id: 'run-suicide', role: 'assistant', parentID: rootID,
      parts: [{ type: 'tool', tool: 'suicide', callID: 'call-suicide', state: { status: 'pending', input: {} } }],
    }
    runtime.pushHostMessage(sessionID, retiredRun)
    const suicideResult = await hooks.tool.suicide.execute({}, context('call-suicide', retiredRun.id))
    assert.match(suicideResult, /finished = true/)

    // Prior to human input, manager tools are denied (retirement frozen / Accepted certificate)
    const forkDuringRetirement = await hooks.tool.fork.execute(
      { calling: 'engineer', name: 'alice', charge: 'check' },
      context('call-fork-stale', 'msg-stale'),
    )
    assert.match(forkDuringRetirement, /(当前不可用|is not available right now)/)

    // Step 3: Human inputs new instructions in the continuous session
    const humanNext = {
      id: 'human-phase-2', role: 'user',
      parts: [{ type: 'text', text: 'Plan approved. Please start implementation.' }],
    }
    await hooks['chat.message'](
      { sessionID, messageID: humanNext.id, agent: 'manager' },
      { message: humanNext, parts: humanNext.parts },
    )
    const humanRequest = {
      messages: [
        user,
        { info: { id: review.id, role: 'assistant', sessionID }, parts: review.parts },
        { info: { id: retiredRun.id, role: 'assistant', sessionID }, parts: retiredRun.parts },
        { info: { id: humanNext.id, role: 'user', sessionID }, parts: humanNext.parts },
      ],
    }
    await hooks['experimental.chat.messages.transform']({ sessionID }, humanRequest)

    // Step 4: After human input, fork must NOT be denied
    const forkSuccessor = await hooks.tool.fork.execute(
      { calling: 'engineer', name: 'alice', charge: 'implement' },
      context('call-fork-successor', 'msg-successor'),
    )
    assert.doesNotMatch(forkSuccessor, /(当前不可用|is not available right now)/, 'fork must be unblocked after human input advances the continuous session')
  })
})

test.todo('WHAT[relay-retirement-008] a controlled in-flight Host interrupt prevents successor dispatch until exact provider-step release and physical interruption complete')
