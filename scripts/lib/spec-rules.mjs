const CLAUSE_LIKE_RE = /\b([a-z][a-z0-9]*(?:-[a-z0-9]+)*)-(\d{3}(?:-[A-Za-z0-9-]+|[a-zA-Z0-9-]+)?)\b/g
const LEGACY_CLAUSE_LIKE_RE = /\b([A-Z][A-Z0-9]*-\d{3}(?:[A-Z]|-[A-Z0-9-]+)?)\b/g
const NON_CLAUSE_IDENTIFIERS = new Set(['SHA-256', 'sha-256', 'utf-8', 'node:test'])
const CLAUSE_HEADING_RE = /^#{1,6}\s+(?:\[(\d{3})\]|([A-Za-z][A-Za-z0-9]*-\d{3}(?:[A-Za-z]|-[A-Za-z0-9-]+)?)\b)/gm

const escapedAlternation = (prefixes) =>
  prefixes.map((prefix) => prefix.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')

export const ruleDocumentName = (pkg) => pkg === 'verification-system' ? 'AXIOMS.md' : 'WHAT.md'

/** Return whether a path should be ignored as a proposals directory (WHAT[011]). */
export const isProposalPath = (path) => /(?:^|[\\/])proposals(?:[\\/]|$)/.test(path)

/** Return unknown/suffixed clause-looking tokens with 1-based lines. */
export const unknownClauseReferences = (text, validClauses = [], prefixes = []) => {
  const isSet = validClauses instanceof Set
  const validSet = isSet ? validClauses : new Set(validClauses)
  const knownPrefixes = new Set(prefixes)
  const findings = []
  const lines = text.split('\n')

  lines.forEach((content, index) => {
    // Check lowercase pkg-NNN tokens
    for (const match of content.matchAll(CLAUSE_LIKE_RE)) {
      const token = match[1] + '-' + match[2]
      if (NON_CLAUSE_IDENTIFIERS.has(token)) continue

      const exact = /^([a-z][a-z0-9-]*)-(\d{3})$/.exec(token)
      if (!exact) {
        // Suffixed token
        findings.push({ token, line: index + 1 })
      } else {
        const pkg = exact[1]
        if (validSet.size > 0) {
          if (!validSet.has(token)) {
            findings.push({ token, line: index + 1 })
          }
        } else if (knownPrefixes.size > 0 && !knownPrefixes.has(pkg)) {
          findings.push({ token, line: index + 1 })
        }
      }
    }

    // Check legacy uppercase tokens if applicable
    for (const match of content.matchAll(LEGACY_CLAUSE_LIKE_RE)) {
      const token = match[1]
      if (NON_CLAUSE_IDENTIFIERS.has(token)) continue

      const exact = /^([A-Z][A-Z0-9]*)-(\d{3})$/.exec(token)
      if (!exact) {
        findings.push({ token, line: index + 1 })
      } else {
        const prefix = exact[1]
        if (validSet.size > 0) {
          if (!validSet.has(token)) findings.push({ token, line: index + 1 })
        } else if (knownPrefixes.size > 0 && !knownPrefixes.has(prefix)) {
          findings.push({ token, line: index + 1 })
        }
      }
    }
  })

  return findings
}

/** Return every Clause-shaped Markdown heading (## [NNN] or legacy ## PREFIX-NNN). */
export const clauseDefinitionHeadings = (text) => {
  const findings = []
  for (const match of text.matchAll(CLAUSE_HEADING_RE)) {
    const id = match[1] ?? match[2]
    findings.push({ id, line: text.slice(0, match.index).split('\n').length })
  }
  return findings
}

export const ruleClauseHeadings = (text, pkg) => {
  if (ruleDocumentName(pkg) !== 'AXIOMS.md') return clauseDefinitionHeadings(text)
  let inSpec = false
  const spec = text.split('\n').map((line) => {
    if (/^#{1,2}\s/.test(line)) inSpec = /^## SPEC(?:\s|$)/.test(line)
    return inSpec ? line : ''
  }).join('\n')
  return clauseDefinitionHeadings(spec)
}

/** Return formal Clause headings while allowing filtering by known prefixes/packages. */
export const formalClauseDefinitionHeadings = (text, prefixes = []) => {
  const known = new Set(prefixes)
  return clauseDefinitionHeadings(text).filter(({ id }) => {
    if (/^\d{3}$/.test(id)) return true
    return known.size === 0 || known.has(id.split('-')[0])
  })
}

/**
 * Fail-closed duplicate clause check extracted from the retired spec gate.
 *
 * Pure: takes document entries `{ file, pkg, text }`, returns violation objects
 * `{ file, line, msg }`.
 * - WHAT[001]: reports every repeated clause ID; first definition wins.
 * - WHAT[005]: reports any clause definition outside the package rule document.
 * - WHAT[011]: ignores entries under `proposals/`.
 */
export const duplicateClauseDefinitions = (entries) => {
  const definitions = new Map() // id -> { file, line, pkg }
  const prefixOwner = new Map() // prefix -> pkg
  const findings = []

  for (const { file, pkg, text } of entries) {
    if (isProposalPath(file)) continue

    const ruleFile = ruleDocumentName(pkg)
    const isRuleDocument = file.split(/[\\/]/).at(-1) === ruleFile
    const allowedLines = new Set(ruleClauseHeadings(text, pkg).map(({ line }) => line))

    for (const { id, line } of clauseDefinitionHeadings(text)) {
      if (!isRuleDocument) {
        findings.push({
          file,
          line,
          msg: `非 ${ruleFile.slice(0, -3)} 文件严禁定义正式条款：${id}（位于 ${file}:${line}）`,
        })
        continue
      }
      if (!allowedLines.has(line)) {
        findings.push({ file, line, msg: `正式条款必须定义在 SPEC 部分：${id}（位于 ${file}:${line}）` })
        continue
      }

      const fullId = /^\d{3}$/.test(id) ? `${pkg}-${id}` : id
      const previous = definitions.get(fullId)
      if (previous) {
        findings.push({
          file,
          line,
          msg: `条款 ID 重复定义：${fullId}（已在 ${previous.file}:${previous.line} 定义）`,
        })
        continue
      }
      definitions.set(fullId, { file, line, pkg })

      if (id.includes('-')) {
        const prefix = id.split('-')[0]
        const owner = prefixOwner.get(prefix)
        if (owner && owner !== pkg) {
          findings.push({ file, line, msg: `前缀 ${prefix}- 被多包定义：${owner} 与 ${pkg}` })
        } else {
          prefixOwner.set(prefix, pkg)
        }
      }
    }
  }
  return findings
}

/** Return references to the retired workflow directories under docs. */
export const legacyWorkflowPathReferences = (text) => {
  const findings = []

  text.split('\n').forEach((content, index) => {
    const line = index + 1
    if (/(?:^|[^A-Za-z])docs\/proposal(?:\/|\b)/.test(content))
      findings.push({ token: 'docs/proposal/', line })
    if (/(?:^|[^A-Za-z])docs\/status(?:\/|\b)/.test(content))
      findings.push({ token: 'docs/status/', line })
  })

  return findings
}

/** Return references to the deleted archive/ tree (2026-08-14 cutover). */
export const archivePathReferences = (text) => {
  const findings = []

  text.split('\n').forEach((content, index) => {
    const match = /(?:^|[^A-Za-z])(archive\/[^\s`"')\]>]*)/.exec(content)
    if (match) findings.push({ token: match[1], line: index + 1 })
  })

  return findings
}

/** Return forbidden implementation/spec dependencies on lifecycle history. */
export const changeDependencyReferences = (text) => {
  const findings = []

  text.split('\n').forEach((content, index) => {
    const line = index + 1
    if (/(?:^|[^A-Za-z])changes\/proposed(?:\/|\b)/.test(content))
      findings.push({ token: 'changes/proposed/', line })
    if (/(?:^|[^A-Za-z])changes\/completed\/[^`)\n>]+\.md(?:\b|#)/.test(content))
      findings.push({ token: 'changes/completed/<file>.md', line })
  })

  return findings
}

/** Return relative Markdown link targets; URL schemes and document-local anchors are excluded. */
export const markdownLocalLinks = (text) => {
  const findings = []
  const link = /\]\((?:<([^>]+)>|([^\s)]+))\)/g

  for (const match of text.matchAll(link)) {
    const raw = match[1] ?? match[2]
    if (!raw || raw.startsWith('#') || /^[a-z][a-z0-9+.-]*:/i.test(raw)) continue
    const withoutFragment = raw.split('#')[0].split('?')[0]
    let target = withoutFragment
    try {
      target = decodeURIComponent(withoutFragment)
    } catch {
      // Invalid URI escaping remains a filesystem miss in the caller.
    }
    findings.push({ target, line: text.slice(0, match.index).split('\n').length })
  }

  return findings
}

/**
 * Return known-prefix clause references, expanding compact spellings:
 * `pkg-003/005/006` checks all three; `pkg-009..012` and `pkg-006…012`
 * check both endpoints. Ranges do not imply that every intermediate number exists.
 */
export const clauseReferences = (text, prefixes = []) => {
  const alternation = prefixes.length > 0
    ? escapedAlternation(prefixes)
    : '[a-z][a-z0-9-]*|[A-Z][A-Z0-9]*'
  const exact = new RegExp(`\\b(${alternation})-(\\d{3})\\b`, 'g')
  const slashTail = new RegExp(`\\b(${alternation})-\\d{3}((?:/\\d{3})+)\\b`, 'g')
  const rangeEnd = new RegExp(`\\b(${alternation})-\\d{3}(?:\\.\\.|…)(\\d{3})\\b`, 'g')
  const findings = []
  const seen = new Set()

  const add = (id, line) => {
    const key = `${line}:${id}`
    if (!seen.has(key)) {
      seen.add(key)
      findings.push({ id, line })
    }
  }

  text.split('\n').forEach((content, index) => {
    const line = index + 1

    for (const match of content.matchAll(exact)) add(`${match[1]}-${match[2]}`, line)

    for (const match of content.matchAll(slashTail)) {
      for (const suffix of match[2].split('/').filter(Boolean)) {
        add(`${match[1]}-${suffix}`, line)
      }
    }

    for (const match of content.matchAll(rangeEnd)) add(`${match[1]}-${match[2]}`, line)
  })

  return findings
}

/** Compare README links for one directory with its exact Markdown file set. */
export const navigationProblems = (navigation, directory, files) => {
  const expected = new Set(files)
  const linked = new Map()
  const escapedDirectory = directory.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const link = new RegExp(`\\]\\((?:<)?(${escapedDirectory}\\/[^)>]+\\.md)(?:>)?\\)`, 'g')

  for (const match of navigation.matchAll(link)) {
    const line = navigation.slice(0, match.index).split('\n').length
    linked.set(match[1], line)
  }

  return {
    missing: [...expected].filter((file) => !linked.has(file)).sort(),
    stale: [...linked]
      .filter(([file]) => !expected.has(file))
      .map(([file, line]) => ({ file, line }))
      .sort((a, b) => a.file.localeCompare(b.file)),
  }
}

