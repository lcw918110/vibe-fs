import test from 'node:test'

// 语言锚定：断言比较英文指令资源（resources/provider/runtime/<name>/en.md 的渲染结果）；
// WANXIANGSHU_PROVIDER_LANGUAGE 是语言阶梯最高优先级，显式设为英文，使断言不随宿主环境语言漂移。
process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const relay = await import("../../../dist/Mission/Relay/Surface.js");

const gap = [{ acceptance_criteria: 'the target state is not yet reached', work_plan: 'close the remaining gap' }]


test('WHAT[relay-assessment-008] Road fold distinguishes repair and finish facts for subsequent instruction selection', () => {
  const opened = relay.openIncumbency(relay.empty(), 'road-1', 'inc-1', 'snapshot-1', 'authority-1')
  assert.equal(opened.ok, true)
  assert.equal(relay.view(opened.state, 'road-1').phase, 'AuditPending')
  assert.equal(relay.certificate(opened.state, 'road-1'), null)
  assert.equal(relay.retirement(opened.state, 'road-1'), null)

  const repaired = relay.assess(
    opened.state,
    'road-1',
    'inc-1',
    'assessment-repair',
    'snapshot-1',
    'authority-1',
    gap,
  )
  assert.equal(repaired.ok, true)
  assert.equal(relay.view(repaired.state, 'road-1').phase, 'WorkOwned')
  assert.equal(relay.certificate(repaired.state, 'road-1'), null)

  const continued = relay.retireContinue(repaired.state, 'road-1', 'inc-1', 'ret-repair', 'run-1', 'tool-1', 'snapshot-1')
  assert.equal(continued.ok, true)
  assert.equal(relay.retirement(continued.state, 'road-1').outcome, 'Continue')
  const next = relay.openIncumbency(continued.state, 'road-1', 'inc-2', 'snapshot-2', 'authority-1')
  assert.equal(next.ok, true)
  assert.equal(relay.view(next.state, 'road-1').phase, 'AuditPending')

  const finished = relay.assess(
    opened.state,
    'road-1',
    'inc-1',
    'assessment-finish',
    'snapshot-1',
    'authority-1',
    [],
  )
  assert.equal(finished.ok, true)
  assert.equal(relay.view(finished.state, 'road-1').phase, 'PerfectAwaitingRetirement')
  assert.equal(relay.certificate(finished.state, 'road-1').valid, true)

  const accepted = relay.retireAccepted(
    finished.state,
    'road-1',
    'inc-1',
    'ret-finish',
    'run-1',
    'tool-1',
    'certificate:assessment-finish',
    'snapshot-1',
  )
  assert.equal(accepted.ok, true)
  assert.equal(relay.retirement(accepted.state, 'road-1').outcome, 'Accepted')
  assert.equal(relay.view(accepted.state, 'road-1').activeIncumbency, null)

  // relay-assessment-005: a valid certificate no longer blocks a successor.
  const allowed = relay.openIncumbency(accepted.state, 'road-1', 'inc-2', 'snapshot-2', 'authority-1')
  assert.equal(allowed.ok, true)

  const invalidated = relay.invalidateCertificate(accepted.state, 'road-1', 'WorkspaceChanged')
  assert.equal(invalidated.ok, true)
  assert.equal(relay.certificate(invalidated.state, 'road-1').valid, false)

  const reopened = relay.openIncumbency(invalidated.state, 'road-1', 'inc-2', 'snapshot-2', 'authority-1')
  assert.equal(reopened.ok, true)
  assert.equal(relay.view(reopened.state, 'road-1').phase, 'AuditPending')
})
}

const {readFileSync} = await import('node:fs')
const {default: assert} = await import('node:assert/strict')
const {withReview, scores} = await import('./support/plugin.mjs')
const renderedInstruction = name => readFileSync(new URL(`../../../resources/provider/runtime/${name}/en.md`, import.meta.url), 'utf8').trim().split('\n').map(line => '# ' + line).join('\n')

for (const grade of ['REVISE', 'PERFECT', 'N/A']) {
  test(`WHAT[relay-assessment-008] actual ${grade} assessment selects exactly its current instruction resource`, async () => {
    await withReview(async ({execute}) => {
      const result = await execute(scores(grade))
      const selected = grade === 'REVISE' ? 'manager-work' : 'manager-finish'
      const other = grade === 'REVISE' ? 'manager-finish' : 'manager-work'
      assert.equal(result, renderedInstruction(selected) + '\n\nrecorded = true\n')
      assert.equal(result.includes(renderedInstruction(other)), false)
    })
  })
}

test('WHAT[relay-assessment-008] empty findings terminate the assessment with the finish instruction', async () => {
  await withReview(async ({execute}) => {
    const result = await execute({ findings: [] })
    assert.equal(result, renderedInstruction('manager-finish') + '\n\nrecorded = true\n')
  })
})

test('WHAT[relay-assessment-008] non-empty findings own repair work and select the work instruction', async () => {
  await withReview(async ({execute}) => {
    const result = await execute(scores('REVISE'))
    assert.equal(result, renderedInstruction('manager-work') + '\n\nrecorded = true\n')
  })
})

test('WHAT[relay-assessment-008] every pre-assessment role ledger and tool surface conceals later assignments and loop mechanics', {todo: 'GAP-193: actual accepted result selection is tested; all earlier surfaces require projection and semantic audit'})
