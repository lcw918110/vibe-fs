import assert from 'node:assert/strict'
import test from 'node:test'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'
import * as obligation from '../../../dist/Persistence/Journal/ObligationJournalSurface.js'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

test('WHAT[obligation-ledger-003] plugin keeps no second desired/applied todo projection beside the Host-native TodoTable', () => {
  // WHAT[obligation-ledger-003]: The plugin does not maintain a second desired/applied
  // todo projection. The retired MagicTodo projection must return null/refusal,
  // and no todo content fields exist in the journal snapshot.
  const dummyHandle = {}
  assert.equal(obligation.snapshotMagicTodo(dummyHandle), null)

  const appendResult = obligation.appendMagicTodo(dummyHandle)
  assert.equal(appendResult.ok, false)
  assert.match(appendResult.error, /appendMagicTodo is retired \(obligation-ledger-007\)/)
})

integrationTest('WHAT[obligation-ledger-003] journal snapshot records only ToolCallId checkpoints and carries no todo content', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ol003-no-second-projection'
    const callID = 'ol003-call-1'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')

    const todos = [{ content: 'test task for obligation ledger', status: 'pending', priority: 'high' }]
    await hooks['tool.execute.before']({ tool: 'todowrite', sessionID, callID }, { args: { todos } })

    await hooks['tool.execute.after'](
      { tool: 'todowrite', sessionID, callID, args: { todos } },
      { title: 'todowrite', output: 'Todos updated', metadata: {} },
    )

    await hooks.event({
      event: {
        type: 'message.part.updated',
        properties: {
          sessionID,
          part: { type: 'tool', tool: 'todowrite', callID, state: { status: 'completed' } },
        },
      },
    })

    const snapshot = journalSurface.JournalSurface_snapshot(runtime.journal)
    assert.deepEqual(snapshot.todoCheckpoints, [{ sessionId: sessionID, checkpoints: [{ callId: callID }] }])

    // Strict negative check: Ensure no todo rows, content, or applied/desired table exist in the snapshot
    assert.equal('todos' in snapshot, false, 'journal snapshot must not carry a todos table')
    assert.equal('todoTable' in snapshot, false, 'journal snapshot must not carry a todoTable')
    assert.equal('desiredTodos' in snapshot, false, 'journal snapshot must not carry desiredTodos')
    assert.equal('appliedTodos' in snapshot, false, 'journal snapshot must not carry appliedTodos')
  })
})

