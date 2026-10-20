import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { run, caseName, render } from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import { createApi, api as apiOf } from '../../../dist/Repository/Programming/Js/RuntimeSurface.js'

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-ls-ignore-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}

const program = (body) => `class Js extends JsProgram {
${body}
}`

const runProgram = (dir, body) =>
  run(dir, 'Engineer', 'en', program(body), 2000, Date.now() + 60_000, 1 << 20, null)

test('WHAT[repository-programming-057] ls honours .gitignore, negation, nested .gitignore, and root .git/info/exclude', () => {
  const { dir, cleanup } = sandbox()
  try {
    mkdirSync(join(dir, '.git', 'info'), { recursive: true })
    mkdirSync(join(dir, 'ignored-dir'))
    mkdirSync(join(dir, 'src', 'nested'), { recursive: true })

    writeFileSync(join(dir, '.git', 'HEAD'), 'ref: refs/heads/main\n', 'utf8')
    writeFileSync(join(dir, '.git', 'info', 'exclude'), 'excluded.txt\n', 'utf8')
    writeFileSync(join(dir, '.gitignore'), '*.log\n!keep.log\nignored-dir/\n', 'utf8')

    writeFileSync(join(dir, 'a.txt'), 'a', 'utf8')
    writeFileSync(join(dir, 'drop.log'), 'drop', 'utf8')
    writeFileSync(join(dir, 'keep.log'), 'keep', 'utf8')
    writeFileSync(join(dir, 'excluded.txt'), 'excluded', 'utf8')
    writeFileSync(join(dir, 'ignored-dir', 'hidden.txt'), 'hidden', 'utf8')

    writeFileSync(join(dir, 'src', '.gitignore'), '*.tmp\n!important.tmp\n', 'utf8')
    writeFileSync(join(dir, 'src', 'index.ts'), 'index', 'utf8')
    writeFileSync(join(dir, 'src', 'scratch.tmp'), 'scratch', 'utf8')
    writeFileSync(join(dir, 'src', 'important.tmp'), 'important', 'utf8')
    writeFileSync(join(dir, 'src', 'nested', 'deep.ts'), 'deep', 'utf8')

    const api = apiOf(createApi(dir)).js

    const rootListing = api.ls('.')
    assert.equal(rootListing.ok, true)
    assert.deepEqual(rootListing.entries, ['.gitignore', 'a.txt', 'keep.log', 'src/'])

    const srcListing = api.ls('src')
    assert.equal(srcListing.ok, true)
    assert.deepEqual(srcListing.entries, ['.gitignore', 'important.tmp', 'index.ts', 'nested/'])
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-057] rg returns parsed JSON and skips ignored paths', async () => {
  const { dir, cleanup } = sandbox()
  try {
    mkdirSync(join(dir, '.git', 'info'), { recursive: true })
    mkdirSync(join(dir, 'dist'))
    mkdirSync(join(dir, 'src', 'sub'), { recursive: true })

    writeFileSync(join(dir, '.gitignore'), 'dist/\n*.bak\n!keep.bak\n', 'utf8')
    writeFileSync(join(dir, 'src', '.gitignore'), '*.gen.ts\n', 'utf8')

    writeFileSync(join(dir, 'dist', 'bundle.js'), 'targetNeedle in ignored dir\n', 'utf8')
    writeFileSync(join(dir, 'old.bak'), 'targetNeedle in ignored bak\n', 'utf8')
    writeFileSync(join(dir, 'keep.bak'), 'targetNeedle in negated bak\n', 'utf8')
    writeFileSync(join(dir, 'src', 'skip.gen.ts'), 'targetNeedle in nested ignored\n', 'utf8')
    writeFileSync(join(dir, 'src', 'a.ts'), 'const x = "targetNeedle";\n', 'utf8')
    writeFileSync(join(dir, 'src', 'sub', 'b.ts'), 'function targetNeedle() {}\nconst y = targetNeedle;\n', 'utf8')

    const outcome = await runProgram(
      dir,
      `  async run() {
    const found = await this.rg(["targetNeedle", "."]);
    const hits = found.messages.flatMap((message) =>
      message.type === "match"
        ? [{ path: message.data.path.text, matched: message.data.submatches[0].match.text }]
        : []);
    return { exitCode: found.exitCode, hits };
  }`,
    )

    assert.equal(caseName(outcome), 'Succeeded')
    const doc = render(outcome)
    assert.match(doc, /exitCode = 0/)
    assert.equal(doc.includes('dist/bundle.js'), false)
    assert.equal(doc.includes('old.bak'), false)
    assert.equal(doc.includes('src/skip.gen.ts'), false)
    assert.match(doc, /path = "\.\/keep\.bak"/)
    assert.match(doc, /path = "\.\/src\/a\.ts"/)
    assert.match(doc, /path = "\.\/src\/sub\/b\.ts"/)
  } finally {
    cleanup()
  }
})
