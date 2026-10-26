import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

// Since df7846ab8 the delegation verbs are programming-surface members, so the
// actual schema that accepts or rejects a calling is the `this.commission`
// member behind the real `todowrite-for-orchestrator` tool, not a host tool.
const commissionProgram = (payload) =>
  `class Js extends JsProgram { async run() { return await this.commission(${JSON.stringify(payload)}, []); } }`

integrationTest('WHAT[action-affordance-009] commission descriptions do not advertise the retired Coordinator / Lead choice rejected by the actual schema', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    assert.equal(hooks.tool.commission, undefined, 'commission is a programming-surface member, not a host tool')
    const sessionID = 'ses-affordance-009-orchestrator'
    await acceptAuthorityRoot(runtime, sessionID, 'orchestrator')
    const commission = async (calling) => String(await hooks.tool['todowrite-for-orchestrator'].execute(
      { program: commissionProgram({ calling, name: 'North Road', charge: 'x' }),
          name: `commission-${calling}`,
          intent: 'Observe this role-specific commission result before continuing.'
        },
      { sessionID, agent: 'orchestrator', callID: `call-009-${calling}`, messageID: `msg-009-${calling}` },
    ))
    const lead = await commission('lead')
    assert.match(lead, /^# ok/, lead)
    assert.doesNotMatch(lead, /code = "unknown-calling"/, 'lead is the legal commission calling')
    const coordinator = await commission('coordinator')
    assert.match(coordinator, /ok = false/, coordinator)
    assert.match(coordinator, /code = "unknown-calling"/, 'the retired coordinator choice is rejected')
    for (const locale of ['en', 'zh-CN']) {
      for (const leaf of ['commission/description', 'commission/arg-calling', 'js-program/base-class-commission']) {
        const text = readFileSync(new URL(`../../../resources/provider/tool/${leaf}/${locale}.md`, import.meta.url), 'utf8')
        assert.doesNotMatch(text, /Coordinator\s*\/\s*Lead/, `${locale}/${leaf}: obsolete selectable personas`)
      }
    }
  })
})

test.todo('WHAT[action-affordance-009] all calling choices explain their actual responsibilities without implying new permissions; one retired-choice regression is not semantic completeness (GAP-078)')
