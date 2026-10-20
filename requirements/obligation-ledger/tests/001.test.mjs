import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import test from 'node:test'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'
import { rolePermissionRules, toolSpecNames } from '../../../dist/OpenCode/Tools/ToolSurface.js'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

test('WHAT[obligation-ledger-001] native todowrite is retired from StaticTools, role permissions, and ablation tool-map', () => {
  const known = toolSpecNames()
  assert.equal(known.includes('todowrite'), false, 'StaticTools.knownToolNames must not contain native todowrite')
  assert.ok(known.includes('todowrite-for-engineer'), 'programming surface tools remain registered')

  for (const role of ['orchestrator', 'manager', 'engineer', 'devops', 'plan']) {
    const rules = rolePermissionRules(role)
    assert.equal('todowrite' in rules, false, `${role} permission rules must not contain native todowrite`)
    assert.equal(rules['*'], 'deny', `${role} wildcard permission stays deny`)
  }

  const toolMap = JSON.parse(readFileSync(resolve(process.cwd(), 'resources/ablation/tool-map.json'), 'utf8'))
  assert.equal('todowrite' in (toolMap.tools ?? {}), false, 'ablation tool-map must not contain native todowrite')
})

integrationTest('WHAT[obligation-ledger-001] only todowrite-for-* calls with a non-empty view object commit ViewCheckpointCommitted', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'obligation-view-gate'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')

    // 1. Native todowrite completed event commits nothing.
    await hooks.event({
      event: {
        type: 'message.part.updated',
        properties: {
          sessionID,
          part: {
            type: 'tool',
            tool: 'todowrite',
            callID: 'retired-native-call',
            state: { status: 'completed', input: { todos: [{ content: 'x', status: 'pending' }], view: { prefixUntil: 1 } } },
          },
        },
      },
    })

    // 2. todowrite-for-engineer without view, with empty object view, or array view commits nothing.
    for (const [callID, input] of [
      ['call-no-view', { program: 'class Js extends JsProgram { async run() { return 1; } }' }],
      ['call-empty-view', { program: 'class Js extends JsProgram { async run() { return 1; } }', view: {} }],
      ['call-null-view', { program: 'class Js extends JsProgram { async run() { return 1; } }', view: null }],
      ['call-array-view', { program: 'class Js extends JsProgram { async run() { return 1; } }', view: [1] }],
    ]) {
      await hooks.event({
        event: {
          type: 'message.part.updated',
          properties: {
            sessionID,
            part: {
              type: 'tool',
              tool: 'todowrite-for-engineer',
              callID,
              state: { status: 'completed', input },
            },
          },
        },
      })
    }

    assert.deepEqual(
      journalSurface.JournalSurface_snapshot(runtime.journal).viewCheckpoints,
      [],
      'neither retired todowrite nor view-less/empty-view programming calls commit a checkpoint',
    )

    // 3. todowrite-for-engineer with a non-empty view object commits the checkpoint.
    await hooks.event({
      event: {
        type: 'message.part.updated',
        properties: {
          sessionID,
          part: {
            type: 'tool',
            tool: 'todowrite-for-engineer',
            callID: 'call-valid-view',
            state: {
              status: 'completed',
              input: {
                program: 'class Js extends JsProgram { async run() { return 1; } }',
                view: { prefixUntil: 2, thenRemove: { 4: 'keep anchor 2' } },
              },
            },
          },
        },
      },
    })

    assert.deepEqual(
      journalSurface.JournalSurface_snapshot(runtime.journal).viewCheckpoints,
      [{ sessionId: sessionID, checkpoints: [{ callId: 'call-valid-view' }] }],
      'a completed todowrite-for-* call with a non-empty view commits ViewCheckpointCommitted',
    )
  })
})