import test from 'node:test'
import assert from 'node:assert/strict'
import { spawn, spawnSync } from 'node:child_process'
import * as fixtureFs from 'node:fs'
import * as fixturePath from 'node:path'
import { tmpdir as fixtureTmpdir } from 'node:os'
import { Readable } from 'node:stream'
import { fileURLToPath } from 'node:url'
import { reportTestStream } from './support/run-inner.mjs'

const runPluginLoadingFixture = (mode, rejectPlugin) => new Promise((resolve, reject) => {
  const env = {
    ...process.env,
    WXS_TIER_INTEGRATION: '0',
    WXS_TIER_RELEASE: '0',
    WXS_FIXTURE_REJECT_PLUGIN: rejectPlugin ? '1' : '0',
  }
  delete env.NODE_TEST_CONTEXT
  const child = spawn(process.execPath, [
    '--loader', new URL('./support/fixtures/plugin-entry-loader.fixture.mjs', import.meta.url).href,
    fileURLToPath(new URL('./support/fixtures/plugin-loading.fixture.mjs', import.meta.url)),
    mode,
  ], {
    env,
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  let stdout = ''
  let stderr = ''
  child.stdout.setEncoding('utf8').on('data', chunk => { stdout += chunk })
  child.stderr.setEncoding('utf8').on('data', chunk => { stderr += chunk })
  child.on('error', reject)
  child.on('close', (status, signal) => resolve({ status, signal, stdout, stderr }))
})

test('WHAT[verification-system-005] unused plugin fixtures defer production entry failures until activation', async (t) => {
  for (const mode of ['import', 'skip']) {
    await t.test(`WHAT[verification-system-005] ${mode} does not activate the rejected plugin entry`, async () => {
      const result = await runPluginLoadingFixture(mode, true)
      assert.equal(result.signal, null, result.stderr)
      assert.equal(result.status, 0, result.stderr)
      assert.match(result.stdout, /plugin fixture imported/)
      assert.doesNotMatch(result.stderr, /PLUGIN_ENTRY_RESOLVED|controlled plugin entry rejection/)
      if (mode === 'skip') assert.match(result.stdout, /integration tier not enabled/)
    })
  }

  const activated = await runPluginLoadingFixture('create', true)
  assert.equal(activated.signal, null, activated.stderr)
  assert.equal(activated.status, 1, activated.stderr)
  assert.match(activated.stdout, /plugin fixture imported/)
  assert.match(activated.stderr, /PLUGIN_ENTRY_RESOLVED/)
  assert.match(activated.stderr, /controlled plugin entry rejection/)
  assert.doesNotMatch(activated.stdout, /plugin fixture created/)
})

test('WHAT[verification-system-005] activating the fixture still loads and creates the real plugin', async () => {
  const result = await runPluginLoadingFixture('create', false)
  assert.equal(result.signal, null, result.stderr)
  assert.equal(result.status, 0, result.stderr)
  assert.equal(result.stderr.match(/PLUGIN_ENTRY_RESOLVED/g)?.length, 1, result.stderr)
  assert.match(result.stdout, /plugin fixture imported/)
  assert.match(result.stdout, /plugin fixture created/)
})

test('WHAT[verification-system-005] reporter failure during consumption cannot strand the runner after source close', async () => {
  const failure = new Error('controlled reporter failure during consumption')
  const outcome = await reportTestStream({
    stream: Readable.from([1, 2], { objectMode: true }),
    reporter: async function* (source) {
      for await (const event of source) {
        assert.equal(event, 1)
        throw failure
      }
    },
  })
  assert.equal(outcome.drained, false)
  assert.equal(outcome.error, failure)
})

test('WHAT[verification-system-005] reporter failure after source completion is reported and cannot produce a successful drain', async () => {
  const failure = new Error('controlled reporter failure')
  const reports = []
  const outcome = await reportTestStream({
    stream: Readable.from([1, 2], { objectMode: true }),
    reporter: async function* (source) {
      for await (const event of source) assert.ok(event)
      throw failure
    },
    send: message => reports.push(message),
  })
  assert.equal(outcome.drained, false)
  assert.equal(outcome.error, failure)
  assert.equal(reports.some(message => message.type === 'runner:error' && message.data.message === failure.message), true)
})

test('WHAT[verification-system-005] successful reporter drains every event before reporting completion', async () => {
  const observed = []
  const outcome = await reportTestStream({
    stream: Readable.from([1, 2], { objectMode: true }),
    reporter: async function* (source) {
      for await (const event of source) observed.push(event)
    },
  })
  assert.deepEqual(observed, [1, 2])
  assert.deepEqual(outcome, { drained: true, error: null })
})

test('WHAT[verification-system-005] the real CI verification step preserves failure and success exit codes', () => {
  const workflow = fixtureFs.readFileSync(new URL('../../../.github/workflows/ci.yml', import.meta.url), 'utf8')
  const block = workflow.match(/        run: \|\n((?:          .*\n)+)/)
  assert.ok(block, 'the CI verification shell step must exist')
  const script = block[1].split('\n').map((line) => line.slice(10)).join('\n')
  const directory = fixtureFs.mkdtempSync(fixturePath.join(fixtureTmpdir(), 'ci-exit-'))
  try {
    const bin = fixturePath.join(directory, 'bin')
    fixtureFs.mkdirSync(bin)
    fixtureFs.mkdirSync(fixturePath.join(directory, '.fable-build/verify-logs'), { recursive: true })
    fixtureFs.writeFileSync(fixturePath.join(bin, 'node'), '#!/bin/sh\nexit "$VERIFY_FIXTURE_EXIT"\n', { mode: 0o755 })
    for (const code of [7, 0]) {
      const result = spawnSync('bash', ['--noprofile', '--norc', '-e', '-o', 'pipefail', '-c', script], {
        cwd: directory,
        env: { ...process.env, PATH: `${bin}${fixturePath.delimiter}${process.env.PATH}`, VERIFY_FIXTURE_EXIT: String(code) },
        encoding: 'utf8',
      })
      assert.equal(result.error, undefined)
      assert.equal(result.signal, null)
      assert.equal(result.status, code, result.stderr)
      if (code !== 0) assert.match(result.stdout, /verify:release exited with 7/)
    }
  } finally {
    fixtureFs.rmSync(directory, { recursive: true, force: true })
  }
})

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

test('WHAT[verification-system-005] traversal errors are not masked (cause preserved)', () => {
  // fail-closed 义务（verification-system-005）：遇数据损坏/边界失配时安全失败，不崩溃吞上下文。
  // The original fail-open path swallowed the traversal error into a green [].
  // Fail-closed means the underlying errno is preserved as `cause` so the
  // failure is explainable, not a silent zero-file OK.
  const root = mkdtempSync(join(tmpdir(), 'e2e-wdf-fc-'))
  try {
    let thrown
    try {
      e2eTestCaseFiles(root)
    } catch (err) {
      thrown = err
    }
    assert.ok(thrown, 'missing root must throw')
    assert.match(thrown.message, /^e2e-watchdog-feed:/, 'error carries the gate prefix')
    assert.ok(thrown.cause, 'underlying traversal error is preserved as cause (not swallowed)')
  } finally {
    cleanup(root)
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { verify, verificationSteps } = await import("../../../scripts/verify.mjs");
const { checks } = await import("../../../scripts/check.mjs");
const { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, readlinkSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { basename, dirname, join, resolve } = await import("node:path");
const { fileURLToPath } = await import("node:url");
const { default: test } = await import("node:test");

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..')
const read = (rel) => readFileSync(join(ROOT, rel), 'utf8')
function createMemorySink() {
  let buf = ''
  return {
    write(chunk) {
      buf += chunk
    },
    get output() {
      return buf
    },
  }
  }
for (const failingLabel of ['format:check', 'check', 'build']) {
  test(`WHAT[verification-system-005] verify halts and marks subsequent steps not-run when ${failingLabel} fails`, async () => {
    const tmpLogDir = mkdtempSync(join(tmpdir(), 'proof-ladder-fail-'))
    const sink = createMemorySink()
  const spawned = []
  const fakeRunStep = async ({ label, argv }) => {
      spawned.push(label)
      if (label === failingLabel) {
        return { label, ok: false, exitCode: 1, signal: null, durationMs: 5 }
  }
      return { label, ok: true, exitCode: 0, signal: null, durationMs: 5 }
    }

    try {
      const root = join(tmpLogDir, 'repository')
      for (const directory of ['src', 'scripts', 'requirements', 'resources']) {
        mkdirSync(join(root, directory), { recursive: true })
        writeFileSync(join(root, directory, 'input.txt'), `${directory}\n`)
      }
      for (const args of [['init', '--quiet', root], ['-C', root, 'add', '.']]) {
        const git = spawnSync('git', args, { encoding: 'utf8' })
        assert.ifError(git.error)
        assert.equal(git.status, 0, git.stderr)
      }
      const result = await verify({
        root,
        release: false,
        runStep: fakeRunStep,
        output: sink,
        logDirectory: tmpLogDir,
})
      assert.equal(result.exitCode, 1)
      assert.equal(result.outcome, 'fail')
      assert.equal(result.failureReason, `step-failed:${failingLabel}`)
      assert.equal(spawned.at(-1), failingLabel, `execution must stop after ${failingLabel}`)

      const failedIdx = result.steps.findIndex((s) => s.label === failingLabel)
      assert.ok(failedIdx >= 0)
      assert.deepEqual(spawned, verificationSteps({ root }).slice(0, failedIdx + 1).map(step => step.label))
      for (const step of result.steps.slice(0, failedIdx)) {
        assert.equal(step.status, 'ok')
      }
      assert.equal(result.steps[failedIdx].status, 'failed')
      for (let i = failedIdx + 1; i < result.steps.length; i++) {
        assert.equal(result.steps[i].status, 'not-run', `step ${result.steps[i].label} must be marked not-run`)
    }
      assert.match(sink.output, /FAIL  verify daily/)
    } finally {
      rmSync(tmpLogDir, { recursive: true, force: true })
    }
})
  }

test('WHAT[verification-system-005] rejected, missing and throwing gates return failure', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'gate-failure-'))
  try {
    const { main } = await import('../../../scripts/check.mjs')
    const gate = join(directory, 'gate.mjs')
    writeFileSync(gate, 'export function check() { return { issues: [{ code: "fail", message: "controlled violation" }] } }\n')
    assert.equal(await main([], { checkList: [gate] }), 1)
    assert.equal(await main([], { checkList: [join(directory, 'missing.mjs')] }), 1)
    const throwing = join(directory, 'throwing.mjs')
    writeFileSync(throwing, 'export function check() { throw new Error("controlled failure") }\n')
    assert.equal(await main([], { checkList: [throwing] }), 1)
    const passing = join(directory, 'passing.mjs')
    writeFileSync(passing, 'export function check() { return { issues: [] } }\n')
    assert.equal(await main([], { checkList: [passing] }), 0)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { StrictMockSignals } = await import("./e2e/support/strict-mock-signals.js");


test('WHAT[verification-system-005] waitAny fatal cancellation removes every registered waiter', async () => {
  const signals = new StrictMockSignals();
  const waiting = signals.waitForAnyExpectation(['original.1', 'guarded.0']);

  signals.fail(new Error('provider mismatch'));

  await assert.rejects(waiting, /provider mismatch/);
  assert.equal(signals._expectationWaiters.size, 0);
});
test('WHAT[verification-system-005] waitAny rejects an open or malformed alternative set', async () => {
  const signals = new StrictMockSignals();

  await assert.rejects(signals.waitForAnyExpectation('original.1'), /array of at least two/);
  await assert.rejects(signals.waitForAnyExpectation(['original.1', 'original.1']), /unique non-blank/);
});
}

{
const { default: assert } = await import("node:assert/strict");
const { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync, chmodSync, readdirSync } = await import("node:fs");
const { join } = await import("node:path");
const { tmpdir } = await import("node:os");
const { default: test } = await import("node:test");
const { walk } = await import("../../../scripts/lib/walk.mjs");


test('WHAT[verification-system-005] walk throws on a missing root instead of returning an empty array', () => {
  const missing = join(tmpdir(), 'walk-fail-closed-missing-' + process.pid)
  rmSync(missing, { recursive: true, force: true })
  assert.throws(
    () => walk(missing, ['.fs']),
    /walk: root .* is not accessible/,
    'a missing root must throw so a gate cannot scan nothing and report OK',
  )
})
test('WHAT[verification-system-005] walk throws on a non-directory root instead of returning [root]', () => {
  const dir = mkdtempSync(join(tmpdir(), 'walk-fail-closed-file-root-'))
  try {
    const file = join(dir, 'leaf.fs')
    writeFileSync(file, 'module X\n')
    assert.throws(
      () => walk(file, ['.fs']),
      /walk: root .* is not a directory/,
      'a non-directory root must throw so a gate cannot treat a single file as a scanned tree',
    )
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[verification-system-005] walk throws on a nested unreadable directory instead of silently skipping it', () => {
  const dir = mkdtempSync(join(tmpdir(), 'walk-fail-closed-nested-'))
  try {
    const nested = join(dir, 'nested')
    mkdirSync(nested)
    writeFileSync(join(nested, 'hidden.fs'), 'module Hidden\n')
    // Remove read+execute permission so readdirSync fails.
    // On root this test may still pass through; guard the assertion so a
    // permission change that did not take effect does not produce a false green.
    try {
      chmodSync(nested, 0o000)
    } catch {
      // Some filesystems reject chmod; skip the nested-permission assertion
      // only if the permission could not be applied at all.
      return
    }
    // Root bypasses permission bits, so chmod 0o000 cannot make a directory
    // unreadable there. When readdirSync still succeeds, the failure path is
    // not constructible in this environment; skip rather than false-green.
    let stillReadable = false
    try {
      readdirSync(nested)
      stillReadable = true
    } catch {
      stillReadable = false
    }
    if (stillReadable) return
    assert.throws(
      () => walk(dir, ['.fs']),
      /walk: readdir failed/,
      'a nested unreadable directory must throw so hidden content cannot evade a scan',
    )
  } finally {
    // Restore permissions before removal so rmSync can clean up.
    try { chmodSync(join(dir, 'nested'), 0o755) } catch {}
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[verification-system-005] walk rejects a symlink entry instead of following or skipping it', () => {
  const dir = mkdtempSync(join(tmpdir(), 'walk-fail-closed-symlink-'))
  try {
    const target = join(dir, 'target.fs')
    writeFileSync(target, 'module Target\n')
    const link = join(dir, 'link.fs')
    symlinkSync(target, link)
    assert.throws(
      () => walk(dir, ['.fs']),
      /walk: refusing to traverse symlink/,
      'a symlink entry must be rejected so hidden content cannot evade a scan via a link',
    )
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[verification-system-005] walk rejects a symlink root instead of following it', () => {
  const dir = mkdtempSync(join(tmpdir(), 'walk-fail-closed-symlink-root-'))
  try {
    const realDir = join(dir, 'real')
    mkdirSync(realDir)
    writeFileSync(join(realDir, 'a.fs'), 'module A\n')
    const linkDir = join(dir, 'linkdir')
    symlinkSync(realDir, linkDir)
    assert.throws(
      () => walk(linkDir, ['.fs']),
      /walk: refusing to traverse symlink root/,
      'a symlink root must be rejected so a gate cannot follow a link into hidden content',
    )
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[verification-system-005] walk returns sorted matching paths on a normal tree', () => {
  // Regression guard: the fail-closed hardening must not break the successful path.
  const dir = mkdtempSync(join(tmpdir(), 'walk-fail-closed-ok-'))
  try {
    mkdirSync(join(dir, 'sub'))
    writeFileSync(join(dir, 'b.fs'), 'module B\n')
    writeFileSync(join(dir, 'a.fs'), 'module A\n')
    writeFileSync(join(dir, 'sub', 'c.fs'), 'module C\n')
    writeFileSync(join(dir, 'ignore.txt'), 'noise\n')
    const result = walk(dir, ['.fs'])
    assert.deepEqual(
      result,
      [join(dir, 'a.fs'), join(dir, 'b.fs'), join(dir, 'sub', 'c.fs')].sort(),
      'a normal tree must return sorted paths matching the extension filter',
    )
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[verification-system-005] walk preserves the SKIP directory set', () => {
  const dir = mkdtempSync(join(tmpdir(), 'walk-fail-closed-skip-'))
  try {
    mkdirSync(join(dir, 'node_modules'))
    mkdirSync(join(dir, 'src'))
    writeFileSync(join(dir, 'node_modules', 'hidden.fs'), 'module Hidden\n')
    writeFileSync(join(dir, 'src', 'visible.fs'), 'module Visible\n')
    writeFileSync(join(dir, 'top.fs'), 'module Top\n')
    const result = walk(dir, ['.fs'])
    assert.deepEqual(
      result,
      [join(dir, 'src', 'visible.fs'), join(dir, 'top.fs')].sort(),
      'SKIP directories (node_modules, .git, etc.) must be preserved',
    )
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
}
