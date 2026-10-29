import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { join } from 'node:path'
import test from 'node:test'
import { run as runBoundaryGate } from '../../../scripts/checks/js-boundary-gate.mjs'
import { scanAll, SURFACE_MANIFEST } from '../../../scripts/lib/test-surface-scan.mjs'
import { createWorkspaceFixture } from './support/workspace-fixture.mjs'

const importSource = (module) => `import * as api from '../../../../dist/${module}'\n`
const hits = (root) => Object.values(scanAll(join(root, 'requirements'))).flat()

test('WHAT[js-semantic-surface-002] the current semantic zone passes the implemented boundary gate', () => {
  assert.equal(runBoundaryGate(), 0)
})

test('WHAT[js-semantic-surface-002] the same gate accepts a registered public import and rejects an internal module', (t) => {
  const { root, write } = createWorkspaceFixture(t)
  const file = 'requirements/consumer/tests/support/api.mjs'
  write(file, importSource(SURFACE_MANIFEST[0].module))
  assert.equal(runBoundaryGate({ root }), 0)
  write(file, importSource('Internal/Unregistered.js'))
  assert.equal(runBoundaryGate({ root }), 1)
  assert.ok(hits(root).some((hit) => hit.rule === 'deep-dist-import'))
})

test('WHAT[js-semantic-surface-002] raw Host canaries do not exempt neighboring semantic tests or helpers', t => {
  const { root, write } = createWorkspaceFixture(t)
  const gate = new URL('../../../scripts/checks/js-boundary-gate.mjs', import.meta.url).href
  const check = () => spawnSync(process.execPath, ['--input-type=module', '-e',
    `const { run } = await import(${JSON.stringify(gate)}); process.exitCode = run()`],
  { cwd: root, encoding: 'utf8' })
  const rawImport = importSource('OpenCode/Plugin/PluginTransforms.js')
  write('requirements/delegation/tests/037.test.mjs', rawImport)
  write('requirements/work/tests/host/008.test.mjs', rawImport)
  write('requirements/managed-session-lifecycle/tests/029.test.mjs', rawImport)
  assert.equal(check().status, 0)
  write('requirements/delegation/tests/038.test.mjs', rawImport)
  write('requirements/work/tests/host/support.mjs', rawImport)
  write('requirements/managed-session-lifecycle/tests/support/029.mjs', rawImport)
  const rejected = check()
  assert.equal(rejected.status, 1)
  assert.match(rejected.stderr, /3 violation\(s\) across 3 file\(s\)/)
})

test('WHAT[js-semantic-surface-002] module discovery, generated-name probing and runtime representation access are detected', (t) => {
  const { root, write } = createWorkspaceFixture(t)
  const source = [
    importSource(SURFACE_MANIFEST[0].module),
    ['Object.', 'entries(api)'].join(''),
    `const prefix = key => key.startsWith('${['Foo', '__'].join('')}')`,
    `const suffix = key => key.endsWith('${['_', 'Bar'].join('')}')`,
    ['const payload = value => value.', 'fields[0]'].join(''),
    ['import { list } from "../../../dist/fable', '_modules/List.js"'].join(''),
  ].join('\n')
  write('requirements/probe/tests/001.test.mjs', source)
  const rules = hits(root).map((hit) => hit.rule)
  for (const rule of ['export-discovery', 'mangled-lookup', 'du-shape', 'fable-modules']) {
    assert.ok(rules.includes(rule), `missing detection: ${rule}`)
  }
  assert.equal(rules.filter((rule) => rule === 'mangled-lookup').length, 2)
})

test('WHAT[js-semantic-surface-002] a template that chooses an unregistered compiled module is detected', (t) => {
  const { root, write } = createWorkspaceFixture(t)
  const source = ['const load = modulePath => import(new URL(`../../../', 'dist',
    '/${modulePath}.js`, import.meta.url))'].join('')
  write('requirements/probe/tests/001.test.mjs', source)
  assert.ok(hits(root).some((hit) => hit.rule === 'template-dist-import'))
})

test('WHAT[js-semantic-surface-002] ordinary data enumeration and standard function binding remain legal', (t) => {
  const { root, write } = createWorkspaceFixture(t)
  write('requirements/probe/tests/001.test.mjs', [
    'const data = { value: 1 }',
    'const entries = Object.entries(data)',
    'const call = (() => entries).bind(null)',
    'export { call }',
  ].join('\n'))
  assert.deepEqual(hits(root), [])
})

test('WHAT[js-semantic-surface-002] native fixture fields and standard object spread are not runtime representation access', async t => {
  const nativeCases = [
    ['plain DTO', ['const data = { fields: { attempt: 2 } }', ['export const value = data.', 'fields'].join('')]],
    ['object spread', ['const fields = { attempt: 2 }', 'export const value = { ...fields }']],
    ['static fixture loop', [
      'const scenarios = [{ name: "first", fields: { attempt: 2 } }, { name: "second", fields: { workId: "other" } }]',
      'for (const scenario of scenarios) {',
      ['  const read = async () => ({ ...scenario.', 'fields })'].join(''),
      '  await read()',
      '}',
    ]],
  ]
  for (const [name, source] of nativeCases) {
    await t.test('WHAT[js-semantic-surface-002] accepts ' + name + ' through the actual gate', t => {
      const { root, write } = createWorkspaceFixture(t)
      write('requirements/consumer/tests/001.test.mjs', source.join('\n'))
      assert.deepEqual(hits(root), [])
      assert.equal(runBoundaryGate({ root }), 0)
    })
  }
})

test('WHAT[js-semantic-surface-002] imported runtime and unproved fields receivers remain forbidden', async t => {
  const fieldRead = receiver => ['export const value = ', receiver, '.', 'fields'].join('')
  const runtimeCases = [
    ['imported namespace', [importSource(SURFACE_MANIFEST[0].module), fieldRead('api')]],
    ['imported result', [importSource(SURFACE_MANIFEST[0].module), 'const data = api.read()', fieldRead('data')]],
    ['unknown parameter', [['export const read = value => value.', 'fields'].join('')]],
    ['shadowed native name', ['const data = { fields: [] }', ['export function read(data) { return data.', 'fields }'].join('')]],
    ['mixed fixture array', [
      importSource(SURFACE_MANIFEST[0].module),
      'const scenarios = [{ fields: [] }, api.read()]',
      ['for (const scenario of scenarios) console.log(scenario.', 'fields)'].join(''),
    ]],
    ['mutated fixture array', [
      importSource(SURFACE_MANIFEST[0].module),
      'const scenarios = [{ fields: [] }]',
      'scenarios.push(api.read())',
      ['for (const scenario of scenarios) console.log(scenario.', 'fields)'].join(''),
    ]],
    ['aliased runtime insertion', [
      importSource(SURFACE_MANIFEST[0].module),
      'const scenarios = [{ fields: [] }]',
      'const alias = scenarios',
      'alias[0] = api.read()',
      ['for (const scenario of scenarios) console.log(scenario.', 'fields)'].join(''),
    ]],
    ['assigned alias runtime insertion', [
      importSource(SURFACE_MANIFEST[0].module),
      'const scenarios = [{ fields: [] }]',
      'let alias',
      'alias = scenarios',
      'alias[0] = api.read()',
      ['for (const scenario of scenarios) console.log(scenario.', 'fields)'].join(''),
    ]],
    ['implicit return alias runtime insertion', [
      importSource(SURFACE_MANIFEST[0].module),
      'const scenarios = [{ fields: [] }]',
      'const getScenarios = () => scenarios',
      'const alias = getScenarios()',
      'alias[0] = api.read()',
      ['for (const scenario of scenarios) console.log(scenario.', 'fields)'].join(''),
    ]],
    ['escaped fixture container', [
      importSource(SURFACE_MANIFEST[0].module),
      'const scenarios = [{ fields: [] }]',
      'api.replaceWithRuntimeValue(scenarios)',
      ['for (const scenario of scenarios) console.log(scenario.', 'fields)'].join(''),
    ]],
    ['computed runtime access', [
      importSource(SURFACE_MANIFEST[0].module),
      'const data = api.read()',
      'export const value = data["fields"]',
    ]],
  ]
  for (const [name, source] of runtimeCases) {
    await t.test('WHAT[js-semantic-surface-002] rejects ' + name + ' at the representation boundary', t => {
      const { root, write } = createWorkspaceFixture(t)
      write('requirements/consumer/tests/001.test.mjs', source.join('\n'))
      const found = hits(root)
      assert.deepEqual({
        duShapeCount: found.filter(hit => hit.rule === 'du-shape').length,
        exitCode: runBoundaryGate({ root }),
      }, { duShapeCount: 1, exitCode: 1 })
    })
  }
})
