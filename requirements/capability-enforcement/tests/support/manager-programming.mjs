import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, writeFileSync, readFileSync, realpathSync, rmSync, existsSync, mkdirSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { rolePredicate } from '../../../../dist/OpenCode/Tools/ToolRegistrySurface.js'
import { generateRole, permissionLabels } from '../../../../dist/Repository/Programming/Js/GeneratorSurface.js'
import { allRoleLabels } from '../../../../dist/Foundation/RolesSurface.js'
import * as runtime from '../../../../dist/Repository/Programming/Js/RuntimeSurface.js'
import { run, runObserved, execute, caseName, failureCode, rewritten, created, render } from '../../../../dist/Repository/Programming/Js/WorkflowSurface.js'

// 运行时绑定由 owner 面构造：生成器给出角色的权限 label，RuntimeSurface 按 label
// 组装 api 成员，测试从不手写 capability 集合或 Fable Set。
const roleApi = (role, dir) => runtime.api(runtime.createApiFor(dir, permissionLabels(role)))

test('WHAT[capability-enforcement-025] registered_js_tools_match_js_tool_generator_output', () => {
  for (const role of allRoleLabels) {
    const generated = generateRole(role, 'en')
    const toolName = `todowrite-for-${role}`
    const admitted = rolePredicate(toolName, role)
    if (generated) {
      assert.equal(admitted, true, `Role ${role} has generated surface ${generated.toolName} but ToolRegistry denied it`)
      assert.equal(generated.toolName, toolName, `Generator tool name mismatch for ${role}`)
    } else {
      assert.equal(admitted, false, `Role ${role} has no generated surface but ToolRegistry admitted ${toolName}`)
    }
  }
})

test('WHAT[capability-enforcement-025] J03_manager_execution_blocks_write_and_edit_without_disk_mutations', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-j03-'))
  try {
    const targetFile = join(dir, 'target.txt')
    writeFileSync(targetFile, 'initial pristine content', 'utf8')

    for (const mutation of [
      "this.edit('target.txt', { find: 'initial', put: 'hacked' })",
      "this.write('created.txt', 'hacked')",
      "this.rewrite('target.txt', 'hacked')",
      "this._api.write('leaked.txt', 'hacked')",
    ]) {
      const program = `class Js extends JsProgram { async run() { await ${mutation}; return { completed: true }; } }`
      const outcome = await run(dir, 'manager', 'en', program, 2000, Date.now() + 60_000, 1 << 20, null)
      assert.equal(caseName(outcome), 'Failed', mutation)
      assert.deepEqual(rewritten(outcome), [])
      assert.deepEqual(created(outcome), [])
      assert.equal(readFileSync(targetFile, 'utf8'), 'initial pristine content')
      assert.equal(existsSync(join(dir, 'created.txt')), false)
      assert.equal(existsSync(join(dir, 'leaked.txt')), false)
    }
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[capability-enforcement-025] J04_shared_read_execution_returns_success_without_file_effects', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-j04-'))
  try {
    const targetFile = join(dir, 'file.txt')
    writeFileSync(targetFile, 'original', 'utf8')

    // 正常读取作为拒绝用例的正向控制，不能只证明空程序成功。
    const outcome = await run(
      dir,
      'engineer',
      'en',
      `class Js extends JsProgram { async run() { const text = await this.cat('file.txt'); return { text }; } }`,
      2000,
      Date.now() + 60_000,
      1 << 20,
      null,
    )
    assert.equal(caseName(outcome), 'Succeeded', 'clean read-only execution succeeds')
    assert.match(render(outcome), /text = "original"/)
    assert.deepEqual(rewritten(outcome), [], 'read-only workflow rewrites nothing')
    assert.deepEqual(created(outcome), [], 'read-only workflow creates nothing')
    assert.equal(readFileSync(targetFile, 'utf8'), 'original', 'target file stays untouched')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[capability-enforcement-025] J05_engineer_and_devops_retain_write_and_edit_capabilities', async () => {
  const engSurface = generateRole('engineer', 'en')
  const devopsSurface = generateRole('devops', 'en')
  assert.equal(engSurface.capabilities.includes('Edit'), false, 'JsCapability.Edit is retired from generated role surfaces')
  assert.ok(engSurface.capabilities.includes('Write'))
  assert.ok(engSurface.capabilities.includes('Fission'))
  assert.equal(devopsSurface.capabilities.includes('Edit'), false, 'JsCapability.Edit is retired from generated role surfaces')
  assert.ok(devopsSurface.capabilities.includes('Write'))
  assert.ok(devopsSurface.capabilities.includes('Exec'))
  assert.equal(devopsSurface.capabilities.includes('Join'), false)

  const dir = mkdtempSync(join(tmpdir(), 'wxs-j05-'))
  try {
    const engApi = roleApi('engineer', dir)
    assert.equal('edit' in engApi, false, 'retired edit member must not be exposed on Engineer api')
    assert.equal(engApi.edit, undefined)
    assert.equal(typeof engApi.write, 'function', 'Engineer must retain write api')
    assert.equal(typeof engApi.fission, 'function', 'Engineer must expose fission api')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[capability-enforcement-025] J06_internal_leaf_and_replica_admission_not_broadened', () => {
  // Bookkeeper / internal leaf 不因 todowrite-for-manager 出现而放宽
  assert.equal(rolePredicate('todowrite-for-bookkeeper', 'manager'), false, 'Manager must deny todowrite-for-bookkeeper')
  assert.equal(rolePredicate('todowrite-for-manager', 'bookkeeper'), false, 'Bookkeeper must deny todowrite-for-manager')
  assert.equal(rolePredicate('todowrite-for-engineer', 'manager'), false, 'Manager must deny todowrite-for-engineer')
  assert.equal(rolePredicate('todowrite-for-devops', 'manager'), false, 'Manager must deny todowrite-for-devops')
  assert.equal(generateRole('bookkeeper', 'en'), null, 'Bookkeeper generates no public surface')
})

test('WHAT[capability-enforcement-025] J07_string_literals_and_paths_with_quotes_treated_as_pure_data', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-j07-'))
  try {
    const specialName = 'file "quoted" and \'single\'.txt'
    writeFileSync(join(dir, specialName), 'special content', 'utf8')
    const api = roleApi('manager', dir)

    // filePath 含引号不改变纯数据解析意图
    const res = api.cat(specialName)
    assert.equal(res.ok, true)
    assert.equal(res.text, 'special content')

    // program 源码中含引号、反斜杠、换行模板字符串，被严格当作数据执行
    const program = `class Js extends JsProgram {
      async run() {
        const text = "\\n\\"escaped\\" and \\'single\\' \\\\ slash";
        return { text };
      }
    }`
    const outcome = await run(dir, 'manager', 'en', program, 2000, Date.now() + 60_000, 1 << 20, null)
    assert.equal(caseName(outcome), 'Succeeded')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[capability-enforcement-025] J08_path_resolution_supports_navigation_outside_workspace_root', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-j08-root-'))
  const outsideDir = mkdtempSync(join(tmpdir(), 'wxs-j08-outside-'))
  try {
    mkdirSync(join(dir, 'sub'), { recursive: true })
    writeFileSync(join(dir, 'sub/foo.txt'), 'sub content', 'utf8')

    // 在 root 外部创建包含内容的独立文件
    const secretOutsideFile = join(outsideDir, 'secret.txt')
    writeFileSync(secretOutsideFile, 'super-secret-outside-content', 'utf8')

    const api = roleApi('manager', dir)

    // 1. 根目录内子路径与相对路径正常放行
    const insideRes = api.cat('sub/foo.txt')
    assert.equal(insideRes.ok, true, 'sub path inside root is allowed')
    assert.equal(insideRes.text, 'sub content')

    // 2. 缺失的相对路径返回 FILE_NOT_FOUND
    const parentEscapeRes = api.cat('../outside-missing.txt')
    assert.equal(parentEscapeRes.ok, false)
    assert.equal(parentEscapeRes.code, 'FILE_NOT_FOUND', 'missing path returns FILE_NOT_FOUND')

    // 3. 外部绝对路径：由操作系统权限约束，正常可读取
    const absoluteRes = api.cat(secretOutsideFile)
    assert.equal(absoluteRes.ok, true, 'absolute path outside root is readable if permission allows')
    assert.equal(absoluteRes.text, 'super-secret-outside-content')

    // 4. ls 外部目录：正常列出文件
    const lsOutsideRes = api.ls(outsideDir)
    assert.equal(lsOutsideRes.ok, true, 'ls for directory outside root succeeds')
    assert.ok(lsOutsideRes.entries.includes('secret.txt'))
  } finally {
    rmSync(dir, { recursive: true, force: true })
    rmSync(outsideDir, { recursive: true, force: true })
  }
})

test('WHAT[capability-enforcement-025] J09_readonly_api_preserves_utf8_and_line_endings', () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-j09-'))
  try {
    writeFileSync(join(dir, 'empty.txt'), '', 'utf8')
    writeFileSync(join(dir, 'crlf.txt'), 'line1\r\nline2\r\n', 'utf8')
    writeFileSync(join(dir, 'chinese.txt'), '你好，世界！万象树架构。', 'utf8')
    writeFileSync(join(dir, 'noeol.txt'), 'first\nsecond', 'utf8')

    const api = roleApi('manager', dir)

    // 空文件
    assert.equal(api.cat('empty.txt').text, '')
    assert.equal(api.stat('empty.txt').stat.size, 0)

    // CRLF 换行
    assert.equal(api.cat('crlf.txt').text, 'line1\r\nline2\r\n')

    // 中文字符串 UTF-8
    assert.equal(api.cat('chinese.txt').text, '你好，世界！万象树架构。')

    // 末行无换行
    assert.equal(api.cat('noeol.txt').text, 'first\nsecond')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[capability-enforcement-025] J10_readonly_api_reports_missing_files_search_errors_and_output_limits', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-j10-'))
  try {
    const sampleText = 'literal [unclosed-bracket and regex text'
    writeFileSync(join(dir, 'sample.txt'), sampleText, 'utf8')

    const api = roleApi('manager', dir)

    // 1. 缺失文件明确返回 FILE_NOT_FOUND
    const miss = api.cat('nonexistent.txt')
    assert.equal(miss.ok, false)
    assert.equal(miss.code, 'FILE_NOT_FOUND')

    const lsMiss = api.ls('nonexistent-dir')
    assert.equal(lsMiss.ok, false)
    assert.equal(lsMiss.code, 'FILE_NOT_FOUND')

    // 2. ls + cat + whereAll 组合搜索语义（WHAT[repository-programming-057]）：
    // 字符串 needle 按归一化精确字面量搜索，即使包含正则元字符（如 [）也按字面量匹配
    const listed = api.ls('.')
    assert.equal(listed.ok, true)
    assert.deepEqual(listed.entries, ['sample.txt'])
    const catRes = api.cat('sample.txt')
    assert.equal(catRes.ok, true)
    const exactHit = api.whereAll(catRes.text, '[unclosed-bracket')
    assert.equal(exactHit.ok, true, 'string needle must be treated as exact literal search')
    assert.equal(exactHit.spans.length, 1)
    const [begin, end] = exactHit.spans[0]
    assert.equal(catRes.text.slice(begin, end), '[unclosed-bracket')

    // 3. 空或纯空白 needle 返回空匹配列表
    const emptyNeedle = api.whereAll(catRes.text, '   ')
    assert.equal(emptyNeedle.ok, true)
    assert.deepEqual(emptyNeedle.spans, [])

    // 4. 非法参数（null/undefined）明确返回 INVALID_ARGUMENT
    const invalidWhereAll = api.whereAll(null, 'some-needle')
    assert.equal(invalidWhereAll.ok, false)
    assert.equal(invalidWhereAll.code, 'INVALID_ARGUMENT', 'null content must fail with INVALID_ARGUMENT')

    const invalidWhere = api.where('sample', null)
    assert.equal(invalidWhere.ok, false)
    assert.equal(invalidWhere.code, 'INVALID_ARGUMENT', 'null needle must fail with INVALID_ARGUMENT')

    // 5. 退役的 JsProgram.file 在运行期拒绝调用（不再暴露锚点搜索成员）
    const retiredFileOutcome = await run(
      dir,
      'manager',
      'en',
      `class Js extends JsProgram { async run() { return await this.file('sample.txt'); } }`,
      2000,
      Date.now() + 60_000,
      1 << 20,
      null,
    )
    assert.equal(caseName(retiredFileOutcome), 'Failed')
    assert.equal(failureCode(retiredFileOutcome), 'PROGRAM_FAILED')

    // 6. 结果超限返回 RESULT_TOO_LARGE
    const bigProgram = `class Js extends JsProgram {
      async run() { return { huge: 'a'.repeat(2000) }; }
    }`
    const outcome = await run(dir, 'manager', 'en', bigProgram, 2000, Date.now() + 60_000, 100, null)
    assert.equal(caseName(outcome), 'Failed')
    assert.equal(failureCode(outcome), 'RESULT_TOO_LARGE')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[capability-enforcement-025] J11_shared_adapter_observation_records_only_successful_distinct_reads', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-j11-'))
  try {
    writeFileSync(join(dir, 'read1.txt'), 'content1', 'utf8')
    writeFileSync(join(dir, 'read2.txt'), 'content2', 'utf8')
    const expectedReads = [
      { path: realpathSync(join(dir, 'read1.txt')), resultBytes: 'content1' },
      { path: realpathSync(join(dir, 'read2.txt')), resultBytes: 'content2' },
    ]

    let recordedReads = []
    let recordedEffects = []
    const program = `class Js extends JsProgram {
      async run() {
        const a = await this.cat('read1.txt');
        const b = await this.cat('read1.txt'); // 重复读取同一文件
        const c = await this.cat('read2.txt');
        try { await this.cat('missing.txt'); } catch (e) {} // 失败读取
        return { done: true };
      }
    }`

    const outcome = await runObserved(
      dir,
      'engineer',
      'en',
      program,
      2000,
      Date.now() + 60_000,
      1 << 20,
      null,
      (reads, effects) => {
        recordedReads = reads
        recordedEffects = effects
      },
    )
    assert.equal(caseName(outcome), 'Succeeded')
    // 真实成功只记一次（去重）
    assert.deepEqual([...recordedReads].sort((left, right) => left.path.localeCompare(right.path)), expectedReads)
    // 失败/被拒读取不计入成功观察
    assert.equal(recordedReads.some(read => read.path === join(realpathSync(dir), 'missing.txt')), false, 'failed read must not be recorded')
    assert.deepEqual(recordedEffects, [], 'read-only manager execution produces no effect paths')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[capability-enforcement-025] J12_sandbox_context_strictly_excludes_ambient_os_capabilities', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-j12-'))
  try {
    const probeProgram = `class Js extends JsProgram {
      async run() {
        let constructorEscape = 'none';
        try {
          const leaked = this.constructor.constructor('return process')();
          constructorEscape = typeof leaked;
        } catch (e) {
          constructorEscape = 'blocked';
        }
        return {
          processType: typeof process,
          requireType: typeof require,
          fsType: typeof fs,
          globalProcess: typeof globalThis.process,
          constructorEscape,
        };
      }
    }`

    const outcome = await run(dir, 'manager', 'en', probeProgram, 2000, Date.now() + 60_000, 1 << 20, null)
    assert.equal(caseName(outcome), 'Succeeded')
    assert.ok(outcome, 'outcome must exist')

    const rendered = render(outcome)
    assert.match(rendered, /processType\s*=\s*"undefined"/, 'process must be undefined in sandbox')
    assert.match(rendered, /requireType\s*=\s*"undefined"/, 'require must be undefined in sandbox')
    assert.match(rendered, /fsType\s*=\s*"undefined"/, 'fs must be undefined in sandbox')
    assert.match(rendered, /globalProcess\s*=\s*"undefined"/, 'globalThis.process must be undefined in sandbox')
    assert.match(rendered, /constructorEscape\s*=\s*"(blocked|undefined)"/, 'constructor escape must not yield process')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

