import assert from 'node:assert/strict'
import test from 'node:test'

import { plan } from '../../../dist/Execution/Delegation/Advisor/Surface.js'

// advisor-[002]: the view is the caller's own context, rendered by SyntheticToml: the authored
// `# ` comment header, one blank line, then one `[[new_work_to_record]]` table per record.
// The declaration says how far the view may reach (`prefixUntil`, exclusive) and which anchors
// arrive as summaries (`thenRemove`).
const message = (anchor, text) => ({ anchor, text })

const request = (messages, { prefixUntil, thenRemove = [] } = {}) => ({
  messages,
  declaration: { prefixUntil, thenRemove },
})

// The authored header prose itself names `[[new_work_to_record]]`, so every table count below is
// scoped to the data body — the part after the header's blank line.
const viewBody = (prompt) => {
  const boundary = prompt.indexOf('\n\n')
  return boundary === -1 ? '' : prompt.slice(boundary + 2)
}

const tableCount = (prompt) => (viewBody(prompt).match(/\[\[new_work_to_record\]\]/g) ?? []).length

test('WHAT[advisor-002] the view prompt keeps the prefix under PrefixUntil in the advisor TOML shape', () => {
  const planned = plan(
    'en',
    [],
    request(
      [
        message(1, 'anchor-one-material'),
        message(2, 'anchor-two-material'),
        message(3, 'anchor-three-material'),
      ],
      { prefixUntil: 3 },
    ),
  )

  assert.equal(planned.hasNewMaterial, true)
  assert.deepEqual(planned.deliveredAnchors, [1, 2])

  const prompt = planned.viewPrompt
  const boundary = prompt.indexOf('\n\n')
  assert.equal(prompt.startsWith('# '), true, 'the authored header opens the view as a comment block')
  assert.equal(
    prompt.slice(0, boundary).split('\n').every((line) => line.startsWith('#')),
    true,
    'every header line is a `# ` comment line',
  )
  assert.equal(boundary > 0, true, 'the header block is separated from the tables by one blank line')
  assert.equal(
    viewBody(prompt).startsWith('[[new_work_to_record]]'),
    true,
    'the data body opens with the first record table',
  )
  assert.equal(tableCount(prompt), 2, 'one table per kept record')
  assert.equal(prompt.includes('record = "anchor-one-material"'), true)
  assert.equal(prompt.includes('record = "anchor-two-material"'), true)
  assert.equal(prompt.includes('anchor-three-material'), false, 'an anchor at PrefixUntil is outside the view')
  assert.equal(
    prompt.indexOf('anchor-one-material') < prompt.indexOf('anchor-two-material'),
    true,
    'records keep the caller anchor order',
  )
  assert.equal(prompt.endsWith('\n'), true)
})

test('WHAT[advisor-002] a ThenRemove pair substitutes that anchor text with its summary', () => {
  const planned = plan(
    'en',
    [],
    request([message(1, 'anchor-one-material'), message(2, 'anchor-two-material')], {
      prefixUntil: 3,
      thenRemove: [[2, 'anchor-two-summary']],
    }),
  )

  assert.deepEqual(planned.deliveredAnchors, [1, 2])
  assert.equal(planned.viewPrompt.includes('record = "anchor-two-summary"'), true)
  assert.equal(
    planned.viewPrompt.includes('anchor-two-material'),
    false,
    'the removed anchor arrives as its summary, not as its text',
  )
  assert.equal(planned.viewPrompt.includes('record = "anchor-one-material"'), true)
  assert.equal(tableCount(planned.viewPrompt), 2)
})

test('WHAT[advisor-002] an empty delta reports HasNewMaterial false and emits no record', () => {
  const delivered = [1, 2]
  const empty = plan(
    'en',
    delivered,
    request([message(1, 'anchor-one-material'), message(2, 'anchor-two-material')], { prefixUntil: 2 }),
  )

  assert.equal(empty.hasNewMaterial, false, 'nothing new was handed over')
  assert.deepEqual(empty.deliveredAnchors, [1, 2], 'a delivered anchor is never dropped')
  assert.equal(viewBody(empty.viewPrompt), '', 'no record is emitted for an empty delta')
  assert.equal(tableCount(empty.viewPrompt), 0)
  assert.equal(empty.viewPrompt.startsWith('# '), true, 'the view prompt keeps its header')

  const noMaterial = plan('en', [], request([], { prefixUntil: 5 }))
  assert.equal(noMaterial.hasNewMaterial, false)
  assert.deepEqual(noMaterial.deliveredAnchors, [])
  assert.equal(tableCount(noMaterial.viewPrompt), 0)
})
