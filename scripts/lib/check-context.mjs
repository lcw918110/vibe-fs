/**
 * check-context.mjs — shared scan snapshot for surviving static gates.
 *
 * Surviving gates each re-scan the same production tree. This module is the
 * single owner of that traversal: every file is read at most once per
 * context, the compile-shard inventory is built lazily and shared, and the
 * producer-consumer boundary (production vs test vs resources) is declared
 * here rather than re-derived in each gate.
 *
 * A context is not a cache across runs. It is a within-run snapshot handed
 * to every gate entry so the whole static layer has one view of the work
 * tree at call time.
 */

import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, join, resolve, relative } from 'node:path'
import { fileURLToPath } from 'node:url'
import { maskFSharpTrivia } from './fsharp-source.mjs'

export const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
export const PRODUCTION_ROOT = 'src/Wanxiangshu'
export const TESTS_ROOT_PATTERN = /^requirements\/[^/]+\/tests\//
export const RESOURCES_ROOT = 'resources'

const norm = (p) => p.replace(/\\/g, '/')

/**
 * Throw when a producer's declared root is unreadable — a gate asking for a
 * tree that does not exist must surface as infrastructure failure, never as
 * "no hits".
 */
const assertReadable = (absolutePath) => {
  if (!existsSync(absolutePath)) throw new Error(`check-context missing root: ${absolutePath}`)
  if (!statSync(absolutePath).isDirectory() && !statSync(absolutePath).isFile()) {
    throw new Error(`check-context path is neither a file nor a directory: ${absolutePath}`)
  }
}

/** Recursively enumerate every file under root that matches a suffix list. */
const walkMany = (dir, suffixes, out = []) => {
  let entries
  try {
    entries = readdirSync(dir, { withFileTypes: true })
  } catch (err) {
    throw new Error(`check-context readdir failed on '${dir}': ${err.message}`)
  }
  for (const entry of entries) {
    const absolute = join(dir, entry.name)
    try {
      if (entry.isDirectory()) {
        if (entry.name === 'node_modules' || entry.name === '.git' || entry.name === 'dist' || entry.name === 'obj' || entry.name === 'bin') continue
        walkMany(absolute, suffixes, out)
      } else if (entry.isFile() && suffixes.some((s) => entry.name.endsWith(s))) {
        out.push(absolute)
      }
    } catch (err) {
      throw new Error(`check-context entry failed on '${absolute}': ${err.message}`)
    }
  }
  return out
}


/**
 * Create the shared context. The repo root is resolved once; all paths are
 * normalized to `repo-relative/` POSIX form so gates can compare them.
 *
 * @param {{root?: string}} [opts]
 */
export const createCheckContext = (opts = {}) => {
  const root = resolve(opts.root ?? ROOT)

  /** Absolute path for a repo-relative or absolute path. */
  const toAbsolute = (p) => (p.startsWith('/') ? p : resolve(root, p))

  /** Repo-relative POSIX path for an absolute or relative path. */
  const toRel = (p) => norm(relative(root, p.startsWith('/') ? p : resolve(root, p)))

  // File-content cache. Modules may share text through this slot to avoid
  // re-reading within a single gate run.
  const textCache = new Map()
  const readText = (relOrAbs) => {
    const absolute = toAbsolute(relOrAbs)
    const cached = textCache.get(absolute)
    if (cached !== undefined) return cached
    const text = readFileSync(absolute, 'utf8')
    textCache.set(absolute, text)
    return text
  }

  /** List of all production .fs / .fsi / .fsproj source paths (repo-relative). */
  let allSourceFiles
  const sourceFiles = () => {
    if (allSourceFiles !== undefined) return allSourceFiles
    const base = join(root, PRODUCTION_ROOT)
    assertReadable(base)
    allSourceFiles = walkMany(base, ['.fs', '.fsi', '.fsproj']).map((abs) => norm(relative(root, abs))).sort()
    return allSourceFiles
  }

  /** Read all production `.fs` / `.fsi` files once, both text and normalized F# code views. */
  let productionFsFiles
  const productionFiles = () => {
    if (productionFsFiles !== undefined) return productionFsFiles
    const base = join(root, PRODUCTION_ROOT)
    assertReadable(base)
    productionFsFiles = walkMany(base, ['.fs', '.fsi'])
      .sort()
      .map((absolute) => {
        if (!existsSync(absolute)) {
          throw new Error(`check-context file disappeared during scan: ${absolute}`)
  }
        const rel = norm(relative(root, absolute))
        const text = readText(absolute)
        const code = maskFSharpTrivia(text)
        return { file: rel, fileAbs: absolute, text, code }
      })
    return productionFsFiles
  }

  /** Read all requirement test sources once. */
  let testFiles
  const requirementTests = () => {
    if (testFiles !== undefined) return testFiles
    const base = join(root, 'requirements')
    testFiles = walkMany(base, ['.test.mjs'])
      .filter((path) => REQUIREMENT_TESTS_JOIN.test(relative(root, path)))
      .sort()
      .map((absolute) => ({ file: norm(relative(root, absolute)), fileAbs: absolute, text: readText(absolute) }))
    return testFiles
  }

  /**
   * True when the file is under `requirements/<pkg>/tests/`.
   */
  const isRequirementTestFile = (file) => {
    const rel = toRel(file)
    const m = /^requirements\/([^/]+)\/tests\//.exec(rel)
    return m ? m[1] : null
  }

  /** Production F# source scanner — shared entry for gates that need code+lines. */
  const withLines = (entries = productionFiles()) => {
    return entries.map(({ file, code, text }) => ({
      file,
      text,
      code,
      lines: text.split('\n'),
      codeLines: code.split('\n'),
    }))
  }

  return {
    root,
    sourceFiles,
    readText,
    toAbsolute,
    toRel,
    productionFiles,
    requirementTests,
    isRequirementTestFile,
    withLines,
    assertReadable,
  }
}

const REQUIREMENT_TESTS_JOIN = /(^|\\|\/)tests\//
