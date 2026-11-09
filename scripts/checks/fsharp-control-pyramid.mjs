#!/usr/bin/env node

import { existsSync, readFileSync } from 'node:fs'
import { relative, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { walk } from '../lib/walk.mjs'

export const ROOT = fileURLToPath(new URL('../..', import.meta.url))
export const DEFAULT_SOURCE_ROOT = 'src/Wanxiangshu'

const norm = (path) => path.replace(/\\/g, '/')

const indentWidth = (line) => {
  let width = 0
  for (const char of line) {
    if (char === ' ') width += 1
    else if (char === '\t') width += 4
    else break
  }
  return width
}

const classifyDecision = (code) => {
  const text = code.trim()
  if (/^match!\s/.test(text)) return 'match!'
  if (/^match\s/.test(text)) return 'match'
  if (/^function(?:\s|$)/.test(text)) return 'function'
  if (/^if\b.*\bthen\s*$/.test(text)) return 'if'
  if (/^try\s*$/.test(text)) return 'try'
  if (/^while\b.*\bdo\s*$/.test(text)) return 'while'
  if (/^for\b.*\bdo\s*$/.test(text)) return 'for'
  return undefined
}

const sameIndentContinuation = (region, code) => {
  const text = code.trimStart()
  if (region.kind === 'match' || region.kind === 'match!' || region.kind === 'function') {
    return text.startsWith('|')
  }
  if (region.kind === 'if') return /^elif\b|^else\b/.test(text)
  if (region.kind === 'try') return /^(with\b|finally\b|\|)/.test(text)
  return false
}

const sanitizeLines = (text) => {
  const lines = text.split('\n')
  const sanitized = []
  let blockDepth = 0
  let tripleString = false

  for (const raw of lines) {
    let output = ''
    let index = 0
    let quoted = false
    let verbatim = false

    while (index < raw.length) {
      if (tripleString) {
        const end = raw.indexOf('"""', index)
        if (end === -1) {
          output += ' '.repeat(raw.length - index)
          index = raw.length
          continue
        }
        output += ' '.repeat(end + 3 - index)
        index = end + 3
        tripleString = false
        continue
      }

      if (blockDepth > 0) {
        const open = raw.indexOf('(*', index)
        const close = raw.indexOf('*)', index)
        if (close === -1 && open === -1) {
          output += ' '.repeat(raw.length - index)
          index = raw.length
          continue
        }
        if (open !== -1 && (close === -1 || open < close)) {
          output += ' '.repeat(open + 2 - index)
          index = open + 2
          blockDepth += 1
          continue
        }
        output += ' '.repeat(close + 2 - index)
        index = close + 2
        blockDepth -= 1
        continue
      }

      if (quoted) {
        const char = raw[index]
        output += ' '
        if (verbatim) {
          if (char === '"') {
            if (raw[index + 1] === '"') {
              output += ' '
              index += 2
              continue
            }
            quoted = false
            verbatim = false
          }
          index += 1
          continue
        }
        if (char === '\\') {
          if (index + 1 < raw.length) output += ' '
          index += 2
          continue
        }
        if (char === '"') quoted = false
        index += 1
        continue
      }

      if (raw.startsWith('//', index)) {
        output += ' '.repeat(raw.length - index)
        index = raw.length
        continue
      }
      if (raw.startsWith('(*', index)) {
        output += '  '
        blockDepth = 1
        index += 2
        continue
      }
      if (raw.startsWith('"""', index)) {
        output += '   '
        tripleString = true
        index += 3
        continue
      }
      if (raw[index] === '"') {
        output += ' '
        quoted = true
        verbatim = index > 0 && raw[index - 1] === '@'
        index += 1
        continue
      }

      output += raw[index]
      index += 1
    }

    sanitized.push(output)
  }

  return sanitized
}

export const scanControlPyramidEntries = (entries) => {
  const violations = []

  for (const { file, text } of entries) {
    const rawLines = text.split('\n')
    const codeLines = sanitizeLines(text)
    const stack = []

    for (let index = 0; index < codeLines.length; index += 1) {
      const code = codeLines[index]
      if (!code.trim()) continue
      const indent = indentWidth(code)

      while (stack.length > 0) {
        const top = stack.at(-1)
        if (indent > top.indent) break
        if (indent === top.indent && sameIndentContinuation(top, code)) break
        stack.pop()
      }

      const kind = classifyDecision(code)
      if (!kind) continue

      const depth = stack.length + 1
      const region = { kind, indent, line: index + 1 }
      // 只报真正的深塔：三层及以上才说明因果顺序已经由缩进承担。
      // 两层（例如 `if 前置检查 then ... else match`）是正常写法，不是债务。
      if (depth >= 3) {
        violations.push({
          kind: kind === 'match' || kind === 'match!' || kind === 'function' ? 'match-pyramid' : 'branch-pyramid',
          file: norm(file),
          line: index + 1,
          outerLine: stack[0].line,
          depth,
          chain: [...stack.map((item) => item.kind), kind],
          text: rawLines[index].trim(),
        })
      }
      stack.push(region)
    }
  }

  return violations
}

export const collectControlPyramidEntries = (repoRoot = ROOT, sourceRoot = DEFAULT_SOURCE_ROOT) => {
  const absoluteRoot = resolve(repoRoot, sourceRoot)
  return walk(absoluteRoot, ['.fs'])
    .filter((absolute) => !norm(absolute).endsWith('Surface.fs'))
    .map((absolute) => ({
      file: norm(relative(repoRoot, absolute)),
      text: readFileSync(absolute, 'utf8'),
    }))
}

const countsByFile = (hits) => {
  const counts = new Map()
  for (const hit of hits) counts.set(hit.file, (counts.get(hit.file) ?? 0) + 1)
  return counts
}

export const renderFailure = (hits, heading = `fsharp-control-pyramid: ${hits.length} violation(s)`) => {
  const lines = [heading, '']
  for (const hit of hits) {
    lines.push(
      `  [${hit.kind}] ${hit.file}:${hit.line} depth=${hit.depth} chain=${hit.chain.join(' → ')}`,
      `    ${hit.text}`,
      '',
    )
  }
  lines.push('run `node scripts/checks/fsharp-control-pyramid.mjs --explain` for the repair manual')
  return lines.join('\n')
}

const parseArgs = (argv) => {
  const options = {
    root: DEFAULT_SOURCE_ROOT,
    explain: false,
    snapshot: false,
  }

  for (const arg of argv) {
    if (arg === '--explain') options.explain = true
    else if (arg.startsWith('--root=')) options.root = arg.slice('--root='.length)
    else if (arg.startsWith('--baseline=')) options.baseline = arg.slice('--baseline='.length)
    else throw new Error(`fsharp-control-pyramid: unknown argument ${arg}`)
  }
  return options
}
export function check(context) {
  const root = context?.root ?? ROOT
  let entries
  if (context?.productionFiles) {
    entries = context.productionFiles()
      .filter(({ file }) => file.endsWith('.fs') && !norm(file).endsWith('Surface.fs'))
      .map(({ file, text }) => ({ file, text }))
  } else {
    entries = collectControlPyramidEntries(root, DEFAULT_SOURCE_ROOT)
  }
  const hits = scanControlPyramidEntries(entries)
  // 报告不拦路：金字塔存量连主仓 master 自身都未清零（门禁引入时即 170+），
  // 先观察，设 PYRAMID_STRICT=1 才升级为硬门禁。与 shard-edge-audit 同形状。
  if (process.env.PYRAMID_STRICT) {
    return {
      issues: hits.map((hit) => ({
        code: hit.kind,
        path: hit.file,
        line: hit.line,
        message: `depth=${hit.depth} chain=${hit.chain.join(' → ')}: ${hit.text}`,
        outerLine: hit.outerLine,
        depth: hit.depth,
        chain: hit.chain,
        text: hit.text,
      })),
      hits,
    }
  }
  console.error(`fsharp-control-pyramid (report-only): ${hits.length} finding(s)`)
  console.error('  Set PYRAMID_STRICT=1 to make this gate fail.')
  return { issues: [], hits }
}

export const runCli = (argv = process.argv.slice(2)) => {
  let options
  try {
    options = parseArgs(argv)
  } catch (error) {
    console.error(error.message)
    return 2
  }

  if (options.explain) {
    console.log(
      [
        'fsharp-control-pyramid 只拒绝一种形状：一个 decision 的 body 继续长第二个 decision，',
        '再长第三个——三层及以上，主要因果顺序已经由缩进承担，读者必须数空格。',
        '',
        '两层（例如 `if 前置检查 then ... else match`）是正常写法，不报。',
        '',
        '修法：给内层 decision 一个真实名字，把边界压平；不要加 suppression，',
        '也不要把嵌套搬进 helper 假装解决。',
      ].join('\n'),
    )
    return 0
  }

  const entries = collectControlPyramidEntries(ROOT, options.root)
  const hits = scanControlPyramidEntries(entries)
  if (hits.length === 0) {
    console.log('fsharp-control-pyramid: clean (0 nested decisions)')
    return 0
  }
  console.log(renderFailure(hits))
  return 1
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const code = runCli()
  if (code !== 0) process.exit(code)
}
