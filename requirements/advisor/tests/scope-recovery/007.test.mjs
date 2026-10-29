import assert from 'node:assert/strict'
import test from 'node:test'
import { DatabaseSync } from 'node:sqlite'
import { mkdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { SqliteJournal } from 'wanxiangli/eventstore'
import {
  scopeProgress, advisorProcessQueue, AdvisorProcessQueue,
  advisorClaim, advisorStageBegin, advisorPending,
} from 'wanxiangli/scope-progress'
import { outboundViewStore } from 'wanxiangli/pair-hint'
import { JournalSurface_initializeStateRoot as stateRoot } from '../../../../dist/Persistence/Journal/Surface.js'
import { withExecutablePlugin, acceptAuthorityRoot } from '../../../verification-system/tests/support/plugin-fixture.mjs'

const until = async predicate => {
  for (let n = 0; n < 100; n++) {
    if (predicate()) return
    await new Promise(resolve => setTimeout(resolve, 10))
  }
  assert.fail('durable ScopeReanchored was not used to recover the old Advisor process')
}

test.skip('WHAT[advisor-007] real ScopeReanchored journal identifies a cold Scope even when all ScopeProgress memory is gone [PRODUCT-RETIRED standalone-resetScope]', async () => {
  await withExecutablePlugin(async (hooks, workspace, _children, runtime) => {
    const session = 'ses-durable-scope-advisor-013'
    const scope = '1'
    const report = '1:previous-round-013'
    const issue = 'What went wrong with the previous charged round?'
    scopeProgress.clear(session)
    advisorProcessQueue.clear(session)
    await acceptAuthorityRoot(runtime, session, 'devops')
    const sealed = outboundViewStore.recordNativeRequest(
      session,
      { model: 'fixture-model', messages: [
        { role: 'system', content: 'original observable system for real Scope reset' },
        { role: 'user', content: 'original task and evidence for the reset boundary' },
      ], tools: [] },
      { providerID: 'fixture', modelID: 'devops' },
    )
    assert.equal(sealed, true)
    const result = await hooks.tool.resetScope.execute(
      { newScopeObjective: 'continue after durable first reset' },
      { sessionID: session, agent: 'devops', callID: 'reset-first-013', messageID: 'provider-reset-013' },
    )
    assert.match(String(result), /Scope reset\. Objective:/)
    assert.equal(scopeProgress.status(session).epoch, scope)
    const database = join(await stateRoot(workspace), 'wanxiangshu', 'advisor-process.sqlite')
    mkdirSync(dirname(database), { recursive: true })
    const use = action => {
      const journal = new SqliteJournal(new DatabaseSync(database))
      try { return action(journal) } finally { journal.close() }
    }
    const processId = AdvisorProcessQueue().enqueue(session, scope, report).processId
    assert.equal(use(j => advisorClaim(j, session, scope, report, issue)).status, 'Committed')
    assert.equal(use(j => advisorStageBegin(j, session, scope, processId, 1, issue, [report])).status, 'Committed')
    scopeProgress.clear(session)
    advisorProcessQueue.clear(session)
    assert.equal(scopeProgress.status(session).known, false)
    assert.equal(scopeProgress.status(session).epoch, undefined,
      'no in-memory estimate or Scope epoch remains after the simulated restart')
    try {
      await hooks['chat.params']({
        sessionID: session, agent: 'devops', model: { providerID: 'fixture', modelID: 'devops' },
      }, {})
      await until(() => advisorProcessQueue.hasActive(session, scope))
      assert.equal(scopeProgress.status(session).known, false,
        'resuming a known Scope must not fabricate a previous charged estimate')
      const events = use(j => j.read('advisor-process:' + session).map(JSON.parse))
      assert.deepEqual(events.map(event => event.kind), ['Admitted', 'StageBegan'])
      assert.deepEqual(use(j => advisorPending(j, session, scope)).pending.map(x => x.reportId), [report])
    } finally {
      scopeProgress.clear(session)
      advisorProcessQueue.clear(session)
      outboundViewStore.clearSession(session)
    }
  })
})
