import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { default: fs } = await import("node:fs");
const { default: os } = await import("node:os");
const { default: path } = await import("node:path");

const causal = await import('../../../dist/Execution/Session/Wait/Surface.js')
const mkWait = (waitKind, ownerKind, subject, producer) =>
  causal.createWait({
    waitKind,
    owner: causal.owner(ownerKind, { id: ownerKind }),
    subject,
    producer,
    escapes: [causal.escape('processLifetime')],
    source: 'causal-wait-bridge.test',
  })
const externalProducer = (kind, identity) => causal.externalProducer(kind, identity)
const readDiagnostic = (workspace) =>
  JSON.parse(fs.readFileSync(path.join(workspace, '.Herdchestra', 'diagnostics', 'causal-waits.json'), 'utf8'))

test('WHAT[causal-wait-008] diagnostic bridge writes a non-Journal snapshot file (current behavior pending storage boundary decision)', async () => {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'causal-bridge-'))
  const registry = causal.createRegistry()
  const wait = mkWait(
    'provider-assessment',
    'RelayWorkflow',
    { incumbency: 'I2', road: 'R17' },
    externalProducer('provider', { run: 'P81' }),
  )
  const lease = causal.enter(registry, wait)
  try {
    await causal.writeSnapshot(workspace, registry)
    const filePath = path.join(workspace, '.Herdchestra', 'diagnostics', 'causal-waits.json')
    assert.equal(fs.existsSync(filePath), true)
    const gitignore = fs.readFileSync(path.join(workspace, '.Herdchestra', '.gitignore'), 'utf8')
    assert.equal(gitignore.trim(), '*', 'runtime data must be git-excluded inside .Herdchestra/.gitignore per durable-events [018]')
    const snap = readDiagnostic(workspace)
    assert.equal(typeof snap.pid, 'number')
    assert.ok(String(snap.sequence).length > 0)
    assert.equal(snap.active.length, 1)
    assert.equal(snap.active[0].waitKind, 'provider-assessment')
    assert.ok(Array.isArray(snap.history))
    assert.ok(Array.isArray(snap.frontiers))
    assert.ok(snap.frontiers.length >= 1)
  } finally {
    causal.dispose(lease)
    fs.rmSync(workspace, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { assertOpaque } = await import("../../verification-system/tests/support/js-contract.mjs");

const causal = await import('../../../dist/Execution/Session/Wait/Surface.js')
const owner = (id) => causal.owner('flow', { id })
const descriptor = (id) =>
  causal.createWait({
    waitKind: 'lifecycle-wait',
    owner: owner(id),
    subject: { target: id },
    producer: causal.externalProducer('capability', { id }),
    escapes: [causal.escape('processLifetime')],
    source: 'wait-lifecycle.test',
  })
const lastTransition = (registry) => {
  const history = causal.snapshot(registry).history
  assert.ok(history.length > 0, 'expected history')
  return history.at(-1)
}

test('WHAT[causal-wait-008] a newly constructed registry starts empty', () => {
  const snapshot = causal.snapshot(causal.createRegistry())
  assert.equal(snapshot.active.length, 0)
  assert.equal(snapshot.history.length, 0)
  assert.equal(snapshot.sequence, 0)
})
}

{
  const assert = (await import('node:assert/strict')).default
  const { spawnSync } = await import('node:child_process')
  const { mkdtempSync, rmSync } = await import('node:fs')
  const { join } = await import('node:path')
  const { tmpdir } = await import('node:os')
  const { fileURLToPath } = await import('node:url')
  const fixture = fileURLToPath(new URL('./fixtures/registry-process.fixture.mjs', import.meta.url))
  test('WHAT[causal-wait-008] a new process does not restore waits from the previous diagnostic file', () => {
    const workspace = mkdtempSync(join(tmpdir(), 'wxs-wait-restart-'))
    const run = mode => {
      const result = spawnSync(process.execPath, [fixture, mode, workspace], { encoding: 'utf8', timeout: 3000 })
      assert.equal(result.status, 0, result.stderr || String(result.error))
      return JSON.parse(result.stdout)
    }
    try {
      const previous = run('write')
      assert.equal(previous.active, 1)
      assert.equal(previous.previousPid, previous.pid)
      const next = run('read')
      assert.notEqual(next.pid, previous.pid)
      assert.equal(next.previousPid, previous.pid)
      assert.equal(next.previousActive, 1, 'the old diagnostic file really remains')
      assert.equal(next.active, 0)
      assert.equal(next.history, 0)
    } finally {
      rmSync(workspace, { recursive: true, force: true })
    }
  })
}
