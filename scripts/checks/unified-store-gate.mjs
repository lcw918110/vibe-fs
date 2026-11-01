/**
 * Unified-store architecture gate.
 *
 * Scanners:
 *   1. feature-ref — refs/wanxiang/ outside Persist/Git ownership (only store may appear there)
 *   2. schema-version-in-store-context — durable event/store protocol versioning (§36)
 *   3. git-bypass — direct git process invocations outside Git/Persist ownership (§37)
 *   4. dual-write — same production module writing EventStore AND Journal NDJSON
 *
 * Scanners (canonical history ownership):
 *   7. feature-history-loop — path-sensitive reader/merge/fold census
 *   8. private-durable-substrate — feature-local NDJSON/SQLite/private stores
 *   9. canonical-shared-program — one-envelope IntegrationRule and shared boot/live integrateOne
 *
 * Dual-write note (Phase 5 DONE for NDJSON substrate): production Boot / NDJSON
 * JournalWriter / dir BlobWriter are deleted. Strategy A keeps AgentJournal as an
 * EventStore-backed surface — AgentJournal-only modules are NOT dual-write.
 * Persist/EventStore-only modules are NOT dual-write. This scanner only flags
 * detectable same-module bridges that write both surfaces. DUAL_WRITE_ALLOWLIST
 * stays empty unless a documented false-positive must be named; do not use it to
 * park real bridges.
 *
 * Modes:
 *   node scripts/checks/unified-store-gate.mjs           scan production + clean-break roots
 *   import { scanFeatureRef, ... } from ...              pure synthetic / fixture tests
 *
 * Does NOT implement EventStore.
 */

import { existsSync, readFileSync } from 'node:fs'
import { relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { walk } from '../lib/walk.mjs'

export const PRODUCTION_ROOT = 'src/Wanxiangshu'

/** Paths (posix, relative to PRODUCTION_ROOT) allowed to own the canonical store ref / Git primitives. */
export const STORE_OWNER_PREFIXES = ['Persistence/', 'Git/', 'Infrastructure/Persist/', 'Infrastructure/Git/']

/**
 * Extra production sites allowed to mention git process tokens outside Persist/Git.
 * Wave C cleared this: host callers go through Infrastructure/Git/GitSubject.fs.
 * New bypasses must NOT be added here — route through Infrastructure/Git (eventually GitGateway).
 */
export const GIT_BYPASS_ALLOWLIST = Object.freeze([])

/**
 * Host / authored-resource schemaVersion sites that are NOT durable event-store protocol
 * versioning (SPEC unknowns + §36: product/host/authored versions are allowed).
 * Listed for documentation; the store-context heuristic already excludes them.
 */
export const NON_STORE_SCHEMA_VERSION_SITES = Object.freeze([
  'Execution/Delegation/Handle/CompletionCodec.fs',
  'Execution/Delegation/Fork/ChildRecovery.fs',
])

/**
 * Documented dual-write false-positive allowlist (posix paths relative to repo root).
 * Keep empty: Strategy A AgentJournal-only code is not a hit; do not allowlist real
 * EventStore+Journal bridges. Phase 5 deleted NDJSON writers — only same-module dual writers RED.
 */
export const DUAL_WRITE_ALLOWLIST = Object.freeze([
  // Verification proof surface: writes exclusively through AgentJournal (the
  // EventStore-backed application journal); `IEventStore` tokens appear only in
  // the Append-gating decorator that parks one physical Append mid-commit.
  'src/Wanxiangshu/Verification/JournalPortObservationSurface.fs',
])

export const SCANNER_IDS = Object.freeze([
  'feature-ref',
  'schema-version-in-store-context',
  'git-bypass',
  'dual-write',
  'feature-history-loop',
  'private-durable-substrate',
  'canonical-shared-program',
])

const norm = (p) => p.replace(/\\/g, '/')

const CANONICAL_INTEGRATOR_PATH =
  'src/Wanxiangshu/Persistence/EventStore/IntegratorEngine.fs'
const INTEGRATION_KERNEL_PATH =
  'src/Wanxiangshu/Persistence/EventStore/IntegrationKernel.fs'

/**
 * Exact physical stream readers/convergence programs. They may observe or order
 * envelopes, but do not own any feature Current. Keep this census exact: a new
 * reader is an architecture decision, not a prefix exception.
 */
export const PHYSICAL_HISTORY_OBSERVER_PATHS = Object.freeze([
  'src/Wanxiangshu/Persistence/EventStore/ProcessEventLog.fs',
  'src/Wanxiangshu/Persistence/EventStore/EventKWayMerge.fs',
  'src/Wanxiangshu/Persistence/EventStore/RetentionSurface.fs',
  'src/Wanxiangshu/Persistence/EventStore/MergeSurface.fs',
  'src/Wanxiangshu/Verification/TemporalSurface.fs',
  'src/Wanxiangshu/Verification/JournalPortObservationSurface.fs',
])

const pathIs = (file, expected) => {
  const n = norm(file)
  return n === expected || n.endsWith(`/${expected}`)
}

const isCanonicalIntegratorPath = (file) => pathIs(file, CANONICAL_INTEGRATOR_PATH)
const isPhysicalHistoryObserverPath = (file) =>
  PHYSICAL_HISTORY_OBSERVER_PATHS.some((expected) => pathIs(file, expected))

/** Exact canonical implementations that own the public single-event/head reader surface. */
export const CANONICAL_EVENT_READER_OWNER_PATHS = Object.freeze([
  'src/Wanxiangshu/Persistence/EventStore/Store.fs',
  'src/Wanxiangshu/Persistence/EventStore/Surface.fs',
  'src/Wanxiangshu/OpenCode/Host/WorkspaceEventStore.fs',
  'src/Wanxiangshu/Persistence/Journal/EventStoreJournalWriter.fs',
  'src/Wanxiangshu/Verification/EventStoreWriterSurface.fs',
  'src/Wanxiangshu/Verification/JournalPortObservationSurface.fs',
])
const isCanonicalEventReaderOwnerPath = (file) =>
  isCanonicalIntegratorPath(file) ||
  isPhysicalHistoryObserverPath(file) ||
  CANONICAL_EVENT_READER_OWNER_PATHS.some((expected) => pathIs(file, expected))
const DURABLE_SUBSTRATE_OWNER_PATHS = Object.freeze([
  'src/Wanxiangshu/Persistence/EventStore/ProcessEventLog.fs',
  'src/Wanxiangshu/Persistence/EventStore/WriterStreamSync.fs',
  'src/Wanxiangshu/Persistence/EventStore/StoreTypes.fs',
  'src/Wanxiangshu/Persistence/EventStore/RetentionSurface.fs',
  'src/Wanxiangshu/OpenCode/Host/WorkspaceEventStore.fs',
  'src/Wanxiangshu/Persistence/Journal/EventStoreJournalWriter.fs',
  'src/Wanxiangshu/Verification/EventStoreWriterSurface.fs',
  // Host-owned lifecycle journal for new work (work.sqlite): the only module
  // that opens and closes it. Registered as its exact physical owner; the
  // cutover to the canonical EventStore is consumer wiring, not this task.
  'src/Wanxiangshu/Mission/Lifecycle/DurableWorkHost.fs',
])
const isDurableSubstrateOwnerPath = (file) =>
  DURABLE_SUBSTRATE_OWNER_PATHS.some((expected) => pathIs(file, expected))

const stripLineComment = (line) => line.replace(/\/\/.*/, '')

/** @param {string} file */
export const relToProduction = (file) => {
  const n = norm(file)
  const root = `${PRODUCTION_ROOT}/`
  if (n.startsWith(root)) return n.slice(root.length)
  if (n.startsWith('src/Wanxiangshu/')) return n.slice('src/Wanxiangshu/'.length)
  return n
}

/** @param {string} rel */
export const isStoreOwnerPath = (rel) =>
  STORE_OWNER_PREFIXES.some((prefix) => rel.startsWith(prefix) || rel === prefix.slice(0, -1))

/** @param {string} rel */
export const isGitBypassAllowed = (rel) =>
  isStoreOwnerPath(rel) || GIT_BYPASS_ALLOWLIST.includes(rel)

/** @param {string} file */
export const isDualWriteAllowed = (file) => {
  const n = norm(file)
  return DUAL_WRITE_ALLOWLIST.some((entry) => n === entry || n.endsWith(`/${entry}`))
}

/**
 * @typedef {{ id: string, file: string, line: number, token?: string, label: string, text: string }} Violation
 */

const FEATURE_REF_RE = /refs\/wanxiang\//
const CANONICAL_STORE_REF = 'refs/wanxiang/store'

/** Always-forbidden durable store protocol version tokens (§36). */
const ALWAYS_STORE_VERSION_RES = [
  { re: /\bstorageVersion\b/, token: 'storageVersion' },
  { re: /\bjournalVersion\b/, token: 'journalVersion' },
  { re: /\bformatVersion\b/, token: 'formatVersion' },
  { re: /\bschema_version\b/, token: 'schema_version' },
]

/** schemaVersion only RED when store/event envelope context is present nearby. */
const SCHEMA_VERSION_RE = /\bschemaVersion\b/
const STORE_CONTEXT_RE =
  /\b(?:EventEnvelope|EventStore|IEventStore|StoreSnapshot|AppendCandidate|GitRawStore|payload_refs|event_type)\b|refs\/wanxiang\/store\b|durable\s+event\s+store|event\s+store\s+protocol/i

/** Line-local git process / argv construction (F# + Fable). */
const GIT_BYPASS_LINE_RES = [
  /\bFileName\s*=\s*"git"/,
  /\bexecFileSync\s*\(\s*"git"/,
  /\bexecFile\s*\(\s*"git"/,
  /\bProcess\.Start\s*\(\s*"git"/,
  /\bspawn(?:Sync)?\s*\(\s*"git"/,
  /\bcommand\s+\w+\s+"git"/,
  /^\s*"git"\s*$/,
]

/** Multiline forms (execFileSync "git" across lines). */
const GIT_BYPASS_MULTILINE_RES = [
  /\bexecFileSync\b[\s\S]{0,80}"git"/,
  /\bexecFile\b[\s\S]{0,80}"git"/,
]

/** Owner-only remote-tracking store tip: refs/wanxiang/remotes/<remote>/store (§14). */
const REMOTE_TRACKING_STORE_REF_RE = /^refs\/wanxiang\/remotes\/[^/]+\/store$/

// A type annotation or a Current read is not a write. The fixture's
// store.Append plus journal.AppendAgent is the shape this gate rejects.
const EVENT_STORE_WRITE_RE =
  /\b(?:store|eventStore|held)\s*\.\s*Append\b|\bAppendCandidate\b|\bEventStore\s*\.\s*(?:append|create|createWithConverge|createWithRetries|commit)\b/
const JOURNAL_NDJSON_WRITE_RE =
  /\b(?:JournalWriter|journal)\s*\.\s*Append|\b(?:AgentJournal|SharedAgentJournal)\s*\.\s*Append\b|\.\s*AppendAgent\b|\.ndjson\b|wanxiangshu\/runtimes/

/** History access/order is owner-sensitive; feature history APIs are always forbidden. */
const HISTORY_OBSERVER_RES = [
  { re: /\bProcessEventLog\.readStreams(?:At)?\b/ },
  { re: /\bEventKWayMerge\.(?:merge|mergeRetained)\b/ },
]

const FEATURE_HISTORY_LOOP_RES = [
  { re: /\b(?:replay|load|read|scan|fold|merge)(?:Events?|History)\b/i },
  { re: /\b(?:replay|load|read|scan|fold|merge)[_-](?:events?|history)\b/i },
  { re: /\bmanualMerge\b/ },
]

/** Canonical EventStore history surfaces are readers even when no helper is named "history". */
const EVENT_STORE_READER_RES = [
  { re: /\.\s*(TryEvent)\b/ },
  { re: /\.\s*(TryHeads)\b/ },
  { re: /\.\s*(AllHeads)\b/ },
  { re: /\b(?:EventStore\.)?Surface\s*\.\s*(read|heads)\b/ },
]

const PRIVATE_DURABLE_SUBSTRATE_RES = [
  { re: /\.(?:ndjson|jsonl)\b/i },
  { re: /\b(?:SQLite|Sqlite|sqlite3)\b/ },
  { re: /\.(?:sqlite|sqlite3|db)\b/i },
  {
    re: /\b(?:PrivateStore|PrivateDurableStore|PrivateEventStore|FeatureStore|FeatureEventStore|LocalFeatureStore|CustomStore|JsonlStore|JsonLineStore)\b/,
  },
  {
    re: /\b(?:System\.IO\.)?File\.(?:AppendAllText|AppendAllLines|WriteAllText|WriteAllLines|WriteAllBytes|OpenWrite|Create)\b/,
  },
  { re: /\b(?:FileStream|StreamWriter)\s*\(/ },
  { re: /\b(?:DbConnection|IDbConnection|SqlConnection|NpgsqlConnection|MySqlConnection)\b/ },
]

/**
 * feature-ref: any refs/wanxiang/ outside Persist/Git; non-canonical store refs always RED.
 * Persist/Git may also own remote-tracking store tips (`refs/wanxiang/remotes/<remote>/store`).
 * @param {string} text
 * @param {string} [file]
 * @returns {Violation[]}
 */
export const scanFeatureRef = (text, file = '<synthetic>') => {
  const rel = relToProduction(file)
  const owner = file === '<synthetic>' ? false : isStoreOwnerPath(rel)
  const lines = text.split('\n')
  const hits = []

  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i]
    const code = stripLineComment(raw)
    if (!FEATURE_REF_RE.test(code)) continue

    // Include `%` so F# sprintf format strings like remotes/%s/store extract fully.
    const refs = code.match(/refs\/wanxiang\/[A-Za-z0-9._/%-]*/g) || []
    for (const ref of refs) {
      if (ref === CANONICAL_STORE_REF || ref.startsWith(`${CANONICAL_STORE_REF}/`)) {
        if (owner) continue
        hits.push({
          id: 'feature-ref',
          file,
          line: i + 1,
          label: `canonical store ref '${CANONICAL_STORE_REF}' only allowed under Infrastructure/Persist|Git`,
          text: raw.trim(),
        })
      } else if (owner && REMOTE_TRACKING_STORE_REF_RE.test(ref)) {
        continue
      } else {
        hits.push({
          id: 'feature-ref',
          file,
          line: i + 1,
          label: `feature-owned ref '${ref}' forbidden; only '${CANONICAL_STORE_REF}' may exist`,
          text: raw.trim(),
        })
      }
    }
  }
  return hits
}

/**
 * schema-version-in-store-context: §36 durable event/store protocol versioning.
 * @param {string} text
 * @param {string} [file]
 * @returns {Violation[]}
 */
export const scanSchemaVersionInStoreContext = (text, file = '<synthetic>') => {
  const lines = text.split('\n')
  const hits = []
  const codeLines = lines.map(stripLineComment)

  for (let i = 0; i < codeLines.length; i++) {
    const code = codeLines[i]
    const raw = lines[i]

    for (const { re, token } of ALWAYS_STORE_VERSION_RES) {
      if (re.test(code)) {
        hits.push({
          id: 'schema-version-in-store-context',
          file,
          line: i + 1,
          label: `durable store protocol version token '${token}' is forbidden (§36)`,
          text: raw.trim(),
        })
      }
    }

    if (SCHEMA_VERSION_RE.test(code)) {
      const lo = Math.max(0, i - 8)
      const hi = Math.min(codeLines.length, i + 9)
      const window = codeLines.slice(lo, hi).join('\n')
      if (STORE_CONTEXT_RE.test(window) || STORE_CONTEXT_RE.test(code)) {
        hits.push({
          id: 'schema-version-in-store-context',
          file,
          line: i + 1,
          label:
            "schemaVersion in event/store context is forbidden; additive vocabulary only (§36)",
          text: raw.trim(),
        })
      }
    }
  }
  return hits
}

/**
 * git-bypass: direct git invocation outside Infrastructure/Git|Persist (plus documented allowlist).
 * @param {string} text
 * @param {string} [file]
 * @returns {Violation[]}
 */
export const scanGitBypass = (text, file = '<synthetic>') => {
  const rel = relToProduction(file)
  if (file !== '<synthetic>' && isGitBypassAllowed(rel)) return []

  const lines = text.split('\n')
  const codeLines = lines.map(stripLineComment)
  const hits = []
  const seenLines = new Set()

  const pushHit = (lineIdx, raw) => {
    if (seenLines.has(lineIdx)) return
    seenLines.add(lineIdx)
    hits.push({
      id: 'git-bypass',
      file,
      line: lineIdx + 1,
      label:
        'direct git process invocation outside Infrastructure/Git|Persist (must use GitGateway ownership)',
      text: raw.trim(),
    })
  }

  for (let i = 0; i < codeLines.length; i++) {
    for (const re of GIT_BYPASS_LINE_RES) {
      if (re.test(codeLines[i])) {
        pushHit(i, lines[i])
        break
      }
    }
  }

  const joined = codeLines.join('\n')
  for (const re of GIT_BYPASS_MULTILINE_RES) {
    const m = joined.match(re)
    if (!m || typeof m.index !== 'number') continue
    const lineIdx = joined.slice(0, m.index).split('\n').length - 1
    pushHit(lineIdx, lines[lineIdx] || m[0])
  }

  return hits
}

/**
 * dual-write: same production module writes EventStore AND Journal surface tokens.
 * Strategy A AgentJournal-only (EventStore-backed) is OK; Persist/EventStore-only OK.
 * NDJSON JournalWriter/Boot production APIs are deleted (Phase 5).
 * @param {string} text
 * @param {string} [file]
 * @returns {Violation[]}
 */
export const scanDualWrite = (text, file = '<synthetic>') => {
  if (file !== '<synthetic>' && isDualWriteAllowed(file)) return []

  const code = text
    .split('\n')
    .map(stripLineComment)
    .join('\n')

  const hasEventStoreWrite = EVENT_STORE_WRITE_RE.test(code)
  const hasJournalWrite = JOURNAL_NDJSON_WRITE_RE.test(code)
  if (!(hasEventStoreWrite && hasJournalWrite)) return []

  const lines = text.split('\n')
  const codeLines = lines.map(stripLineComment)
  let lineIdx = 0
  for (let i = 0; i < codeLines.length; i++) {
    if (EVENT_STORE_WRITE_RE.test(codeLines[i]) || JOURNAL_NDJSON_WRITE_RE.test(codeLines[i])) {
      lineIdx = i
      break
    }
  }

  return [
    {
      id: 'dual-write',
      file,
      line: lineIdx + 1,
      label:
        'dual-write bridge: same module writes EventStore and Journal surface (forbidden; AgentJournal-only OK, EventStore-only OK)',
      text: (lines[lineIdx] || '').trim(),
    },
  ]
}

/**
 * Feature code must not open/order durable history or build a second history
 * fold. Exact physical convergence/readers may observe streams; only the
 * CanonicalIntegrator may interpret them into business Current.
 * @param {string} text
 * @param {string} [file]
 * @returns {Violation[]}
 */
export const scanFeatureHistoryLoop = (text, file = '<synthetic>') => {
  if (isCanonicalIntegratorPath(file)) return []

  const observer = isPhysicalHistoryObserverPath(file)
  const readerOwner = isCanonicalEventReaderOwnerPath(file)
  const lines = text.split('\n')
  const codeLines = lines.map(stripLineComment)
  const hits = []

  const push = (lineIdx, token, label) => {
    hits.push({
      id: 'feature-history-loop',
      file,
      line: lineIdx + 1,
      token,
      label,
      text: lines[lineIdx].trim(),
    })
  }

  for (let i = 0; i < codeLines.length; i++) {
    const code = codeLines[i]
    for (const { re } of FEATURE_HISTORY_LOOP_RES) {
      const match = code.match(re)
      if (match) {
        const token = match[0]
        push(
          i,
          token,
          `feature history loop token '${token}' is forbidden; only CanonicalIntegrator derives business Current`,
        )
      }
    }

    if (!observer) {
      for (const { re } of HISTORY_OBSERVER_RES) {
        const match = code.match(re)
        if (match) {
          const token = match[0]
          push(
            i,
            token,
            `history reader/order token '${token}' is forbidden at this path; only exact physical observers or CanonicalIntegrator may use it`,
          )
        }
      }
    }

    if (!readerOwner) {
      for (const { re } of EVENT_STORE_READER_RES) {
        const match = code.match(re)
        if (match) {
          const token = match[1] || match[0]
          push(
            i,
            token,
            `EventStore reader token '${token}' is forbidden at this path; only exact canonical reader owners and physical verification may use it`,
          )
        }
      }
    }
  }

  // A diagnostic History field must not lend durable ownership to an unrelated
  // collection. Follow lexical bindings and their referenced local helpers.
  if (!observer) {
    for (const lineIdx of manualHistoryMergeLines(lines)) {
      push(lineIdx, 'manual merge',
        "feature history loop token 'manual merge' is forbidden; only CanonicalIntegrator derives business Current")
    }
  }

  return hits
}

const historyBindings = (lines) => {
  const bindings = []
  for (let start = 0; start < lines.length; start++) {
    const declaration = lines[start].match(/^(\s*)(let|and|(?:static\s+)?member|override|default|do|new|type|module|namespace|interface)\b/)
    if (!declaration) continue
    const [, indentation, kind] = declaration
    const indent = indentation.length
    const flatModule = /^(?:module|namespace)$/.test(kind) && !lines[start].includes('=')
    let end = start + 1
    for (; end < lines.length; end++) {
      if (!lines[end].trim()) continue
      if (flatModule && (!/^\s*(?:module|namespace)\b/.test(lines[end]) || lines[end].includes('='))) continue
      if (lines[end].match(/^\s*/)[0].length <= indent) break
    }
    const parent = bindings.findLast((binding) => binding.start < start && start < binding.end) ?? null
    const body = lines.slice(start, end).join('\n').slice(declaration[0].length)
    const separator = kind === 'do' ? -1 : body.indexOf('=')
    const header = separator < 0 ? '' : body.slice(0, separator)
    const named = /^(?:let|and)$/.test(kind)
      ? header.match(/^\s*(?:(?:rec|inline|private|internal|public|mutable)\s+)*([A-Za-z_][\w']*)/)
      : null
    const moduleName = /^(?:module|namespace)$/.test(kind)
      ? lines[start].slice(declaration[0].length).match(/^\s*(?:(?:rec|private|internal|public)\s+)*([A-Za-z_][\w']*)/)?.[1]
      : null
    const previous = bindings.findLast((binding) => binding.parent === parent)
    bindings.push({
      start, end, parent,
      name: named?.[1] ?? moduleName ?? null,
      module: Boolean(moduleName),
      visibleFrom: kind === 'and' ? previous?.visibleFrom ?? start : start,
      parameters: new Set(moduleName ? [] : (header.slice(named?.[0].length ?? 0).match(/[A-Za-z_][\w']*/g) ?? [])),
      expressionOffset: declaration[0].length + separator + 1,
      scopeOnly: /^(?:type|module|namespace|interface)$/.test(kind),
      history: false,
      collection: false,
      sortLines: new Set(),
      helpers: new Set(),
    })
  }
  return bindings
}

const referencedHistoryHelper = (bindings, owner, name, line) => {
  for (let scope = owner; ; scope = scope.parent) {
    const helper = bindings.findLast((binding) =>
      binding.name === name && binding.parent === scope && binding.visibleFrom <= line)
    if (helper) return helper
    if (!scope || scope.parameters.has(name)) return null
  }
}

const referencedQualifiedHistoryHelper = (bindings, owner, reference, line) => {
  const [name, ...members] = reference.split('.')
  let helper = referencedHistoryHelper(bindings, owner, name, line)
  for (const member of members) {
    if (!helper?.module) return null
    helper = bindings.findLast((binding) =>
      binding.name === member && binding.parent === helper && binding.visibleFrom <= line)
  }
  return helper?.scopeOnly ? null : helper
}

const historyLambdaShadows = (lines, bindings) => {
  const text = lines.join('\n')
  const offsets = [0]
  for (const line of lines) offsets.push(offsets.at(-1) + line.length + 1)
  const openings = []
  const pairs = []
  for (const token of text.matchAll(/[()[\]{}]/g)) {
    if ('([{'.includes(token[0])) openings.push(token.index)
    else if (openings.length) pairs.push({ start: openings.pop(), end: token.index })
  }
  const scopes = []
  for (const lambda of text.matchAll(/\bfun\s+([\s\S]*?)\s*->/g)) {
    const line = offsets.findLastIndex((offset) => offset <= lambda.index)
    const owner = bindings.findLast((binding) => binding.start <= line && line < binding.end)
    let end = offsets[owner?.end] ?? text.length
    for (const pair of pairs) {
      if (pair.start < lambda.index && lambda.index < pair.end) end = Math.min(end, pair.end)
    }
    scopes.push({
      start: lambda.index, end,
      parameters: new Set(lambda[1].match(/[A-Za-z_][\w']*/g) ?? []),
    })
  }
  return (name, line, column) => {
    const position = offsets[line] + column
    return scopes.some((scope) => scope.start <= position && position < scope.end && scope.parameters.has(name))
  }
}

const manualHistoryMergeLines = (sourceLines) => {
  // Mask literals and comments while retaining line/column coordinates.
  const lines = sourceLines.join('\n')
    .replace(/"""[\s\S]*?"""|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|\/\/[^\n]*|\(\*[\s\S]*?\*\)/g,
      (literal) => literal.replace(/[^\n]/g, ' '))
    .split('\n')
  const bindings = historyBindings(lines)
  const lambdaShadows = historyLambdaShadows(lines, bindings)
  const owners = []
  for (const binding of bindings) {
    for (let line = binding.start; line < binding.end; line++) owners[line] = binding
  }
  for (const binding of bindings) {
    if (binding.scopeOnly) continue
    let offset = 0
    for (let line = binding.start; line < binding.end; line++) {
      const code = lines[line]
      const expressionStart = Math.max(0, binding.expressionOffset - offset)
      const expression = code.slice(expressionStart)
      offset += code.length + 1
      if (owners[line] !== binding) continue
      binding.history ||= /\b(?:streams|history)\b/i.test(code)
      binding.collection ||= /\b(?:List|Seq|Array)\.(?:concat|collect)\b/.test(expression)
      if (/\b(?:List|Seq|Array)\.sortBy\b/.test(expression)) binding.sortLines.add(line)
      for (const match of expression.matchAll(/(?<![\w'.])\b([A-Za-z_][\w']*(?:\.[A-Za-z_][\w']*)*)\b/g)) {
        if (lambdaShadows(match[1].split('.')[0], line, expressionStart + match.index)) continue
        const helper = referencedQualifiedHistoryHelper(bindings, binding, match[1], line)
        if (helper) binding.helpers.add(helper)
      }
    }
  }

  // The finite binding graph includes recursive helper groups.
  let changed = true
  while (changed) {
    changed = false
    for (const binding of bindings) {
      for (const helper of binding.helpers) {
        const previousHistory = binding.history
        const previousCollection = binding.collection
        const previousSortCount = binding.sortLines.size
        binding.history ||= helper.history
        binding.collection ||= helper.collection
        for (const line of helper.sortLines) binding.sortLines.add(line)
        changed ||= previousHistory !== binding.history
          || previousCollection !== binding.collection
          || previousSortCount !== binding.sortLines.size
      }
    }
  }
  const hits = new Set()
  for (const binding of bindings) {
    if (binding.history && binding.collection) {
      for (const line of binding.sortLines) hits.add(line)
    }
  }
  return [...hits].sort((left, right) => left - right)
}

/**
 * Feature-local NDJSON/SQLite/private stores are a second durable substrate.
 * Only the exact canonical physical-store programs may own their representation.
 * @param {string} text
 * @param {string} [file]
 * @returns {Violation[]}
 */
export const scanPrivateDurableSubstrate = (text, file = '<synthetic>') => {
  if (isDurableSubstrateOwnerPath(file)) return []

  const lines = text.split('\n')
  const hits = []
  for (let i = 0; i < lines.length; i++) {
    const code = stripLineComment(lines[i])
    for (const { re } of PRIVATE_DURABLE_SUBSTRATE_RES) {
      const match = code.match(re)
      if (!match) continue
      const token = match[0]
      hits.push({
        id: 'private-durable-substrate',
        file,
        line: i + 1,
        token,
        label: `feature-local durable substrate token '${token}' is forbidden; use the canonical EventStore`,
        text: lines[i].trim(),
      })
      break
    }
  }
  return hits
}

/**
 * Positive shared-program shape: IntegrationRule is a one-envelope oracle and
 * both boot replay and live preparation invoke the same integrateOne program.
 * @param {{ file: string, text: string }[]} entries
 * @returns {Violation[]}
 */
export const scanCanonicalSharedProgram = (entries) => {
  const hits = []
  const findEntry = (expected) => entries.find((entry) => pathIs(entry.file, expected))
  const kernel = findEntry(INTEGRATION_KERNEL_PATH)
  const canonical = findEntry(CANONICAL_INTEGRATOR_PATH)

  const missing = (file, line, token, label, text = '') =>
    hits.push({ id: 'canonical-shared-program', file, line, token, label, text })

  if (!kernel) {
    missing(INTEGRATION_KERNEL_PATH, 1, 'IntegrationRule', 'canonical IntegrationRule definition is missing')
  } else {
    const lines = kernel.text.split('\n')
    const start = lines.findIndex((line) => /^\s*type\s+IntegrationRule\s*=/.test(stripLineComment(line)))
    const end = start < 0 ? -1 : lines.findIndex((line, i) => i > start && /^\s*type\s+/.test(stripLineComment(line)))
    const block = start < 0 ? '' : lines.slice(start, end < 0 ? lines.length : end).map(stripLineComment).join('\n')
    const hasOneEnvelopeOracle =
      /\bIntegrate\s*:\s*obj\s*->\s*EventEnvelope\s*->\s*Result</.test(block) &&
      !/\bEventEnvelope\s+(?:list|array|seq)\b|\b(?:History|Streams|Events)Reader\b/.test(block)
    if (!hasOneEnvelopeOracle) {
      missing(
        kernel.file,
        start < 0 ? 1 : start + 1,
        'IntegrationRule.Integrate(EventEnvelope)',
        'IntegrationRule must consume exactly one EventEnvelope',
        start < 0 ? '' : lines[start].trim(),
      )
    }
  }

  if (!canonical) {
    missing(CANONICAL_INTEGRATOR_PATH, 1, 'integrateOne', 'canonical shared integration program is missing')
    return hits
  }

  const lines = canonical.text.split('\n')
  const codeLines = lines.map(stripLineComment)
  const definition = codeLines.findIndex((line) => /^\s*let\s+private\s+integrateOne\b/.test(line))
  if (definition < 0) {
    missing(canonical.file, 1, 'integrateOne', 'canonical single-envelope integration program is missing')
  }

  const indentation = (line) => (line.match(/^\s*/) || [''])[0].length
  const regionFrom = (startRe, boundaryRe) => {
    const start = codeLines.findIndex((line) => startRe.test(line))
    if (start < 0) return { start, body: [] }
    const indent = indentation(codeLines[start])
    let end = codeLines.length
    for (let i = start + 1; i < codeLines.length; i++) {
      const line = codeLines[i]
      if (line.trim() && indentation(line) <= indent && boundaryRe.test(line)) {
        end = i
        break
      }
    }
    return { start, body: codeLines.slice(start, end) }
  }

  const hasDirectCall = (body, callee) => {
    const call = new RegExp(`\\b${callee}\\b(?=\\s+(?:[A-Za-z0-9_("']))`)
    return body.some((line) => {
      if (/^\s*let\s+_\s*=/.test(line)) return false
      if (new RegExp(`^\\s*let(?:\\s+private)?\\s+${callee}\\b`).test(line)) return false
      return call.test(line)
    })
  }

  const assertDirectCall = (region, callee, token, label) => {
    if (!hasDirectCall(region.body, callee)) {
      missing(
        canonical.file,
        region.start < 0 ? 1 : region.start + 1,
        token,
        label,
        region.start < 0 ? '' : lines[region.start].trim(),
      )
    }
  }

  const replay = regionFrom(/^\s*let\s+private\s+replay\b/, /^\s*let\b|^\s*\{\s*new\b/)
  const prepareLive = regionFrom(/^\s*let\s+prepareLive\b/, /^\s*let\b|^\s*\{\s*new\b/)
  const reloadLocal = regionFrom(
    /^\s*member\s+[^.]+\.ReloadLocal\b/,
    /^\s*member\b|^\s*\}\s*$/,
  )
  const prepareLiveMember = regionFrom(
    /^\s*member\s+[^.]+\.PrepareLive\b/,
    /^\s*member\b|^\s*\}\s*$/,
  )

  assertDirectCall(
    reloadLocal,
    'replay',
    'ReloadLocal->replay',
    'ReloadLocal must directly call the canonical replay program',
  )
  assertDirectCall(
    replay,
    'integrateOne',
    'replay->integrateOne',
    'replay must directly call the canonical integrateOne program',
  )
  assertDirectCall(
    prepareLive,
    'integrateOne',
    'PrepareLive->integrateOne',
    'PrepareLive path must directly call the canonical integrateOne program',
  )
  assertDirectCall(
    prepareLiveMember,
    'prepareLive',
    'PrepareLive->integrateOne',
    'PrepareLive must directly enter the helper that calls integrateOne',
  )
  return hits
}

/**
 * Run Phase 1–3 + clean-break scanners on one file body.
 * @param {string} text
 * @param {string} [file]
 * @returns {Violation[]}
 */
export const scanText = (text, file = '<synthetic>') => [
  ...scanFeatureRef(text, file),
  ...scanSchemaVersionInStoreContext(text, file),
  ...scanGitBypass(text, file),
  ...scanDualWrite(text, file),
  ...scanFeatureHistoryLoop(text, file),
  ...scanPrivateDurableSubstrate(text, file),
]

/** @param {{ file: string, text: string }[]} entries */
export const scanFiles = (entries) => {
  const violations = []
  for (const entry of entries) {
    for (const hit of scanText(entry.text, entry.file)) violations.push(hit)
  }
  return violations
}

export const collectProductionEntries = (root = PRODUCTION_ROOT) => {
  if (!existsSync(root)) {
    throw new Error(`unified-store-gate: required directory '${root}' does not exist`)
  }
  return walk(root, ['.fs']).map((file) => ({
    file: norm(relative('.', file) || file),
    text: readFileSync(file, 'utf8'),
  }))
}

export function check(context) {
  const root = context?.root ?? process.cwd()
  let production
  if (context?.productionFiles) {
    production = context.productionFiles().filter(({ file }) => file.endsWith('.fs')).map(({ file, text }) => ({ file, text }))
  } else {
    production = collectProductionEntries(resolve(root, PRODUCTION_ROOT))
  }
  const violations = [
    ...scanFiles(production),
    ...scanCanonicalSharedProgram(production),
  ]
  return {
    issues: violations.map((v) => {
      const token = v.token === undefined ? '' : ` token='${v.token}'`
      return {
        code: v.id,
        path: v.file,
        line: v.line,
        token: v.token,
        message: `${v.label}${token}: ${v.text.slice(0, 160)}`,
      }
    }),
    productionCount: production.length,
    violations,
  }
}

export function runCli() {
  const { issues, productionCount, violations } = check()
  if (issues.length === 0) {
    console.log(
      `unified-store-gate: OK — ${productionCount} production files, scanners=${SCANNER_IDS.join(',')}`,
    )
    return 0
  }
  console.error(`unified-store-gate: ${issues.length} violation(s)\n`)
  for (const v of violations) {
    const token = v.token === undefined ? '' : ` token='${v.token}'`
    console.error(`  [${v.id}] ${v.file}:${v.line}${token}  ${v.label}`)
    console.error(`    ${v.text.slice(0, 160)}`)
  }
  return 1
}

const isMain =
  process.argv[1] !== undefined &&
  resolve(fileURLToPath(import.meta.url)) === resolve(process.argv[1])

if (isMain) {
  const code = runCli()
  if (code !== 0) process.exit(code)
}
