import assert from 'node:assert/strict'
import test from 'node:test'
import * as forkTool from '../../../dist/Execution/Delegation/Fork/OpenCode/ToolSurface.js'
import { withForkRuntime } from './support/fork-runtime.mjs'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

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

// Since df7846ab8 fork and commission are programming-surface members: the
// actual schema is the member call behind the real role surfaces, and the
// Host registry no longer carries either verb.
const memberProgram = (verb, payload) =>
  `class Js extends JsProgram { async run() { return await this.${verb}(${JSON.stringify(payload)}, []); } }`

integrationTest('WHAT[participant-horizon-010] actual fork and commission schemas expose only their legal new-participant identities', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    assert.equal(hooks.tool.fork, undefined, 'fork is a programming-surface member, not a host tool')
    assert.equal(hooks.tool.commission, undefined, 'commission is a programming-surface member, not a host tool')
    const manager = 'ses-horizon-010-manager'
    const orchestrator = 'ses-horizon-010-orchestrator'
    await acceptAuthorityRoot(runtime, manager, 'manager')
    await acceptAuthorityRoot(runtime, orchestrator, 'orchestrator')
    let call = 0
    const run = async (tool, sessionID, agent, verb, payload) => String(await hooks.tool[tool].execute(
      { program: memberProgram(verb, payload),
          name: `horizon-010-${call + 1}`,
          intent: 'Observe this delegation receipt before reading the participant horizon.'
        },
      { sessionID, agent, callID: `call-010-${++call}`, messageID: `msg-010-${call}` },
    ))
    const fork = (calling, name) => run('todowrite-for-manager', manager, 'manager', 'fork', { calling, name, charge: 'x' })
    const commission = (calling, name) => run('todowrite-for-orchestrator', orchestrator, 'orchestrator', 'commission', { calling, name, charge: 'x' })
    const refusedCode = (text) => text.match(/code = "([^"]+)"/)?.[1]
    for (const role of ['devops', 'manager', 'bookkeeper', 'coder', 'inspector', 'browser', 'inquiry', 'distiller']) {
      const text = await fork(role, `Road ${role}`)
      assert.match(text, /ok = false/, `fork rejects ${role}: ${text}`)
      assert.ok(['calling-conflict', 'unknown-calling'].includes(refusedCode(text)), `fork rejects ${role} by calling: ${text}`)
    }
    const engineer = await fork('engineer', 'Ada')
    assert.match(engineer, /ok = true/, `fork accepts engineer: ${engineer}`)
    const lead = await commission('lead', 'North Road')
    assert.notEqual(refusedCode(lead), 'unknown-calling', `commission accepts lead: ${lead}`)
    for (const calling of ['engineer', 'devops', 'coordinator', 'bookkeeper']) {
      const text = await commission(calling, `Road ${calling}`)
      assert.equal(refusedCode(text), 'unknown-calling', `commission rejects ${calling}: ${text}`)
    }
  })
})
