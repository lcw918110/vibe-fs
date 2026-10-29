import assert from 'node:assert/strict'
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import { withAblationFixture } from './support/ablation-fixture.mjs'
import { check } from '../../../scripts/checks/ablation-manifest.mjs'

const root = fileURLToPath(new URL('../../..', import.meta.url))
const nodes = JSON.parse(readFileSync(join(root, 'resources/ablation/nodes.json'), 'utf8')).nodes

test('WHAT[feature-ablation-001] manifest discovery uses migrated package rules without an old-file fallback', (t) => {
  const directory = mkdtempSync(join(tmpdir(), 'ablation-migrated-rules-'))
  t.after(() => rmSync(directory, { recursive: true, force: true }))
  const write = (path, content) => {
    const file = join(directory, path)
    mkdirSync(dirname(file), { recursive: true })
    writeFileSync(file, content)
  }
  write('requirements/INDEX.md', '| `verification-system` | tests |\n')
  write('requirements/verification-system/AXIOMS.md', '## SPEC\n### [001] Rule\n')
  write('resources/ablation/nodes.json', JSON.stringify({ nodes: [
    { id: 'verification-system', package: 'verification-system', kind: 'package', borrowed_surface: [] },
  ], edges: [] }))
  write('resources/ablation/tool-map.json', '{"tools":{}}')
  write('resources/ablation/fact-map.json', '{"facts":{"fact":"verification-system"}}')
  write('resources/ablation/profiles.json', '{"profiles":{"production":{"modes":{"verification-system":"active"}}}}')
  write('src/Wanxiangshu/OpenCode/Tools/StaticTools.fs', 'let knownToolNames = []\n')
  assert.deepEqual(check({ root: directory }).issues, [])
  rmSync(join(directory, 'requirements/verification-system/AXIOMS.md'))
  write('requirements/verification-system/WHAT.md', '## [001] Obsolete rule\n')
  assert.deepEqual(check({ root: directory }).issues.map(({ code }) => code), ['node-orphan'])
})

test('WHAT[feature-ablation-001] every package has exactly one same-name primary node', () => {
  const packages = readdirSync(join(root, 'requirements'), { withFileTypes: true })
    .filter((entry) => entry.isDirectory() && entry.name !== 'proposals')
    .map((entry) => entry.name).sort()
  const primary = nodes.filter((node) => node.kind === 'package')
  assert.deepEqual(primary.map((node) => node.id).sort(), packages)
  for (const node of primary) assert.equal(node.package, node.id)
  for (const node of nodes.filter((entry) => entry.kind === 'slice')) {
    const parent = primary.find((entry) => entry.id === node.parent)
    assert.ok(parent, `missing parent of ${node.id}`)
    assert.equal(node.package, parent.package)
  }
})

test('WHAT[feature-ablation-001] the real loader accepts a valid registry in an isolated package', async () => {
  await withAblationFixture(() => {}, (surface) => {
    assert.equal(surface.load().ok, true)
    assert.deepEqual(surface.manifestNodeIds().sort(), nodes.map((node) => node.id).sort())
  })
})

test('WHAT[feature-ablation-001] the real loader rejects missing and duplicate primary nodes', async () => {
  for (const mutate of [
    (doc) => { doc.nodes.find((node) => node.id === 'feature-ablation').kind = 'custom' },
    (doc) => { doc.nodes.push({ ...doc.nodes.find((node) => node.id === 'feature-ablation'), id: 'extra-primary' }) },
  ]) {
    await withAblationFixture(mutate, (surface) => {
      const result = surface.load()
      assert.equal(result.ok, false)
      assert.equal(result.kind, 'InvalidManifest')
      assert.deepEqual(surface.manifestNodeIds(), [])
    })
  }
})

test('WHAT[feature-ablation-001] child nodes must name an existing parent in their own package', async () => {
  for (const parent of [undefined, 'missing-parent', 'requirement-system']) {
    await withAblationFixture((doc) => {
      doc.nodes.push({ id: 'feature-ablation.example', package: 'feature-ablation', station: 0,
        kind: 'slice', parent, borrowed_surface: [] })
    }, (surface) => {
      const result = surface.load()
      assert.equal(result.ok, false)
      assert.equal(result.kind, 'InvalidManifest')
    })
  }
})
