import assert from 'node:assert/strict'
import test from 'node:test'
import { readFileSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '../../..')
const SOURCE_ROOT = join(ROOT, 'src/Wanxiangshu')

const read = (path) => readFileSync(path, 'utf8')

// WHAT[repository-programming-041]：JsToolWorkflow 的 lifecycle 审计
// persistence（Parked/Unparked/Abandoned/Reaped）必须沿生产组装路径接线：
// PluginHostInterop → ToolRegistry.create → JsToolSpec.create →
// JsToolWorkflow.execute，与 jsTransactionPersistence 共用同一
// WorkspaceEventStore。执行链上不得留下硬编码 None。

test('WHAT[repository-programming-041] PluginHostInterop builds lifecycle persistence from the same workspace event store and hands it to ToolRegistry.create', () => {
  const interop = read(join(SOURCE_ROOT, 'OpenCode/Host/PluginHostInterop.fs'))

  assert.match(
    interop,
    /let! commonDirOpt =\s*\n\s*match workspaceDirectory with\s*\n\s*\| Some ws -> task \{ let! dir = RuntimePath\.stateRoot ws in return Some dir \}\s*\n\s*\| None -> Task\.FromResult None\s*\n[\s\S]*?let jsLifecyclePersistence =\s*\n\s*commonDirOpt\s*\n\s*\|\> Option\.bind WorkspaceEventStore\.tryCurrent\s*\n\s*\|\> Option\.map JsToolsTransactionStore\.createLifecyclePersistence/,
    'lifecycle persistence must come from the single durable store owner'
  )
  assert.match(interop, /jsTransactionPersistence\s*\n\s*jsLifecyclePersistence\s*\n\s*continueManagerLoop/)
})

test('WHAT[repository-programming-041] ToolRegistry.create carries jsLifecyclePersistence through to every generated JsToolSpec', () => {
  const registry = read(join(SOURCE_ROOT, 'OpenCode/Tools/ToolRegistry.fs'))
  const registrySignature = read(join(SOURCE_ROOT, 'OpenCode/Tools/ToolRegistry.fsi'))

  assert.match(registrySignature, /jsLifecyclePersistence: IJsLifecyclePersistence option ->/)
  assert.match(registry, /\(jsLifecyclePersistence: IJsLifecyclePersistence option\)/)
  assert.match(
    registry,
    /JsToolSpec\.create\s*\n\s*factory\s*\n\s*surface\s*\n\s*\(defaultArg workspaceDirectory ""\)\s*\n\s*jsTransactionPersistence\s*\n\s*jsLifecyclePersistence/
  )
})

test('WHAT[repository-programming-041] JsToolSpec.execute call site passes the lifecycle persistence instead of a literal None', () => {
  const toolHost = read(join(SOURCE_ROOT, 'Repository/Programming/Js/OpenCode/ToolHost.fs'))

  assert.match(toolHost, /\(lifecyclePersistence: IJsLifecyclePersistence option\)/)
  assert.doesNotMatch(toolHost, /persistence\s*\n\s*None/, 'the execute call site must not hard-code None')
  assert.match(
    toolHost,
    /persistence\s*\n\s*lifecyclePersistence\s*\n\s*\(fileAccessObservation \|> Option\.map \(fun observe -> observe ctx\)\)/
  )
})

test('WHAT[repository-programming-041] the workflow records Parked, Unparked and Abandoned audit events on the execute chain', () => {
  const workflow = read(join(SOURCE_ROOT, 'Repository/Programming/Js/OpenCode/ToolWorkflow.fs'))

  assert.match(workflow, /recordLifecycle lifecyclePersistence JsLifecycleEventType\.Parked site\.Handle site\.SessionId/)
  assert.match(workflow, /recordLifecycle lifecyclePersistence JsLifecycleEventType\.Unparked handle sessionId/)
  assert.match(workflow, /recordLifecycle lifecyclePersistence JsLifecycleEventType\.Abandoned handle sessionId/)
})

test('WHAT[repository-programming-041] host-side ToolHostSurface and WorkflowSurface build lifecycle persistence from their single store', () => {
  const toolHostSurface = read(join(SOURCE_ROOT, 'Repository/Programming/Js/OpenCode/ToolHostSurface.fs'))
  const workflowSurface = read(join(SOURCE_ROOT, 'Repository/Programming/Js/WorkflowSurface.fs'))

  assert.match(toolHostSurface, /Some\(JsTransactionSurface\.lifecyclePersistenceOf store\)/)
  assert.match(toolHostSurface, /JsToolSpec\.create factory surface workspaceRoot persistence lifecyclePersistence None/)
  assert.match(workflowSurface, /Some\(JsTransactionSurface\.lifecyclePersistenceOf store\)/)
  assert.match(workflowSurface, /persistence\s*\n\s*lifecyclePersistence\s*\n\s*None/)
})

test('WHAT[repository-programming-041] all four lifecycle event types live in the single TransactionStore audit stream', () => {
  const store = read(join(SOURCE_ROOT, 'Repository/Programming/Js/TransactionStore.fs'))

  for (const eventType of ['Parked', 'Unparked', 'Abandoned', 'Reaped']) {
    assert.match(store, new RegExp(`JsLifecycleEventType\\.${eventType} -> `))
  }
  assert.match(store, /createLifecyclePersistence/)
  assert.match(store, /appendLifecycleEvent/)
})

test('WHAT[repository-programming-041] PluginSessionScope carries the lifecycle persistence through its construction chain', () => {
  const scope = read(join(SOURCE_ROOT, 'OpenCode/Host/PluginSessionScope.fs'))
  const scopeSignature = read(join(SOURCE_ROOT, 'OpenCode/Host/PluginSessionScope.fsi'))
  const runtime = read(join(SOURCE_ROOT, 'OpenCode/Host/PluginRuntimeScope.fs'))
  const boot = read(join(SOURCE_ROOT, 'OpenCode/Plugin/PluginBoot.fs'))

  assert.match(
    scopeSignature,
    /jsLifecyclePersistence: Wanxiangshu\.Repository\.Programming\.Js\.IJsLifecyclePersistence option/
  )
  assert.match(scope, /jsLifecyclePersistence: IJsLifecyclePersistence option/)
  assert.match(
    runtime,
    /jsLifecyclePersistence: Wanxiangshu\.Repository\.Programming\.Js\.IJsLifecyclePersistence option/
  )
  assert.match(runtime, /PluginSessionScope\(journal, isModelLeaseExternallyOwned, jsLifecyclePersistence\)/)
  assert.match(
    boot,
    /\|\> Option\.map Wanxiangshu\.Repository\.Programming\.Js\.JsToolsTransactionStore\.createLifecyclePersistence/
  )
})

test('WHAT[repository-programming-041] ClearSession records a Reaped audit event per reaped handle with close-isolated failure handling', () => {
  const scope = read(join(SOURCE_ROOT, 'OpenCode/Host/PluginSessionScope.fs'))
  const parkedSites = read(join(SOURCE_ROOT, 'Repository/Programming/Js/OpenCode/ParkedSites.fs'))

  // The handles are enumerated before reapSession consumes the sites.
  assert.match(scope, /let reapedHandles = JsParkedSites\.handlesForSession sessionId/)
  assert.match(scope, /do! attempt \(fun \(\) -> JsParkedSites\.reapSession sessionId\)/)
  assert.match(scope, /JsLifecycleEventType\.Reaped/)
  assert.match(scope, /durable\.AppendLifecycleEvent event/)
  assert.doesNotMatch(
    parkedSites,
    /appendLifecycleEvent/,
    'the parked table stays a process-live owner; the durable Reaped audit belongs to the caller'
  )
})

// ── 行为级落盘用例（GAP-2）──────────────────────────────────────────────────
// 上面的形状断言证明接线存在；本用例证明运行期真实落盘：store 以实参进入
// execute 调用链（与 040 的 rm/mv durable 用例同一传递形态），lifecycle
// persistence 从该 store 同源构造，park 现场必须在该 store 的目录里留下
// 携带本次 handle 的记录字节。TransactionSurface 依 durable-events-015
// 故意不暴露历史读取，所以证据面是存储的物理字节，不是另一份 API。
// recordLifecycle 被移除、persistence 断链或 handle 不进载荷时，本用例即红。

import { mkdtempSync, readdirSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { randomUUID } from 'node:crypto'
import { execute, caseName, parkedHandle } from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import { create as createEventStore, dispose as disposeEventStore } from '../../../dist/Persistence/EventStore/Surface.js'

const walkFiles = (directory, found = []) => {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory()) walkFiles(join(directory, entry.name), found)
    else found.push(join(directory, entry.name))
  }
  return found
}

test('WHAT[repository-programming-041] park, unpark and abandon through the wired store append real Parked/Unparked/Abandoned records into that store', async () => {
  const workspace = mkdtempSync(join(tmpdir(), 'wxs-041-park-'))
  const storeDir = mkdtempSync(join(tmpdir(), 'wxs-041-store-'))
  const store = createEventStore(storeDir, randomUUID().replaceAll('-', ''))
  try {
    const program = `class Js extends JsProgram {
  async run() {
    const decision = await this.park({ stage: 'durable-041' });
    return { decided: decision.value };
  }
}`
    const call = (args) =>
      execute(workspace, 'Coder', 'en', 'durable-park-041', args, 2000, Date.now() + 60_000, 1 << 20, store)

    const parked = await call({ program })
    assert.equal(caseName(parked), 'Parked')
    const handle = parkedHandle(parked)
    assert.ok(typeof handle === 'string' && handle.length > 0)

    const resumed = await call({ unpark: { handle, value: { go: 'ahead' } } })
    assert.equal(caseName(resumed), 'Succeeded', 'unpark must resume the parked program')

    // 第二次 park 以 abandon 终结：Parked/Unparked/Abandoned 三类事件同住
    // 同一审计流，全部经真实 execute 调用链写入同一个 store。
    const parked2 = await call({ program })
    assert.equal(caseName(parked2), 'Parked')
    const secondHandle = parkedHandle(parked2)
    const abandoned = await call({ abandon: { handle: secondHandle } })
    assert.equal(caseName(abandoned), 'Failed', 'abandon ends with a Failed outcome (Abandoned, no value)')

    // 恢复路径不得依赖同一进程内存：每条记录都必须已写进 store 目录的字节。
    // lifecycle append 是 best-effort 异步任务（recordLifecycle 不阻塞工具
    // 返回），沿用 5 秒轮询、50ms 步进；超时仍未落盘则失败并列出缺失项，
    // 断言强度不变。TransactionSurface 依 durable-events-015 不暴露历史读取，
    // 所以证据面是存储的物理字节（.ndjson 明文），不是另一份 API。
    const needles = [handle, secondHandle, 'JsLifecycleParked', 'JsLifecycleUnparked', 'JsLifecycleAbandoned']
    const deadline = Date.now() + 5000
    for (;;) {
      const blobs = walkFiles(storeDir).map((file) => readFileSync(file))
      const missing = needles.filter((needle) => !blobs.some((bytes) => bytes.includes(needle, 'utf8')))
      if (missing.length === 0) break
      if (Date.now() > deadline) {
        assert.fail(`durable lifecycle records missing after 5s polling: ${missing.join(', ')}`)
      }
      await new Promise((resolve) => setTimeout(resolve, 50))
    }
  } finally {
    disposeEventStore(store)
    rmSync(storeDir, { recursive: true, force: true })
    rmSync(workspace, { recursive: true, force: true })
  }
})