import assert from 'node:assert/strict'
import test from 'node:test'
import * as forkTool from '../../../dist/Execution/Delegation/Fork/OpenCode/ToolSurface.js'
import { withForkRuntime } from './support/fork-runtime.mjs'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'
import { withPlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'

test('WHAT[participant-horizon-010] actual Manager horizon includes the runtime-bound fixed devops byname', async () => {
  await withForkRuntime(async ({ runtime, owner }) => {
    const roster = await forkTool.executeHorizon(runtime, owner)
    assert.match(roster, /devops/)
    assert.ok(!roster.includes(owner))
    assert.equal(forkTool.durableLifecycleByname(runtime, owner, 'devops'), 'Active')
    assert.deepEqual(forkTool.workSnapshot(runtime, owner), [], 'an idle visible binding is not admitted work')
    assert.equal(forkTool.promptCount(runtime), 0)
  })
})

integrationTest('WHAT[participant-horizon-010] actual fork and commission schemas expose only their legal new-participant identities', async () => {
  await withPlugin(async (hooks) => {
    assert.equal(hooks.tool.fork.args.calling.safeParse('engineer').success, true)
    assert.equal(hooks.tool.commission.args.calling.safeParse('lead').success, true)
    for (const role of ['devops', 'manager', 'bookkeeper', 'coder', 'inspector', 'browser', 'inquiry', 'distiller']) {
      assert.equal(hooks.tool.fork.args.calling.safeParse(role).success, false, role)
    }
    for (const calling of ['engineer', 'devops', 'coordinator', 'bookkeeper']) {
      assert.equal(hooks.tool.commission.args.calling.safeParse(calling).success, false, calling)
    }
  })
})
