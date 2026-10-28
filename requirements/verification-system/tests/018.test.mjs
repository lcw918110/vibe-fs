import assert from 'node:assert/strict'
import { readFileSync, readdirSync } from 'node:fs'
import { join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import { compilerEvidenceReferences } from './support/compiler-evidence-scan.mjs'

test('WHAT[verification-system-018] compiler evidence guard rejects direct FCS use and allows ordinary Fable compilation', () => {
  assert.deepEqual(compilerEvidenceReferences('dotnet tool run fable\nnode scripts/build.mjs\n'), [])
  for (const source of [
    '#r "nuget: FSharp.Compiler.Service"',
    'open FSharp.Compiler.CodeAnalysis',
    'FSharpChecker.Create()',
    'load("Fable.Compiler.dll")',
    'load("Fable.AST.dll")',
    'scanCompilerObservationsV1()',
  ]) assert.equal(compilerEvidenceReferences(source).length, 1, source)
})

test('WHAT[verification-system-018] the build adapter may use compiler APIs but cannot add evidence scans', () => {
  const options = { allowCompilerApi: true }
  assert.deepEqual(compilerEvidenceReferences('open FSharp.Compiler.SourceCodeServices\nload("Fable.Compiler.dll")', options), [])
  for (const source of [
    'scanCompilerObservationsV1()', 'scanDslCompilerEvidence()',
    'runLocalityDependencyScan()', 'scanProductionLocalitySliceReportV1()', 'locality-symbol-uses',
  ]) assert.equal(compilerEvidenceReferences(source, options).length, 1, source)
})

test('WHAT[verification-system-018] source and tooling contain no known compiler evidence entry points', () => {
  const root = fileURLToPath(new URL('../../..', import.meta.url))
  const hits = []
  const scan = (directory) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name)
      if (entry.isDirectory()) {
        if (!['obj', 'bin', 'node_modules', '.git'].includes(entry.name)) scan(path)
      } else if (/\.(?:mjs|cjs|js|fs|fsi|fsx|fsproj|sh|ps1|json)$/.test(entry.name)) {
        const allowCompilerApi = ['scripts/compiler/Checker.fs', 'scripts/compiler/toolchain.mjs']
          .includes(relative(root, path).replaceAll('\\', '/'))
        for (const hit of compilerEvidenceReferences(readFileSync(path, 'utf8'), { allowCompilerApi })) hits.push({ path, ...hit })
      }
    }
  }
  scan(join(root, 'scripts'))
  scan(join(root, 'src'))
  assert.deepEqual(hits, [])
})
