import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync, readFileSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '../../..')
const SOURCE_ROOT = join(ROOT, 'src/Wanxiangshu')

const read = (path) => readFileSync(path, 'utf8')

// WHAT[repository-programming-043]：rm/mv 双语资源与四层接线契约。
// rm/mv 成员文案是 tool/js-program/rm、tool/js-program/mv 的 zh-CN.md/en.md
// 成对双语资源，两语言陈述同一契约要素——签名、POSIX 语义、as-is 即时
// 生效、EXDEV 拒绝且不留两侧并存中间态——且不携带未替换占位符。四层接线
// 保持同构：JsFragmentRegistry 的 rm/mv fragment（含 js.rm/js.mv
// RuntimeBindingKey）→ ToolsBindings 的成员绑定 → Description 的资源路径
// 与 capability 条件规则块 → ToolHost 的双语装载。底层文件系统效果与稳定
// 失败映射归 MutationFs 原语（removePath/movePath），不在成员绑定内二次
// 定义。本文件为纯静态断言（读源码与资源文本），不 import dist 产物。

const resourcePath = (name) => ({
  zh: join(ROOT, `resources/provider/tool/js-program/${name}/zh-CN.md`),
  en: join(ROOT, `resources/provider/tool/js-program/${name}/en.md`),
})

// provider-language-006/007：占位符集合按 {{name}} 形态提取比较。
const placeholders = (text) => [...text.matchAll(/\{\{(\w+)\}\}/g)].map((m) => m[1]).sort()

test('WHAT[repository-programming-043] rm and mv ship paired bilingual resources with identical placeholder sets and no unreplaced residue', () => {
  for (const name of ['rm', 'mv']) {
    const { zh, en } = resourcePath(name)
    assert.equal(existsSync(zh), true, `${name}/zh-CN.md must exist`)
    assert.equal(existsSync(en), true, `${name}/en.md must exist`)

    const zhText = read(zh)
    const enText = read(en)
    assert.deepEqual(placeholders(zhText), placeholders(enText), `${name} placeholder parity`)
    // 资源是即用文案：不得残留未替换的 {{...}} 占位符。
    assert.ok(!zhText.includes('{{'), `${name}/zh-CN.md must not carry unreplaced placeholders`)
    assert.ok(!enText.includes('{{'), `${name}/en.md must not carry unreplaced placeholders`)
  }
})

test('WHAT[repository-programming-043] both languages state the rm contract elements: signature, empty-directory POSIX semantics and as-is immediacy', () => {
  const zh = read(resourcePath('rm').zh)
  const en = read(resourcePath('rm').en)

  // 签名。
  assert.ok(zh.includes('rm(path)'), 'zh-CN must state the rm(path) signature')
  assert.ok(en.includes('rm(path)'), 'en must state the rm(path) signature')
  // POSIX 语义：删文件或空目录，拒绝非空目录。
  assert.ok(zh.includes('空目录'), 'zh-CN must state the empty-directory semantics')
  assert.ok(zh.includes('非空'), 'zh-CN must state the non-empty refusal')
  assert.ok(en.includes('empty directory'), 'en must state the empty-directory semantics')
  assert.ok(en.includes('non-empty'), 'en must state the non-empty refusal')
  // as-is 即时生效。
  assert.ok(zh.includes('即删即生效'), 'zh-CN must state the as-is immediate effect')
  assert.ok(en.includes('Takes effect immediately'), 'en must state the as-is immediate effect')
})

test('WHAT[repository-programming-043] both languages state the mv contract elements: signature, POSIX mv semantics and EXDEV refusal without a two-sided intermediate state', () => {
  const zh = read(resourcePath('mv').zh)
  const en = read(resourcePath('mv').en)

  // 签名。
  assert.ok(zh.includes('mv(from, to)'), 'zh-CN must state the mv(from, to) signature')
  assert.ok(en.includes('mv(from, to)'), 'en must state the mv(from, to) signature')
  // POSIX 语义：POSIX mv、支持覆盖。
  assert.ok(zh.includes('POSIX mv'), 'zh-CN must state the POSIX mv semantics')
  assert.ok(zh.includes('覆盖'), 'zh-CN must state overwrite is allowed')
  assert.ok(en.includes('POSIX mv'), 'en must state the POSIX mv semantics')
  assert.ok(en.includes('overwrite'), 'en must state overwrite is allowed')
  // EXDEV：跨设备移动报错并回滚，不留两侧并存的中间态。
  assert.ok(zh.includes('EXDEV'), 'zh-CN must state the EXDEV refusal')
  assert.ok(zh.includes('中间态'), 'zh-CN must state no two-sided intermediate state survives')
  assert.ok(en.includes('EXDEV'), 'en must state the EXDEV refusal')
  assert.ok(en.includes('never leaving both sides present'), 'en must state no two-sided intermediate state survives')
})

test('WHAT[repository-programming-043] JsFragmentRegistry carries the rm/mv fragments with js.rm and js.mv runtime binding keys', () => {
  const capability = read(join(SOURCE_ROOT, 'Repository/Programming/Js/Capability.fs'))

  // rm fragment：Remove capability、成员名与签名同处一个 fragment。
  assert.match(
    capability,
    /let rm: JsCapabilityFragment =\s*\n\s*\{ Capability = JsCapability\.Remove\s+MemberName = "rm"\s+Signature = "rm\(path\)"/,
  )
  assert.match(capability, /RuntimeBindingKey = "js\.rm" }/, 'the rm fragment must bind js.rm')

  // mv fragment：Move capability、成员名与签名同处一个 fragment。
  assert.match(
    capability,
    /let mv: JsCapabilityFragment =\s*\n\s*\{ Capability = JsCapability\.Move\s+MemberName = "mv"\s+Signature = "mv\(from, to\)"/,
  )
  assert.match(capability, /RuntimeBindingKey = "js\.mv" }/, 'the mv fragment must bind js.mv')

  // 两个 fragment 进入确定性投影的 canonical 成员序。
  const allBlock = capability.match(/let all: JsCapabilityFragment list =\s*\[([\s\S]*?)\]/)
  assert.ok(allBlock, 'the fragment registry must declare the canonical member list')
  assert.deepEqual(
    allBlock[1].split(/\s+/).filter(Boolean),
    ['write', 'rm', 'mv', 'cat', 'ls', 'rg', 'stat', 'mkdir', 'whereAll', 'where', 'call', 'fork', 'dag', 'join', 'fission', 'horizon', 'advisor', 'park'],
  )
})

test('WHAT[repository-programming-043] ToolsBindings binds the rm/mv members behind Remove/Move capabilities and delegates the effects to MutationFs primitives', () => {
  const bindings = read(join(SOURCE_ROOT, 'Repository/Programming/Js/ToolsBindings.fs'))

  // 成员绑定存在。
  assert.match(bindings, /let private makeRmMember/)
  assert.match(bindings, /"rm"\s*\n\s*==> fun \(path: string\) ->/)
  assert.match(bindings, /let private makeMvMember/)
  assert.match(bindings, /"mv"\s*\n\s*==> fun \(fromPath: string\) \(toPath: string\) ->/)

  // capability 门禁挂载：运行时按 Remove/Move 投影成员。
  assert.match(
    bindings,
    /if Set\.contains JsCapability\.Remove capabilities then\s*\n\s*makeRmMember root staging readSnapshots ledger/,
  )
  assert.match(
    bindings,
    /if Set\.contains JsCapability\.Move capabilities then\s*\n\s*makeMvMember root staging readSnapshots ledger/,
  )

  // 底层文件系统效果归 MutationFs 原语，成员绑定不二次定义原语本身。
  assert.match(bindings, /JsMutationFs\.removePath full/)
  assert.match(bindings, /JsMutationFs\.movePath fromFull toFull/)
  assert.doesNotMatch(
    bindings,
    /let\s+(private\s+)?(removePath|movePath|removeSync|renameSync)\b/,
    'the member bindings must not define the filesystem primitives',
  )
})

test('WHAT[repository-programming-043] Description declares the rm/mv resource paths and mounts them as capability-conditioned rule blocks', () => {
  const description = read(join(SOURCE_ROOT, 'Repository/Programming/Js/Description.fs'))

  // 资源路径常量与盘上资源位置一致。
  assert.match(description, /let Rm = "tool\/js-program\/rm"/)
  assert.match(description, /let Mv = "tool\/js-program\/mv"/)

  // capability 条件规则块：rulesFor 按 Remove/Move 投影 prose.Rm/prose.Mv。
  assert.match(
    description,
    /if has capabilities JsCapability\.Remove then\s*\n\s*prose\.Rm\s*\n\s*if has capabilities JsCapability\.Move then\s*\n\s*prose\.Mv/,
  )

  // Prose record 携带 Rm/Mv 文案字段，装载结果有确定的落点。
  assert.match(description, /Rm: string\s*\n\s*Mv: string/)
})

test('WHAT[repository-programming-043] ToolHost loads the rm/mv prose bilingually through the same resource paths', () => {
  const toolHost = read(join(SOURCE_ROOT, 'Repository/Programming/Js/OpenCode/ToolHost.fs'))

  // 双语装载：text 按当前语言渲染资源路径，不内嵌多语散文。
  assert.match(
    toolHost,
    /let private text \(lang: ProviderLanguage\) \(path: string\) =\s*\n\s*ProviderProse\.render lang path Map\.empty/,
  )
  assert.match(toolHost, /Rm = text lang JsCanonicalDescription\.Path\.Rm/)
  assert.match(toolHost, /Mv = text lang JsCanonicalDescription\.Path\.Mv/)
})
