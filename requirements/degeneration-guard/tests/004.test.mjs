import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, rmSync, unlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { Worker } from 'node:worker_threads'
import { encode } from 'gpt-tokenizer/encoding/o200k_base'
import { materializeLoopDetectorEnvelope } from '../../../scripts/build.mjs'
import { deriveLoopDetectorEnvelope, encodeParallel, loadLoopDetectorRepositoryCorpusV1 } from '../../../scripts/lib/derive-loop-detector-envelope.mjs'
import {
  loopDetectorEnvelopeDistPath,
  loopDetectorEnvelopeRepositoryPath,
} from '../../../scripts/lib/loop-detector-envelope-paths.mjs'
import { loopDetectorRepositoryInputFiles } from '../../../scripts/lib/loop-detector-repository-corpus.mjs'

test('WHAT[degeneration-guard-004] selector admits tracked source documents and excludes generated vendor fixture structured deleted and untracked paths', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'wanxiangshu-loop-selector-'))
  try {
    execFileSync('git', ['init', '-q'], { cwd: root })
    const files = ['src/code.fs', 'note.md', 'deleted.md', 'node_modules/module.js', 'vendor/lib.py', 'fixtures/case.md', 'golden/result.md', 'generated/code.fs', 'data.json', 'events.jsonl', 'values.csv']
    for (const file of files) {
      mkdirSync(path.dirname(path.join(root, file)), { recursive: true })
      writeFileSync(path.join(root, file), 'text\n')
    }
    execFileSync('git', ['add', '-f', ...files], { cwd: root })
    unlinkSync(path.join(root, 'deleted.md'))
    writeFileSync(path.join(root, 'untracked.md'), 'untracked\n')
    const selected = loopDetectorRepositoryInputFiles(root)
    assert.ok(selected.every(path.isAbsolute))
    assert.deepEqual(selected.map(file => path.relative(root, file)), ['note.md', 'src/code.fs'])
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[degeneration-guard-004] selected bytes are tracked before UTF-8 and generated-marker filtering', () => {
  const reads = []
  const bytes = new Map([
    ['a.md', Buffer.from('first source\n')],
    ['b.fs', Buffer.from('module Second\nlet value = 2\n')],
    ['c.md', Buffer.from([0xc3, 0x28])],
    ['d.fs', Buffer.from('// auto-generated\nlet generated = 3\n')],
  ])
  const corpus = loadLoopDetectorRepositoryCorpusV1('/fixture-root', {
    selectInputFiles: () => [...bytes.keys()].reverse().map(file => `/fixture-root/${file}`),
    readFile: file => { const name = file.slice('/fixture-root/'.length); reads.push(name); return bytes.get(name) },
  })
  assert.deepEqual(reads, [...bytes.keys()])
  assert.deepEqual(corpus.selectedInputs.map(({ path }) => path), [...bytes.keys()])
  assert.deepEqual(corpus.texts, ['first source\n', 'module Second\nlet value = 2\n'])
  assert.ok(corpus.selectedInputs.every(({ blob_digest }) => /^sha256:[0-9a-f]{64}$/.test(blob_digest)))
  let outsideRead = false
  assert.throws(() => loadLoopDetectorRepositoryCorpusV1('/fixture-root', {
    selectInputFiles: () => ['/outside-root/secret.md'],
    readFile: () => { outsideRead = true; return Buffer.from('unreachable') },
  }), { code: 'generated-selected-input-outside-root' })
  assert.equal(outsideRead, false)
})

test('WHAT[degeneration-guard-004] tracked input byte changes affect derived input digests without a numeric snapshot', async () => {
  const derive = text => deriveLoopDetectorEnvelope('/fixture-root', {
    selectInputFiles: () => ['/fixture-root/a.md'], readFile: () => Buffer.from(text),
  })
  const first = await derive('ordinary source text\n')
  const same = await derive('ordinary source text\n')
  const changed = await derive('different source text\n')
  assert.deepEqual(first, same)
  assert.notEqual(first.selectedInputs[0].blob_digest, changed.selectedInputs[0].blob_digest)
})

test('WHAT[degeneration-guard-004] parallel tokenization equals whole-stream tokenization across safe and unsafe newline candidates', async () => {
  const fixture = ['export class OrderProcessor {', '  // comment with slash /', '  /// doc comment', '  async processOrder(orderId: string) {}', '}', '', 'const message = "你好，世界！🚀";', '// 中文与多行换行', '', 'let count = 42;', '// ' + 'long text payload '.repeat(100)].join('\n')
  const expected = Array.from(encode(fixture))
  assert.deepEqual(await encodeParallel(fixture, 1), expected)
  assert.deepEqual(await encodeParallel(fixture, 4), expected)
})

test('WHAT[degeneration-guard-004] worker failure rejects only after all spawned workers have terminated', async () => {
  const spawned = []
  const fixture = 'class Alpha {\nrun() { return 1; }\n}\nclass Beta {\ncompute() { return 2; }\n}\nlet value = 12345;'
  await assert.rejects(() => encodeParallel(fixture, 4, {
    workerFactory: (source, options) => {
      const worker = spawned.length === 1 ? new Worker('process.exit(42)', { eval: true }) : new Worker(source, options)
      spawned.push(worker)
      return worker
    },
  }), /loop detector tokenize worker exited with 42/)
  assert.ok(spawned.length > 0)
  for (const worker of spawned) assert.equal(worker.threadId, -1)
})

test('WHAT[degeneration-guard-004] derived envelope bytes stay out of the corpus via the generated marker', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'wanxiangshu-loop-envelope-'))
  try {
    execFileSync('git', ['init', '-q'], { cwd: root })
    mkdirSync(path.dirname(path.join(root, loopDetectorEnvelopeRepositoryPath)), { recursive: true })
    writeFileSync(path.join(root, loopDetectorEnvelopeRepositoryPath), '// auto-generated from the repository SSOT; do not edit by hand.\nexport const normalWeightedDistinctCount = 1\n')
    mkdirSync(path.join(root, 'src'), { recursive: true })
    writeFileSync(path.join(root, 'src/keep.fs'), 'module Keep\n')
    execFileSync('git', ['add', '-f', loopDetectorEnvelopeRepositoryPath, 'src/keep.fs'], { cwd: root })
    const corpus = loadLoopDetectorRepositoryCorpusV1(root)
    assert.deepEqual(corpus.selectedInputs.map(({ path: inputPath }) => inputPath), [loopDetectorEnvelopeRepositoryPath, 'src/keep.fs'])
    assert.deepEqual(corpus.texts, ['module Keep\n'])
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[degeneration-guard-004] build materializes the tracked repository envelope artifact into dist', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'wanxiangshu-loop-materialize-'))
  try {
    const source = path.join(root, loopDetectorEnvelopeRepositoryPath)
    const target = path.join(root, loopDetectorEnvelopeDistPath)
    mkdirSync(path.dirname(source), { recursive: true })
    assert.throws(() => materializeLoopDetectorEnvelope(root), /missing repository loop detector envelope artifact/)
    writeFileSync(source, '// auto-generated from the repository SSOT; do not edit by hand.\nexport const minimumWeightedDistinctCount = 0.5\n')
    materializeLoopDetectorEnvelope(root)
    assert.equal(readFileSync(target, 'utf8'), readFileSync(source, 'utf8'))
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test.todo('WHAT[degeneration-guard-004] actual build binds generator selector selected bytes and runtime traversal to one staged input (GAP-145)')
