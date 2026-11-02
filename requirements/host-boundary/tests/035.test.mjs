// WHAT[host-boundary-035]: the omp entry module holds no product decision —
// every forbidden class (prose, product import, decision control flow, mutable state,
// decision vocabulary, product resource lookup, runtime product lookup) is rejected on a
// planted violation, and the real entry module and its interface are accepted.
import test from 'node:test'

{
const { default: assert } = await import('node:assert/strict')
const { mkdtempSync, mkdirSync, writeFileSync, rmSync, readFileSync } = await import('node:fs')
const { tmpdir } = await import('node:os')
const { join, dirname } = await import('node:path')
const { fileURLToPath } = await import('node:url')

const ROOT = dirname(fileURLToPath(import.meta.url))
const REPO = join(ROOT, '../../..')
const GATE = join(REPO, 'scripts/checks/omp-entry-decision-gate.mjs')
const {
  analyzeEntryModule,
  locateEntryModule,
  ENTRY_FOLDER,
} = await import(GATE)

// One planted violation per forbidden class. The gate keeps no copy of these:
// a counterexample belongs to the test that asserts it.
const PLANTED_VIOLATIONS = [
  {
    code: 'omp-entry-prose',
    label: 'rendered prose',
    source: [
      'namespace Wanxiangshu.Composition.Omp',
      'module OmpExtension =',
      '    [<ExportDefault>]',
      '    let refuse (pi: obj) = "This turn is not admitted, please retry later."',
    ].join('\n'),
  },
  {
    code: 'omp-entry-product-import',
    label: 'product import',
    source: [
      'namespace Wanxiangshu.Composition.Omp',
      'open Wanxiangshu.Interaction.Admission',
      'module OmpExtension =',
      '    [<ExportDefault>]',
      '    let decide = ChatAdmissionIntent.decide',
    ].join('\n'),
  },
  {
    code: 'omp-entry-decision-control-flow',
    label: 'admission branch',
    source: [
      'namespace Wanxiangshu.Composition.Omp',
      'module OmpExtension =',
      '    [<ExportDefault>]',
      '    let decide admitted = if admitted then 1 else 0',
    ].join('\n'),
  },
  {
    code: 'omp-entry-decision-state',
    label: 'mutable state',
    source: [
      'namespace Wanxiangshu.Composition.Omp',
      'module OmpExtension =',
      '    [<ExportDefault>]',
      '    let mutable admitted = 0',
    ].join('\n'),
  },
  {
    code: 'omp-entry-decision-vocabulary',
    label: 'retry/budget decision',
    source: [
      'namespace Wanxiangshu.Composition.Omp',
      'module OmpExtension =',
      '    [<ExportDefault>]',
      '    let budget = 3',
    ].join('\n'),
  },
  {
    code: 'omp-entry-resource-lookup',
    label: 'product resource lookup',
    source: [
      'namespace Wanxiangshu.Composition.Omp',
      'open Wanxiangshu.Resources',
      'module OmpExtension =',
      '    [<ExportDefault>]',
      '    let prompt = PromptCatalog.lookup "admission"',
    ].join('\n'),
  },
  {
    code: 'omp-entry-reflection',
    label: 'runtime product lookup',
    source: [
      'namespace Wanxiangshu.Composition.Omp',
      'module OmpExtension =',
      '    [<ExportDefault>]',
      '    let product = Assembly.Load "Wanxiangshu"',
    ].join('\n'),
  },
]

/** Materialize a planted entry module under a scratch repository root. */
const plantedRoot = (source) => {
  const root = mkdtempSync(join(tmpdir(), 'omp-entry-gate-'))
  const folder = join(root, ENTRY_FOLDER)
  mkdirSync(folder, { recursive: true })
  writeFileSync(join(folder, 'OmpExtension.fs'), `${source}\n`)
  return root
}

test('WHAT[host-boundary-035] the real omp entry module carries no product decision', () => {
  const located = locateEntryModule(REPO)
  assert.equal(located.ok, true, located.reason)
  const issues = analyzeEntryModule(REPO)
  assert.deepEqual(issues, [], issues.map((issue) => `${issue.path}:${issue.line} ${issue.message}`).join('\n'))
})

test('WHAT[host-boundary-035] every planted product decision is rejected by its own forbidden class', () => {
  for (const planted of PLANTED_VIOLATIONS) {
    const root = plantedRoot(planted.source)
    try {
      const issues = analyzeEntryModule(root)
      const codes = issues.map((issue) => issue.code)
      assert.ok(
        codes.includes(planted.code),
        `planted '${planted.label}' was not rejected by ${planted.code}; got ${codes.join(', ') || 'no issue'}`,
      )
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  }
})

test('WHAT[host-boundary-035] a planted admission decision in the real entry module is rejected', () => {
  const root = mkdtempSync(join(tmpdir(), 'omp-entry-gate-real-'))
  try {
    const folder = join(root, ENTRY_FOLDER)
    mkdirSync(folder, { recursive: true })
    const real = locateEntryModule(REPO)
    writeFileSync(join(folder, 'OmpExtension.fs'), readFileSync(real.implementation, 'utf8'))
    writeFileSync(
      join(folder, 'OmpExtension.fs'),
      '\n    let refuse (pi: obj) = if pi = null then "This turn is not admitted; please retry later." else "denied"\n',
      { flag: 'a' },
    )
    const issues = analyzeEntryModule(root)
    const codes = issues.map((issue) => issue.code)
    assert.ok(codes.includes('omp-entry-prose'), `expected prose rejection, got ${codes.join(', ') || 'no issue'}`)
    assert.ok(codes.includes('omp-entry-decision-control-flow'), `expected control-flow rejection, got ${codes.join(', ') || 'no issue'}`)
    assert.ok(codes.includes('omp-entry-decision-vocabulary'), `expected decision-vocabulary rejection, got ${codes.join(', ') || 'no issue'}`)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[host-boundary-035] the gate refuses to pass when the entry module or its export site is missing', () => {
  const root = mkdtempSync(join(tmpdir(), 'omp-entry-gate-empty-'))
  try {
    const issues = analyzeEntryModule(root)
    assert.equal(issues.length, 1)
    assert.equal(issues[0].code, 'omp-entry-missing')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

const ENTRY_IMPLEMENTATION = () => {
  const found = locateEntryModule(REPO)
  assert.equal(found.ok, true, found.reason)
  return readFileSync(found.implementation, 'utf8')
}

// [035] states two halves about the entry module: it holds no product decision (enforced by
// the gate), and it actually wires the compiled product in as a plain IHostPlugin value and
// calls activateOnOmp. The gate only reads the first half, so an entry module that wires
// nothing passes it. These rules carry the second half, each with a planted counterexample.
const WIRING_RULES = [
  {
    code: 'omp-entry-plugin-value',
    pattern: /let\s+plugin\s*:\s*IHostPlugin\s*=\s*HostPlugin\.create\b/,
    message: 'the entry module does not build the product as a plain IHostPlugin value from the second-host plugin surface',
  },
  {
    code: 'omp-entry-activate-call',
    pattern: /Adapter\.activateOnOmp\s+pi\s+plugin\b/,
    message: 'the entry module does not hand that plugin value to the omp adapter through activateOnOmp',
  },
  {
    code: 'omp-entry-factory-shape',
    pattern: /<ExportDefault>[\s\S]{0,120}let\s+defaultExport\s+\(pi:\s*obj\)\s*:\s*unit/,
    message: 'the omp default export must be the factory function itself (returning unit), not the result of activation',
  },
]

const FORBIDDEN_WIRING = [
  {
    code: 'omp-entry-fixture-substitute',
    needle: /adapters\/omp|fixture/i,
    message: 'the entry module reaches for the offline TS skeleton or a fixture instead of the compiled product',
  },
  {
    code: 'omp-entry-dynamic-import',
    needle: /DynamicImport|importModule|import\s*\(/,
    message: 'the entry module resolves the product at runtime; [035] requires it to receive the product as a value',
  },
]

const wiringIssues = (source) => [
  ...WIRING_RULES.filter((rule) => !rule.pattern.test(source)).map((rule) => rule.code),
  ...FORBIDDEN_WIRING.filter((rule) => rule.needle.test(source)).map((rule) => rule.code),
]

test('WHAT[host-boundary-035] the real omp entry module wires the compiled product into activateOnOmp', () => {
  const codes = wiringIssues(ENTRY_IMPLEMENTATION())
  assert.deepEqual(codes, [], codes.join(', '))
})

test('WHAT[host-boundary-035] an entry module that wires nothing is rejected by the wiring rule', () => {
  const unwired = [
    'namespace Wanxiangshu.Composition.Omp',
    'module OmpExtension =',
    '    [<ExportDefault>]',
    '    let defaultExport (pi: obj) : unit = ()',
  ].join('\n')
  assert.deepEqual(wiringIssues(unwired), ['omp-entry-plugin-value', 'omp-entry-activate-call'])
})

test('WHAT[host-boundary-035] a default export that returns the activation result instead of being the factory is rejected', () => {
  const boxed = [
    'namespace Wanxiangshu.Composition.Omp',
    'open Wanxiangshu.Host.SecondHost',
    'open Wanxiangshu.Omp',
    'module OmpExtension =',
    '    [<ExportDefault>]',
    '    let defaultExport (pi: obj) : obj =',
    '        let plugin: IHostPlugin = HostPlugin.create clock createTimer',
    '        box (Adapter.activateOnOmp pi plugin [])',
  ].join('\n')
  assert.deepEqual(wiringIssues(boxed), ['omp-entry-factory-shape'])
})

test('WHAT[host-boundary-035] a fixture-bound entry that never reaches the compiled product is rejected', () => {
  const fixtureBound = [
    'namespace Wanxiangshu.Composition.Omp',
    'module OmpExtension =',
    '    // wires a fixture skeleton instead of the compiled product',
    "    let plugin = import ('../fixtures/fixture.js')",
    '    [<ExportDefault>]',
    '    let defaultExport (pi: obj) : unit = ()',
  ].join('\n')
  const codes = wiringIssues(fixtureBound)
  assert.ok(codes.includes('omp-entry-fixture-substitute'), codes.join(', '))
  assert.ok(codes.includes('omp-entry-dynamic-import'), codes.join(', '))
})
}
