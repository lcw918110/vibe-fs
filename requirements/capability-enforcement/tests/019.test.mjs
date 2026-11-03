import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { superviseNodeTest } from '../../verification-system/tests/e2e/support/supervise-node-test.mjs'
import { UNIT_VERDICT_SILENCE_MS } from '../../verification-system/tests/e2e/support/time-budget.js'
import {
  assertNativeSerializationRejected, assertCodecSerializationRejected,
  createDurableMaterial, assertMaterialsCannotAdmit, assertLeaseNonDurability,
} from './support/quiescence-durability.mjs'
import { assertJsData, assertOpaque } from '../../verification-system/tests/support/js-contract.mjs'
import * as quiescence from '../../../dist/OpenCode/Host/QuiescenceSurface.js'

const SESSION = 'ses-process-capability'

const accepted = { accepted: true, failure: null }

const rejected = (failure) => ({ accepted: false, failure })

const assertResult = (actual, expected, label = 'quiescence result') => {
  assertJsData(actual, label)
  assert.deepEqual(actual, expected, label)
}

const freshPermit = (gate, session = SESSION) => {
  assertOpaque(gate, 'quiescence gate')
  quiescence.beginAttempt(gate, session)
  const permit = quiescence.observeIdle(gate, session)
  assertOpaque(permit, 'quiescence permit')
  return permit
}

test('WHAT[capability-enforcement-019] a different gate requires its own current-attempt admission', () => {
  const priorGate = quiescence.create()
  const priorPermit = freshPermit(priorGate)
  const currentGate = quiescence.create()

  assertResult(quiescence.tryConsume(currentGate, priorPermit), rejected('WrongOwner'))

  const unownedIdle = quiescence.observeIdle(currentGate, SESSION)
  assertOpaque(unownedIdle, 'unowned idle permit')
  assertResult(quiescence.tryConsume(currentGate, unownedIdle), rejected('NoFreshIdle'))

  const currentPermit = freshPermit(currentGate)
  assertResult(quiescence.tryConsume(currentGate, currentPermit), accepted)
})

test('WHAT[capability-enforcement-019] native JSON refuses direct and object-array nested live permits without consuming or revoking them', () => {
  const gate = quiescence.create()
  const permit = freshPermit(gate)
  assertNativeSerializationRejected(permit)
  assertResult(quiescence.tryConsume(gate, permit), accepted)
  assertNativeSerializationRejected(permit)
  assertResult(quiescence.tryRelease(gate, permit), accepted)
  assertResult(quiescence.tryConsume(gate, permit), accepted)
  const current = freshPermit(gate)
  assertResult(quiescence.tryConsume(gate, permit), rejected('Superseded'))
  assertResult(quiescence.tryConsume(gate, current), accepted)
})

test('WHAT[capability-enforcement-019] real Fact Journal and Event codecs refuse live permission while durable evidence round-trips without authority', () => {
  const gate = quiescence.create()
  const permit = freshPermit(gate)
  const material = createDurableMaterial()
  assertCodecSerializationRejected(permit, material)
  assertMaterialsCannotAdmit(gate, material)
  assertResult(quiescence.tryConsume(gate, permit), accepted, 'codec refusals and restored data must not consume a live opportunity')
})

test('WHAT[capability-enforcement-019] actual execution lease tokens refuse native JSON and codecs without restoring authority from durable receipts', async () => {
  await assertLeaseNonDurability(createDurableMaterial())
})

test('WHAT[capability-enforcement-019] two supervised independent Node processes require fresh admission after durable material crosses the exit boundary', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'quiescence-process-boundary-'))
  const fixture = fileURLToPath(new URL('./support/process-capability.fixture.mjs', import.meta.url))
  const env = { ...process.env, WXS_CAPABILITY_FIXTURE_DIRECTORY: directory, NODE_TEST_CONCURRENCY: '1' }
  delete env.NODE_TEST_CONTEXT
  try {
    const producerSummary = await superviseNodeTest({
      files: [fixture], label: 'capability-019-producer', logPrefix: 'capability-019-producer',
      silenceMs: UNIT_VERDICT_SILENCE_MS, env: { ...env, WXS_CAPABILITY_FIXTURE_PHASE: 'produce' },
      throwOnFailure: true,
    })
    assert.deepEqual(producerSummary, { passed: 1, failed: 0 })
    const materialPath = join(directory, 'durable-material.json')
    const materialBytes = readFileSync(materialPath, 'utf8')
    const producer = JSON.parse(readFileSync(join(directory, 'producer.json'), 'utf8'))
    assert.ok(Number.isSafeInteger(producer.pid) && producer.pid > 0)
    assert.notEqual(producer.pid, process.pid)
    assert.equal(producer.session, SESSION)

    const successorSummary = await superviseNodeTest({
      files: [fixture], label: 'capability-019-successor', logPrefix: 'capability-019-successor',
      silenceMs: UNIT_VERDICT_SILENCE_MS, env: { ...env, WXS_CAPABILITY_FIXTURE_PHASE: 'recover' },
      throwOnFailure: true,
    })
    assert.deepEqual(successorSummary, { passed: 1, failed: 0 })
    const successor = JSON.parse(readFileSync(join(directory, 'successor.json'), 'utf8'))
    assert.ok(Number.isSafeInteger(successor.pid) && successor.pid > 0)
    assert.notEqual(successor.pid, process.pid)
    assert.deepEqual(successor, {
      pid: successor.pid, session: SESSION,
      historicalAdmission: rejected('NoFreshIdle'),
      freshAdmission: accepted, duplicateAdmission: rejected('AlreadyConsumed'),
    })
    assert.equal(readFileSync(materialPath, 'utf8'), materialBytes)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test.todo('WHAT[capability-enforcement-019] remaining PhysicalHandle boundaries include SessionQuiescenceGate and ModelRouting RuntimeHandle and PortHandle; their native serialization and restore consumers are not covered by the permit and execution-lease proofs')

test('WHAT[capability-enforcement-019] live opaque permit resources stay bounded to the current session attempt', () => {
  const gate = quiescence.create()
  assert.equal(quiescence.livePermitCount(gate), 0)

  for (let attempt = 0; attempt < 256; attempt += 1) {
    quiescence.beginAttempt(gate, SESSION)
    const first = quiescence.observeIdle(gate, SESSION)
    const replay = quiescence.observeIdle(gate, SESSION)
    assertOpaque(first, 'current idle permit')
    assertOpaque(replay, 'replayed current idle permit')
    assert.equal(quiescence.livePermitCount(gate), 1, 'idle replay and attempt churn must not grow the resource registry')
  }

  quiescence.revoke(gate, SESSION)
  assert.equal(quiescence.livePermitCount(gate), 0, 'revocation must release current-attempt permit resources')

  freshPermit(gate)
  quiescence.dropSession(gate, SESSION)
  assert.equal(quiescence.livePermitCount(gate), 0, 'session cleanup must release permit resources')
})
