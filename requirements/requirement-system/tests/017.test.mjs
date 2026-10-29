import assert from 'node:assert/strict'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import { inspectRequirements } from './support/structure.mjs'

test('WHAT[requirement-system-017] the executable verifier scans the complete requirements tree', () => {
  const problems = inspectRequirements(fileURLToPath(new URL('../../..', import.meta.url)))
  assert.deepEqual(problems, [], problems.join('\n'))
})

test('WHAT[requirement-system-017] real discovery reads migrated SPEC clauses and refuses old-document fallback', () => {
  const root = mkdtempSync(join(tmpdir(), 'spec-migrated-tree-'))
  try {
    const pkg = join(root, 'requirements/verification-system')
    mkdirSync(join(pkg, 'tests'), { recursive: true })
    writeFileSync(join(root, 'requirements/INDEX.md'), '| `verification-system` | tests |\n')
    writeFileSync(join(pkg, 'WHY.md'), '# Reasons\n')
    writeFileSync(join(pkg, 'WHAT.md'), '# Navigation\n[Rules](AXIOMS.md)\n')
    writeFileSync(join(pkg, 'AXIOMS.md'), '## SPEC\n### [001] Current rule\n')
    writeFileSync(join(pkg, 'tests/001.test.mjs'), 'test("WHAT[verification-system-001] example", () => {})\n')
    assert.deepEqual(inspectRequirements(root), [])
    writeFileSync(join(pkg, 'WHAT.md'), '## [001] Old rule\n')
    assert.match(inspectRequirements(root).join('\n'), /非 AXIOMS 文件/)
    writeFileSync(join(pkg, 'WHAT.md'), '# Navigation\n')
    writeFileSync(join(pkg, 'AXIOMS.md'), '## SPEC\n')
    assert.match(inspectRequirements(root).join('\n'), /001.test.mjs: clause 001 is absent/)
    writeFileSync(join(pkg, 'WHAT.md'), '## [001] Old rule\n')
    rmSync(join(pkg, 'AXIOMS.md'))
    assert.deepEqual(inspectRequirements(root), ['verification-system/AXIOMS.md: expected file'])
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[requirement-system-017] a deleted clause reports its surviving test, including nested files', () => {
  const root = mkdtempSync(join(tmpdir(), 'spec-tree-'))
  try {
    const pkg = join(root, 'requirements/sample')
    mkdirSync(join(pkg, 'tests/nested'), { recursive: true })
    writeFileSync(join(root, 'requirements/INDEX.md'), '| `sample` | example |\n')
    writeFileSync(join(pkg, 'WHY.md'), '# Reasons\n')
    writeFileSync(join(pkg, 'WHAT.md'), '## [001] Rule\n')
    writeFileSync(join(pkg, 'tests/nested/001.test.mjs'), 'test("WHAT[sample-001] example", () => {})\n')
    assert.deepEqual(inspectRequirements(root), [])
    writeFileSync(join(pkg, 'WHAT.md'), '# No surviving clauses\n')
    const problems = inspectRequirements(root)
    assert.equal(problems.length, 1)
    assert.match(problems[0], /nested\/001.test.mjs: clause 001 is absent; notify the user/)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
