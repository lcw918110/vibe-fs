import assert from 'node:assert/strict'
import { readFileSync, readdirSync, statSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import { duplicateClauseDefinitions, isProposalPath } from '../../../scripts/lib/spec-rules.mjs'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..')
const REQUIREMENTS = join(ROOT, 'requirements')
const INDEX_FILE = join(ROOT, 'requirements/INDEX.md')

const read = (path) => readFileSync(path, 'utf8')

const packageNamesFromIndexTables = () => {
  const text = read(INDEX_FILE)
  return [...new Set([...text.matchAll(/^\| `([a-z][a-z0-9-]*)` \|/gm)].map((match) => match[1]))]
}

test('WHAT[requirement-system-001] declared clause IDs have one owner in the indexed packages', () => {
  const fromIndex = packageNamesFromIndexTables()
  const dirs = readdirSync(REQUIREMENTS)
    .filter((entry) => !isProposalPath(entry) && statSync(join(REQUIREMENTS, entry)).isDirectory())
    .sort()

  const unknown = dirs.filter((dir) => !fromIndex.includes(dir))
  assert.deepEqual(unknown, [], `requirements/ must not contain INDEX-external package dirs: ${unknown.join(', ')}`)

  const entries = []
  for (const pkg of fromIndex) {
    for (const name of ['WHAT.md', 'AXIOMS.md']) {
      const file = join(REQUIREMENTS, pkg, name)
      try {
        entries.push({ file, pkg, text: read(file) })
      } catch {
        // Missing rule documents are governed by [006]/[017].
      }
    }
  }

  const duplicates = duplicateClauseDefinitions(entries)
  assert.deepEqual(duplicates, [], `clause definitions must be globally unique:\n${duplicates.map((d) => d.msg).join('\n')}`)
})

test('WHAT[requirement-system-001] repeated definitions are rejected while package-local numbers remain distinct', () => {
  const first = { file: 'requirements/alpha/WHAT.md', pkg: 'alpha', text: '## [001] First\n' }
  const other = { file: 'requirements/beta/WHAT.md', pkg: 'beta', text: '## [001] Other\n' }
  assert.deepEqual(duplicateClauseDefinitions([first, other]), [])
  const findings = duplicateClauseDefinitions([{ ...first, text: first.text + '## [001] Duplicate\n' }, other])
  assert.equal(findings.length, 1)
  assert.match(findings[0].msg, /alpha-001/)
})
