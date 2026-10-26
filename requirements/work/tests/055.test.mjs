import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const source = (path) => readFileSync(new URL(path, import.meta.url), 'utf8')

test('WHAT[work-055] the work support module no longer answers with an S sequence or Promoted', () => {
  const contract = source('./support/contract.mjs')
  assert.equal(/export function (confirmationCount|openWork|deliverOnce|deliverTwice|projectSuccessor|reviewFromFindings|admitAction)/.test(contract), false)
  assert.equal(contract.includes('S1,S2,S3'), false)
  assert.equal(contract.includes('Promoted'), false)
  for (const path of ['./002.test.mjs', './004.test.mjs', './006.test.mjs', './008.test.mjs', './041.test.mjs', './045.test.mjs']) {
    const text = source(path)
    assert.equal(text.includes('projectSuccessor'), false, path)
    assert.equal(text.includes('retainedPriorAssistant'), false, path)
    assert.equal(text.includes("reviewOutcome: 'Promoted'"), false, path)
  }
})
