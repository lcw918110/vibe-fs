#!/usr/bin/env node
// WHAT[js-semantic-surface-003] surface manifest gate.
//
// Registration grants no authority by itself. Every registered module must be
// owned by a current requirement, governed by current WHAT laws,
// implemented by a compiled source file, and imported by a real
// executable contract test.

import { existsSync, readFileSync } from 'node:fs'
import { dirname, isAbsolute, join, relative, resolve } from 'node:path'
import { pathToFileURL } from 'node:url'

import { SURFACE_MANIFEST } from '../lib/test-surface-scan.mjs'
import { parseModule, walkSyntax } from '../lib/js-syntax.mjs'
import { walk } from '../lib/walk.mjs'
import { ruleClauseHeadings, ruleDocumentName } from '../lib/spec-rules.mjs'

const read = (root, path) => readFileSync(join(root, path), 'utf8')
export const whatIds = (text, owner) => [...new Set(ruleClauseHeadings(text, owner).map(({ id }) => id))]

const sourceCompileStem = (source) => {
  const prefix = 'src/Wanxiangshu/'
  if (!source.startsWith(prefix) || !source.endsWith('.fs')) return undefined
  return source.slice(prefix.length, -'.fs'.length)
}

export const validateSurfaceManifest = (manifest = SURFACE_MANIFEST, root = process.cwd()) => {
  const failures = []
  const fail = (message) => failures.push(message)
  const requirements = join(root, 'requirements')
  // W5 cutover: the wrapper aggregate is deleted. Production sources are
  // enumerated by walking shard .fsproj declarations — that single set is
  // what a test source label must name before the code below accepts it.
  const productionSources = new Set()
  const shardRoot = join(root, 'src/Wanxiangshu')
  for (const file of walk(shardRoot, ['.fsproj'])) {
    const text = readFileSync(file, 'utf8')
    for (const match of text.matchAll(/<Compile\s+Include="([^"]+\.fs)"\s*\/?\s*>/g)) {
      productionSources.add(match[1].replace(/\\/g, '/'))
    }
  }
  const testFiles = walk(requirements, ['.test.mjs'])
  const testSources = testFiles.map((file) => {
    const source = readFileSync(file, 'utf8')
    const syntax = parseModule(source, file)
    return { file, source, syntax }
  })
  const seenModules = new Set()

  if (!Array.isArray(manifest)) {
    return ['surface manifest must be an array']
  }

  // Pre-collect all dist imports across all test files
  const importedDistPaths = new Set()
  for (const { file, syntax } of testSources) {
    if (!syntax) continue
    walkSyntax(syntax, (node) => {
      if (
        (node.type === 'ImportDeclaration' || node.type === 'ImportExpression') &&
        typeof node.source?.value === 'string'
      ) {
        const specifier = node.source.value
        if (!specifier.startsWith('.') && !isAbsolute(specifier)) return
        const target = resolve(dirname(file), specifier)
        const emitted = relative(resolve(root, 'dist'), target).replace(/\\/g, '/')
        if (emitted !== '..' && !emitted.startsWith('../') && !isAbsolute(emitted)) {
          importedDistPaths.add(emitted)
        }
      }
    })
  }

  for (const entry of manifest) {
    if (entry === null || typeof entry !== 'object' || Array.isArray(entry)) {
      fail('manifest entry must be an object')
      continue
    }
    const label = typeof entry.module === 'string' ? entry.module : '<missing module>'
    if (seenModules.has(label)) fail(`${label}: duplicate manifest module`)
    seenModules.add(label)

    if (!/^[A-Za-z0-9_-]+(?:\/[A-Za-z0-9_-]+)*\.js$/.test(entry.module ?? '')) {
      fail(`${label}: module must be a relative emitted .js path`)
    }
    if (!/^[a-z0-9-]+$/.test(entry.owner ?? '')) fail(`${label}: owner must be a package slug`)
    if (!Array.isArray(entry.laws) || entry.laws.length === 0 || entry.laws.some((law) => typeof law !== 'string')) {
      fail(`${label}: laws must be a non-empty list of law ids`)
    }
    if (!['json', 'opaque-capability'].includes(entry.representation)) {
      fail(`${label}: invalid representation ${entry.representation}`)
    }
    if (!['pure', 'resource'].includes(entry.kind)) fail(`${label}: invalid kind ${entry.kind}`)

    const ownerRulePath = `requirements/${entry.owner}/${ruleDocumentName(entry.owner)}`
    if (!existsSync(join(root, ownerRulePath))) {
      fail(`${label}: missing owner rule document (${ownerRulePath})`)
      continue
    }

    const laws = Array.isArray(entry.laws) ? entry.laws : []
    const lawOwners = entry.lawOwners && typeof entry.lawOwners === 'object' ? entry.lawOwners : {}
    for (const law of laws) {
      const lawOwner = typeof lawOwners[law] === 'string' ? lawOwners[law] : entry.owner
      const lawRulePath = `requirements/${lawOwner}/${ruleDocumentName(lawOwner)}`
      if (!existsSync(join(root, lawRulePath))) {
        fail(`${label}: law ${law} owner rule document is missing (${lawRulePath})`)
        continue
      }
      const lawIds = new Set(whatIds(read(root, lawRulePath), lawOwner))
      const numMatch = /-(\d{3}[a-z]?)$/.exec(law)
      const lawNum = numMatch ? numMatch[1] : null
      if (!lawIds.has(law) && !(lawNum && lawIds.has(lawNum))) fail(`${label}: law ${law} is absent from ${lawRulePath}`)
    }

    if (typeof entry.source !== 'string' || !existsSync(join(root, entry.source))) {
      fail(`${label}: missing production source ${entry.source}`)
    }

    if (typeof entry.module === 'string' && !existsSync(join(root, 'dist', entry.module))) {
      fail(`${label}: missing emitted surface dist/${entry.module}`)
    }
    const compileStem = sourceCompileStem(entry.source ?? '')
    if (!compileStem) {
      fail(`${label}: source must be a src/Wanxiangshu .fs path`)
    } else {
      // compileStem is already repo-root-relative under src/Wanxiangshu/.
      const rel = `${compileStem}.fs`.replace(/\\/g, '/')
      if (!productionSources.has(rel)) {
        fail(`${label}: ${compileStem}.fs is not compiled by the canonical source inventory`)
      }
    }

    if (typeof entry.module === 'string' && !importedDistPaths.has(entry.module)) {
      fail(`${label}: no .test.mjs imports the registered surface`)
    }
  }
  return failures
}

export const run = ({ root = process.cwd(), manifest = SURFACE_MANIFEST } = {}) => {
  const failures = validateSurfaceManifest(manifest, root)
  if (failures.length > 0) {
    console.error(`js-surface-manifest: ${failures.length} violation(s)`)
    for (const failure of failures) console.error(`  ${failure}`)
    return 1
  }
  console.log(`js-surface-manifest: OK — ${manifest.length} registered surfaces, laws and contract imports closed`)
  return 0
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href
if (isMain) process.exit(run())
