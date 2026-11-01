#!/usr/bin/env node
// Focused architecture checks — static gates run by scripts/check.mjs; violations fail closed (verification-system-005).
// Usage: node scripts/checks/architecture.mjs

import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'
import { walk } from '../lib/walk.mjs'
import { scanAdapterThinness } from './adapter-thinness.mjs'

const PRODUCTION_ROOT = 'src/Wanxiangshu'
const SRC_ROOT = 'src'
const OWNER_FSPROJ = /^src\/Wanxiangshu\/Wanxiangshu\.(?:Owner|Shard)\..+\.fsproj$/
const PURE_DIRS = [`${PRODUCTION_ROOT}/Foundation/`]
const RESOURCE_DIR = `${PRODUCTION_ROOT}/Resources/`
const UPPER_NAMESPACES = ['Wanxiangshu.OpenCode', 'Wanxiangshu.Session', 'Wanxiangshu.Process']
const PACKAGE_RESOURCE_READ = /PackageResources\./
const HOST_ADAPTER_DIRS = ['Codec', 'Host', 'Signals', 'Plugin']
  .map((dir) => `${PRODUCTION_ROOT}/OpenCode/${dir}/`)
  .concat([`${PRODUCTION_ROOT}/Host/`])
const HOST_SDK_IMPORT = /@opencode-ai\//
const isKernelFile = (file) =>
  !HOST_ADAPTER_DIRS.some((dir) => file.startsWith(dir)) &&
  !file.startsWith(`${PRODUCTION_ROOT}/OpenCode/`) &&
  !file.startsWith(`${PRODUCTION_ROOT}/Composition/`) &&
  !file.includes('/OpenCode/') &&
  !file.endsWith('Surface.fs') &&
  !file.endsWith('Surface.fsi')
const referencesOpenCode = (text) =>
  text.split('\n').some((line) => !/^\s*\/\//.test(line) && /\bWanxiangshu\.OpenCode\b/.test(line))
// ⑨ (CUT-40): a domain-local adapter namespace is `Wanxiangshu.<Domain…>.OpenCode` (the code under a
// `<Domain>/…/OpenCode/` folder). Gate ⑧ only counts the top-level `Wanxiangshu.OpenCode`.
const DOMAIN_ADAPTER_REF = /\bWanxiangshu\.(?!OpenCode\b)[A-Z]\w*(?:\.[A-Z]\w*)*\.OpenCode\b/
const isAdapterArea = (file) =>
  HOST_ADAPTER_DIRS.some((dir) => file.startsWith(dir)) ||
  file.startsWith(`${PRODUCTION_ROOT}/OpenCode/`) ||
  file.includes('/OpenCode/')
const referencesDomainAdapter = (text) =>
  text.split('\n').some((line) => !/^\s*\/\//.test(line) && DOMAIN_ADAPTER_REF.test(line))
const HOST_SDK_CLIENT = /\?client\b|\bclient\s*(?:\?|\.(?:session|event|app|config|tui|find|file|project|path)\b)/

const norm = (path) => path.replace(/\\/g, '/')
const isFs = (path) => path.endsWith('.fs')
const isFsi = (path) => path.endsWith('.fsi')

export function scanArchitecture(productionFiles, read, options) {
  const violations = []
  const fail = (gate, message) => violations.push({ gate, message })

  const productionFs = productionFiles.filter(isFs).map(norm)
  // Gates ⑧/⑨ (CUT-40 F40-03) also read signature files: an `open` in a .fsi is a dependency too.
  const productionSources = productionFs.concat(productionFiles.filter(isFsi).map(norm))

  // ① sole F# source root under src/
  {
    if (!existsSync(SRC_ROOT) || !statSync(SRC_ROOT).isDirectory()) {
      fail('source-root', `${SRC_ROOT}/ missing`)
    } else {
      for (const entry of readdirSync(SRC_ROOT, { withFileTypes: true })) {
        const full = join(SRC_ROOT, entry.name)
        if (entry.isDirectory()) {
          if (entry.name !== 'Wanxiangshu') {
            const stray = walk(full, ['.fs', '.fsproj'])
            if (stray.length > 0) {
              fail('source-root', `${full}/ contains F# sources; only ${PRODUCTION_ROOT}/ is allowed`)
            }
          }
        } else if (entry.name.endsWith('.fs') || entry.name.endsWith('.fsproj')) {
          fail('source-root', `${full}: F# source outside ${PRODUCTION_ROOT}/`)
        }
      }
    }
    const outside = walk(SRC_ROOT, ['.fs', '.fsproj']).filter((file) => {
      const rel = norm(relative('.', file))
      return rel !== PRODUCTION_ROOT && !rel.startsWith(`${PRODUCTION_ROOT}/`)
    })
    for (const file of outside) fail('source-root', `${norm(file)}: F# source outside ${PRODUCTION_ROOT}/`)
  }

  // ② each .fs belongs to exactly one owner-locality project; flattened Fable emit mirrors the same set.
  {
    // W5 cutover: the wrapper aggregate is deleted and the compile-order
    // manifest carries the canonical list — this gate only needs to assert
    // every on-disk .fs has exactly one owning shard.
    const ownerProjects = productionFiles.filter((file) => OWNER_FSPROJ.test(norm(file)))
    const declared = ownerProjects.flatMap((project) => {
      const text = read(project)
      return [...text.matchAll(/<Compile\s+Include="([^"]+\.fs)"\s*\/?\s*>/g)].map((m) =>
        norm(`${PRODUCTION_ROOT}/${m[1]}`),
      )
    })
    const counts = new Map()
    for (const path of declared) counts.set(path, (counts.get(path) ?? 0) + 1)
    const onDisk = new Set(productionFs)
    for (const [path, n] of counts) {
      if (n > 1) fail('fsproj-drift', `${path}: compiled by ${n} owner-locality projects`)
      if (!onDisk.has(path)) fail('fsproj-drift', `owner-locality project declares '${path}' which does not exist`)
    }
    for (const path of onDisk) {
      if (!counts.has(path)) fail('fsproj-drift', `${path}: on disk but not compiled by an owner-locality project`)
    }
  }

  // ⑤ Kernel/Domain must not reference upper infrastructure namespaces
  for (const file of productionFs) {
    if (!PURE_DIRS.some((dir) => file.startsWith(dir))) continue
    const text = read(file)
    for (const upper of UPPER_NAMESPACES) {
      if (text.includes(upper)) fail('dependency-direction', `${file}: pure core references '${upper}'`)
    }
  }

  // ⑤ Kernel/Domain must not use Fable.Core.JsInterop
  for (const file of productionFs) {
    if (!PURE_DIRS.some((dir) => file.startsWith(dir))) continue
    if (read(file).includes('Fable.Core.JsInterop')) {
      fail('host-boundary', `${file}: pure core must not use Fable.Core.JsInterop`)
    }
  }

  // ⑥ package resource reads only under Infrastructure/Resources/
  for (const file of productionFs) {
    if (file.startsWith(RESOURCE_DIR)) continue
    if (PACKAGE_RESOURCE_READ.test(read(file))) {
      fail('resource-boundary', `${file}: package resource I/O must live under ${RESOURCE_DIR}`)
    }
  }

  // ⑦ Host SDK is consumed only inside the explicit adapter area (CUT-34, D-07):
  // `@opencode-ai/*` imports and the SDK `client` handle stay in OpenCode/{Host,Codec,Signals,Plugin}/ or Host/.
  for (const file of productionFs) {
    if (HOST_ADAPTER_DIRS.some((dir) => file.startsWith(dir))) continue
    const text = read(file)
    if (HOST_SDK_IMPORT.test(text)) {
      fail('host-sdk-boundary', `${file}: imports the OpenCode SDK outside the adapter area`)
    }
    if (HOST_SDK_CLIENT.test(text)) {
      fail('host-sdk-boundary', `${file}: touches the OpenCode SDK client outside the adapter area`)
    }
  }

  // ⑧ Kernel-to-adapter imports: outside the adapter area, the composition root (Composition/),
  // domain-local OpenCode/ adapter folders and plain-JS *Surface.fs boundaries, a production file
  // must not reference `Wanxiangshu.OpenCode`. The recorded baseline reached zero; it is gone, so
  // the rule is absolute again.
  for (const file of productionSources) {
    if (!isKernelFile(file)) continue
    if (!referencesOpenCode(read(file))) continue
    fail('adapter-import', `${file}: kernel code references Wanxiangshu.OpenCode (adapter layer)`)
  }

  // ⑨ Domain-local adapter imports: outside the adapter area (top-level OpenCode/, Host/, any
  // `…/OpenCode/` folder), no production file — kernel, Composition/ or *Surface.fs alike — may open
  // or qualify an in-domain `Wanxiangshu.<Domain>.OpenCode` namespace. The recorded baseline reached
  // zero; it is gone, so the rule is absolute again.
  for (const file of productionSources) {
    if (isAdapterArea(file)) continue
    if (!referencesDomainAdapter(read(file))) continue
    fail('domain-adapter-import', `${file}: references an in-domain *.OpenCode adapter namespace`)
  }

  // ⑩ adapter-thinness ratchet, ⑪ port-purity and ⑫ port-family integrity (HNI-04, HNI-11):
  // see scripts/checks/adapter-thinness.mjs.
  for (const v of scanAdapterThinness(productionFs, read, { root: options?.root }).violations) violations.push(v)

  // ⑨b A namespace must not hide its folder (CUT-40 F40-02): ⑨ recognises adapter code by its
  // `….OpenCode` namespace, so every source under an `OpenCode/` folder declares a namespace with an
  // `OpenCode` segment, and no source outside one does.
  for (const file of productionSources) {
    const decl = /^(?:namespace|module)\s+(?:rec\s+)?([\w.]+)/m.exec(read(file))
    if (!decl) continue
    const inFolder = file.startsWith(`${PRODUCTION_ROOT}/OpenCode/`) || file.includes('/OpenCode/')
    const inNamespace = decl[1].split('.').includes('OpenCode')
    if (inFolder && !inNamespace) {
      fail('adapter-namespace-folder', `${file}: lives under an OpenCode/ adapter folder but declares '${decl[1]}'`)
    } else if (!inFolder && inNamespace) {
      fail('adapter-namespace-folder', `${file}: declares adapter namespace '${decl[1]}' outside an OpenCode/ folder`)
    }
  }

  return { violations, productionFsCount: productionFs.length }
}

export function check(context) {
  const root = context?.root ?? process.cwd()
  const read = (path) => {
    if (context && typeof context.readText === 'function') return context.readText(path)
    if (!sources.has(path)) sources.set(path, readFileSync(path, 'utf8'))
    return sources.get(path)
  }
  const sources = new Map()

  if (!existsSync(join(root, PRODUCTION_ROOT))) {
    return {
      issues: [
        {
          code: 'architecture-root-missing',
          message: `architecture: required directory '${PRODUCTION_ROOT}' does not exist`,
        },
      ],
    }
  }

  const productionFiles = context?.sourceFiles
    ? context.sourceFiles()
    : walk(join(root, PRODUCTION_ROOT), ['.fs', '.fsi', '.fsproj']).map((p) => relative(root, p))
  const { violations } = scanArchitecture(productionFiles, read, { root })

  const issues = violations.map(({ gate, message }) => ({
    code: gate,
    message,
  }))

  return { issues }
}

function runCli() {
  const result = check({ root: process.cwd() })
  if (result.issues.length === 0) {
    console.log('architecture: OK')
    process.exit(0)
  }

  const byGate = new Map()
  for (const { code, message } of result.issues) {
    if (!byGate.has(code)) byGate.set(code, [])
    byGate.get(code).push(message)
  }

  console.error(`architecture: ${result.issues.length} violation(s)\n`)
  for (const [gate, messages] of byGate) {
    console.error(`${gate} (${messages.length})`)
    for (const message of messages) console.error(`  ${message}`)
    console.error('')
  }
  process.exit(1)
}

const isEntry = process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]
if (isEntry) {
  runCli()
}
