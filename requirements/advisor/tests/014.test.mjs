import assert from 'node:assert/strict'
import test from 'node:test'
import { DatabaseSync } from 'node:sqlite'
import { join } from 'node:path'
import { SqliteJournal } from 'wanxiangli/eventstore'
import { scopeProgress } from 'wanxiangli/scope-progress'
import { stateRoot } from '../../../dist/Persistence/Journal/RuntimePath.js'
import { withExecutablePlugin, acceptAuthorityRoot } from '../../verification-system/tests/support/plugin-fixture.mjs'

const code = 'class Js extends JsProgram { async run() { return {ok: true}; } }'
const args = (id, estimate) => ({
  name: id, program: code,
  intent: 'A real, source-attributed provider tool with an explicit remaining estimate.',
  remainingScopeTurnsEstimate: estimate,
})

test('WHAT[advisor-014] real OpenCode todowrite restores its charged estimate and ResetDue from the SQLite ledger after memory loss', async () => {
  await withExecutablePlugin(async (hooks, workspace, _created, runtime) => {
    const session = 'ses-progress-journal-014'
    const epoch = '0'
    scopeProgress.clear(session)
    await acceptAuthorityRoot(runtime, session, 'devops')
    const tool = hooks.tool['todowrite-for-devops']
    const invoke = (name, run, value) => tool.execute(args(name,value), {
      sessionID: session, agent: 'devops', callID: name, messageID: run,
    })
    const params = () => hooks['chat.params']({
      sessionID: session, agent: 'devops', model: { providerID:'fixture', modelID:'devops' },
    }, {})
    const root = await stateRoot(workspace)
    const readFacts = () => {
      const db = join(root, 'wanxiangshu', 'advisor-process.sqlite')
      const journal = new SqliteJournal(new DatabaseSync(db))
      try {
        const stream = `scope-progress:${session.length}:${session}:${epoch.length}:${epoch}`
        return journal.read(stream).map(JSON.parse)
      } finally { journal.close() }
    }
    try {
      assert.match(String(await invoke('report-a','provider-one',5)), /# ok/)
      await params()
      assert.deepEqual([scopeProgress.status(session).estimate, scopeProgress.status(session).resetDue], [5,false])
      assert.deepEqual(readFacts().map(x => x.kind), ['Recorded','Settled'])
      scopeProgress.clear(session)
      assert.equal(scopeProgress.status(session).known,false,
        'without the current Scope identity we do not guess which historical balance applies')
      await params()
      assert.deepEqual([scopeProgress.status(session).estimate,scopeProgress.status(session).resetDue],[5,false],
        'Host reconstructs baseline from trusted Scope identity and real SQLite')
      assert.match(String(await invoke('report-b','provider-two',3)), /# ok/)
      await params()
      assert.deepEqual([scopeProgress.status(session).estimate,scopeProgress.status(session).resetDue],[3,false])
      assert.match(String(await invoke('report-zero','provider-three',0)), /# ok/)
      await params()
      assert.deepEqual([scopeProgress.status(session).estimate,scopeProgress.status(session).resetDue],[0,true])
      scopeProgress.clear(session)
      await params()
      assert.deepEqual([scopeProgress.status(session).estimate,scopeProgress.status(session).resetDue],[0,true],
        'a zero estimate latch survives a lost in-memory actor')
      const facts = readFacts()
      assert.deepEqual(facts.filter(f => f.kind==='Settled').map(f => f.estimate),[5,3,0])
      assert.equal(facts.filter(f => f.kind==='Recorded').length,3)
      assert.equal(facts.filter(f => f.kind==='Settled').length,3)
    } finally { scopeProgress.clear(session) }
  })
})
