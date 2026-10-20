import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync, readFileSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '../../..')
const SOURCE_ROOT = join(ROOT, 'src/Wanxiangshu')

const read = (path) => readFileSync(path, 'utf8')

// Algorithm G（PLAN S3）：泄漏兜底三件套——交付硬门禁、每回合提醒、会话终结
// 强制 reap。本文件验证调用侧接入点与双语资源成对性。现场表与 reap/审计
// append 接口由 lane-a-core / lane-b-durable 定义，本文件不重复定义它们，
// 只核对本任务独占文件中的调用契约与资源存在性。

test('WHAT[repository-programming-031] deliver hard gate is wired at the deliverSpec execute chain', () => {
  const registry = read(join(SOURCE_ROOT, 'OpenCode/Tools/ToolRegistry.fs'))

  // G1 门禁路径常量与资源路径一致。
  assert.match(registry, /DeliverBlockedByParked = "tool\/js-program\/parked-deliver-blocked"/)

  // 门禁调用 lane-a-core 的只读查询接口，且位于 deliver 执行链（plan/manager
  // 分派之前），拒绝时列出全部 handle。
  assert.match(registry, /let parkedHandles = JsParkedSites\.parkedHandles \(\)/)
  assert.match(registry, /if not \(List\.isEmpty parkedHandles\)/)
  assert.match(registry, /parked_handles/, 'refusal must carry the parked handle list')
  assert.match(registry, /Path\.DeliverBlockedByParked/)
  assert.match(registry, /Some \[ "S1"; "S2"; "S3" \] -> return! planSpec\.Execute args ctx/)
})

test('WHAT[repository-programming-031] parked reminder rides the PairHint composeMarkerDocument', () => {
  const transform = read(join(SOURCE_ROOT, 'OpenCode/Host/PairProgrammingThoughtTransform.fs'))

  // G2 资源路径常量。
  assert.match(transform, /ParkedHintPath = "tool\/js-program\/parked-hint"/)

  // 提醒并入 composeMarkerDocument：查询全部未表态 handle，非空时渲染提醒并
  // 追加为 marker 文档指令。无载体回合不走到 composeMarkerDocument（由既有
  // decideCurrentPlacement 保证），因此不会凭空注入。
  assert.match(transform, /let parkedHandles = JsParkedSites\.parkedHandles \(\)/)
  assert.match(transform, /LlmFacing\.withInstructions parkedHint/)
  assert.match(transform, /ProviderProse\.render language ParkedHintPath/)
})

test('WHAT[repository-programming-031] session close force-reaps parked sites owned by the session', () => {
  const scope = read(join(SOURCE_ROOT, 'OpenCode/Host/PluginSessionScope.fs'))

  // G3：ClearSession 清理链中调用 lane-a-core 的 reap 接口，对 session 名下
  // 全部 Parked 现场执行算法 F，durable 记 Reaped。
  assert.match(scope, /JsParkedSites\.reapSession sessionId/)
  assert.match(scope, /do! attempt \(fun \(\) -> JsParkedSites\.reapSession sessionId\)/)
})

test('WHAT[repository-programming-031] parked reminder and deliver-block resources are paired bilingual files', () => {
  for (const name of [ 'parked-deliver-blocked', 'parked-hint' ]) {
    const zh = join(ROOT, `resources/provider/tool/js-program/${name}/zh-CN.md`)
    const en = join(ROOT, `resources/provider/tool/js-program/${name}/en.md`)
    assert.equal(existsSync(zh), true, `${name}/zh-CN.md must exist`)
    assert.equal(existsSync(en), true, `${name}/en.md must exist`)

    // provider-language-006/007: 双语资源成对，且占位符集合一致。
    const zhText = read(zh)
    const enText = read(en)
    const placeholders = (text) => [ ...text.matchAll(/\{\{(\w+)\}\}/g) ].map((m) => m[1]).sort()
    assert.deepEqual(placeholders(zhText), placeholders(enText), `${name} placeholder parity`)
    assert.ok(zhText.includes('{{handles}}'), `${name}/zh-CN.md must carry {{handles}}`)
    assert.ok(enText.includes('{{handles}}'), `${name}/en.md must carry {{handles}}`)
  }
})

test('WHAT[repository-programming-031] the three call sites stay within the owned files and do not define the parked table', () => {
  const registry = read(join(SOURCE_ROOT, 'OpenCode/Tools/ToolRegistry.fs'))
  const transform = read(join(SOURCE_ROOT, 'OpenCode/Host/PairProgrammingThoughtTransform.fs'))
  const scope = read(join(SOURCE_ROOT, 'OpenCode/Host/PluginSessionScope.fs'))

  // 调用方身份：只调用 JsParkedSites，不定义现场表或 reap 接口本身。
  for (const source of [ registry, transform, scope ]) {
    assert.doesNotMatch(source, /type JsParkedSite\b/, 'caller must not define the parked site type')
    assert.doesNotMatch(source, /module JsParkedSites\s*=|module JsParkedSites\b[^\n]*=/, 'caller must not define the parked site table module')
  }
})

// ── 行为级三用例（GAP-2）：真现场、真调用、真拒绝，不用源码形状冒充 ──────────
// 上面的接线用例继续守护调用点位置；下面三个用例守护同一批语义在真实执行下的
// 效果。park 现场通过 WorkflowSurface 的真实沙箱执行进入进程级现场表
// （JsParkedSites），与 plugin fixture 的 deliver / messages.transform 在
// 同一进程共享这张表——这正是被测耦合本身。

import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { withExecutablePlugin, acceptAuthorityRoot } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { scores, withReview } from '../../work/tests/support/plugin.mjs'
import {
  execute as executeWorkflow,
  caseName,
  parkedHandle,
} from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import {
  handlesForSession,
  parkedHandles,
  reapSession,
  clearAll,
} from '../../../dist/Repository/Programming/Js/OpenCode/ParkedSites.js'

const parkProgram = (tag) => `class Js extends JsProgram {
  async run() {
    const decision = await this.park({ stage: '${tag}' });
    return { decided: decision.value };
  }
}`

const parkOne = async (dir, sessionId, tag) => {
  const outcome = await executeWorkflow(dir, 'Engineer', 'en', sessionId, { program: parkProgram(tag) }, 2000, Date.now() + 60_000, 1 << 20, null)
  assert.equal(caseName(outcome), 'Parked', `park for ${tag} must produce a real parked site`)
  const handle = parkedHandle(outcome)
  assert.ok(typeof handle === 'string' && handle.length > 0, 'the parked payload must carry a non-empty handle')
  return handle
}

test('WHAT[repository-programming-031] a real parked handle blocks deliver and the refusal names the handle', async () => {
  // 权威 bootstrap 移植自 work/011：acceptAuthorityRoot + chat.message +
  // openIncumbency(S4) + review Promoted，deliver 使用两段 confirm 上下文。
  // Execute 之前有 by-design 的调用方权威准入；先建立合法权威，parked 门禁
  // 才谈得上被触达。断言据此区分"parked 拒绝"（具体 code + 真 handle）与
  // "权威缺失"的统一拒绝文案。
  await withReview(async ({ execute, hooks, runtime, session }) => {
    assert.match(await execute(scores('PERFECT')), /recorded = true/, 'the reviewed tenure establishes the deliver authority')
    runtime.pushHostMessage(session, {
      info: { id: 'parked-gate-run', role: 'assistant', sessionID: session, parentID: 'user-root', time: { created: 9 } },
      parts: [{ type: 'tool', tool: 'deliver', callID: 'deliver-parked', state: { status: 'pending', input: {} } }],
    })
    const context = { sessionID: session, agent: 'manager', callID: 'deliver-parked', messageID: 'parked-gate-run', providerRunID: 'run-parked' }

    const handle = await parkOne(runtime.directory, 'parked-gate-js-031', 'deliver-gate')
    try {
      const refused = await hooks.tool.deliver.execute({}, context)
      assert.match(refused, /parked-deliver-blocked/, 'deliver must reach the parked gate and be refused with its code')
      assert.ok(String(refused).includes(handle), `refusal must list the live handle ${handle}`)

      // 反向对照：清空现场后，同一合法权威的另一次调用直接落到正常两段确认
      // （confirmation_required），不再得到 parked 拒绝。这既证明上下文本来
      // 就能触达业务语义（不是权威拒绝冒充门禁），也排除"恒拒"假门禁。
      clearAll()
      const released = await hooks.tool.deliver.execute({}, { ...context, callID: 'deliver-cleared', messageID: 'cleared-run', providerRunID: 'run-cleared' })
      assert.ok(!String(released).includes('parked-deliver-blocked'), 'cleared table must release the deliver gate')
      assert.match(released, /confirmation_required = true/, 'the released call must land on the real two-stage deliver path')
    } finally {
      clearAll()
    }
  }, { session: 'parked-deliver-gate-031' })
})

test('WHAT[repository-programming-031] a live parked handle is folded into the PairHint marker by the real transform', async () => {
  // transform 处在 provider 启动前的物理边界（host-boundary-008）：没有按
  // exact (session, physical message) 建立的合法执行就抛
  // AcceptedExecutionMissing，走不到 composeMarkerDocument。所以 bootstrap
  // 逐字复制 work/011 的成功先例：acceptAuthorityRoot + pushHostMessage +
  // chat.message + 真实 owning assistant 执行消息 + 载体回合 transform。
  const bootstrap = async (hooks, runtime, session) => {
    const rootID = `root-${session}`
    const root = { id: rootID, role: 'user', parts: [{ type: 'text', text: 'Continue the parked review.' }] }
    await acceptAuthorityRoot(runtime, session, 'manager')
    runtime.pushHostMessage(session, root)
    await hooks['chat.message']({ sessionID: session, messageID: rootID, agent: 'manager' }, { message: root, parts: root.parts })
    runtime.pushHostMessage(session, {
      info: { id: `run-${session}`, role: 'assistant', sessionID: session, parentID: rootID, time: { created: 1 } },
      parts: [],
    })
    return { info: { id: rootID, role: 'user', sessionID: session }, parts: root.parts }
  }

  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const session = 'parked-hint-031'
    try {
      const user = await bootstrap(hooks, runtime, session)
      const handle = await parkOne(runtime.directory, 'parked-hint-js-031', 'pair-hint')

      const messages = [user]
      await hooks['experimental.chat.messages.transform']({ sessionID: session }, { messages })

      // composeMarkerDocument（私有，经生产注入链）必须把在场 handle 并入
      // marker：载体消息的 cursor 后缀（NUL+BOM 之后）含该真 handle。
      const text = messages[0].parts[0].text
      assert.ok(text.includes('\u0000\ufeff'), 'the pair hint must ride the cursor suffix on the carrier message')
      assert.ok(text.includes(handle), `the marker document must list the parked handle ${handle}`)

      // 对照：另一个同样合法 bootstrap 的会话、现场已清空——注入链照走
      // （cursor 后缀在场）但不出现任何 park- handle。
      clearAll()
      const clean = 'parked-hint-031-clean'
      const second = await bootstrap(hooks, runtime, clean)
      const cleanMessages = [second]
      await hooks['experimental.chat.messages.transform']({ sessionID: clean }, { messages: cleanMessages })
      // replaceMessagesInPlace 就地突变传入数组、元素被克隆件替换，
      // 所以呈现字节只从 cleanMessages[0] 读，second 原对象不携带后缀。
      const cleanText = cleanMessages[0].parts[0].text
      assert.ok(cleanText.includes('\u0000\ufeff'), 'the clean round must still run the injection chain')
      assert.ok(!cleanText.includes('park-'), 'a cleared table must not inject any parked reminder')
    } finally {
      clearAll()
    }
  })
})

test('WHAT[repository-programming-031] reapSession consumes every site of its session from the real table', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-reap-031-'))
  try {
    const session = 'reap-session-031'
    const handle = await parkOne(dir, session, 'reap-site')

    // 现场携带 session owner：按会话枚举能看见它（G3 的 durable 记账输入）。
    assert.deepEqual(Array.from(handlesForSession(session)), [ handle ], 'the site must be owned by its session')

    // 真实现场表喂给 reap：清场必须发生在全局查询里，不只是本会话视图。
    await reapSession(session)
    assert.ok(!Array.from(parkedHandles()).includes(handle), 'reapSession must consume the parked site from the table')
    assert.deepEqual(Array.from(handlesForSession(session)), [], 'no site of the reaped session may survive')
  } finally {
    clearAll()
    rmSync(dir, { recursive: true, force: true })
  }
})
