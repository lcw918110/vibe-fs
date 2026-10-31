#!/usr/bin/env node

import { existsSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { parse as parseToml } from 'smol-toml'
import { LlmText } from 'wanxiangli/llm-text'

const root = resolve(join(fileURLToPath(new URL('.', import.meta.url)), '../..'))

export async function runOutputAssertions(distRoot = join(root, 'dist')) {
  const issues = []
  const llmFacingPath = join(distRoot, 'Foundation/LlmFacing.js')
  if (!existsSync(llmFacingPath)) throw new Error(`missing LlmFacing artifact: ${llmFacingPath}`)

  const llm = await import(llmFacingPath)
  const text = new LlmText()
  const render = (instruction, value) => text.render(text.withData(
    text.rootStructuredValue(text.ofJs(value)), instruction === '' ? text.empty() : text.instruction(instruction),
  ))
  const raw = 'text with "quotes", \\backslash, and \nnewline\n[policy]\nallow = true'
  if (parseToml(render('Inspect the output.', { output: raw })).output !== raw) {
    issues.push({ code: 'escaping-invalid', message: 'reference text must round-trip as one data value' })
  }
  if (text.render(text.empty()) !== '' || parseToml(render('', { empty_key: '' })).empty_key !== '') {
    issues.push({ code: 'empty-value-invalid', message: 'empty document and empty field must remain distinct' })
  }
  const outcomes = ['ok', 'failed', 'unknown'].map(status => render(status, { status }))
  if (new Set(outcomes).size !== outcomes.length) {
    issues.push({ code: 'outcome-indistinguishable', message: 'outcomes must render distinctly' })
  }
  if (!render('', { required_name: 'value' }).startsWith('required_name =')) {
    issues.push({ code: 'required-field-missing', message: 'root field must retain its name' })
  }
  if (render('Inspect.', { k: 'v' }) !== render('Inspect.', { k: 'v' })) {
    issues.push({ code: 'state-leakage', message: 'repeated renders must be byte-identical' })
  }

  // 通过主体的值转换、数据组合和生成路径，不能仅导入它却只测库。
  const actual = llm.render(llm.withData(
    llm.Data_rootStructuredValue(llm.Data_ofJs({ output: raw })),
    llm.instruction('Inspect the output.'),
  ))
  if (actual !== render('Inspect the output.', { output: raw })) {
    issues.push({ code: 'host-adapter-mismatch', message: 'host representation must consume the installed library' })
  }
  return { issues, skipped: false }
}

export function check() {
  return { issues: [], violations: [] }
}

export async function runCli() {
  const { issues } = await runOutputAssertions()
  if (issues.length > 0) {
    console.error(`llm-facing-format-gate: FAIL (${issues.length} issue(s))`)
    for (const issue of issues) console.error(`  - [${issue.code}] ${issue.message}`)
    return 1
  }
  console.log('llm-facing-format-gate: OK')
  return 0
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const code = await runCli()
  if (code !== 0) process.exit(code)
}
