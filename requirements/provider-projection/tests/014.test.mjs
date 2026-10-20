import assert from 'node:assert/strict'
import test from 'node:test'
import { parse as parseToml } from 'smol-toml'
import * as join from '../../../dist/Execution/Delegation/Fork/OpenCode/JoinSurface.js'
import * as warmStart from '../../../dist/Repository/Investigation/WarmStartSurface.js'

const assertInstructionsBeforeData = (text) => {
  let dataStarted = false
  for (const line of text.trimEnd().split('\n')) {
    if (!line.trim()) continue
    if (line.startsWith('#')) assert.equal(dataStarted, false, line)
    else dataStarted = true
  }
}

test('WHAT[provider-projection-014] actual mixed Join result keeps late responsibility instructions before physical data', () => {
  const text = join.renderBatch('en', [
    { kind: 'pty-exited', ptyId: 'private-pty', terminalLabel: 'test run', outcome: 'exit 7', code: '', message: '' },
    { kind: 'completed', agentId: 'private-agent', agentName: 'Ada', role: 'Engineer', runId: 'private-run', view: 'NEXT-RESPONSIBILITY' },
  ])
  assertInstructionsBeforeData(text)
  assert.match(text, /NEXT-RESPONSIBILITY/)
  assert.deepEqual(parseToml(text), { exit_code: 7 })
})

test('WHAT[provider-projection-014] actual warm-start appendix composes base instructions and reference data before rendering', () => {
  const text = warmStart.appendToProviderPrompt(['Hints are reference data.'], 'BASE-INSTRUCTION', [{ ordinal: 1, query: 'probe', hints: [{
    keywordOrdinal: 1, localRank: 1, filePath: 'fixture.js', startLine: 1, endLine: 1, content: 'HINT-DATA', score: 0.5, totalLines: 1,
  }] }])
  assertInstructionsBeforeData(text)
  assert.match(text, /BASE-INSTRUCTION/)
  const data = parseToml(text)
  assert.equal(data.repository_hint[0].content, 'HINT-DATA')
  assert.equal(data.repository_search[0].query, 'probe')
})

test.todo('WHAT[provider-projection-014] all physical payload call paths compose once; the tested Join and warm-start outputs do not prove every caller (GAP-082)')
