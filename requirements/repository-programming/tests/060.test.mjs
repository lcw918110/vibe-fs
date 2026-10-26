import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync, writeFileSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { randomUUID } from 'node:crypto'
import { execute, caseName, failureCode, rewritten } from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import { deliveredValue, deliveredViewOf, clearDeliveredValue } from '../../../dist/Repository/Programming/Js/RuntimeSurface.js'
import { create as createEventStore, dispose as disposeEventStore, createAppendFailureStore } from '../../../dist/Persistence/EventStore/Surface.js'

// LC-007: 工具结算失败（如 ledger append 失败）时，不得留下冒充成功的交付见证；已落盘文件变化如实保留。
const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-060-work-'))
  const storeDir = mkdtempSync(join(tmpdir(), 'wxs-060-store-'))
  const baseStore = createEventStore(storeDir, randomUUID().replaceAll('-', ''))
  return {
    dir,
    baseStore,
    cleanup: () => {
      disposeEventStore(baseStore)
      rmSync(storeDir, { recursive: true, force: true })
      rmSync(dir, { recursive: true, force: true })
    },
  }
}

const program = (body) => `class Js extends JsProgram {
  async run() {
${body}
  }
}`

test('WHAT[repository-programming-060] LC-007: failure during ledger settlement leaves deliveredValue and deliveredView empty', async () => {
  const { dir, baseStore, cleanup } = sandbox()
  const sessionId = 'sess-060-ledger-fail'
  try {
    clearDeliveredValue(sessionId)
    writeFileSync(join(dir, 'effect.txt'), 'initial', 'utf8')

    const failStore = createAppendFailureStore(baseStore, {
      code: 'AppendNotAttempted',
      phase: 'BeforePhysicalAppend',
      cause: new Error('simulated ledger append error'),
      failAt: 1,
    }, () => {})

    const outcome = await execute(
      dir,
      'Coder',
      'en',
      sessionId,
      {
        name: 'ledger-failure',
        intent: 'Wait for physical file effects and their durable ledger settlement.',
        program: program(`
    this.write('effect.txt', 'mutated-data');
    return { done: true };
`),
      },
      3000,
      Date.now() + 60_000,
      1 << 20,
      failStore,
    )

    // 工具整体返回 Failed
    assert.equal(caseName(outcome), 'Failed')
    assert.equal(failureCode(outcome), 'TRANSACTION_PREPARE_NOT_ATTEMPTED')

    // 交付见证为空：deliveredValue / deliveredViewOf 必须为 null
    assert.equal(deliveredValue(sessionId), null, 'delivered value must be null after ledger failure')
    assert.equal(deliveredViewOf(sessionId), null, 'delivered view must be null after ledger failure')

    // 已落盘文件变化如实保留，不使用回滚文件假装副作用未发生
    assert.deepEqual(rewritten(outcome), ['effect.txt'])
    assert.equal(readFileSync(join(dir, 'effect.txt'), 'utf8'), 'mutated-data', 'file effect must land as-is')
  } finally {
    clearDeliveredValue(sessionId)
    cleanup()
  }
})

test('WHAT[repository-programming-060] LC-007: commit phase append failure also leaves deliveredValue empty while preserving effects', async () => {
  const { dir, baseStore, cleanup } = sandbox()
  const sessionId = 'sess-060-commit-fail'
  try {
    clearDeliveredValue(sessionId)
    writeFileSync(join(dir, 'target.txt'), 'before', 'utf8')

    // failAt = 2: 第一次 Prepared 成功，第二次 Committed 失败
    const failStore = createAppendFailureStore(baseStore, {
      code: 'CommitUnknown',
      phase: 'DurabilityBarrier',
      cause: new Error('simulated commit failure'),
      failAt: 2,
    }, () => {})

    const outcome = await execute(
      dir,
      'Coder',
      'en',
      sessionId,
      {
        name: 'ledger-failure',
        intent: 'Wait for physical file effects and their durable ledger settlement.',
        program: program(`
    this.write('target.txt', 'after');
    return { done: true };
`),
      },
      3000,
      Date.now() + 60_000,
      1 << 20,
      failStore,
    )

    assert.equal(caseName(outcome), 'Failed')
    assert.equal(failureCode(outcome), 'TRANSACTION_COMMIT_UNKNOWN')
    assert.equal(deliveredValue(sessionId), null)
    assert.equal(deliveredViewOf(sessionId), null)
    assert.deepEqual(rewritten(outcome), ['target.txt'])
    assert.equal(readFileSync(join(dir, 'target.txt'), 'utf8'), 'after')
  } finally {
    clearDeliveredValue(sessionId)
    cleanup()
  }
})
