import assert from 'node:assert/strict'
import test from 'node:test'
import { createJoinProbe, joinProbeDeliver } from '../../../dist/Execution/Delegation/Fork/JoinSurface.js'
import {
  physicalUserMessageOf,
  childTurn,
  forkCall,
  joinCall,
  observeTerminals,
  withRealFork,
} from './support/real-fork.mjs'

const prose = 'DELIVERED PROSE'

test('WHAT[delegation-033] clean natural stop settles a journal-less run with the final assistant prose', { timeout: 3000 }, async () => {
  const result = await joinProbeDeliver(createJoinProbe(), [], 'current report')
  assert.equal(result.status, 'completed')
  assert.equal(result.body, 'current report')
  assert.doesNotMatch(result.body, /\[\[session\]\]/, 'the natural-stop return is prose, never a session payload')
  assert.deepEqual(result.returned, [], 'a natural stop carries no structured return material')
})

test('WHAT[delegation-033] clean natural stop settles the child run without requiring a deliver member', async () => {
  const owner = 'ses-owner-033'
  await withRealFork(owner, async ({ hooks, createdIds, runtime }) => {
    const terminals = await observeTerminals(runtime)
    try {
      const first = await forkCall(hooks, owner, 'Ada', 'DELIVER THIS WORK')
      assert.match(first, /ok = true/)
      const child = createdIds[0]
      await childTurn(hooks, runtime, child, `run-${child}-1`, prose, 'RESULT-PROSE', terminals.awaitRun)

      const completed = terminals.seen.find(
        (entry) => entry.sessionId === child && entry.outcome?.kind === 'Completed',
      )
      assert.ok(completed, 'the delivering run publishes its completed terminal')
    } finally {
      terminals.dispose()
    }
  })
})

test('WHAT[delegation-033] the natural-stop handback carries only the terminal turn body prose', async () => {
  const owner = 'ses-owner-033-prose-only'
  await withRealFork(owner, async ({ hooks, createdIds, runtime }) => {
    const terminals = await observeTerminals(runtime)
    try {
      const first = await forkCall(hooks, owner, 'Ada', 'REPORT CLEANLY')
      assert.match(first, /ok = true/)
      const child = createdIds[0]

      // The terminal turn mixes thinking, body prose and a settled tool
      // result; only the body prose may reach the parent join.
      await runtime.awaitIngress()
      const physical = physicalUserMessageOf(runtime, child)
      const assistant = {
        info: {
          id: `run-${child}-mixed`,
          role: 'assistant',
          sessionID: child,
          parentID: physical.id,
          agent: 'engineer',
          providerID: 'provider',
          modelID: 'engineer-model',
          time: { created: 2 },
        },
        parts: [
          { type: 'reasoning', text: 'SECRET-THOUGHT-033' },
          { type: 'text', text: 'BODY-PROSE-033' },
          { type: 'tool', tool: 'todowrite', callID: 'call-mixed-033', state: { status: 'completed', input: {}, output: 'TOOL-OUTPUT-033' } },
        ],
      }
      runtime.pushHostMessage(child, assistant)
      const history = await runtime.client.session.messages({ path: { id: child } })
      const messages = history.data.map(message => ({
        ...message,
        info: { ...(message.info ?? message), sessionID: child },
      }))
      await hooks['experimental.chat.messages.transform']({ sessionID: child }, { messages })
      assistant.info.time.completed = 3
      assistant.info.finish = 'stop'
      await hooks.event({ event: { type: 'message.updated', properties: { info: assistant.info } } })
      await hooks.event({ event: { type: 'session.idle', properties: { sessionID: child } } })
      await terminals.awaitRun(child, `run-${child}-mixed`)

      const joined = await joinCall(hooks, owner)
      assert.match(joined, /BODY-PROSE-033/, 'the body prose is handed back')
      assert.doesNotMatch(joined, /SECRET-THOUGHT-033/, 'thinking never travels to the parent')
      assert.doesNotMatch(joined, /TOOL-OUTPUT-033/, 'tool material never travels to the parent')
    } finally {
      terminals.dispose()
    }
  })
})
