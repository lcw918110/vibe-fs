#!/usr/bin/env node
// JS-001 / JS-004 static gate: no handwritten role→JS tool matrix, no
// Meditator filesystem JS.
//
// The only legitimate js-* tool name is the one JsToolGenerator.toolNameFor
// builds at runtime ("js-" + roleName). Any literal per-role js-* name in
// production source means a handwritten variant was introduced — fail closed.
//
// Usage: node scripts/checks/js-surface-gate.mjs

import { readFileSync } from 'node:fs'
import { walk } from '../lib/walk.mjs'

const PRODUCTION_ROOT = 'src/Wanxiangshu'

/**
 * Literal per-role js-* tool names — only the generator may produce them, at
 * runtime. The Host permission matrix (Tools/StaticTools.fs knownToolNames) is
 * the one legitimate static enumeration: the schema layer must name every tool
 * to emit concrete allow/deny pairs. Everywhere else a literal js-* role name
 * means a handwritten variant was introduced — fail closed.
 */
export const HANDWRITTEN_ROLE_TOOL_TOKENS = [
  'js-coder',
  'js-inspector',
  'js-reviewer',
  'js-browser',
  'js-inquiry',
]

export const DEPRECATED_ROLE_TOOL_TOKENS = [
  'js-coder',
  'js-inspector',
  'js-browser',
  'js-inquiry',
  'js-reviewer',
]

export const REQUIRED_ACTIVE_JS_TOOLS = [
  'todowrite-for-engineer',
  'todowrite-for-devops',
  'todowrite-for-bookkeeper',
  'todowrite-for-manager',
  'todowrite-for-orchestrator',
]

/** Files where the static enumeration is legitimate (permission matrix only). */
const PERMISSION_MATRIX_FILES = [
  'src/Wanxiangshu/OpenCode/Tools/StaticTools.fs',
  'src/Wanxiangshu/Tools/StaticTools.fs',
]

const norm = (path) => path.replace(/\\/g, '/')

export const scanEntries = (entries) => {
  const violations = []
  let staticToolsText = null

  for (const { file, text } of entries) {
    const normalized = norm(file)
    if (PERMISSION_MATRIX_FILES.includes(normalized)) {
      staticToolsText = text
    }
    const lines = text.split('\n')
    const check = (i, token, kind) =>
      violations.push({ file, line: i + 1, token, kind, text: lines[i].trim() })
    const skipHandwritten = PERMISSION_MATRIX_FILES.includes(normalized)

    for (let i = 0; i < lines.length; i++) {
      // 1. 废弃角色工具绝对拦截：即使在权限矩阵文件中，也严禁出现废弃角色工具
      if (skipHandwritten) {
        for (const token of DEPRECATED_ROLE_TOOL_TOKENS) {
          if (lines[i].includes(token)) check(i, token, 'deprecated-role-tool')
        }
      } else {
        // 2. 手写角色工具拦截（非权限矩阵文件）
        for (const token of HANDWRITTEN_ROLE_TOOL_TOKENS) {
          if (lines[i].includes(token)) check(i, token, 'handwritten-role-tool')
        }
      }
    }
  }

  // 3. 新工具 surface 完备性正向验证：StaticTools.fs 中必须完整枚举所有活跃角色的 js-* 工具
  if (staticToolsText) {
    const knownMatch = staticToolsText.match(/let knownToolNames =\s*\[([\s\S]*?)\]/)
    const knownBlock = knownMatch ? knownMatch[1] : ''
    for (const tool of REQUIRED_ACTIVE_JS_TOOLS) {
      if (!knownBlock.includes(`"${tool}"`)) {
        violations.push({
          file: 'src/Wanxiangshu/OpenCode/Tools/StaticTools.fs',
          line: 1,
          token: tool,
          kind: 'missing-active-js-tool',
          text: `knownToolNames must contain ${tool}`,
        })
      }
    }
  }

  return violations
}

const main = () => {
  const entries = walk(PRODUCTION_ROOT)
    .filter((file) => file.endsWith('.fs') || file.endsWith('.fsi'))
    .map((file) => ({ file: norm(file), text: readFileSync(file, 'utf8') }))
  const violations = scanEntries(entries)
  if (violations.length === 0) {
    console.log('js-surface-gate: OK — no handwritten js-* role variants')
    return
  }
  console.error(`js-surface-gate: ${violations.length} 处违规`)
  for (const v of violations) {
    console.error(`  ${v.file}:${v.line}  ${v.kind}: ${v.token}  (${v.text})`)
  }
  process.exit(1)
}

if (import.meta.url === `file://${process.argv[1]}`) {
  main()
}
