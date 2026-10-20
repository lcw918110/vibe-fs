import test from 'node:test'
import assert from 'node:assert/strict'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

integrationTest('WHAT[effect-accounting-008] programming surface with view commits its compression checkpoint only after execution succeeds', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'effect-008-todowrite'
    const callID = 'effect-008-todo-1'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')

    const beforeOutput = {
      args: {
        program: 'class Js extends JsProgram { async run() { return {}; } }',
        view: { prefixUntil: 1 },
      },
    }

    await hooks['tool.execute.before']({ tool: 'todowrite-for-engineer', sessionID, callID }, beforeOutput)

    const beforeSnapshot = journalSurface.JournalSurface_snapshot(runtime.journal)
    assert.deepEqual(beforeSnapshot.viewCheckpoints, [], 'before admission must not commit a checkpoint')

    await hooks['tool.execute.after'](
      { tool: 'todowrite-for-engineer', sessionID, callID, args: beforeOutput.args },
      { title: 'todowrite-for-engineer', output: 'ok', metadata: {} },
    )

    assert.deepEqual(
      journalSurface.JournalSurface_snapshot(runtime.journal).viewCheckpoints,
      [],
      'after runs while the Host tool part is still running',
    )

    await hooks.event({
      event: {
        type: 'message.part.updated',
        properties: {
          sessionID,
          part: {
            type: 'tool',
            tool: 'todowrite-for-engineer',
            callID,
            state: {
              status: 'completed',
              input: {
                program: 'class Js extends JsProgram { async run() { return {}; } }',
                view: { prefixUntil: 1 },
              },
            },
          },
        },
      },
    })

    const afterSnapshot = journalSurface.JournalSurface_snapshot(runtime.journal)
    assert.deepEqual(afterSnapshot.viewCheckpoints, [
      {
        sessionId: sessionID,
        checkpoints: [{ callId: callID }],
      },
    ])
  })
})

integrationTest('WHAT[effect-accounting-008] programming surface failure does not commit a compression checkpoint', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'effect-008-todowrite-failed'
    const callID = 'effect-008-todo-failed-1'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')

    const beforeOutput = {
      args: {
        program: 'class Js extends JsProgram { async run() { return {}; } }',
        view: { prefixUntil: 1 },
      },
    }

    await hooks['tool.execute.before']({ tool: 'todowrite-for-engineer', sessionID, callID }, beforeOutput)

    await hooks['tool.execute.after'](
      { tool: 'todowrite-for-engineer', sessionID, callID, args: beforeOutput.args },
      { title: 'todowrite-for-engineer', output: 'failure', metadata: {} },
    )

    await hooks.event({
      event: {
        type: 'message.part.updated',
        properties: {
          sessionID,
          part: {
            type: 'tool',
            tool: 'todowrite-for-engineer',
            callID,
            state: {
              status: 'error',
              input: {
                program: 'class Js extends JsProgram { async run() { return {}; } }',
                view: { prefixUntil: 1 },
              },
            },
          },
        },
      },
    })

    assert.deepEqual(journalSurface.JournalSurface_snapshot(runtime.journal).viewCheckpoints, [])
  })
})

