// PRODUCT-RETIRED compat-pass (2026-10-29): remainingScopeTurnsEstimate billing / ResetDue is not product contract; see scope-retention. Skipped cases are kept for archaeology only.
import assert from 'node:assert/strict'
import test from 'node:test'
import { DatabaseSync } from 'node:sqlite'
import { join } from 'node:path'
import { SqliteJournal } from 'wanxiangli/eventstore'
import { scopeProgress } from 'wanxiangli/scope-progress'
import { JournalSurface_initializeStateRoot as stateRoot } from '../../../../dist/Persistence/Journal/Surface.js'
import { withExecutablePlugin, acceptAuthorityRoot } from '../../../verification-system/tests/support/plugin-fixture.mjs'

const input = (call, estimate) => ({
  name: call,
  program: 'class Js extends JsProgram { async run() { return {ok:true}; } }',
  intent: 'Check distinct physical workspace SQLite ownership without changing files.',
  remainingScopeTurnsEstimate: estimate,
})

test.skip('WHAT[advisor-007] two live OpenCode plugin workspaces cannot redirect another Session SQLite progress [PRODUCT-RETIRED estimate]', async () => {
  const first = 'ses-cross-workspace-a-015'
  const second = 'ses-cross-workspace-b-015'
  scopeProgress.clear(first)
  scopeProgress.clear(second)
  const params = (hooks, session) => hooks['chat.params']({
    sessionID: session, agent: 'devops', model: { providerID:'fixture', modelID:'devops' },
  }, {})
  const call = (hooks, session, suffix, estimate) => hooks.tool['todowrite-for-devops'].execute(input('call-' + suffix, estimate), {
    sessionID: session, agent: 'devops', callID: 'call-' + suffix, messageID: 'provider-' + suffix,
  })
  const read = async (workspace, session) => {
    const root = await stateRoot(workspace)
    const journal = new SqliteJournal(new DatabaseSync(join(root,'wanxiangshu','advisor-process.sqlite')))
    try {
      const stream = `scope-progress:${session.length}:${session}:1:0`
      return journal.read(stream).map(JSON.parse)
    } finally { journal.close() }
  }
  try {
    await withExecutablePlugin(async (a, workspaceA, _ca, runtimeA) => {
      await acceptAuthorityRoot(runtimeA, first, 'devops')
      await params(a, first)
      assert.match(String(await call(a, first, 'a1', 7)), /# ok/)
      await params(a, first)
      assert.equal(scopeProgress.status(first).estimate, 7)
      await withExecutablePlugin(async (b, workspaceB, _cb, runtimeB) => {
        await acceptAuthorityRoot(runtimeB, second, 'devops')
        await params(b, second)
        assert.match(String(await call(b, second, 'b1', 11)), /# ok/)
        await params(b, second)
        assert.equal(scopeProgress.status(second).estimate, 11)
        assert.equal(scopeProgress.status(first).estimate, 7,
          'plugin B initialization cannot change plugin A durable journal factory')
        assert.match(String(await call(a, first, 'a2', 4)), /# ok/)
        await params(a, first)
        assert.equal(scopeProgress.status(first).estimate, 4)
        assert.equal(scopeProgress.status(second).estimate, 11)
        assert.deepEqual((await read(workspaceA, first)).filter(x => x.kind === 'Settled').map(x => x.estimate),[7,4])
        assert.deepEqual((await read(workspaceB, second)).filter(x => x.kind === 'Settled').map(x => x.estimate),[11])
        assert.deepEqual(await read(workspaceB, first), [],
          'a Session from plugin A must not leave any progress fact in plugin B SQLite')
      })
    })
  } finally {
    scopeProgress.clear(first)
    scopeProgress.clear(second)
  }
})
