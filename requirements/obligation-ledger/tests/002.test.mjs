import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

const here = path.dirname(fileURLToPath(import.meta.url))
const installedTodoCanaryRunner = path.join(here, 'support/run-native-todo-replacement-canary.mjs')

integrationTest('WHAT[obligation-ledger-002] programming-surface view declarations accumulate in the durable checkpoint window across todowrite-for-* roles', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'obligation-view-window'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')

    const calls = [
      { callID: 'view-cp-1', tool: 'todowrite-for-engineer', view: { prefixUntil: 1 } },
      { callID: 'view-cp-2', tool: 'todowrite-for-devops', view: { prefixUntil: 1 } },
      { callID: 'view-cp-3', tool: 'todowrite-for-manager', view: { prefixUntil: 3 } },
      { callID: 'view-cp-4', tool: 'todowrite-for-plan', view: { prefixUntil: 4 } },
    ]

    for (const { callID, tool, view } of calls) {
      await hooks.event({
        event: {
          type: 'message.part.updated',
          properties: {
            sessionID,
            part: {
              type: 'tool',
              tool,
              callID,
              state: {
                status: 'completed',
                input: { program: 'class Js extends JsProgram { async run() { return {}; } }', view },
              },
            },
          },
        },
      })
    }

    const snapshot = journalSurface.JournalSurface_snapshot(runtime.journal)
    const window = snapshot.viewCheckpoints.find((entry) => entry.sessionId === sessionID)
    assert.deepEqual(
      window?.checkpoints,
      calls.map((c) => ({ callId: c.callID })),
      'all four completed view declarations are recorded in order',
    )
  })
})

const runInstalledTodoCanary = () => {
  const launched = spawnSync(process.execPath, [installedTodoCanaryRunner], {
    cwd: path.resolve(here, '../../..'),
    encoding: 'utf8',
  })
  assert.equal(launched.status, 0, launched.stderr || launched.stdout)
  return JSON.parse(launched.stdout)
}

integrationTest('WHAT[obligation-ledger-002] installed OpenCode Host runs todowrite-for-engineer with view and commits ViewCheckpointCommitted per session', () => {
  const evidence = runInstalledTodoCanary()

  assert.equal(evidence.versions.opencode, '1.18.29', `OpenCode version drifted: ${evidence.versions.opencode}`)
  assert.equal(evidence.versions.plugin, '1.18.29', `plugin version drifted: ${evidence.versions.plugin}`)
  assert.equal(evidence.observed.todowriteAdvertised, false, 'native todowrite must not be advertised on the Engineer provider wire')
  assert.equal(evidence.observed.programmingToolAdvertised, true, 'todowrite-for-engineer must be advertised on the Engineer provider wire')
  assert.deepEqual(
    evidence.observed.checkpointsBySession[evidence.sessions.sessionA],
    ['ol002-a-view'],
    'session A records only the call that declared a non-empty view',
  )
  assert.deepEqual(
    evidence.observed.checkpointsBySession[evidence.sessions.sessionB],
    ['ol002-b-view'],
    'session B records its own view checkpoint without touching session A',
  )
})