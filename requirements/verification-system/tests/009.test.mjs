import test from 'node:test'
import { testDeclarations } from './support/test-declarations.mjs'

{
const { default: assert } = await import("node:assert/strict");
const { existsSync, mkdtempSync, mkdirSync, writeFileSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const { E2E_ROOT_REL, SOLE_ENTRY, e2eTestCaseFiles, scanE2EWatchdogFeed } = await import("./e2e/support/watchdog-feed-scan.mjs");

const makeTempRoot = (layout) => {
  const root = mkdtempSync(join(tmpdir(), 'e2e-wdf-fc-'))
  const e2e = join(root, E2E_ROOT_REL)
  if (layout.e2eDir !== false) mkdirSync(e2e, { recursive: true })
  for (const name of layout.files ?? []) {
    writeFileSync(join(e2e, name), '// throwaway\n')
  }
  if (layout.e2eIsFile) {
    rmSync(e2e, { recursive: true, force: true })
    writeFileSync(e2e, 'not a directory\n')
  }
  return root
}
const cleanup = (root) => rmSync(root, { recursive: true, force: true })

test('WHAT[verification-system-009] missing e2e root fails closed, not green with zero files', () => {
  // A gate whose path criterion points at a non-existent directory is a fake
  // gate (always-passing). Missing root must throw, never return [].
  const root = mkdtempSync(join(tmpdir(), 'e2e-wdf-fc-'))
  try {
    assert.throws(
      () => e2eTestCaseFiles(root),
      /e2e root missing or unreadable/,
      'missing e2e root must fail closed (throw), not return []',
    )
  } finally {
    cleanup(root)
  }
})
test('WHAT[verification-system-009] non-directory e2e root fails closed', () => {
  const root = makeTempRoot({ e2eIsFile: true })
  try {
    assert.throws(
      () => e2eTestCaseFiles(root),
      /e2e root is not a directory/,
      'a file where the e2e root directory should be must fail closed',
    )
  } finally {
    cleanup(root)
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, mkdirSync, writeFileSync, rmSync, readdirSync, readFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { default: path } = await import("node:path");
const { fileURLToPath } = await import("node:url");
const { assessIntegrationEntryCoverage } = await import("./support/integration-entry-coverage.mjs");
const { discoverSuiteTests, discoverIntegrationTests, discoverRepositoryIntegrationTests } = await import("./support/discover-suite-tests.mjs");
const { spawnSync } = await import("node:child_process");
const { pathToFileURL } = await import("node:url");
const { walk } = await import("../../../scripts/lib/walk.mjs");

const assess = (discoveredTests, wiredTests, childOwnedTests = []) =>
  assessIntegrationEntryCoverage({ discoveredTests, wiredTests, childOwnedTests })
const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '../../..')
const packageIntegrationDir = path.join(root, 'requirements/distribution/tests/integration/package')
const normalize = (file) => path.relative(root, file).split(path.sep).join('/')

test('WHAT[verification-system-009] integration entry coverage accepts an exact reachable set', () => {
  assert.deepEqual(
    assess(
      ['requirements/a/tests/integration/a.test.mjs', 'requirements/b/tests/integration/b.test.mjs'],
      ['requirements/a/tests/integration/a.test.mjs', 'requirements/b/tests/integration/b.test.mjs'],
    ),
    { ok: true, missingFromEntry: [], staleEntry: [], duplicateWiring: [] },
  )
})
test('WHAT[verification-system-009] integration entry coverage delegates the exact declared child-owned set', () => {
  assert.deepEqual(
    assess(
      [
        'requirements/a/tests/integration/a.test.mjs',
        'requirements/distribution/tests/integration/package/layout.test.mjs',
      ],
      ['requirements/a/tests/integration/a.test.mjs'],
      ['requirements/distribution/tests/integration/package/layout.test.mjs'],
    ),
    { ok: true, missingFromEntry: [], staleEntry: [], duplicateWiring: [] },
  )
})
test('WHAT[verification-system-009] discoverSuiteTests lists every package *.test.mjs and excludes the runner', () => {
  const packageTestsDir = path.join(root, 'requirements/distribution/tests')
  const discovered = discoverSuiteTests(packageTestsDir)
  // The four real package suites are all picked up.
  assert.ok(discovered.includes('001.test.mjs'))
  assert.ok(discovered.includes('003.test.mjs'))
  assert.ok(discovered.includes('004.test.mjs'))
  assert.ok(discovered.includes('008.test.mjs'))
  // The runner itself and any non-test file are excluded by suffix.
  assert.ok(!discovered.includes('run.mjs'))
  // Deterministic, sorted, deduplicated.
  assert.deepEqual(discovered, [...new Set(discovered)].sort())
})
test('WHAT[verification-system-009] discoverSuiteTests auto-includes an added test and excludes non-test files', () => {
  const scratch = mkdtempSync(path.join(tmpdir(), 'pkg-suite-'))
  try {
    writeFileSync(path.join(scratch, 'alpha.test.mjs'), '// noop\n')
    writeFileSync(path.join(scratch, 'beta.test.mjs'), '// noop\n')
    writeFileSync(path.join(scratch, 'run.mjs'), '// runner\n')
    writeFileSync(path.join(scratch, 'helper.mjs'), '// helper\n')
    mkdirSync(path.join(scratch, 'nested'))
    writeFileSync(path.join(scratch, 'nested', 'ignored.test.mjs'), '// nested\n')
    const discovered = discoverSuiteTests(scratch)
    assert.deepEqual(discovered, ['alpha.test.mjs', 'beta.test.mjs'])
  } finally {
    rmSync(scratch, { recursive: true, force: true })
  }
})
test('WHAT[verification-system-009] discovery rejects a missing required suite directory', () => {
  const scratch = mkdtempSync(path.join(tmpdir(), 'missing-suite-'))
  try {
    assert.throws(() => discoverSuiteTests(path.join(scratch, 'tests')), { code: 'ENOENT' })
    writeFileSync(path.join(scratch, 'tests'), 'not a directory')
    assert.throws(() => discoverSuiteTests(path.join(scratch, 'tests')), { code: 'ENOTDIR' })
  } finally {
    rmSync(scratch, { recursive: true, force: true })
  }
})
test('WHAT[verification-system-009] integration discovery ignores examples in strings and comments', () => {
  const source = '// integrationTest("comment", () => {})\nconst example = \'integrationTest("text", () => {})\'\nintegrationTest("actual", () => {})'
  assert.deepEqual(testDeclarations(source), [{ kind: 'integrationTest', title: 'actual', line: 3 }])
  assert.throws(() => testDeclarations('integrationTest('), SyntaxError)
})
test('WHAT[verification-system-009] integration discovery selects real declarations and propagates malformed source', () => {
  const scratch = mkdtempSync(path.join(tmpdir(), 'integration-discovery-'))
  try {
    writeFileSync(path.join(scratch, 'actual.test.mjs'), 'integrationTest("actual", () => {})')
    writeFileSync(path.join(scratch, 'todo.test.mjs'), 'integrationTest.todo("pending")')
    writeFileSync(path.join(scratch, 'example.test.mjs'), '// integrationTest("comment", () => {})\nconst text = "WXS_TIER_INTEGRATION integrationTest()"')
    writeFileSync(path.join(scratch, 'import-only.test.mjs'), 'import { integrationTest } from "./helper.mjs"')
    writeFileSync(path.join(scratch, 'ordinary.test.mjs'), 'test("unit", () => {})')
    assert.deepEqual(discoverIntegrationTests(scratch), [
      path.join(scratch, 'actual.test.mjs'), path.join(scratch, 'todo.test.mjs'),
    ])
    writeFileSync(path.join(scratch, 'broken.test.mjs'), 'integrationTest(')
    assert.throws(() => discoverIntegrationTests(scratch), SyntaxError)
  } finally {
    rmSync(scratch, { recursive: true, force: true })
  }
})
test('WHAT[verification-system-009] the actual repository discovery refuses missing or malformed suites with nonzero exit', () => {
  const scratch = mkdtempSync(path.join(tmpdir(), 'integration-missing-'))
  try {
    mkdirSync(path.join(scratch, 'requirements/broken-package'), { recursive: true })
    const moduleUrl = pathToFileURL(path.join(root, 'requirements/verification-system/tests/support/discover-suite-tests.mjs')).href
    const env = { ...process.env }
    delete env.NODE_TEST_CONTEXT
    const args = ['--input-type=module', '-e',
      `import { discoverRepositoryIntegrationTests } from ${JSON.stringify(moduleUrl)}; discoverRepositoryIntegrationTests(process.argv[1]);`, scratch,
    ]
    const result = spawnSync(process.execPath, args, { encoding: 'utf8', env })
    assert.equal(result.error, undefined)
    assert.equal(result.signal, null)
    assert.equal(result.status, 1, result.stderr)
    assert.match(result.stderr, /ENOENT/)
    assert.ok(result.stderr.includes(path.join(scratch, 'requirements/broken-package/tests')))
    mkdirSync(path.join(scratch, 'requirements/broken-package/tests'))
    writeFileSync(path.join(scratch, 'requirements/broken-package/tests/001.test.mjs'), 'integrationTest(')
    const malformed = spawnSync(process.execPath, args, { encoding: 'utf8', env })
    assert.equal(malformed.error, undefined)
    assert.equal(malformed.signal, null)
    assert.equal(malformed.status, 1, malformed.stderr)
    assert.match(malformed.stderr, /SyntaxError/)
  } finally {
    rmSync(scratch, { recursive: true, force: true })
  }
})
test('WHAT[verification-system-009] the real integration entry covers every discovered integration test', () => {
  const packageTestsDir = path.join(root, 'requirements/distribution/tests')
  const requirementsDir = path.join(root, 'requirements')
  const discoveredIntegrationTests = readdirSync(requirementsDir, { withFileTypes: true })
    .filter((entry) => entry.isDirectory() && !['distribution', 'proposals'].includes(entry.name))
    .sort((a, b) => a.name.localeCompare(b.name))
    .flatMap((entry) => {
      const testsDir = path.join(requirementsDir, entry.name, 'tests')
      return readdirSync(testsDir)
        .filter((name) => name.endsWith('.test.mjs'))
        .sort()
        .map((name) => normalize(path.join(testsDir, name)))
        .filter((file) => {
          const text = readFileSync(path.join(root, file), 'utf8')
          return testDeclarations(text).some(({ kind }) => kind === 'integrationTest')
        })
    })
    .sort()
  const childOwnedIntegrationTests = discoverSuiteTests(packageTestsDir)
    .filter((name) => {
      const text = readFileSync(path.join(packageTestsDir, name), 'utf8')
      return testDeclarations(text).some(({ kind }) => kind === 'integrationTest')
    })
    .map((name) => normalize(path.join(packageTestsDir, name)))
  const entryFiles = (entry) => {
    const env = { ...process.env }
    delete env.NODE_TEST_CONTEXT
    const result = spawnSync(process.execPath, [path.join(root, entry), '--dry-run'], {
      cwd: root, encoding: 'utf8', env,
    })
    assert.equal(result.error, undefined)
    assert.equal(result.signal, null)
    assert.equal(result.status, 0, result.stderr || result.stdout)
    return result.stdout.split('\n').filter((line) => line.startsWith('    requirements/')).map((line) => line.trim())
  }
  const wiredIntegrationTests = entryFiles('requirements/verification-system/tests/integration/run.mjs')
  const actualChildOwnedTests = entryFiles('requirements/distribution/tests/integration/package/run.mjs')
  assert.deepEqual(wiredIntegrationTests, discoverRepositoryIntegrationTests(root).map(normalize))
  assert.deepEqual(actualChildOwnedTests, childOwnedIntegrationTests)
  const result = assessIntegrationEntryCoverage({
    discoveredTests: [...discoveredIntegrationTests, ...childOwnedIntegrationTests].sort(),
    wiredTests: wiredIntegrationTests,
    childOwnedTests: actualChildOwnedTests,
  })
  assert.equal(result.ok, true, JSON.stringify(result, null, 2))
  assert.ok(childOwnedIntegrationTests.length > 0)
  assert.ok(discoveredIntegrationTests.length > 0)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { checks } = await import("../../../scripts/check.mjs");
const { existsSync, readFileSync, readdirSync } = await import("node:fs");
const { basename, dirname, join, resolve } = await import("node:path");
const { fileURLToPath } = await import("node:url");
const { default: test } = await import("node:test");

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..')

test('WHAT[verification-system-009] every ladder step target exists as a real file', () => {
  // 层序里的每个入口都必须是真实文件：指向不存在文件的命令恒为「没跑到」，
  // 层序 pin 就退化成文字装饰（verification-system-009 静态门禁必须命中真实路径）。
  const required = [
    'scripts/check.mjs',
    'scripts/build.mjs',
    'requirements/verification-system/tests/run.mjs',
    'requirements/verification-system/tests/integration/run.mjs',
    'requirements/distribution/tests/integration/package/run.mjs',
    'scripts/warmup-opencode.mjs',
    'requirements/verification-system/tests/014.test.mjs',
  ]
  for (const rel of required) {
    assert.ok(existsSync(join(ROOT, rel)), `ladder step target missing: ${rel}`)
  }
})
test('WHAT[verification-system-009] every wired gate path exists and every gate script is registered or explicitly aggregated', () => {
  for (const gatePath of checks) {
    assert.ok(
      existsSync(gatePath),
      `check.mjs wires a gate that does not exist: ${gatePath}`,
    )
  }

  const dirEntries = readdirSync(join(ROOT, 'scripts/checks'))

  // Data files that are not gate scripts
  const nonGateFiles = new Set([
    'owner-impact-corpus.json',
    'subsystems.json',
    // Helper / non-gate modules:
    'fsharp-control-pyramid-guide.mjs',
    'js-module-linkage.mjs',
    'js-surface-gate.mjs',
    'js-surface-manifest.mjs',
    'llm-facing-format-gate.mjs',
    'shard-edge-audit.mjs',
  ])

  const wiredBasenames = new Set(checks.map((p) => basename(p)))
  const gateScriptsInDir = dirEntries.filter((f) => f.endsWith('.mjs') && !nonGateFiles.has(f))

  // A gate script reaches the release gate set in one of two ways: it is registered in
  // check.mjs, or a registered gate imports it and calls it — that is how
  // adapter-thinness.mjs runs (architecture.mjs imports scanAdapterThinness). Counting
  // registrations alone would call that a missing gate, so the invariant is that every
  // gate script is reachable from the registered set.
  const aggregated = new Set()
  for (const gatePath of checks) {
    const text = readFileSync(gatePath, 'utf8')
    for (const name of dirEntries) {
      if (!name.endsWith('.mjs') || wiredBasenames.has(name)) continue
      if (text.includes(`./${name}`) || text.includes(`'${name}'`)) aggregated.add(name)
    }
  }

  const unreachable = gateScriptsInDir.filter((f) => !wiredBasenames.has(f) && !aggregated.has(f))
  assert.deepEqual(
    unreachable,
    [],
    'every gate script in scripts/checks must be registered in check.mjs or aggregated by a registered gate',
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { copyFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { spawnSync } = await import("node:child_process");
const { default: test } = await import("node:test");
const { fileURLToPath } = await import("node:url");

const repositoryRoot = fileURLToPath(new URL('../../..', import.meta.url))
const write = (root, path, text) => {
  const target = join(root, path)
  mkdirSync(join(target, '..'), { recursive: true })
  writeFileSync(target, text)
}
const runNode = (root, args) => {
  const env = { ...process.env }
  delete env.NODE_TEST_CONTEXT
  return spawnSync(process.execPath, args, {
    cwd: root,
    encoding: 'utf8',
    env,
  })
}

test('WHAT[verification-system-009] repository closure gates reject an unassigned production source and package member', () => {
  const fixture = mkdtempSync(join(tmpdir(), 'repository-closure-'))

  try {
    mkdirSync(join(fixture, 'scripts/checks'), { recursive: true })
    mkdirSync(join(fixture, 'scripts/lib'), { recursive: true })
    copyFileSync(
      join(repositoryRoot, 'scripts/checks/subsystems.mjs'),
      join(fixture, 'scripts/checks/subsystems.mjs'),
    )
    copyFileSync(
      join(repositoryRoot, 'scripts/lib/compile-shards.mjs'),
      join(fixture, 'scripts/lib/compile-shards.mjs'),
    )
    write(fixture, 'scripts/checks/subsystems.json', JSON.stringify({
      schema_version: 1,
      subsystems: [{ id: 'fixture', legacy_owners: [] }],
    }))
    write(fixture, 'src/Wanxiangshu/Wanxiangshu.fsproj', `
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Owned.fsi"/>
    <Compile Include="Owned.fs"/>
  </ItemGroup>
</Project>
`)
    write(fixture, 'src/Wanxiangshu/Wanxiangshu.Shard.fixture.owned.fsproj', `
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <WanxiangshuSubsystem>fixture</WanxiangshuSubsystem>
    <WanxiangshuCompileShard>owned</WanxiangshuCompileShard>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Owned.fsi"/>
    <Compile Include="Owned.fs"/>
  </ItemGroup>
</Project>
`)
    write(fixture, 'src/Wanxiangshu/Owned.fsi', 'namespace ClosureFixture\n')
    write(fixture, 'src/Wanxiangshu/Owned.fs', 'namespace ClosureFixture\n')
    const validSubsystemGate = runNode(fixture, ['scripts/checks/subsystems.mjs'])
    assert.equal(validSubsystemGate.status, 0, validSubsystemGate.stderr || validSubsystemGate.stdout)
    assert.match(validSubsystemGate.stdout, /subsystems: OK/)

    write(fixture, 'src/Wanxiangshu/Unowned.fs', 'namespace ClosureFixture\n')
    const subsystemGate = runNode(fixture, ['scripts/checks/subsystems.mjs'])
    assert.equal(subsystemGate.status, 1, subsystemGate.stderr || subsystemGate.stdout)
    assert.match(subsystemGate.stderr, /production source coverage mismatch/)
    assert.match(subsystemGate.stderr, /src\/Wanxiangshu\/Unowned\.fs/)

    mkdirSync(join(fixture, 'requirements/distribution/tests'), { recursive: true })
    copyFileSync(
      join(repositoryRoot, 'requirements/distribution/tests/004.test.mjs'),
      join(fixture, 'requirements/distribution/tests/004.test.mjs'),
    )
    const packageArgs = [
      '--test',
      '--test-reporter=tap',
      'requirements/distribution/tests/004.test.mjs',
    ]
    write(fixture, 'package.json', JSON.stringify({ files: ['dist/', 'resources/'], bundleDependencies: ['wanxiangli'] }))
    const validPackageGate = runNode(fixture, packageArgs)
    assert.equal(validPackageGate.status, 0, validPackageGate.stderr || validPackageGate.stdout)
    assert.match(validPackageGate.stdout, /# tests 1\b/)
    assert.match(validPackageGate.stdout, /# pass 1\b/)

    for (const files of [['resources/'], ['dist/'], ['dist/', 'resources/', 'src/'], ['resources/', 'dist/']]) {
      write(fixture, 'package.json', JSON.stringify({ files, bundleDependencies: ['wanxiangli'] }))
      const packageGate = runNode(fixture, packageArgs)
      assert.equal(packageGate.status, 1, packageGate.stderr || packageGate.stdout)
      assert.match(packageGate.stdout, /not ok 1 - WHAT\[distribution-004\]/)
      assert.match(packageGate.stdout, /code: 'ERR_ASSERTION'/)
      assert.match(packageGate.stdout, /operator: 'deepStrictEqual'/)
      assert.match(packageGate.stdout, /# tests 1\b/)
      assert.match(packageGate.stdout, /# fail 1\b/)
    }
  } finally {
    rmSync(fixture, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-009] clean-break symbol gate is red on an empty directory, a read failure, and a scan that did not run', async () => {
  const { default: assert } = await import('node:assert/strict')
  const { mkdirSync, mkdtempSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const { assess, check: scanCleanBreak, registrationGap } = await import('../../../scripts/checks/clean-break-symbols.mjs')
  const { checks } = await import('../../../scripts/check.mjs')
  const empty = mkdtempSync(join(tmpdir(), 'clean-break-empty-'))
  try {
    mkdirSync(join(empty, 'src'))
    const emptyResult = scanCleanBreak({ root: empty })
    assert.equal(emptyResult.ok, false)
    assert.ok(emptyResult.issues.some((issue) => issue.code === 'scan-empty'))
  } finally {
    rmSync(empty, { recursive: true, force: true })
  }

  const unreadable = scanCleanBreak({
    root: join(import.meta.dirname, 'fixtures/clean-break-symbols/legal'),
    readText() {
      throw new Error('clean-break fixture read denied')
    },
  })
  assert.equal(unreadable.ok, false)
  assert.ok(unreadable.issues.some((issue) => issue.code === 'read-failed'))

  const notRun = assess({ ran: false })
  assert.equal(notRun.ok, false)
  assert.equal(notRun.issues[0].code, 'scan-not-run')
  assert.equal(registrationGap([]).code, 'scan-not-run')
  assert.equal(registrationGap(checks), null)
})
}
