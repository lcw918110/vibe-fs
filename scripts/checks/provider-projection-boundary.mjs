#!/usr/bin/env node

import { existsSync, readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const normalizePath = (path) => path.replace(/\\/g, '/')

export const PROVIDER_PROJECTION_CORE_FILES = Object.freeze([
  'src/Wanxiangshu/Participant/Provider/Projection/Model.fs',
  'src/Wanxiangshu/Participant/Provider/Projection/Intent.fs',
  'src/Wanxiangshu/Participant/Provider/Projection/Planner.fs',
  'src/Wanxiangshu/Participant/Provider/Projection/Renderer.fs',
  'src/Wanxiangshu/Participant/Provider/Projection/Surface.fs',
])

export const PROVIDER_PROJECTION_OWNER_FILES = Object.freeze([
  ...PROVIDER_PROJECTION_CORE_FILES,
  'src/Wanxiangshu/OpenCode/Codec/ProjectionMessageEdit.fs',
  'src/Wanxiangshu/OpenCode/Codec/ProviderProjectionSurface.fs',
  'src/Wanxiangshu/OpenCode/Codec/ProviderWireDecode.fs',
  'src/Wanxiangshu/OpenCode/Codec/ProviderWireCapture.fs',
])

const STRENGTH_IMPORT_RE = /^\s*open\s+Wanxiangshu\.Strength(?:\.|\b)/
const STRENGTH_API_RE = /\btryApplyStrength[A-Za-z0-9_']*\b/g
const POLICY_IDENTIFIER_RE = /\b[A-Za-z_][A-Za-z0-9_']*\b/g
const FOREIGN_OWNER_REFERENCE_RE =
  /\bWanxiangshu\.(?:Context|Interaction|Execution\.Session\.Recovery)(?:\.[A-Za-z_][A-Za-z0-9_']*)*/g
const FOREIGN_MATERIALIZATION_RE =
  /\b(?:ActivatePrefixEpoch|InsertRepair|SuppressTransportOnly|ReanchorAfterCompaction)\b|\bProjectionConstants\.RepairInstruction\b/g
const PROJECTION_CORE_FILE_SET = new Set(PROVIDER_PROJECTION_CORE_FILES)

/**
 * Replace comments and strings with spaces while retaining newlines and source
 * positions. Strength-specific error prose is scanned separately from raw text.
 * @param {string} source
 */
const codeOnly = (source) => {
  let result = ''
  let index = 0
  let blockDepth = 0
  let stringKind = null

  while (index < source.length) {
    const char = source[index]
    const next = source[index + 1]

    if (blockDepth > 0) {
      if (char === '(' && next === '*') {
        result += '  '
        index += 2
        blockDepth += 1
      } else if (char === '*' && next === ')') {
        result += '  '
        index += 2
        blockDepth -= 1
      } else {
        result += char === '\n' ? '\n' : ' '
        index += 1
      }
      continue
    }

    if (stringKind !== null) {
      if (char === '\n') {
        result += '\n'
        index += 1
      } else if (stringKind === 'regular' && char === '\\' && next !== undefined) {
        result += '  '
        index += 2
      } else if (stringKind === 'verbatim' && char === '"' && next === '"') {
        result += '  '
        index += 2
      } else if (char === '"') {
        result += ' '
        index += 1
        stringKind = null
      } else {
        result += ' '
        index += 1
      }
      continue
    }

    if (char === '/' && next === '/') {
      const end = source.indexOf('\n', index)
      if (end === -1) return result + ' '.repeat(source.length - index)
      result += ' '.repeat(end - index)
      index = end
    } else if (char === '(' && next === '*') {
      result += '  '
      index += 2
      blockDepth = 1
    } else if (char === '@' && next === '"') {
      result += '  '
      index += 2
      stringKind = 'verbatim'
    } else if (char === '"') {
      result += ' '
      index += 1
      stringKind = 'regular'
    } else {
      result += char
      index += 1
    }
  }

  return result
}

const lineAt = (source, offset) => {
  let line = 1
  for (let index = 0; index < offset; index += 1) {
    if (source.charCodeAt(index) === 10) line += 1
  }
  return line
}

/**
 * @typedef {{
 *   id: 'provider-projection-owner',
 *   rule: 'strength-import' | 'strength-api' | 'foreign-owner-reference' | 'foreign-materialization',
 *   file: string,
 *   line: number,
 *   text: string,
 * }} ProviderProjectionOwnerViolation
 */

const scanSource = (file, source, isProjectionCore) => {
  const normalizedFile = normalizePath(file)
  const code = codeOnly(source)
  const violations = []
  const codeLines = code.split('\n')
  const sourceLines = source.split('\n')

  for (let index = 0; index < codeLines.length; index += 1) {
    if (STRENGTH_IMPORT_RE.test(codeLines[index])) {
      violations.push({
        id: 'provider-projection-owner',
        rule: 'strength-import',
        file: normalizedFile,
        line: index + 1,
        text: sourceLines[index].trim(),
      })
    }
  }

  const recoveryReferenceLines = new Set()
  if (isProjectionCore) {
    FOREIGN_OWNER_REFERENCE_RE.lastIndex = 0
    for (
      let match = FOREIGN_OWNER_REFERENCE_RE.exec(code);
      match !== null;
      match = FOREIGN_OWNER_REFERENCE_RE.exec(code)
    ) {
      const line = lineAt(code, match.index)
      const strengthImport =
        match[0].startsWith('Wanxiangshu.Strength') && STRENGTH_IMPORT_RE.test(codeLines[line - 1])
      if (strengthImport) continue
      if (match[0].startsWith('Wanxiangshu.Execution.Session.Recovery')) {
        recoveryReferenceLines.add(line)
      }
      violations.push({
        id: 'provider-projection-owner',
        rule: 'foreign-owner-reference',
        file: normalizedFile,
        line,
        text: match[0],
      })
    }

    FOREIGN_MATERIALIZATION_RE.lastIndex = 0
    for (
      let match = FOREIGN_MATERIALIZATION_RE.exec(code);
      match !== null;
      match = FOREIGN_MATERIALIZATION_RE.exec(code)
    ) {
      violations.push({
        id: 'provider-projection-owner',
        rule: 'foreign-materialization',
        file: normalizedFile,
        line: lineAt(code, match.index),
        text: match[0],
      })
    }
  }

  STRENGTH_API_RE.lastIndex = 0
  for (let match = STRENGTH_API_RE.exec(code); match !== null; match = STRENGTH_API_RE.exec(code)) {
    violations.push({
      id: 'provider-projection-owner',
      rule: 'strength-api',
      file: normalizedFile,
      line: lineAt(code, match.index),
      text: match[0],
    })
  }

  return violations.sort((left, right) =>
    left.line - right.line || left.rule.localeCompare(right.rule) || left.text.localeCompare(right.text))
}

/**
 * Scan an injectable source string under the five-file projection-core rules.
 * The explicit API keeps fixtures independent of production path names.
 * @param {string} file
 * @param {string} source
 * @returns {ProviderProjectionOwnerViolation[]}
 */
export const scanProjectionCoreSource = (file, source) => scanSource(file, source, true)

/**
 * Scan one configured provider-projection owner from an injectable source string.
 * Core-only rules are selected from the canonical production path set.
 * @param {string} file
 * @param {string} source
 * @returns {ProviderProjectionOwnerViolation[]}
 */
export const scanProviderProjectionSource = (file, source) =>
  scanSource(file, source, PROJECTION_CORE_FILE_SET.has(normalizePath(file)))

/**
 * @param {{ file: string, source: string }[]} entries
 * @returns {ProviderProjectionOwnerViolation[]}
 */
export const scanProviderProjectionEntries = (entries) =>
  entries.flatMap(({ file, source }) => scanProviderProjectionSource(file, source))

/**
 * Scan every fixed owner boundary. Missing owners fail closed rather than
 * silently shrinking the architecture surface.
 * @param {string} repoRoot
 * @param {readonly string[]} [files]
 * @returns {ProviderProjectionOwnerViolation[]}
 */
export const scanProviderProjectionRepo = (repoRoot, files = PROVIDER_PROJECTION_OWNER_FILES) => {
  const entries = files.map((file) => {
    const absolute = resolve(repoRoot, file)
    if (!existsSync(absolute)) {
      throw new Error(`provider-projection-owner: scan owner missing: ${file}`)
    }
    return { file, source: readFileSync(absolute, 'utf8') }
  })
  return scanProviderProjectionEntries(entries)
}

export function check(context) {
  const root = context?.root ?? resolve(dirname(fileURLToPath(import.meta.url)), '../..')
  const entries = PROVIDER_PROJECTION_OWNER_FILES.map((file) => {
    const absolute = resolve(root, file)
    if (!existsSync(absolute)) {
      throw new Error(`provider-projection-owner: scan owner missing: ${file}`)
    }
    const source = context?.readText ? context.readText(file) : readFileSync(absolute, 'utf8')
    return { file, source }
  })
  const violations = scanProviderProjectionEntries(entries)
  return {
    issues: violations.map((v) => ({
      code: v.rule,
      path: v.file,
      line: v.line,
      message: v.text,
    })),
  }
}

export function runCli(argv, root) {
  const { issues } = check({ root })
  if (issues.length === 0) {
    console.log('provider-projection-owner: OK')
    return 0
  }
  console.error('provider-projection-owner: VIOLATIONS')
  for (const issue of issues) {
    console.error(`  ${issue.path}:${issue.line} [${issue.code}] ${issue.message}`)
  }
  return 1
}

export const run = (repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')) => {
  const violations = scanProviderProjectionRepo(repoRoot)
  if (violations.length === 0) {
    console.log('provider-projection-owner: OK')
    return 0
  }

  console.error('provider-projection-owner: VIOLATIONS')
  for (const violation of violations) {
    console.error(`  ${violation.file}:${violation.line} [${violation.rule}] ${violation.text}`)
  }
  return 1
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = runCli()
}
