import assert from 'node:assert/strict'
import { rmSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import { run, validateSurfaceManifest } from '../../../scripts/checks/js-surface-manifest.mjs'
import { SURFACE_MANIFEST } from '../../../scripts/lib/test-surface-scan.mjs'
import { createManifestFixture } from './support/manifest-fixture.mjs'

const ROOT = fileURLToPath(new URL('../../..', import.meta.url))

test('WHAT[js-semantic-surface-003] current registrations have real owners, compiled sources, outputs and imports', () => {
  assert.ok(SURFACE_MANIFEST.length > 0)
  assert.deepEqual(validateSurfaceManifest(SURFACE_MANIFEST, ROOT), [])
})

test('WHAT[js-semantic-surface-003] registration is static evidence and does not require consumer authorization or callback execution', (t) => {
  const fixture = createManifestFixture(t)
  const { root, entry, write, testFile, importPath } = fixture
  assert.deepEqual(validateSurfaceManifest([entry], root), [])
  write(testFile, `const unused = () => import(${JSON.stringify(importPath)})\n`)
  assert.deepEqual(validateSurfaceManifest([entry], root), [])
  write(testFile, `const prose = ${JSON.stringify('import ' + JSON.stringify(importPath))}\n`)
  assert.match(validateSurfaceManifest([entry], root).join('\n'), /no .test.mjs imports/)
})

test('WHAT[js-semantic-surface-003] migrated rule owners and law owners resolve through AXIOMS without WHAT fallback', (t) => {
  const { root, entry, write } = createManifestFixture(t)
  const migrated = { ...entry, owner: 'verification-system', laws: ['verification-system-001'] }
  write('requirements/verification-system/AXIOMS.md', '## SPEC\n### [001] Current rule\n')
  assert.deepEqual(validateSurfaceManifest([migrated], root), [])
  const borrowedLaw = { ...entry, laws: ['verification-system-001'],
    lawOwners: { 'verification-system-001': 'verification-system' } }
  assert.deepEqual(validateSurfaceManifest([borrowedLaw], root), [])
  write('requirements/verification-system/WHAT.md', '## [001] Obsolete rule\n')
  write('requirements/verification-system/AXIOMS.md', '## AXIOM\n### [001] Outside SPEC\n## SPEC\n')
  assert.match(validateSurfaceManifest([migrated], root).join('\n'), /law verification-system-001 is absent.*AXIOMS/)
  write('requirements/verification-system/AXIOMS.md', '## SPEC\n### [002] Different rule\n')
  assert.match(validateSurfaceManifest([migrated], root).join('\n'), /law verification-system-001 is absent.*AXIOMS/)
  rmSync(join(root, 'requirements/verification-system/AXIOMS.md'))
  assert.match(validateSurfaceManifest([migrated], root).join('\n'), /missing owner.*AXIOMS/)
  assert.match(validateSurfaceManifest([borrowedLaw], root).join('\n'), /law verification-system-001 owner.*missing.*AXIOMS/)
})

test('WHAT[js-semantic-surface-003] a body reference cannot resurrect a retired law', (t) => {
  const { root, entry, write } = createManifestFixture(t)
  write('requirements/owner/WHAT.md', '# owner\n\n## [002] Another law\nSee WHAT[OWNER-001].\n')
  assert.match(validateSurfaceManifest([{ ...entry, laws: ['OWNER-001'] }], root).join('\n'), /law OWNER-001 is absent/)
})

test('WHAT[js-semantic-surface-003] a misleading dist suffix is not an import of the registered module', (t) => {
  const { root, entry, write, testFile } = createManifestFixture(t)
  const unrelated = './missing/' + 'dist/' + entry.module
  write(testFile, `import { value } from ${JSON.stringify(unrelated)}\n`)
  assert.match(validateSurfaceManifest([entry], root).join('\n'), /no .test.mjs imports/)
})

test('WHAT[js-semantic-surface-003] malformed metadata and duplicate modules fail the same registration gate', (t) => {
  const { root, entry } = createManifestFixture(t)
  for (const [field, invalid, expected] of [
    ['owner', 'missing-owner', /missing owner/],
    ['source', 'src/Wanxiangshu/Missing.fs', /missing production source/],
    ['representation', 'runtime-internals', /invalid representation/],
    ['kind', 'unclassified', /invalid kind/],
    ['laws', [], /non-empty list/],
    ['laws', ['owner-099'], /law owner-099 is absent/],
  ]) assert.match(validateSurfaceManifest([{ ...entry, [field]: invalid }], root).join('\n'), expected, field)
  assert.match(validateSurfaceManifest([entry, entry], root).join('\n'), /duplicate manifest module/)
  assert.deepEqual(validateSurfaceManifest([entry], root), [])
  assert.equal(run({ root, manifest: [{ ...entry, laws: ['owner-099'] }] }), 1)
  assert.equal(run({ root, manifest: [entry] }), 0)
})

test('WHAT[js-semantic-surface-003] uncompiled and un-emitted sources cannot satisfy registration', (t) => {
  const { root, entry, write } = createManifestFixture(t)
  write('src/Wanxiangshu/Wanxiangshu.Owner.owner.fsproj', '<Project/>')
  assert.match(validateSurfaceManifest([entry], root).join('\n'), /not compiled/)
  write('src/Wanxiangshu/Wanxiangshu.Owner.owner.fsproj', '<Project><ItemGroup><Compile Include="Owner/Surface.fs"/></ItemGroup></Project>')
  rmSync(join(root, 'dist', entry.module))
  assert.match(validateSurfaceManifest([entry], root).join('\n'), /missing emitted surface/)
})

test('WHAT[js-semantic-surface-003] explicit law ownership resolves against that owner and its current headings', (t) => {
  const { root, entry, write } = createManifestFixture(t)
  const delegated = { ...entry, laws: ['other-007'], lawOwners: { 'other-007': 'other' } }
  assert.deepEqual(validateSurfaceManifest([delegated], root), [
    'Owner/Surface.js: law other-007 owner rule document is missing (requirements/other/WHAT.md)',
  ])
  write('requirements/other/WHAT.md', '# other\n\n## [007] Contract\n')
  assert.deepEqual(validateSurfaceManifest([delegated], root), [])
  write('requirements/other/WHAT.md', '# other\n\n## [008] Changed contract\n')
  assert.match(validateSurfaceManifest([delegated], root).join('\n'), /law other-007 is absent/)
})
