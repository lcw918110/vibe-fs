import assert from 'node:assert/strict'
import test from 'node:test'
import { DatabaseSync } from 'node:sqlite'
import { mkdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { SqliteJournal } from 'wanxiangli/eventstore'
import { scopeProgress, advisorProcessQueue, AdvisorProcessQueue, advisorClaim, advisorStageBegin, advisorPending } from 'wanxiangli/scope-progress'
import { JournalSurface_initializeStateRoot as stateRoot } from '../../../../dist/Persistence/Journal/Surface.js'
import { withExecutablePlugin, acceptAuthorityRoot } from '../../../verification-system/tests/support/plugin-fixture.mjs'

const until = async predicate => {
  for (let attempt = 0; attempt < 80; attempt++) {
    if (predicate()) return
    await new Promise(resolve => setTimeout(resolve, 10))
  }
  assert.fail('OpenCode did not observe the proven pending Advisor process')
}

test('WHAT[advisor-007] OpenCode resumes only the proven current Scope and refuses an uncertain model-request replay', async () => {
  await withExecutablePlugin(async (hooks, workspace, _created, runtime) => {
    const sid = 'ses-advisor-cold-012', epoch = '0', report = '0:run-advisor-cold-012'
    const issue = 'What stopped your progress?'
    scopeProgress.clear(sid)
    advisorProcessQueue.clear(sid)
    await acceptAuthorityRoot(runtime, sid, 'devops')
    const db = join(await stateRoot(workspace), 'wanxiangshu', 'advisor-process.sqlite')
    mkdirSync(dirname(db), { recursive: true })
    const use = action => {
      const journal = new SqliteJournal(new DatabaseSync(db))
      try { return action(journal) } finally { journal.close() }
    }
    const process = AdvisorProcessQueue().enqueue(sid, epoch, report).processId
    assert.equal(use(journal => advisorClaim(journal, sid, epoch, report, issue)).status, 'Committed')
    assert.equal(use(journal => advisorStageBegin(journal, sid, epoch, process, 1, issue, [report])).status, 'Committed')
    const snapshot = () => use(journal => journal.read('advisor-process:' + sid).map(JSON.parse))
    const params = () => hooks['chat.params']({ sessionID: sid, agent: 'devops', model: { providerID: 'fixture', modelID: 'devops' } }, {})
    try {
      await params()
      assert.equal(advisorProcessQueue.hasActive(sid, epoch), false,
        'without a proven active Scope, Host cannot invent recovery authority')
      scopeProgress.reset(sid, epoch)
      await params()
      await until(() => advisorProcessQueue.hasActive(sid, epoch))
      assert.deepEqual(snapshot().map(frame => frame.kind), ['Admitted', 'StageBegan'],
        'the prior paid Provider stage must not be sent a second time')
      assert.deepEqual(use(journal => advisorPending(journal, sid, epoch)).pending.map(item => item.reportId), [report])
      await params()
      assert.deepEqual(snapshot().map(frame => frame.kind), ['Admitted', 'StageBegan'])
    } finally {
      scopeProgress.clear(sid)
      advisorProcessQueue.clear(sid)
    }
  })
})
