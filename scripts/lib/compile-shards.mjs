import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { dirname, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

const MODULE_DIR = dirname(fileURLToPath(import.meta.url))
export const REPOSITORY_ROOT = resolve(MODULE_DIR, '../..')
export const SOURCE_ROOT = resolve(REPOSITORY_ROOT, 'src/Wanxiangshu')
// WP5: the aggregate fsproj is deleted; nothing reads it. The constant
// path lives no longer — callers that still need an exclusion handle
// pass `aggregatePath: null` and production sources come solely from the
// shard graph.
export const AGGREGATE_PROJECT = null

const SHARD_PROJECT = /^Wanxiangshu\.(?:Owner|Shard)\..+\.fsproj$/

const norm = (value) => value.replace(/\\/g, '/')
const repoPath = (root, value) => norm(relative(root, value))
const property = (text, name) => text.match(new RegExp(`<${name}>([^<]+)</${name}>`))?.[1]?.trim() ?? ''

function includes(text, tag) {
  return [...text.matchAll(new RegExp(`<${tag}\\s+Include="([^"]+)"\\s*\\/?\\s*>`, 'g'))]
    .map((match) => match[1])
}

export function parseCompileShardProject(projectPath, { repositoryRoot = REPOSITORY_ROOT } = {}) {
  const text = readFileSync(projectPath, 'utf8')
  const projectDirectory = dirname(projectPath)
  const compileFiles = includes(text, 'Compile').map((entry) => resolve(projectDirectory, entry))
  const references = includes(text, 'ProjectReference').map((entry) => resolve(projectDirectory, entry))
  return {
    projectPath,
    projectRepoPath: repoPath(repositoryRoot, projectPath),
    text,
    explicitSubsystem: property(text, 'WanxiangshuSubsystem'),
    explicitCompileShard: property(text, 'WanxiangshuCompileShard'),
    legacyOwner: property(text, 'WanxiangshuSemanticOwner'),
    legacyLocality: property(text, 'WanxiangshuOwnerLocality'),
    legacyKind: property(text, 'WanxiangshuOwnerLocalityKind'),
    // Declared order — canonical within-shard compile order (fsi before fs).
    compileItems: compileFiles,
    implementationFiles: compileFiles.filter((entry) => entry.endsWith('.fs')),
    signatureFiles: compileFiles.filter((entry) => entry.endsWith('.fsi')),
    references,
  }
}

export function productionSources(root) {
  const result = []
  const visit = (directory) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const fullPath = resolve(directory, entry.name)
      if (entry.isDirectory()) {
        if (entry.name.startsWith('.') || entry.name === 'obj' || entry.name === 'bin') continue
        visit(fullPath)
      }
      else if (entry.isFile() && entry.name.endsWith('.fs') && !entry.name.endsWith('.fsi')) result.push(fullPath)
    }
  }
  visit(root)
  return result.sort()
}

function applicationSources(repositoryRoot, sourceRoot) {
  const adapterRoot = resolve(repositoryRoot, 'wanxiangtong')
  return [...productionSources(sourceRoot), ...(existsSync(adapterRoot) ? productionSources(adapterRoot) : [])]
}

function parseAggregate(aggregatePath) {
  if (!aggregatePath || !existsSync(aggregatePath)) {
    // WP2: the aggregate file is optional — the shard graph alone must own
    // production sources. Return an empty sentinel; callers assert coverage
    // by comparing shard union against the filesystem, not this file.
    return {
      text: '',
      implementationFiles: [],
      signatureFiles: [],
      references: [],
      missing: true,
    }
  }
  const text = readFileSync(aggregatePath, 'utf8')
  const directory = dirname(aggregatePath)
  return {
    text,
    implementationFiles: includes(text, 'Compile').filter((entry) => entry.endsWith('.fs')).map((entry) => resolve(directory, entry)),
    signatureFiles: includes(text, 'Compile').filter((entry) => entry.endsWith('.fsi')).map((entry) => resolve(directory, entry)),
    references: includes(text, 'ProjectReference').map((entry) => resolve(directory, entry)),
    missing: false,
  }
}

function sameSet(left, right) {
  if (left.size !== right.size) return false
  for (const value of left) if (!right.has(value)) return false
  return true
}

/**
 * Deterministic topological order over the compile-shard DAG.
 *
 * Cross-shard order must not depend on the iteration order MSBuild or the
 * filesystem happened to offer: ties break on the shard's lexical
 * projectRepoPath so every run emits the same canonical sequence. Within a
 * shard the fsproj's declared `<Compile>` order is preserved verbatim.
 */
function topologicalOrder(projects) {
  const ordered = []
  const visited = new Set()
  const pending = [...projects.keys()].sort((a, b) =>
    projects.get(a).projectRepoPath.localeCompare(projects.get(b).projectRepoPath))
  const visit = (projectPath, stack = []) => {
    if (visited.has(projectPath)) return
    if (stack.includes(projectPath)) {
      throw new Error(`compile-shard graph contains a cycle at ${projectPath}`)
    }
    stack.push(projectPath)
    const sortedRefs = [...projects.get(projectPath).references].sort((a, b) =>
      projects.get(a).projectRepoPath.localeCompare(projects.get(b).projectRepoPath))
    for (const reference of sortedRefs) visit(reference, stack)
    stack.pop()
    visited.add(projectPath)
    ordered.push(projectPath)
  }
  for (const projectPath of pending) visit(projectPath)
  return ordered
}

/**
 * Canonical production compile-item sequence derived entirely from the
 * shard graph: topological over the cross-shard DAG, declared order inside
  * each shard. Priority: an explicit order file `compile-order.txt` next to
  * the shard graph (one source entry per line, repo-relative under
  * `src/Wanxiangshu`) wins — F#'s sensitivity to file order justifies a
  * checked-manifest; when that file is absent the topological walk over
  * declared order still yields a deterministic result.
 */
export function canonicalShardCompileItems(projects, { orderFilePath = null } = {}) {
  if (orderFilePath && existsSync(orderFilePath)) {
    const lines = readFileSync(orderFilePath, 'utf8')
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter((line) => line.length > 0 && !line.startsWith('#'))
    const rootDir = dirname(orderFilePath)
  const seen = new Set()
    const items = []
    const declared = new Set()
    for (const project of projects.values()) {
      for (const source of project.compileItems) declared.add(source)
    }
    for (const line of lines) {
      const abs = resolve(rootDir, line.replace(/\//g, sep))
      if (!declared.has(abs)) {
        throw new Error(`compile-order.txt mentions '${line}' which no shard compiles`)
      }
      if (!seen.has(abs)) {
        seen.add(abs)
        items.push(abs)
      }
    }
    // A compile-order.txt that omits a shard's source is a drift bug — surface
    // it instead of silently treating the order file as authoritative.
    for (const project of projects.values()) {
      for (const source of project.compileItems) {
      if (!seen.has(source)) {
          throw new Error(`compile-order.txt missing source '${relative(rootDir, source)}'`)
        }
      }
    }
    return items
  }

  const seen = new Set()
  const ordered = []
  for (const projectPath of topologicalOrder(projects)) {
    for (const source of projects.get(projectPath).compileItems) {
      if (!seen.has(source)) {
        seen.add(source)
        ordered.push(source)
      }
    }
  }
  return ordered
}

export function assertProductionSourcesAssigned({
  repositoryRoot = REPOSITORY_ROOT,
  sourceRoot = resolve(repositoryRoot, 'src/Wanxiangshu'),
  discoveredSources = new Set(applicationSources(repositoryRoot, sourceRoot)),
  shardImplementations = new Set(),
  aggregate = { missing: true, text: '', implementationFiles: [], signatureFiles: [], references: [] },
} = {}) {
  const normShard = new Set([...shardImplementations].map(norm))
  const normDiscovered = new Set([...discoveredSources].map(norm))
  if (!sameSet(normShard, normDiscovered)) {
    const unassigned = [...normDiscovered].filter((source) => !normShard.has(source)).map((source) => repoPath(repositoryRoot, source))
    const stale = [...normShard].filter((source) => !normDiscovered.has(source)).map((source) => repoPath(repositoryRoot, source))
    throw new Error(`production source coverage mismatch unassigned=[${unassigned.slice(0, 12).join(', ')}] stale=[${stale.slice(0, 12).join(', ')}]`)
  }
  const srcDir = resolve(repositoryRoot, 'src')
  if (existsSync(srcDir)) {
    for (const entry of readdirSync(srcDir, { withFileTypes: true })) {
      const fullPath = resolve(srcDir, entry.name)
      if (entry.isDirectory() && entry.name !== 'Wanxiangshu') {
        const stray = productionSources(fullPath)
        if (stray.length > 0) throw new Error(`${repoPath(repositoryRoot, fullPath)}: F# sources outside ${repoPath(repositoryRoot, sourceRoot)}`)
      } else if (entry.isFile() && (entry.name.endsWith('.fs') || entry.name.endsWith('.fsproj'))) {
        throw new Error(`${repoPath(repositoryRoot, fullPath)}: F# source outside ${repoPath(repositoryRoot, sourceRoot)}`)
      }
    }
  }
  if (!aggregate.missing) {
    if (aggregate.text.includes('<WanxiangshuEmitProject>') && !/<WanxiangshuEmitProject>true<\/WanxiangshuEmitProject>/.test(aggregate.text)) {
      throw new Error(aggregate.path ? `${repoPath(repositoryRoot, aggregate.path)}: flattened marker missing` : 'flattened marker missing (synthesised)')
    }
    if (new Set(aggregate.implementationFiles).size !== aggregate.implementationFiles.length) {
      throw new Error(aggregate.path ? `${repoPath(repositoryRoot, aggregate.path)}: duplicate production Compile implementation entry` : 'duplicate production Compile implementation entry (synthesised)')
    }
  }
}

export function readCompileShardInventory({
  repositoryRoot = REPOSITORY_ROOT,
  sourceRoot = resolve(repositoryRoot, 'src/Wanxiangshu'),
  aggregatePath = null,
} = {}) {
  // The aggregate file is advisory-only: when present it must still equal
  // the shard union, but its absence must not block the production pipeline.
  const projectPaths = readdirSync(sourceRoot)
    .filter((name) => SHARD_PROJECT.test(name))
    .map((name) => resolve(sourceRoot, name))
    .sort()
  const projects = new Map(projectPaths.map((projectPath) => [projectPath, parseCompileShardProject(projectPath, { repositoryRoot })]))
  if (projects.size === 0) throw new Error('no compile-shard projects found')

  const sourceProject = new Map()
  for (const project of projects.values()) {
    if (project.implementationFiles.length === 0) throw new Error(`${project.projectRepoPath}: compile shard has no production .fs`)
    if (new Set(project.implementationFiles).size !== project.implementationFiles.length) throw new Error(`${project.projectRepoPath}: duplicate .fs entry`)
    if (new Set(project.signatureFiles).size !== project.signatureFiles.length) throw new Error(`${project.projectRepoPath}: duplicate .fsi entry`)
    if (new Set(project.references).size !== project.references.length) throw new Error(`${project.projectRepoPath}: duplicate ProjectReference`)

    const signatures = new Set(project.signatureFiles)
    for (const implementationPath of project.implementationFiles) {
      if (!existsSync(implementationPath)) throw new Error(`${repoPath(repositoryRoot, implementationPath)}: source is missing`)
      const signaturePath = implementationPath.replace(/\.fs$/, '.fsi')
      if (!signatures.has(signaturePath) || !existsSync(signaturePath)) {
        throw new Error(`${repoPath(repositoryRoot, implementationPath)}: compile shard must carry its sibling .fsi`)
      }
      const previous = sourceProject.get(implementationPath)
      if (previous) throw new Error(`${repoPath(repositoryRoot, implementationPath)}: compiled by both ${previous.projectRepoPath} and ${project.projectRepoPath}`)
      sourceProject.set(implementationPath, project)
    }
    for (const signaturePath of project.signatureFiles) {
      const implementationPath = signaturePath.slice(0, -1)
      if (!project.implementationFiles.includes(implementationPath)) {
        throw new Error(`${repoPath(repositoryRoot, signaturePath)}: signature has no sibling implementation in the same compile shard`)
      }
    }
    for (const reference of project.references) {
      if (!projects.has(reference)) throw new Error(`${project.projectRepoPath}: ProjectReference is not a compile shard: ${repoPath(repositoryRoot, reference)}`)
      if (reference === project.projectPath) throw new Error(`${project.projectRepoPath}: compile shard references itself`)
    }
  }

  // Cycle detection via the same canonical-order walk — a cycle throws
  // inside topologicalOrder before the derived sequence is committed.
  topologicalOrder(projects)

  const aggregate = aggregatePath ? parseAggregate(aggregatePath) : { missing: true, text: '', implementationFiles: [], signatureFiles: [], references: [] }
  if (aggregate.references.length !== 0) throw new Error(`${repoPath(repositoryRoot, aggregate.path ?? '?')}: aggregate emitter must not contain ProjectReference`)
  const shardImplementations = new Set([...sourceProject.keys()])
  const shardSignatures = new Set([...projects.values()].flatMap((project) => project.signatureFiles))
  if (!aggregate.missing) {
    if (!sameSet(shardImplementations, new Set(aggregate.implementationFiles))) throw new Error('aggregate .fs compile set differs from compile-shard union')
    if (!sameSet(shardSignatures, new Set(aggregate.signatureFiles))) throw new Error('aggregate .fsi compile set differs from compile-shard union')
  }
  const discoveredSources = new Set(applicationSources(repositoryRoot, sourceRoot))
  assertProductionSourcesAssigned({
    repositoryRoot,
    sourceRoot,
    discoveredSources,
    shardImplementations,
    aggregate,
  })


  return {
    repositoryRoot,
    sourceRoot,
    aggregatePath,
    aggregateMissing: aggregate.missing,
    canonicalCompileItems: canonicalShardCompileItems(projects, {
      orderFilePath: resolve(sourceRoot, 'compile-order.txt'),
    }),
    projects,
    sourceProject,
    sourceCount: sourceProject.size,
    projectReferenceCount: [...projects.values()].reduce((sum, project) => sum + project.references.length, 0),
  }
}

export function stronglyConnectedComponents(nodes, edges) {
  const adjacency = new Map([...nodes].map((node) => [node, []]))
  for (const [from, to] of edges) adjacency.get(from)?.push(to)
  let index = 0
  const indices = new Map()
  const lowlink = new Map()
  const stack = []
  const onStack = new Set()
  const components = []
  const visit = (node) => {
    indices.set(node, index)
    lowlink.set(node, index++)
    stack.push(node)
    onStack.add(node)
    for (const next of adjacency.get(node) ?? []) {
      if (!indices.has(next)) {
        visit(next)
        lowlink.set(node, Math.min(lowlink.get(node), lowlink.get(next)))
      } else if (onStack.has(next)) lowlink.set(node, Math.min(lowlink.get(node), indices.get(next)))
    }
    if (lowlink.get(node) !== indices.get(node)) return
    const component = []
    while (stack.length > 0) {
      const current = stack.pop()
      onStack.delete(current)
      component.push(current)
      if (current === node) break
    }
    components.push(component.sort())
  }
  for (const node of nodes) if (!indices.has(node)) visit(node)
  return components.sort((left, right) => right.length - left.length || left[0].localeCompare(right[0]))
}
