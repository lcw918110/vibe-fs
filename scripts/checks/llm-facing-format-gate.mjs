#!/usr/bin/env node

import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(join(fileURLToPath(new URL('.', import.meta.url)), '../..'))
const sourceRoot = join(root, 'src/Wanxiangshu')
const lowLevelTomlAccess = /\bSyntheticToml\./
const formattingOwners = new Set([
  'src/Wanxiangshu/Foundation/LlmFacing.fs',
  'src/Wanxiangshu/Foundation/SyntheticTomlSurface.fs',
  'src/Wanxiangshu/Execution/Delegation/Advisor/View.fs',
])

const walk = (directory) =>
  readdirSync(directory).flatMap((name) => {
    const path = join(directory, name)
    return statSync(path).isDirectory() ? walk(path) : path.endsWith('.fs') ? [path] : []
  })

export function check(context) {
  const r = context?.root ?? root
  const sRoot = join(r, 'src/Wanxiangshu')
  let entries
  if (context?.productionFiles) {
    entries = context.productionFiles().filter(({ file }) => file.endsWith('.fs')).map(({ file, text }) => ({ rel: file, text }))
  } else {
    entries = walk(sRoot).map((p) => ({
      rel: relative(r, p).replaceAll('\\', '/'),
      text: readFileSync(p, 'utf8'),
    }))
  }
  const violations = []
  for (const { rel, text } of entries) {
    const codeText = text
      .split('\n')
      .map((line) => line.replace(/\/\/.*$/, ''))
      .join('\n')
    if (!formattingOwners.has(rel) && lowLevelTomlAccess.test(codeText)) {
      violations.push(`${rel}: direct SyntheticToml access bypasses LlmFacing`)
    }
  }
  return {
    issues: violations.map((v) => ({
      code: 'llm-facing-format-violation',
      message: v,
    })),
    violations,
  }
}

export function runCli() {
  const { issues } = check()
  if (issues.length > 0) {
    console.error('llm-facing-format-gate: FAIL')
    for (const issue of issues) console.error(`- ${issue.message}`)
    return 1
  }
  console.log('llm-facing-format-gate: OK — LlmFacing owns synthetic LLM representation')
  return 0
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const code = runCli()
  if (code !== 0) process.exit(code)
}
