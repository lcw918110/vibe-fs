import assert from 'node:assert/strict'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { packageProblems } from './support/structure.mjs'

test('WHAT[requirement-system-006] package completeness accepts the two documents and a tests directory', () => {
  const directory = mkdtempSync(join(tmpdir(), 'spec-package-'))
  try {
    writeFileSync(join(directory, 'WHY.md'), '# Reasons\n')
    writeFileSync(join(directory, 'WHAT.md'), '# Rules\n')
    mkdirSync(join(directory, 'tests'))
    assert.deepEqual(packageProblems(directory), [])
    for (const name of ['WHY.md', 'WHAT.md', 'tests']) {
      const path = join(directory, name)
      rmSync(path, { recursive: true })
      assert.equal(packageProblems(directory).length, 1, `missing ${name}`)
      if (name === 'tests') writeFileSync(path, 'not a directory')
      else mkdirSync(path)
      assert.equal(packageProblems(directory).length, 1, `wrong type: ${name}`)
      rmSync(path, { recursive: true })
      if (name === 'tests') mkdirSync(path)
      else writeFileSync(path, '# Restored\n')
    }
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[requirement-system-006] migrated package requires AXIOMS even when the old WHAT file exists', () => {
  const root = mkdtempSync(join(tmpdir(), 'spec-migrated-'))
  try {
    const directory = join(root, 'verification-system')
    mkdirSync(join(directory, 'tests'), { recursive: true })
    writeFileSync(join(directory, 'WHY.md'), '# Reasons\n')
    writeFileSync(join(directory, 'AXIOMS.md'), '## SPEC\n### [001] Rule\n')
    assert.deepEqual(packageProblems(directory), [])
    writeFileSync(join(directory, 'WHAT.md'), '## [001] Obsolete rule\n')
    rmSync(join(directory, 'AXIOMS.md'))
    assert.deepEqual(packageProblems(directory), ['AXIOMS.md: expected file'])
    mkdirSync(join(directory, 'AXIOMS.md'))
    assert.deepEqual(packageProblems(directory), ['AXIOMS.md: expected file'])
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
