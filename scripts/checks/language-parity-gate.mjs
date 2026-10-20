#!/usr/bin/env node
/**
 * ARCH-016 Gate C — Language Parity (HOST-026 / PROMPT-017/019/020).
 * Gate F — Office Capability Integrity (ARCH-017).
 * Every provider semantic directory must contain en.md + zh-CN.md locale leaves.
 * Protocol identifiers (code spans + TipIdentity / hyphenated tool names) must
 * be the same form in both locales.
 *
 * Usage: node scripts/checks/language-parity-gate.mjs
 */

import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { walk } from '../lib/walk.mjs'

export const PROVIDER_ROOT = 'resources/provider'
export const LOCALE_FILES = Object.freeze(['en.md', 'zh-CN.md'])
export const PROVIDER_RESOURCES_REL = 'src/Wanxiangshu/Participant/Provider/ProviderResources.fs'
export const PROVIDER_LANGUAGE_BINDING_REL =
  'src/Wanxiangshu/OpenCode/Host/ProviderLanguageBinding.fs'
export const STATIC_TOOLS_REL = 'src/Wanxiangshu/OpenCode/Tools/StaticTools.fs'

/**
 * Production sources whose Class A text must follow the bound language. A
 * hardcoded `ProviderLanguage.English` in these files ships English prose to a
 * Chinese-bound session (provider-language-008).
 */
export const LLM_FACING_SOURCES = Object.freeze([
  'src/Wanxiangshu/Resources/PromptSurface.fs',
  'src/Wanxiangshu/Resources/PromptResources.fs',
  'src/Wanxiangshu/OpenCode/Host/ManagedAgentConfig.fs',
  'src/Wanxiangshu/Execution/Session/OpenCode/HorizonSurface.fs',
  'src/Wanxiangshu/Mission/Obligation/Todo/OpenCode/MagicTodoHostSurface.fs',
])

/// `ProviderLanguage.English` used as a rendering language, not as a match
/// arm over a language parameter (which is legitimate: both cases must exist).
const HARDCODED_ENGLISH = /ProviderProse\.(?:render|instructionLines)\s+ProviderLanguage\.English|ProviderResources\.readText\s+ProviderLanguage\.English|\(\s*ProviderLanguage\.English\s+output\s*\)|applyDefinition\s+ProviderLanguage\.English/

/**
 * provider-language-008: no LLM-facing module may hardcode the English
 * rendering language. The session's bound language — or the global preference
 * when no session is in scope — is the only lawful source.
 * @param {string} text
 * @param {string} path
 * @returns {Violation[]}
 */
export const scanHardcodedEnglish = (text, path) => {
  /** @type {Violation[]} */
  const violations = []
  text.split('\n').forEach((line, index) => {
    if (!HARDCODED_ENGLISH.test(line)) return
    violations.push({
      code: 'hardcoded-english',
      path,
      detail: `line ${index + 1} renders Class A prose in a hardcoded English language: ${line.trim()}`,
    })
  })
  return violations
}

const norm = (p) => p.replace(/\\/g, '/')

const escapeRegExp = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')

/**
 * Directories under provider root that host at least one locale leaf.
 * @param {string} providerAbs
 * @returns {string[]} semantic paths relative to provider root (e.g. role/manager)
 */
export const listSemanticResourceDirs = (providerAbs) => {
  if (!existsSync(providerAbs)) return []
  const dirs = new Set()
  for (const abs of walk(providerAbs)) {
    const base = abs.replace(/\\/g, '/').split('/').pop() ?? ''
    if (!LOCALE_FILES.includes(base)) continue
    dirs.add(norm(relative(providerAbs, dirname(abs))))
  }
  return [...dirs].sort()
}

/**
 * Hyphenated tool names only — short English verbs (`read`, `run`) are not
 * protocol markers when bare in prose.
 * @param {string} text StaticTools.fs source
 * @returns {string[]}
 */
export const listHyphenatedToolNames = (text) => {
  const block = text.match(/let\s+knownToolNames\s*=\s*\[([\s\S]*?)\]/)
  if (!block) return []
  return [...block[1].matchAll(/"([^"]+)"/g)]
    .map((m) => m[1])
    .filter((name) => name.includes('-'))
    .sort()
}

/**
 * @param {string} text
 * @returns {Set<string>}
 */
export const extractCodeSpans = (text) => {
  const withoutFences = text.replace(/```[\s\S]*?```/g, '')
  /** @type {Set<string>} */
  const spans = new Set()
  for (const hit of withoutFences.matchAll(/`([^`\n]+)`/g)) {
    spans.add(hit[1])
  }
  return spans
}

/**
 * @param {string} text
 * @param {Iterable<string>} catalog
 * @returns {Set<string>}
 */
export const extractCatalogHits = (text, catalog) => {
  /** @type {Set<string>} */
  const hits = new Set()
  for (const id of catalog) {
    if (!id) continue
    if (new RegExp(`\\b${escapeRegExp(id)}\\b`).test(text)) hits.add(id)
  }
  return hits
}

/**
 * @param {string} text
 * @param {{ tipIdentities?: Iterable<string>, toolNames?: Iterable<string> }} [catalogs]
 * @returns {Set<string>}
 */
export const extractProtocolIdentifiers = (text, catalogs = {}) => {
  /** @type {Set<string>} */
  const ids = new Set(extractCodeSpans(text))
  for (const id of extractCatalogHits(text, catalogs.tipIdentities ?? [])) ids.add(id)
  for (const id of extractCatalogHits(text, catalogs.toolNames ?? [])) ids.add(id)
  return ids
}

/**
 * @typedef {{ code: string, path: string, detail?: string }} Violation
 */

/**
 * @param {string[]} semanticDirs paths relative to provider root
 * @param {string} providerAbs absolute provider root
 * @returns {Violation[]}
 */
export const scanParity = (semanticDirs, providerAbs) => {
  /** @type {Violation[]} */
  const violations = []
  for (const semantic of semanticDirs) {
    for (const locale of LOCALE_FILES) {
      const rel = join(PROVIDER_ROOT, semantic, locale)
      const abs = join(providerAbs, semantic, locale)
      if (!existsSync(abs)) {
        violations.push({
          code: locale === 'en.md' ? 'missing-en' : 'missing-zh-cn',
          path: rel,
          detail: `locale leaf missing for semantic resource ${semantic}`,
        })
      }
    }
  }
  return violations
}

/**
 * @param {Set<string>} a
 * @param {Set<string>} b
 * @returns {{ onlyA: string[], onlyB: string[] }}
 */
export const setDiff = (a, b) => ({
  onlyA: [...a].filter((x) => !b.has(x)).sort(),
  onlyB: [...b].filter((x) => !a.has(x)).sort(),
})

const PLACEHOLDER_RE = /\{\{([A-Za-z][A-Za-z0-9_]*)\}\}/g

/**
 * Named `{{placeholder}}` operands (PROMPT-019). Values are language-invariant;
 * the placeholder *set* must be identical across EN / zh-CN.
 * @param {string} text
 * @returns {Set<string>}
 */
export const extractPlaceholders = (text) => {
  /** @type {Set<string>} */
  const names = new Set()
  PLACEHOLDER_RE.lastIndex = 0
  let m
  while ((m = PLACEHOLDER_RE.exec(text)) !== null) names.add(m[1])
  return names
}

/**
 * EN/zh-CN `{{name}}` sets must be equal (PROMPT-019 structural parity).
 * @param {string[]} semanticDirs
 * @param {string} providerAbs
 * @returns {Violation[]}
 */
export const scanPlaceholderParity = (semanticDirs, providerAbs) => {
  /** @type {Violation[]} */
  const violations = []
  for (const semantic of semanticDirs) {
    const enAbs = join(providerAbs, semantic, 'en.md')
    const zhAbs = join(providerAbs, semantic, 'zh-CN.md')
    if (!existsSync(enAbs) || !existsSync(zhAbs)) continue
    const enPh = extractPlaceholders(readFileSync(enAbs, 'utf8'))
    const zhPh = extractPlaceholders(readFileSync(zhAbs, 'utf8'))
    const { onlyA: onlyEn, onlyB: onlyZh } = setDiff(enPh, zhPh)
    if (onlyEn.length === 0 && onlyZh.length === 0) continue
    violations.push({
      code: 'placeholder-parity',
      path: norm(join(PROVIDER_ROOT, semantic)),
      detail:
        `placeholders differ — only-en: [${onlyEn.join(', ')}]; ` +
        `only-zh-CN: [${onlyZh.join(', ')}]`,
    })
  }
  return violations
}

/**
 * EN/zh-CN protocol identifier sets must be equal (AC20).
 * @param {string[]} semanticDirs
 * @param {string} providerAbs
 * @param {{ tipIdentities?: Iterable<string>, toolNames?: Iterable<string> }} [catalogs]
 * @returns {Violation[]}
 */
export const scanIdentifierParity = (semanticDirs, providerAbs, catalogs = {}) => {
  /** @type {Violation[]} */
  const violations = []
  for (const semantic of semanticDirs) {
    const enAbs = join(providerAbs, semantic, 'en.md')
    const zhAbs = join(providerAbs, semantic, 'zh-CN.md')
    if (!existsSync(enAbs) || !existsSync(zhAbs)) continue
    const enIds = extractProtocolIdentifiers(readFileSync(enAbs, 'utf8'), catalogs)
    const zhIds = extractProtocolIdentifiers(readFileSync(zhAbs, 'utf8'), catalogs)
    const { onlyA: onlyEn, onlyB: onlyZh } = setDiff(enIds, zhIds)
    if (onlyEn.length === 0 && onlyZh.length === 0) continue
    violations.push({
      code: 'identifier-parity',
      path: norm(join(PROVIDER_ROOT, semantic)),
      detail:
        `protocol identifiers differ — only-en: [${onlyEn.join(', ')}]; ` +
        `only-zh-CN: [${onlyZh.join(', ')}]`,
    })
  }
  return violations
}

/**
 * Host may observe the raw environment value, but provider language defaulting
 * and parsing belong to the Participant/Provider owner.
 * @param {string} text ProviderLanguageBinding.fs source
 * @returns {Violation[]}
 */
export const scanProviderLanguageBinding = (text) => {
  /** @type {Violation[]} */
  const violations = []
  const reject = (detail) =>
    violations.push({
      code: 'provider-language-policy',
      path: PROVIDER_LANGUAGE_BINDING_REL,
      detail,
    })

  if (!text.includes('Environment.GetEnvironmentVariable')) {
    reject('Host binding must observe the raw provider-language environment value')
  }
  if (!/ProviderLanguage\s*\.\s*(?:fromPreferenceObservation|fromObservationLadder)/.test(text)) {
    reject(
      'Host binding must delegate provider-language defaulting and parsing to ProviderLanguage.fromPreferenceObservation or ProviderLanguage.fromObservationLadder',
    )
  }
  if (/ProviderLanguage\s*\.\s*English/.test(text)) {
    reject('ProviderLanguage.English fallback belongs to Participant/Provider owner, not Host')
  }
  if (/ProviderLanguage\s*\.\s*tryParse/.test(text)) {
    reject('ProviderLanguage.tryParse belongs to Participant/Provider owner, not Host')
  }
  if (/String\s*\.\s*IsNullOrWhiteSpace|\.IsNullOrWhiteSpace\s*\(/.test(text)) {
    reject('provider-language whitespace/default policy belongs to Participant/Provider owner, not Host')
  }
  if (/\|\s*None\s*->\s*ProviderLanguage\b|Option\s*\.\s*default(?:Value|With)/.test(text)) {
    reject('provider-language default branches belong to Participant/Provider owner, not Host')
  }
  if (/["'](?:en|en-US|zh|zh-CN|zh_CN|English|SimplifiedChinese)["']/i.test(text)) {
    reject('provider-language aliases belong to Participant/Provider owner, not Host')
  }
  return violations
}

/**
 * @param {string} [repoRoot]
 * @param {{ tipIdentities?: Iterable<string>, toolNames?: Iterable<string> }} [catalogOverrides]
 * @returns {{ ok: boolean, violations: Violation[], semanticDirs: string[] }}
 */

/**
 * provider-language-012: Bilingual prompt semantic parity for core roles.
 */
export const ROLE_PARITY_SPECS = [
  {
    role: 'engineer',
    enPatterns: [
      /only role permitted to use Fission/i,
      /Sphinx invocation[\s\S]*?standard Engineer[\s\S]*?not add a read-only restriction/i,
    ],
    zhPatterns: [
      /唯一允许使用 Fission/,
      /Sphinx 内部调用使用标准 Engineer 权限[\s\S]*?不另设只读限制/,
    ],
  },
  {
    role: 'devops',
    enPatterns: [
      /cannot Fission/i,
      /do not create or dispatch other engineering agents/i,
      /direct (?:engineering and autonomous local )?repair/i,
    ],
    zhPatterns: [
      /不能 Fission/,
      /不创建或差遣其他工程代理/,
      /直接(?:工程与自主局部)?修复/,
    ],
  },
  {
    role: 'manager',
    enPatterns: [
      /cannot use Fission/i,
      /do not create (?:copies of yourself|management clones)/i,
    ],
    zhPatterns: [
      /不能使用 Fission/,
      /不创建自己的副本|不创建管理分身/,
    ],
  },
]

export const FORBIDDEN_PROMPT_PATTERNS = [
  {
    pattern: /Manager\s*(?:(?:可以|能够|允许|可|支持)\s*(?:使用\s*)?Fission|可分身|能够分身|进行分身)/i,
    name: 'manager-fission-grant',
  },
  {
    pattern: /DevOps\s*(?:(?:可以|能够|允许|可|支持)\s*(?:使用\s*)?Fission|可分身|能够分身|进行分身)/i,
    name: 'devops-fission-grant',
  },
  {
    pattern: /Manager\s*(?:can|may|is permitted to|is allowed to)\s*(?:use\s*)?Fission/i,
    name: 'manager-fission-grant-en',
  },
  {
    pattern: /DevOps\s*(?:can|may|is permitted to|is allowed to)\s*(?:use\s*)?Fission/i,
    name: 'devops-fission-grant-en',
  },
]

/**
 * Scan core role prompts for bilingual semantic parity (provider-language-012).
 * @param {string} providerAbs
 * @returns {Violation[]}
 */
export const scanRolePromptParity = (providerAbs) => {
  /** @type {Violation[]} */
  const violations = []
  const roleRoot = join(providerAbs, 'role')

  for (const spec of ROLE_PARITY_SPECS) {
    const enPath = join(roleRoot, spec.role, 'en.md')
    const zhPath = join(roleRoot, spec.role, 'zh-CN.md')
    if (!existsSync(enPath) || !existsSync(zhPath)) continue

    const enText = readFileSync(enPath, 'utf8')
    const zhText = readFileSync(zhPath, 'utf8')

    for (const pat of spec.enPatterns) {
      if (!pat.test(enText)) {
        violations.push({
          code: 'role-prompt-parity',
          path: norm(join(PROVIDER_ROOT, 'role', spec.role, 'en.md')),
          detail: `core role prompt missing required parity anchor: ${pat}`,
        })
      }
    }

    for (const pat of spec.zhPatterns) {
      if (!pat.test(zhText)) {
        violations.push({
          code: 'role-prompt-parity',
          path: norm(join(PROVIDER_ROOT, 'role', spec.role, 'zh-CN.md')),
          detail: `core role prompt missing required parity anchor: ${pat}`,
        })
      }
    }
  }

  return violations
}

/**
 * Scan provider markdown files for affirmative invalid fission claims (provider-language-012).
 * Strictly distinguishes affirmative grants from legitimate negative guards.
 * @param {string} providerAbs
 * @returns {Violation[]}
 */
export const scanForbiddenPromptPhrases = (providerAbs) => {
  /** @type {Violation[]} */
  const violations = []
  if (!existsSync(providerAbs)) return violations

  for (const abs of walk(providerAbs)) {
    if (!abs.endsWith('.md')) continue
    const text = readFileSync(abs, 'utf8')
    const lines = text.split('\n')

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i]
      for (const fp of FORBIDDEN_PROMPT_PATTERNS) {
        if (fp.pattern.test(line)) {
          violations.push({
            code: 'forbidden-fission-claim',
            path: norm(relative(resolve(providerAbs, '..', '..'), abs)),
            detail: `line ${i + 1} matches forbidden affirmative fission claim ${fp.name}: ${line.trim()}`,
          })
        }
      }
    }
  }

  return violations
}

export const scanRepo = (repoRoot = process.cwd(), catalogOverrides) => {
  /** @type {Violation[]} */
  const violations = []
  const providerAbs = resolve(repoRoot, PROVIDER_ROOT)

  if (!existsSync(providerAbs)) {
    violations.push({ code: 'missing-provider-root', path: PROVIDER_ROOT, detail: 'provider root missing' })
    return { ok: false, violations, semanticDirs: [] }
  }

  const semanticDirs = listSemanticResourceDirs(providerAbs)
  if (semanticDirs.length === 0) {
    violations.push({
      code: 'no-semantic-resources',
      path: PROVIDER_ROOT,
      detail: 'no semantic resource directories with locale leaves found',
    })
  } else {
    violations.push(...scanParity(semanticDirs, providerAbs))
  }

  const tipIdentities = catalogOverrides?.tipIdentities ?? []
  const toolNames =
    catalogOverrides?.toolNames ??
    (() => {
      const abs = resolve(repoRoot, STATIC_TOOLS_REL)
      return existsSync(abs) ? listHyphenatedToolNames(readFileSync(abs, 'utf8')) : []
    })()

  if (semanticDirs.length > 0) {
    violations.push(...scanIdentifierParity(semanticDirs, providerAbs, { tipIdentities, toolNames }))
    violations.push(...scanPlaceholderParity(semanticDirs, providerAbs))
    violations.push(...scanRolePromptParity(providerAbs))
    violations.push(...scanForbiddenPromptPhrases(providerAbs))
  }

  const bindingAbs = resolve(repoRoot, PROVIDER_LANGUAGE_BINDING_REL)
  if (!existsSync(bindingAbs)) {
    violations.push({
      code: 'missing-file',
      path: PROVIDER_LANGUAGE_BINDING_REL,
      detail: 'ProviderLanguageBinding.fs missing',
    })
  } else {
    violations.push(...scanProviderLanguageBinding(readFileSync(bindingAbs, 'utf8')))
  }

  return { ok: violations.length === 0, violations, semanticDirs }
}

export function check(context) {
  const root = context?.root ?? process.cwd()
  const result = scanRepo(root)
  return {
    issues: result.violations.map((v) => ({
      code: v.code,
      path: v.path,
      message: v.detail ?? v.code,
    })),
    semanticDirs: result.semanticDirs,
    ok: result.ok,
  }
}

export function runCli() {
  const result = check()
  if (result.ok) {
    console.log(
      `language-parity-gate: OK — ${result.semanticDirs.length} semantic resource(s); ` +
        'each has en.md + zh-CN.md; protocol identifiers match; ' +
        'placeholders match',
    )
    return 0
  }
  console.error(`language-parity-gate: ${result.issues.length} violation(s)\n`)
  for (const v of result.issues) console.error(`  ${v.path}: ${v.code}${v.message ? ` — ${v.message}` : ''}`)
  return 1
}

const formatViolation = (v) => {
  const detail = v.detail ? ` — ${v.detail}` : ''
  return `  ${v.path}: ${v.code}${detail}`
}

const isMain =
  process.argv[1] !== undefined &&
  resolve(fileURLToPath(import.meta.url)) === resolve(process.argv[1])

if (isMain) {
  const code = runCli()
  if (code !== 0) process.exit(code)
}
