import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync } from 'node:fs'
import { DatabaseSync } from 'node:sqlite'
import { join } from 'node:path'
import { SqliteJournal } from 'wanxiangli/eventstore'
import { advisorClaim, scopeProgress } from 'wanxiangli/scope-progress'
import { withExecutablePlugin, acceptAuthorityRoot } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { stateRoot } from '../../../dist/Persistence/Journal/RuntimePath.js'
import { negotiationRememberSystem, negotiationRememberMessages, negotiationRememberModel, negotiationForgetSession } from '../../../dist/OpenCode/Plugin/PluginTransforms.js'

const program = 'class Js extends JsProgram { async run() { return { seen: true }; } }'
const args = (name, remainingScopeTurnsEstimate = 4) => ({
  name, program, intent: 'Observe this Provider round without modifying the workspace.',
  remainingScopeTurnsEstimate,
})
const sleep = (ms) => new Promise(resolve => setTimeout(resolve, ms))
const waitFor = async (predicate) => {
  for (let attempt = 0; attempt < 90; attempt++) {
    if (predicate()) return
    await sleep(20)
  }
  assert.fail('Advisor admission was not durably observed')
}

test('WHAT[advisor-007] real OpenCode progress escalation commits SQLite identity before SDK consultation', async () => {
  await withExecutablePlugin(async (hooks, directory, _created, runtime) => {
    const session = 'advisor-durable-physical-007'
    scopeProgress.clear(session)
    await acceptAuthorityRoot(runtime, session, 'devops')
    const executor = hooks.tool['todowrite-for-devops']
    const context = (id, run) => ({ sessionID: session, agent: 'devops', callID: id, messageID: run })
    const model = { providerID: 'fixture', modelID: 'devops' }
    const sdk = runtime.client.session
    const db = join(await stateRoot(directory), 'wanxiangshu', 'advisor-process.sqlite')
    const readClaim = () => {
      assert.equal(existsSync(db), true, 'the durable claim must exist before any SDK side effect')
      const journal = new SqliteJournal(new DatabaseSync(db))
      try { return journal.read('advisor-process:' + session) } finally { journal.close() }
    }
    const originalPrompt = sdk.prompt
    const originalCreate = sdk.create
    let created = 0
    sdk.create = async (...args) => {
      const durableBefore = readClaim().map(JSON.parse)
      assert.deepEqual(durableBefore.map(x => x.kind), ['Admitted', 'StageBegan'], 'stage 1 reservation must commit before SDK session.create')
      created++
      return originalCreate(...args)
    }
    let asked = 0
    sdk.prompt = async () => {
      const durableBefore = readClaim().map(JSON.parse)
      assert.deepEqual(durableBefore.map(x => x.kind), ['Admitted', 'StageBegan'], 'stage 1 reservation must commit before SDK session.prompt')
      asked++
      return { error: { message: 'independent provider failure; do not ask again' } }
    }
    try {
      assert.match(await executor.execute(args('baseline'), context('t1', 'provider-1')), /# ok/)
      await hooks['chat.params']({ sessionID: session, model }, {})
      assert.equal(scopeProgress.status(session).estimate, 4)
      negotiationRememberSystem({ sessionID: session }, { system: ['Real worker system instruction'] })
      negotiationRememberMessages(session, { messages: [{ info: { id: 'worker-user-007', role: 'user', sessionID: session }, parts: [{ type: 'text', text: 'Observed worker evidence before this consultation.' }] }] })
      negotiationRememberModel({ sessionID: session, model })
      assert.match(await executor.execute(args('stalled', 4), context('t2', 'provider-2')), /# ok/)
      await hooks['chat.params']({ sessionID: session, model }, {})
      const charged = scopeProgress.settle(session, '0', 'provider-2')
      assert.equal(charged.charged, true, 'the second physical run must be recorded and settled')
      assert.equal(charged.advisor, true, 'a round without the promised 0.1 drop must trigger Advisor before checking SQLite')
      await waitFor(() => existsSync(db) && (() => {
        const journal = new SqliteJournal(new DatabaseSync(db))
        try { return journal.read('advisor-process:' + session).some(frame => JSON.parse(frame).kind === 'Admitted') }
        finally { journal.close() }
      })())
      const inspect = () => {
        const journal = new SqliteJournal(new DatabaseSync(db))
        try { return journal.read('advisor-process:' + session) } finally { journal.close() }
      }
      const admitted = inspect().map(JSON.parse).filter(frame => frame.kind === 'Admitted')
      assert.equal(admitted.length, 1, 'one SQLite claim is committed for the stalled Provider round')
      const reservation = admitted[0]
      assert.equal(reservation.scope, '0')
      assert.equal(reservation.report, '0:provider-2')
      assert.ok(typeof reservation.issue === 'string' && reservation.issue.length > 0)
      await waitFor(() => asked === 1)
      assert.equal(created, 1, 'the first confirmed reservation issues exactly one temporary SDK session')
      await waitFor(() => inspect().map(JSON.parse).some(frame => frame.kind === 'StageBegan'))
      await sleep(50)
      assert.deepEqual(inspect().map(JSON.parse).map(frame => frame.kind), ['Admitted', 'StageBegan'],
        'an SDK Provider error without terminal proof must remain Unknown, not Finished(Failed)')
      await hooks['chat.params']({ sessionID: session, model }, {})
      await sleep(30)
      assert.equal(asked, 1, 'a later Host model boundary cannot replay a paid request of unknown outcome')
      assert.equal(created, 1)
      assert.deepEqual(inspect().map(JSON.parse).map(frame => frame.kind), ['Admitted', 'StageBegan'])
      const replay = (() => {
        const journal = new SqliteJournal(new DatabaseSync(db))
        try { return advisorClaim(journal, session, '0', '0:provider-2', reservation.issue) }
        finally { journal.close() }
      })()
      assert.equal(replay.status, 'Replayed')
      assert.equal(replay.accepted, false, 'the recovered owner must not create a second paid consultation')
      assert.equal(inspect().map(JSON.parse).filter(frame => frame.kind === 'Admitted').length, 1)
      assert.equal(asked, 1)
    } finally {
      sdk.prompt = originalPrompt
      sdk.create = originalCreate
      negotiationForgetSession(session)
      scopeProgress.clear(session)
    }
  })
})
