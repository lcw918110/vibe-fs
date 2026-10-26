import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'
import { relayHandover } from './support/contract.mjs'
import * as scopeMod from '../../../dist/OpenCode/Tools/ToolRuntimeScopeSurface.js'
import { withAdmittedChildren } from '../../managed-session-lifecycle/tests/support/admitted-child-work.mjs'

const evaluateBlockers =
  scopeMod.evaluateRetirementBlockers ||
  (scopeMod.ToolRuntimeScopeSurface && scopeMod.ToolRuntimeScopeSurface.evaluateRetirementBlockers)

test('WHAT[work-044] Relayed keeps the fixed DevOps and its received work for the successor', () => {
  const work = new Work('work-044')
  work.execute('r1', { type: 'Review', office: 'Plan', providerRunId: 'run', toolCallId: 'r1', updates: [{ key: 'gap', value: 'fix' }] })
  const bound = work.execute('bind', { type: 'BindDevOps', trigger: 'Ask', devopsId: 'd', target: 'model-a' })
  assert.equal(bound.result.kind, 'DevOpsBound')
  const relayed = work.execute('r2', { type: 'Review', office: 'Plan', providerRunId: 'run', toolCallId: 'r2', updates: [{ key: 'gap', value: null }] })
  assert.equal(relayed.result.kind, 'Relayed')
  assert.equal(work.inspect().devopsId, 'd')
  const handover = relayHandover(relayed.result, { devopsId: 'd', received: ['job-1'] })
  assert.equal(handover.ok, true)
  assert.equal(handover.orphaned, false)
  assert.deepEqual(handover.kept, ['job-1'])
  assert.equal(handover.written.some((event) => event.type === 'DevOpsBound'), false)
})

test('WHAT[work-044] RetirementBlockersFor excludes fixed DevOps agent and PTYs while blocking on active Engineer work', async () => {
  assert.equal(typeof evaluateBlockers, 'function', 'evaluateRetirementBlockers must be exported')

  await withAdmittedChildren(
    'work-044-devops-handover',
    'manager-road-044',
    [
      { agentId: 'devops', sessionId: 'devops-child-044', role: 'devops' },
      { agentId: 'engineer-1', sessionId: 'engineer-child-044', role: 'engineer' },
    ],
    async (journal) => {
      // Only fixed DevOps active: 0 blockers for Manager tenure relay
      const onlyDevOps = await evaluateBlockers(journal, {
        managerSessionId: 'manager-road-044',
        devopsChildSessionId: 'devops-child-044',
        engineerChildSessionId: 'engineer-child-044',
        devopsPtys: ['devops-bg-pty'],
        engineerPtys: [],
        managerHasDevopsAgent: true,
        managerHasEngineerAgent: false,
      })
      assert.deepEqual(
        onlyDevOps,
        [],
        'fixed DevOps run and its PTYs survive across Manager relay and must not block retirement',
      )

      // Engineer active with PTY: blocks Manager retirement
      const withEngineer = await evaluateBlockers(journal, {
        managerSessionId: 'manager-road-044',
        devopsChildSessionId: 'devops-child-044',
        engineerChildSessionId: 'engineer-child-044',
        devopsPtys: ['devops-bg-pty'],
        engineerPtys: ['eng-active-pty'],
        managerHasDevopsAgent: true,
        managerHasEngineerAgent: true,
      })
      assert.equal(
        withEngineer.some((b) => b.includes('engineer-child-044')),
        true,
        'active Engineer child run must block Manager retirement',
      )
      assert.equal(
        withEngineer.includes('fork:engineer-child-044:pty:eng-active-pty'),
        true,
        'active Engineer PTY lease must block Manager retirement',
      )
      assert.equal(
        withEngineer.some((b) => b.includes('devops')),
        false,
        'fixed DevOps must still be excluded when Engineer blockers are present',
      )
    },
  )
})
