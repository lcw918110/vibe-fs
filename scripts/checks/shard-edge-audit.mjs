#!/usr/bin/env node
// shard-edge-audit.mjs — focused 编译 shard 缺边审计（纯词法）。
//
// 背景：focused 增量编译按 shard 的 ProjectReference 传递闭包选源；闭包
// 算法（planImpactFromInventory）递归完整，缺陷在 shard 依赖图数据层——
// shard 源码引用了其闭包外 shard 定义的命名空间/模块/类型时，以该 shard
// 为根的 focused 编译报「类型未定义」。全量编译因 compile-order.txt 的
// 全序掩盖缺边，只有 focused 才暴露。
//
// 本工具复用 build 侧 fsproj 图解析（compile-shards.mjs 的
// readCompileShardInventory），对每个 shard 的 .fs/.fsi 做受限词法扫描，
// 对照传递闭包输出「源码引用了符号但其 shard 闭包不含定义所在 shard」
// 的疑似缺边清单。纯文本扫描，不引入 FCS、不启动编译器（WHAT-018）。
//
// 这是审计工具，不是构建兜底：不进构建路径、不改闭包、不进 check.mjs。
// 清单是排查起点不是结论；误报可接受，漏报尽量少（宁可多报）。手动运行：
//   node scripts/checks/shard-edge-audit.mjs [--json] [--strict] [--shard <片段>]
//
// 置信度分级：
//   high   — open 命名空间 / 全限定引用 / 词法可见的模块前缀引用，其全部
//            定义 shard 都不在闭包（词法证据与图证据直接矛盾）。
//   medium — 同名模块/命名空间多 shard 定义且全部不在闭包（无法唯一指认）。
//   low    — 纯类型名粗匹配：全局唯一定义且不在闭包。同名多 shard 定义
//            （如 SessionId）不报——词法无法定位该补哪条边。
//
// 命名空间引用按 *层级路径* 解析（仅 open / 全限定两条规则）：F# 里声明
// `namespace A.B.C` 就让 `A.B` 与 `A` 作为父容器存在，故 `open A.B` 只要
// 仓库里存在 `A.B` 自身或任一后代命名空间（`A.B.<...>`）的声明即合法。引用
// 的「定义 shard 集」因此是「精确声明 ∪ 后代声明」，闭包含其中任一即视为
// 可解析、不报；全部在闭包外才报 high。module 全路径前缀不走此规则——module
// 不是层级容器，不会因后代 module 存在而让 `Outer.Inner` 可解析。
//
// 归因规则（决定「哪段点链算引用」）：
//   - 全限定引用按 *前缀* 解析：对 `Wanxiangshu.A.B.C` 只承认 `Wanxiangshu.A`
//     / `Wanxiangshu.A.B` … 这些从首段开始的命名空间或 module 全路径，取最长
//     可解析前缀。中段撞名不算证据：`Wanxiangshu.Execution.Delegation.SyncDelegate`
//     不会把中段 `Delegation` 归因到仓库里的同名 module。
//   - 裸模块前缀引用（`ProviderLifecycle.start`）必须是点链首段，且该 module
//     在文件里 *词法可见*：顶层 module 恒可见；否则要求文件 open 了它的父
//     命名空间，或引用点位于其父命名空间/模块（含祖辈）作用域内。字段访问
//     与 FQN 中段（`plan.Profile.X`、`Wanxiangshu.Execution.Delegation.Fork`）
//     因此不再冒充模块引用。
//   - 外部库同名 module（`open Thoth.Json` 的 Decode/Encode 撞仓库
//     Sphinx/V2/Wire/{Decode,Encode}.fs）因不满足可见性而不成证据。
//
// 已知漏报边界（词法扫描原理性看不见）：
//   - AutoOpen module 的隐式可见符号（无显式 open/前缀）；
//   - CE builder 等小写 let 绑定值（taskResult 等，非 type/module 声明）；
//   - 继承/接口实现的隐式类型依赖、运算符解析依赖；
//   - open 命名空间部分覆盖时，实际使用的具体类型恰在缺失 shard（open
//     规则不报，只有纯类型名规则可能兜住）；
//   - module private/internal 声明不索引（跨 shard 本不可见）；
//   - 非 `Wanxiangshu` 起头的点链只认首段，嵌套模块的二级短名（`Outer.Inner`
//     的 `Inner`）与经祖辈命名空间隐性可见的引用可能不报。
// 已知误报边界：
//   - 局部标识符/字段名撞全局唯一类型名（low 级）；
//   - module 与 type 同名时按 module 规则优先，可能错报置信度级别；
//   - 同名 module 在文件作用域内确实可见、但实际解析到别处（如本地 let 遮蔽）。

import { dirname, relative, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { readFileSync } from 'node:fs'
import { readCompileShardInventory } from '../lib/compile-shards.mjs'
import { maskFSharpTrivia } from '../lib/fsharp-source.mjs'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')

const UPPER_IDENT = /^[A-Z][A-Za-z0-9_']*/
const isUpperIdent = (name) => UPPER_IDENT.test(name)

// 逐行结构模式。模块声明的覆盖范围与旧版整文件正则一致：带 private/internal
// 修饰符的声明不匹配（跨 shard 不可见，索引只会造噪声）。
const NAMESPACE_LINE = /^namespace\s+(?:rec\s+)?([A-Za-z_][A-Za-z0-9_'.]*)\s*$/
const ROOT_MODULE_LINE = /^module\s+([A-Za-z_][A-Za-z0-9_'.]*)\s*(=)?\s*$/
const NESTED_MODULE_LINE = /^[ \t]+module\s+([A-Za-z_][A-Za-z0-9_']*)\s*(=)?\s*$/
const OPEN_LINE = /^[ \t]*open\s+(?:type\s+)?([A-Za-z_][A-Za-z0-9_'.]*)/
const TYPE_LINE = /^[ \t]*(?:type|and)\s+([A-Za-z_][A-Za-z0-9_']*)/

/**
 * 逐行结构扫描：命名空间/模块声明（含嵌套 module 的全路径）、open 列表，
 * 以及每行所处的词法作用域（enclosing namespace/module 路径）。
 * 作用域是「裸模块名是否可见」判定的依据。
 */
export function scanFileStructure(code) {
  const namespaces = []
  const modules = [] // { name, path, parent, topLevel }
  const types = []
  const opens = new Set()
  const lines = code.split('\n')
  const scopeByLine = new Array(lines.length).fill(null)
  const stack = [] // { indent, path } — 当前 module 嵌套链
  let namespacePath = null

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index]

    const namespaceMatch = NAMESPACE_LINE.exec(line)
    if (namespaceMatch) {
      namespacePath = namespaceMatch[1]
      stack.length = 0
      namespaces.push(namespacePath)
      scopeByLine[index] = namespacePath
      continue
    }

    const nestedMatch = NESTED_MODULE_LINE.exec(line)
    if (nestedMatch) {
      const indent = line.length - line.trimStart().length
      while (stack.length > 0 && stack[stack.length - 1].indent >= indent) stack.pop()
      const enclosing = stack.length > 0 ? stack[stack.length - 1].path : namespacePath
      const path = enclosing === null ? nestedMatch[1] : `${enclosing}.${nestedMatch[1]}`
      modules.push({ name: nestedMatch[1], path, parent: enclosing, topLevel: false })
      stack.push({ indent, path })
      scopeByLine[index] = path
      continue
    }

    const rootMatch = ROOT_MODULE_LINE.exec(line)
    if (rootMatch) {
      // 文件级 module 声明：短名挂在当前 namespace 下；带点名的声明自带绝对
      // 路径并隐含其父命名空间。无 `=` 时整个文件余下内容都属于该 module。
      while (stack.length > 0 && stack[stack.length - 1].indent >= 0) stack.pop()
      const enclosing = stack.length > 0 ? stack[stack.length - 1].path : null
      const segments = rootMatch[1].split('.')
      const qualified = segments.length > 1 ? segments.slice(0, -1).join('.') : (namespacePath ?? enclosing)
      const path = qualified === null ? rootMatch[1] : `${qualified}.${rootMatch[1]}`
      modules.push({ name: segments[segments.length - 1], path, parent: qualified, topLevel: qualified === null })
      if (segments.length > 1) namespaces.push(qualified)
      stack.push({ indent: rootMatch[2] === '=' ? 0 : -1, path })
      scopeByLine[index] = path
      continue
    }

    const openMatch = OPEN_LINE.exec(line)
    if (openMatch) opens.add(openMatch[1])

    const typeMatch = TYPE_LINE.exec(line)
    if (typeMatch) types.push(typeMatch[1])

    scopeByLine[index] = stack.length > 0 ? stack[stack.length - 1].path : namespacePath
  }

  return { namespaces, modules, types, opens, scopeByLine }
}

/** ProjectReference 传递闭包（含自身）。图来自 compile-shards 解析，不重造。 */
export function transitiveClosure(projects, startPath) {
  const closure = new Set()
  const queue = [startPath]
  while (queue.length > 0) {
    const current = queue.pop()
    if (closure.has(current)) continue
    closure.add(current)
    for (const reference of projects.get(current).references) queue.push(reference)
  }
  return closure
}

/**
 * 全仓符号索引：namespace / module（全路径）/ type 声明 → 定义 shard 集合。
 * 只收 Wanxiangshu 仓内声明；BCL 与 FSharp.Core 符号天然不在索引中，
 * 引用它们不会产生 finding。
 */
export function buildSymbolIndex(inventory, readText) {
  const namespaces = new Map()
  const modulesByPath = new Map() // module 全路径 → { name, parent, topLevel, projects }
  const types = new Map()
  const fileToProject = new Map()
  const fileScopes = new Map() // 源文件 → { opens, scopeByLine }

  const addProject = (map, name, projectPath) => {
    if (!map.has(name)) map.set(name, new Set())
    map.get(name).add(projectPath)
  }
  const addType = (name, projectPath) => {
    if (!types.has(name)) types.set(name, new Set())
    types.get(name).add(projectPath)
  }

  // readText 约定返回 maskFSharpTrivia 后的 code（注释/字符串已遮）。
  for (const [projectPath, project] of inventory.projects) {
    for (const source of project.compileItems) {
      const code = readText(source)
      fileToProject.set(source, projectPath)
      const structure = scanFileStructure(code)
      fileScopes.set(source, { opens: structure.opens, scopeByLine: structure.scopeByLine })

      for (const name of structure.namespaces) addProject(namespaces, name, projectPath)

      for (const declared of structure.modules) {
        if (!isUpperIdent(declared.name)) continue
        let entry = modulesByPath.get(declared.path)
        if (entry === undefined) {
          entry = { name: declared.name, parent: declared.parent, topLevel: declared.topLevel, projects: new Set() }
          modulesByPath.set(declared.path, entry)
        }
        entry.projects.add(projectPath)
      }

      for (const name of structure.types) {
        if (isUpperIdent(name)) addType(name, projectPath)
      }
    }
  }

  const modulesByName = new Map() // module 短名 → [entry]（全路径去重）
  for (const entry of modulesByPath.values()) {
    if (!modulesByName.has(entry.name)) modulesByName.set(entry.name, [])
    modulesByName.get(entry.name).push(entry)
  }

  return { namespaces, modulesByPath, modulesByName, types, fileToProject, fileScopes }
}

const lineAt = (code, index) => code.slice(0, index).split('\n').length

/**
 * 审计一个 shard 的全部源文件，返回疑似缺边 findings。
 *
 * @param inventory readCompileShardInventory() 的返回值
 * @param projectPath 被审计 shard 的 fsproj 绝对路径
 * @param index buildSymbolIndex() 的返回值
 * @param readText (absPath) => masked code 的读取函数
 */
export function auditShard(inventory, projectPath, index, readText) {
  const { namespaces, modulesByPath, modulesByName, types } = index
  const project = inventory.projects.get(projectPath)
  const closure = transitiveClosure(inventory.projects, projectPath)
  const findings = new Map()

  const record = (kind, symbol, confidence, missingShards, file, line) => {
    const key = `${kind}|${symbol}|${[...missingShards].sort().join(',')}`
    const existing = findings.get(key)
    if (existing) {
      if (existing.evidence.length < 3) existing.evidence.push({ file, line })
      return
    }
    findings.set(key, {
      shard: project.projectRepoPath,
      kind,
      symbol,
      confidence,
      missingShards: [...missingShards].sort(),
      evidence: [{ file, line }],
    })
  }

  const repoFile = (absolute) => relative(repositoryRoot, absolute).replace(/\\/g, '/')

  const missingFrom = (defining) => [...defining].filter((shard) => !closure.has(shard))

  /**
   * 命名空间引用的定义 shard 集：精确声明 ∪ 后代声明。F# 命名空间是层级
   * 路径，`namespace A.B.C` 的声明同时让 `A.B` 存在，所以 `open A.B` 只需要
   * 仓库里存在 `A.B` 或其任一后代命名空间的声明。两者都没有时返回 null
   * （仓外命名空间，不成证据）。
   */
  const namespaceResolutions = new Map()
  const resolveNamespace = (path) => {
    const cached = namespaceResolutions.get(path)
    if (cached !== undefined) return cached
    const exact = namespaces.get(path)
    const prefix = `${path}.`
    const all = new Set(exact ?? [])
    for (const [name, defining] of namespaces) {
      if (!name.startsWith(prefix)) continue
      for (const shard of defining) all.add(shard)
    }
    const resolution = all.size === 0 ? null : all
    namespaceResolutions.set(path, resolution)
    return resolution
  }

  /**
   * 裸模块短名的词法可见性：顶层 module 恒可见（全局命名空间）；否则要求
   * 文件 open 了它的父命名空间/模块，或引用点位于其父命名空间/模块（含祖辈）
   * 的作用域内。外部库同名 module（Thoth.Json 的 Decode/Encode）与字段访问
   * 都不满足此条，因而不再冒充仓库符号。
   */
  const isModuleVisible = (source, line, entry) => {
    if (entry.topLevel) return true
    if (entry.parent === null) return false
    const fileScope = index.fileScopes.get(source)
    if (fileScope === undefined) return false
    if (fileScope.opens.has(entry.parent)) return true
    const scope = fileScope.scopeByLine[line - 1]
    return typeof scope === 'string' && (scope === entry.parent || scope.startsWith(`${entry.parent}.`))
  }

  for (const source of project.compileItems) {
    const code = readText(source)
    const file = repoFile(source)

    for (const openMatch of code.matchAll(/^[ \t]*open\s+(Wanxiangshu[A-Za-z0-9_'.]*)/gm)) {
      const namespace = openMatch[1]
      const defining = resolveNamespace(namespace)
      if (defining === null) continue
      const missing = missingFrom(defining)
      if (missing.length === defining.size) {
        record('open', namespace, 'high', defining, file, lineAt(code, openMatch.index))
      } else if (missing.length > 0) {
        // 部分覆盖（含「精确声明在闭包外但后代声明在闭包内」）：open 本身仍
        // 可解析，不报，等具体符号命中兜底。
      }
    }

    // 全限定引用：只承认从首段开始的最长可解析前缀（命名空间或 module 全
    // 路径）。中段撞名不再是证据。
    for (const qualifiedMatch of code.matchAll(/\b(Wanxiangshu(?:\.[A-Za-z_][A-Za-z0-9_']*)+)/g)) {
      const segments = qualifiedMatch[1].split('.')
      for (let depth = segments.length; depth >= 2; depth -= 1) {
        const prefix = segments.slice(0, depth).join('.')
        const asNamespace = resolveNamespace(prefix)
        if (asNamespace !== null) {
          const missing = missingFrom(asNamespace)
          if (missing.length === asNamespace.size) {
            record('qualified', prefix, 'high', asNamespace, file, lineAt(code, qualifiedMatch.index))
          }
          break
        }
        const asModule = modulesByPath.get(prefix)
        if (asModule !== undefined) {
          const missing = missingFrom(asModule.projects)
          if (missing.length === asModule.projects.size) {
            record('module-prefix', asModule.name, asModule.projects.size === 1 ? 'high' : 'medium', asModule.projects, file, lineAt(code, qualifiedMatch.index))
          }
          break
        }
      }
    }

    // 裸模块前缀：必须是点链首段（排除字段访问 `plan.Profile.X` 与 FQN 中段
    // `Wanxiangshu.Execution.Delegation.Fork`），且该 module 在文件里词法可见。
    for (const prefixMatch of code.matchAll(/\b([A-Z][A-Za-z0-9_']*)\s*\.\s*[A-Za-z_][A-Za-z0-9_']*/g)) {
      const prefix = prefixMatch[1]
      if (prefix === 'Wanxiangshu') continue
      if (prefixMatch.index > 0 && code[prefixMatch.index - 1] === '.') continue
      const line = lineAt(code, prefixMatch.index)
      const candidateModules = (modulesByName.get(prefix) ?? []).filter((entry) => isModuleVisible(source, line, entry))
      if (candidateModules.length > 0) {
        const defining = new Set(candidateModules.flatMap((entry) => [...entry.projects]))
        const missing = missingFrom(defining)
        if (missing.length === defining.size) {
          record('module-prefix', prefix, defining.size === 1 ? 'high' : 'medium', defining, file, line)
        }
        continue
      }
      const asType = types.get(prefix)
      if (asType && asType.size === 1) {
        const defining = [...asType][0]
        if (!closure.has(defining)) {
          record('static-member', prefix, 'low', asType, file, line)
        }
      }
    }

    for (const identMatch of code.matchAll(/\b([A-Z][A-Za-z0-9_']*)\b/g)) {
      const name = identMatch[1]
      const defining = types.get(name)
      if (!defining || defining.size !== 1) continue
      const only = [...defining][0]
      if (!closure.has(only)) {
        record('type-name', name, 'low', defining, file, lineAt(code, identMatch.index))
      }
    }
  }

  return [...findings.values()]
}

/** 全仓审计。readText 返回 mask 后的 code（注释/字符串已遮）。 */
export function auditAllShards(inventory, readRawText) {
  const maskedCache = new Map()
  const readText = (absolute) => {
    const cached = maskedCache.get(absolute)
    if (cached !== undefined) return cached
    const masked = maskFSharpTrivia(readRawText(absolute))
    maskedCache.set(absolute, masked)
    return masked
  }

  const index = buildSymbolIndex(inventory, readText)
  const findings = []
  for (const projectPath of inventory.projects.keys()) {
    findings.push(...auditShard(inventory, projectPath, index, readText))
  }
  return findings.sort((a, b) =>
    a.shard === b.shard
      ? a.symbol.localeCompare(b.symbol)
      : a.shard.localeCompare(b.shard))
}

const formatFinding = (finding) => {
  const evidence = finding.evidence.map((spot) => `${spot.file}:${spot.line}`).join(', ')
  return `[${finding.confidence}] ${finding.shard} -> ${[...finding.missingShards].map((shard) => shard.replace(/^.*\//, '')).join(', ')}: ${finding.kind} '${finding.symbol}' (${evidence})`
}

export async function main(argv = process.argv.slice(2)) {
  const json = argv.includes('--json')
  const strict = argv.includes('--strict')
  const shardFilter = argv.includes('--shard') ? argv[argv.indexOf('--shard') + 1] : null

  const inventory = readCompileShardInventory()
  let findings = auditAllShards(inventory, (absolute) => readFileSync(absolute, 'utf8'))
  if (shardFilter) findings = findings.filter((f) => f.shard.includes(shardFilter))

  if (json) {
    console.log(JSON.stringify({ shardCount: inventory.projects.size, findings }, null, 2))
  } else {
    console.log(`Audited ${inventory.projects.size} shards: ${findings.length} finding(s)`)
    for (const finding of findings) console.log(formatFinding(finding))
  }
  return strict && findings.length > 0 ? 1 : 0
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href
if (isMain) {
  main(process.argv.slice(2)).then((code) => {
    process.exit(code)
  })
}
