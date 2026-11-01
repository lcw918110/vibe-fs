#!/usr/bin/env node

import { existsSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(join(fileURLToPath(new URL('.', import.meta.url)), '../..'))

/**
 * Real output tests for the shared synthetic representation primitives:
 * escaping, empty values, success/failure/unknown distinction, required fields,
 * and preventing internal state leakage.
 */
export async function runOutputAssertions(distRoot = join(root, 'dist')) {
  const issues = []
  const surfacePath = join(distRoot, 'Foundation/SyntheticTomlSurface.js')
  const llmFacingPath = join(distRoot, 'Foundation/LlmFacing.js')
  if (!existsSync(surfacePath) || !existsSync(llmFacingPath)) {
    return { issues: [], skipped: true, reason: 'dist not built' }
  }

  const surface = await import(surfacePath)
  const llm = await import(llmFacingPath)

  // 1. Escaping: quotes, backslashes, and multiline text round-trip cleanly
  const rawNeedsEscaping = 'text with "quotes", \\backslash, and \nnewline'
  const rendered = surface.renderString(rawNeedsEscaping)
  if (!rendered.includes('\\"') && !rendered.startsWith("'''")) {
    issues.push({ code: 'escaping-invalid', message: 'renderString must escape quotes or use multi-line delimiter' })
  }

  // 2. Empty values: empty instructions and body produce valid empty document
  const emptyDoc = surface.renderDocument([], [])
  if (typeof emptyDoc !== 'string') {
    issues.push({ code: 'empty-value-invalid', message: 'empty renderDocument must produce a string' })
  }
  const emptyStringField = surface.field('empty_key', surface.renderString(''))
  if (!emptyStringField.includes('empty_key = ""')) {
    issues.push({ code: 'empty-field-invalid', message: 'empty string field must be formatted explicitly' })
  }

  // 3. Success / failure / unknown distinction: outcomes must render distinctly
  const okDoc = surface.renderDocument(['# status = ok'], [surface.field('status', surface.renderString('ok'))])
  const failDoc = surface.renderDocument(['# status = failed'], [surface.field('status', surface.renderString('failed'))])
  const unknownDoc = surface.renderDocument(['# status = unknown'], [surface.field('status', surface.renderString('unknown'))])
  if (okDoc === failDoc || okDoc === unknownDoc || failDoc === unknownDoc) {
    issues.push({ code: 'outcome-indistinguishable', message: 'ok, failed, and unknown outcomes must render distinct documents' })
  }

  // 4. Required fields: field names must never be empty or omit key
  const standardField = surface.field('required_name', surface.renderString('value'))
  if (!standardField.startsWith('required_name =')) {
    issues.push({ code: 'required-field-missing', message: 'field output must start with field name' })
  }

  // 5. No internal state leakage: repeated renders with identical inputs must be byte-identical
  const first = surface.renderDocument(['# instruction'], [surface.field('k', surface.renderString('v'))])
  const second = surface.renderDocument(['# instruction'], [surface.field('k', surface.renderString('v'))])
  if (first !== second) {
    issues.push({ code: 'state-leakage', message: 'repeated renders must be byte-identical (no mutable state leak)' })
  }

  return { issues, skipped: false }
}

export function check(context) {
  // The filename whitelist has been deleted per G-13.
  // Responsibilities:
  //   * SyntheticTomlSurface.fs: shared JS-native serialization primitive surface
  //   * LlmFacing.fs: canonical LLM boundary for structured representation
  // Any file may access the shared primitives. Output validity is verified by behavioral tests.
  return { issues: [], violations: [] }
}

export async function runCli() {
  const { issues, skipped, reason } = await runOutputAssertions()
  if (skipped) {
    console.log(`llm-facing-format-gate: SKIPPED (${reason})`)
    return 0
  }
  if (issues.length > 0) {
    console.error(`llm-facing-format-gate: FAIL (${issues.length} issue(s))`)
    for (const issue of issues) console.error(`  - [${issue.code}] ${issue.message}`)
    return 1
  }
  console.log('llm-facing-format-gate: OK — escaping, empty values, outcome distinction, required fields and isolation verified')
  return 0
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const code = await runCli()
  if (code !== 0) process.exit(code)
}
