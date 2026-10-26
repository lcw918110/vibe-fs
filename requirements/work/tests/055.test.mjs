import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'

const source = (path) => readFileSync(new URL(path, import.meta.url), 'utf8')

const probeCalls = [
  '.encodeOpening(',
  '.seedOpen(',
  '.seedOpenAs(',
  '.readProbe(',
  '.reviewProbe(',
  '.deliverProbe(',
  '.seedReview(',
  '.seedReviewAs(',
  '.seedDeliver(',
  '.documentView(',
  '.finalPromotionProbe(',
  '.reopenProbe(',
  '.deliverStateProbe(',
  '.replayProbe(',
  '.replayStateProbe(',
  '.detachedOpeningProbe(',
  '.commitProbe(',
  '.decodeDocument(',
]

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

  const root = fileURLToPath(new URL('.', import.meta.url))
  const files = []
  const walk = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const path = join(dir, entry.name)
      if (entry.isDirectory()) walk(path)
      else if (entry.name.endsWith('.mjs') && entry.name !== '055.test.mjs') files.push(path)
    }
  }
  walk(root)
  for (const path of files) {
    const text = readFileSync(path, 'utf8')
    for (const probe of probeCalls) {
      assert.equal(text.includes(probe), false, `${path} still calls ${probe}`)
    }
  }
})
