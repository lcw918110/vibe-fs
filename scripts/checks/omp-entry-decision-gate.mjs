#!/usr/bin/env node
// omp entry gate: the omp entry module must hold no product decision.
//
// The entry module is the single file under `src/Wanxiangshu/Composition/Omp/`
// that carries `[<ExportDefault>]`, plus the `.fsi` beside it. It is wiring:
// it receives the compiled product as a plain `IHostPlugin` value and calls
// `activateOnOmp`. It holds no admission, retry, budget, prose or state, it
// imports no product module, and it never looks product symbols up at runtime.
//
// This gate reads only those two files, never another lane's. A missing entry
// module or an ambiguous `[<ExportDefault>]` site is an issue, not a pass.

import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const repositoryRoot = resolve(here, '../..')

/** Folder that owns the omp entry module. */
export const ENTRY_FOLDER = 'src/Wanxiangshu/Composition/Omp'

/**
 * Namespaces the entry module may name. Everything else is a product module.
 *
 * O-3: "imports no product module"; "never imports product internals"; the
 * wiring surface O-3 prescribes is exactly the neutral ports, the omp adapter
 * and the second-host plugin value: "the Fable-built product is passed in as a
 * plain `IHostPlugin` value".
 */
export const ALLOWED_NAMESPACES = [
  'System',
  'Fable.Core',
  'Wanxiangshu.Composition.Omp',
  'Wanxiangshu.Host.Ports',
  'Wanxiangshu.Host.SecondHost',
  'Wanxiangshu.Omp',
]

/**
 * Decision vocabulary. O-3: "holds no product decision (no admission rule, no
 * retry, no budget, no prose, no state)". DESIGN.md §1 rule 3 lists the same
 * set: "admit/deny a turn or tool, decide retry/abort, compute budgets,
 * render product prose, or keep product state".
 */
export const DECISION_VOCABULARY =
  /\b(?:admit|admission|admitted|refuse|refusal|deny|denied|allow|allowed|quota|retry|retries|retrying|backoff|attempt|attempts|budget|budgets|tokenLimit|tokenBudget|maxTokens|limit|throttle|rateLimit|state|states)\w*/gi

/**
 * Prose lookup. DESIGN.md §1 rule 3: "render product prose" is a product
 * decision; the product's prose lives in the product's resource tree
 * (`src/Wanxiangshu/Resources/`), never in an adapter.
 */
export const RESOURCE_LOOKUP =
  /\b(?:PackageResources|PromptResources|PromptCatalog|Resources)\b|\b(?:File|Directory|IO|FileIO)\.[A-Za-z]+\b|\breadFile\b|\breadAllText\b/g

/**
 * Control flow that could carry a decision. O-3: "no admission rule, no
 * retry, no budget, no prose, no state" — a branch, a loop or a mutable
 * binding is where such a decision would live.
 */
export const DECISION_CONTROL_FLOW =
  /(?<![\w.])(?:if|match|try|while|for|do|failwith|raise)(?![\w])/g

/** Mutable state. O-3: "no ... state"; DESIGN.md §1 rule 3: "keep product state". */
export const MUTABLE_STATE = /\blet\s+mutable\b|\bmutable\b/g

/** Reflection / dynamic lookup. O-3: "no reflection", "never looks up product symbols at runtime". */
export const REFLECTION = /\b(?:Reflection|Assembly|Type\.GetType|Activator|GetType|DynamicInvoke|InvokeMember)\b|\btypeof\b/g

/** Mask F# comments only (strings are preserved): prose detection needs them. */
export function maskFSharpComments(source) {
  const masked = source.split('')
  const blank = (index) => {
    if (masked[index] !== '\n' && masked[index] !== '\r') masked[index] = ' '
  }
  const blankPair = (index) => {
    blank(index)
    blank(index + 1)
  }

  let index = 0
  while (index < source.length) {
    if (source.startsWith('//', index)) {
      while (index < source.length && source[index] !== '\n') blank(index++)
      continue
    }
    if (source.startsWith('(*', index)) {
      let depth = 1
      blankPair(index)
      index += 2
      while (index < source.length && depth > 0) {
        if (source.startsWith('(*', index)) {
          depth++
          blankPair(index)
          index += 2
        } else if (source.startsWith('*)', index)) {
          depth--
          blankPair(index)
          index += 2
        } else {
          blank(index++)
        }
      }
      continue
    }
    index++
  }
  return masked.join('')
}

/** Mask F# comments and string literals: code-only view for structural checks. */
export function maskFSharpCode(source) {
  const masked = source.split('')
  const blank = (index) => {
    if (masked[index] !== '\n' && masked[index] !== '\r') masked[index] = ' '
  }
  const blankPair = (index) => {
    blank(index)
    blank(index + 1)
  }
  let index = 0
  while (index < source.length) {
    if (source.startsWith('//', index)) {
      while (index < source.length && source[index] !== '\n') blank(index++)
      continue
    }
    if (source.startsWith('(*', index)) {
      let depth = 1
      blankPair(index)
      index += 2
      while (index < source.length && depth > 0) {
        if (source.startsWith('(*', index)) {
          depth++
          blankPair(index)
          index += 2
        } else if (source.startsWith('*)', index)) {
          depth--
          blankPair(index)
          index += 2
        } else {
          blank(index++)
        }
      }
      continue
    }
    if (source.startsWith('"""', index)) {
      blankPair(index)
      blankPair(index + 1)
      index += 3
      while (index < source.length && !source.startsWith('"""', index)) blank(index++)
      if (index < source.length) {
        blankPair(index)
        blankPair(index + 1)
        index += 3
      }
      continue
    }
    if (source.startsWith('@"', index)) {
      blankPair(index)
      index += 2
      while (index < source.length) {
        if (source.startsWith('""', index)) {
          blankPair(index)
          index += 2
        } else if (source[index] === '"') {
          blank(index++)
          break
        } else {
          blank(index++)
        }
      }
      continue
    }
    if (source[index] === '"') {
      blank(index++)
      while (index < source.length) {
        if (source[index] === '\\') {
          blank(index++)
          if (index < source.length) blank(index++)
        } else if (source[index] === '"') {
          blank(index++)
          break
        } else {
          blank(index++)
        }
      }
      continue
    }
    index++
  }
  return masked.join('')
}

/** 1-based line number of a character offset. */
const lineOf = (text, offset) => text.slice(0, offset).split('\n').length

/**
 * Locate the entry module: the single file under the omp composition folder
 * that carries `[<ExportDefault>]`. Fail closed when the site is missing or
 * ambiguous (the omp bundle must have exactly one default export).
 */
export function locateEntryModule(root) {
  const folder = join(root, ENTRY_FOLDER)
  if (!existsSync(folder)) {
    return { ok: false, reason: `omp composition folder is missing: ${ENTRY_FOLDER}` }
  }
  const files = readdirSync(folder)
    .filter((name) => name.endsWith('.fs'))
    .sort()
  const sites = files.filter((name) => /\[\s*<ExportDefault>\s*\]/.test(readFileSync(join(folder, name), 'utf8')))
  if (sites.length === 0) {
    return { ok: false, reason: `no [<ExportDefault>] entry module under ${ENTRY_FOLDER}` }
  }
  if (sites.length > 1) {
    return { ok: false, reason: `ambiguous omp entry module under ${ENTRY_FOLDER}: ${sites.join(', ')}` }
  }
  const implementation = join(folder, sites[0])
  const interfaceFile = implementation.replace(/\.fs$/, '.fsi')
  return {
    ok: true,
    implementation,
    interfaceFile: existsSync(interfaceFile) ? interfaceFile : null,
  }
}

/** True when a dotted name is inside the allowed (non-product) namespace set. */
export function isAllowedNamespace(name) {
  return ALLOWED_NAMESPACES.some(
    (allowed) => name === allowed || allowed.startsWith(`${name}.`) || name.startsWith(`${allowed}.`),
  )
}

/**
 * Analyze one entry-module source text.
 *
 * @param {string} text  the raw F# source
 * @param {string} path  repo-relative path, used in the issues
 * @returns {{code: string, path: string, line: number, message: string}[]}
 */
export function analyzeEntrySource(text, path) {
  const issues = []
  const commentFree = maskFSharpComments(text)
  const code = maskFSharpCode(text)

  // (a) Prose. O-3: "holds no product decision (no admission rule, no retry,
  //     no budget, no prose, no state)"; DESIGN.md §1 rule 3: an adapter may
  //     not "render product prose". A string literal in the entry module is
  //     rendered prose; the product's prose lives in its resource tree.
  const literalRe = /"""[\s\S]*?"""|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"/g
  for (const match of commentFree.matchAll(literalRe)) {
    issues.push({
      code: 'omp-entry-prose',
      path,
      line: lineOf(commentFree, match.index),
      message: 'the omp entry module renders prose: O-3 forbids "no ... prose" in the entry module; product prose belongs to the product resource tree',
    })
  }

  // (b) Product import. O-3: "imports no product module"; "never imports
  //     product internals". Only the prescribed neutral wiring surface may be
  //     named.
  for (const match of code.matchAll(/\bWanxiangshu(?:\.[A-Za-z0-9_]+)*/g)) {
    const name = match[0]
    if (isAllowedNamespace(name)) continue
    issues.push({
      code: 'omp-entry-product-import',
      path,
      line: lineOf(code, match.index),
      message: `the omp entry module names product namespace '${name}': O-3 requires "imports no product module"`,
    })
  }

  // (c) Decision control flow. O-3: "no admission rule, no retry, no budget,
  //     no prose, no state" — a branch, a loop or a mutable binding is where
  //     such a decision would live.
  for (const match of code.matchAll(DECISION_CONTROL_FLOW)) {
    issues.push({
      code: 'omp-entry-decision-control-flow',
      path,
      line: lineOf(code, match.index),
      message: `the omp entry module contains control flow ('${match[0]}'): O-3 says the entry module "contains wiring only ... and holds no product decision"`,
    })
  }
  for (const match of code.matchAll(MUTABLE_STATE)) {
    issues.push({
      code: 'omp-entry-decision-state',
      path,
      line: lineOf(code, match.index),
      message: "the omp entry module keeps mutable state: O-3 says the entry module holds \"no ... state\"",
    })
  }

  // (d) Decision vocabulary. O-3: "no admission rule, no retry, no budget";
  //     DESIGN.md §1 rule 3: an adapter may not "admit/deny a turn or tool,
  //     decide retry/abort, compute budgets".
  for (const match of code.matchAll(DECISION_VOCABULARY)) {
    issues.push({
      code: 'omp-entry-decision-vocabulary',
      path,
      line: lineOf(code, match.index),
      message: `the omp entry module carries decision vocabulary ('${match[0]}'): O-3 forbids "no admission rule, no retry, no budget" in the entry module`,
    })
  }

  // (e) Resource / prose lookup. DESIGN.md §1 rule 3: "render product prose"
  //     is a core decision; the entry module must not reach into the product's
  //     resource tree.
  for (const match of code.matchAll(RESOURCE_LOOKUP)) {
    issues.push({
      code: 'omp-entry-resource-lookup',
      path,
      line: lineOf(code, match.index),
      message: `the omp entry module reaches into the product resource tree ('${match[0]}'): O-3 says the entry module "imports no product module"`,
    })
  }

  // (f) Reflection / runtime product lookup. O-3: "no reflection, no dynamic
  //     import of product internals"; "never looks up product symbols at
  //     runtime".
  for (const match of code.matchAll(REFLECTION)) {
    issues.push({
      code: 'omp-entry-reflection',
      path,
      line: lineOf(code, match.index),
      message: `the omp entry module performs runtime lookup ('${match[0]}'): O-3 says "no reflection" and "never looks up product symbols at runtime"`,
    })
  }

  return issues
}

/**
 * Analyze the entry module under a repository root.
 *
 * @param {string} root repository root (absolute)
 * @returns {{code: string, path?: string, line?: number, message: string}[]}
 */
export function analyzeEntryModule(root) {
  const located = locateEntryModule(root)
  if (!located.ok) {
    return [{ code: 'omp-entry-missing', message: located.reason }]
  }
  const issues = []
  const toRel = (absolute) => relative(root, absolute).replaceAll('\\', '/')
  issues.push(...analyzeEntrySource(readFileSync(located.implementation, 'utf8'), toRel(located.implementation)))
  if (located.interfaceFile) {
    issues.push(
      ...analyzeEntrySource(readFileSync(located.interfaceFile, 'utf8'), toRel(located.interfaceFile)),
    )
  }
  return issues
}

/** The check entry point used by `scripts/check.mjs`. */
export function check(context) {
  const root = context?.root ?? repositoryRoot
  return { issues: analyzeEntryModule(root) }
}

/** Standalone CLI. `--root <dir>` checks that repository instead of this one. */
export function runCli(argv = process.argv.slice(2)) {
  const rootIndex = argv.indexOf('--root')
  const root = rootIndex >= 0 && argv[rootIndex + 1] ? resolve(argv[rootIndex + 1]) : repositoryRoot
  const { issues } = check({ root })
  if (issues.length > 0) {
    console.error('omp-entry-decision-gate FAILED:')
    for (const issue of issues) {
      const loc = issue.path ? `${issue.path}${issue.line ? `:${issue.line}` : ''} ` : ''
      console.error(`  - ${loc}[${issue.code}] ${issue.message}`)
    }
    return 1
  }
  console.log('omp-entry-decision-gate OK')
  return 0
}

const isMain = process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url
if (isMain) {
  const code = runCli()
  if (code !== 0) process.exit(code)
}
