import assert from 'node:assert/strict'
import test from 'node:test'
import { parse as parseToml } from 'smol-toml'
import * as join from '../../../dist/Execution/Delegation/Fork/OpenCode/JoinSurface.js'
import { scanText } from '../../../scripts/checks/provider-leak-gate.mjs'

const completed = (agentName, view = '') => ({
  kind: 'completed', agentId: 'private-agent', agentName, role: 'Engineer', runId: 'private-run', view,
})

test('WHAT[participant-horizon-003] actual Join renderer distinguishes return, failure and abandonment without control DTO fields', () => {
  for (const name of ['Ada', 'engineer', 'weird raw name']) {
    const text = join.renderBatch('en', [completed(name, 'Summary\nRecent work')])
    assert.ok(text.includes(`${name} has returned.`))
    assert.ok(text.includes('Summary'))
    assert.ok(text.includes('Recent work'))
    assert.deepEqual(parseToml(text), {})
    assert.ok(!text.includes('private-agent'))
    assert.ok(!text.includes('private-run'))
  }
  const failed = join.renderBatch('en', [{
    kind: 'failed', agentId: 'private-agent', agentName: 'Ada', role: 'Engineer', runId: 'private-run', code: 'PRIVATE-CODE', message: 'Could not open input.',
  }])
  assert.match(failed, /could not complete the charge/)
  assert.match(failed, /Could not open input/)
  assert.deepEqual(parseToml(failed), {})
  assert.ok(!failed.includes('PRIVATE-CODE'))
  const abandoned = join.renderBatch('en', [{ kind: 'abandoned', agentId: 'private-agent', agentName: 'Ada', reason: 'parent cancellation' }])
  assert.match(abandoned, /Ada did not return from this charge/)
  assert.deepEqual(parseToml(abandoned), {})
})

test('WHAT[participant-horizon-003] actual wait and failure outcomes are natural-language consequences', () => {
  for (const [reason, message] of [
    ['OperatorAbort', /Your waiting was interrupted/],
    ['UserMessageArrived', /Something nearer has arrived/],
    ['DeadlineExpired', /No return reached you before your waiting ended/],
  ]) {
    const text = join.renderInterrupted('en', reason)
    assert.match(text, message)
    assert.deepEqual(parseToml(text), {})
  }
  for (const [error, message] of [
    ['Empty', /nothing away to receive/], ['NothingToJoin', /nothing away to receive/],
    ['Cancelled', /wait was cancelled/], ['JoinInProgress', /already in progress/],
    ['Abandoned', /did not return/], ['NotFound', /No one by that name/],
    ['TimedOut', /waiting ended/], ['TerminalMaterializationFailed', /return could not be gathered/],
  ]) {
    const text = join.renderForkError('en', error)
    assert.match(text, message)
    assert.deepEqual(parseToml(text), {})
  }
})

test('WHAT[participant-horizon-003] mixed results preserve their input order and physical output stays data', () => {
  const text = join.renderBatch('en', [
    { kind: 'failed', agentId: 'private-a', agentName: 'Ada', role: 'Engineer', runId: 'run-a', code: 'E', message: 'FAILED-FIRST' },
    { kind: 'pty-aborted', ptyId: 'private-terminal', terminalLabel: 'Terminal', outcome: 'interrupted', code: 'C', message: 'status = "business data"' },
    completed('Bob', 'RETURNED-LAST'),
  ])
  assert.ok(text.indexOf('FAILED-FIRST') < text.indexOf('Terminal was interrupted'))
  assert.ok(text.indexOf('Terminal was interrupted') < text.indexOf('RETURNED-LAST'))
  assert.equal(parseToml(text).output, 'status = "business data"')
  assert.equal(parseToml(text).status, undefined)
  assert.ok(!text.includes('private-terminal'))
})

test('WHAT[participant-horizon-003] current source checker accepts prose and rejects its generic-state DTO fixture', () => {
  assert.deepEqual(scanText('JoinResultRenderer.fs', 'let text = "Your waiting ended."'), [])
  assert.ok(scanText('JoinResultRenderer.fs', 'field "status" (str "interrupted")').some((hit) => hit.id === 'field-status'))
})
