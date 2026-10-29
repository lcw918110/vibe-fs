#!/usr/bin/env node

import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { ruleDocumentName } from '../lib/spec-rules.mjs'

const ROOT = process.cwd()

export function check(context) {
  const root = context?.root ?? ROOT
  const readAt = (rel) => readFileSync(join(root, rel), 'utf8')
  const issues = []
  const index = readAt('requirements/INDEX.md')
  const nodes = JSON.parse(readAt('resources/ablation/nodes.json'))
  const toolMap = JSON.parse(readAt('resources/ablation/tool-map.json'))
  const factMap = JSON.parse(readAt('resources/ablation/fact-map.json'))
  const profiles = JSON.parse(readAt('resources/ablation/profiles.json'))
  const staticTools = readAt('src/Wanxiangshu/OpenCode/Tools/StaticTools.fs')

  const packageDirs = () =>
    readdirSync(join(root, 'requirements'), { withFileTypes: true })
      .filter((entry) => entry.isDirectory())
      .map((entry) => entry.name)
      .filter((name) => {
        try {
          readAt(`requirements/${name}/${ruleDocumentName(name)}`)
          return true
        } catch {
          return false
        }
      })

  const primaryNodes = nodes.nodes.filter((node) => node.kind === 'package' && node.status !== 'revoked').map((node) => node.id)
  const packages = packageDirs()

  for (const pkg of packages) {
    if (!index.includes(`\`${pkg}\``)) {
      issues.push({ code: 'index-missing', message: `requirements/${pkg} missing from INDEX.md` })
    }
  }

  for (const id of primaryNodes) {
    if (!packages.includes(id)) {
      issues.push({ code: 'node-orphan', message: `ablation node ${id} has no requirements/${id}` })
    }
  }

  for (const pkg of packages) {
    if (pkg === 'feature-ablation') continue
    if (!primaryNodes.includes(pkg)) {
      issues.push({ code: 'node-missing', message: `package ${pkg} missing from resources/ablation/nodes.json` })
    }
  }

  const knownMatch = staticTools.match(/let knownToolNames =\s*\[([\s\S]*?)\]/)
  const knownTools = knownMatch
    ? [...knownMatch[1].matchAll(/"([^"]+)"/g)].map((match) => match[1])
    : []
  // SphinxMcp.permissionKey is "sphinx_*"
  if (staticTools.includes('SphinxMcp.permissionKey')) {
    knownTools.push('sphinx_*')
  }

  for (const tool of knownTools) {
    if (!toolMap.tools[tool]) {
      issues.push({ code: 'tool-unmapped', message: `known tool ${tool} missing from resources/ablation/tool-map.json` })
    }
  }

  for (const [tool, node] of Object.entries(toolMap.tools)) {
    if (!knownTools.includes(tool)) {
      issues.push({ code: 'tool-stale', message: `tool-map entry ${tool} not in StaticTools.knownToolNames` })
    }
    if (!nodes.nodes.some((entry) => entry.id === node)) {
      issues.push({ code: 'tool-node', message: `tool ${tool} maps to unknown node ${node}` })
    }
  }

  for (const [tag, node] of Object.entries(factMap.facts)) {
    if (!nodes.nodes.some((entry) => entry.id === node)) {
      issues.push({ code: 'fact-node', message: `fact ${tag} maps to unknown node ${node}` })
    }
  }

  if (Object.keys(factMap.facts).length === 0) {
    issues.push({ code: 'fact-empty', message: 'fact-map.json has no entries' })
  }

  if (!profiles.profiles.production) {
    issues.push({ code: 'profile-production', message: 'profiles.json missing production profile' })
  }

  // Verify revoked package nodes are completely removed
  const REVOKED_PACKAGES = ['external-investigation', 'output-distillation']
  for (const revoked of REVOKED_PACKAGES) {
    const node = nodes.nodes.find((n) => n.id === revoked)
    if (node) {
      issues.push({
        code: 'node-revoked-not-deleted',
        message: `revoked package node ${revoked} must be removed from nodes.json`,
      })
    }
  }

  // Verify no node contains revoked status or attribute
  for (const node of nodes.nodes) {
    if (node.status === 'revoked' || node.revoked !== undefined) {
      issues.push({
        code: 'node-revoked-stale',
        message: `node ${node.id} has revoked status or attribute; revoked nodes must be deleted`,
      })
    }
  }

  // Verify borrowed_surface exists and is string[] on every node
  for (const node of nodes.nodes) {
    if (!Array.isArray(node.borrowed_surface)) {
      issues.push({
        code: 'node-borrowed-surface-missing',
        message: `node ${node.id} missing borrowed_surface array`,
      })
    } else {
      for (const item of node.borrowed_surface) {
        if (typeof item !== 'string') {
          issues.push({
            code: 'node-borrowed-surface-invalid',
            message: `node ${node.id} borrowed_surface contains non-string item: ${item}`,
          })
        }
      }
    }
  }

  // Verify parent edges are completely removed
  const parentEdges = (nodes.edges || []).filter((e) => e.kind === 'parent')
  if (parentEdges.length > 0) {
    issues.push({
      code: 'edge-parent-stale',
      message: `found ${parentEdges.length} parent edges; parent must be node attribute`,
    })
  }

  // Verify edge kinds: only station-order and borrow allowed
  for (const edge of nodes.edges || []) {
    const kind = edge.kind ?? 'station-order'
    if (kind !== 'station-order' && kind !== 'borrow') {
      issues.push({
        code: 'edge-kind-invalid',
        message: `edge ${edge.from} -> ${edge.to} has invalid kind '${edge.kind}'; only station-order and borrow allowed`,
      })
    }
  }

  // Verify slice nodes declare valid parent
  const nodeMap = new Map(nodes.nodes.map((n) => [n.id, n]))
  for (const node of nodes.nodes.filter((n) => n.kind === 'slice')) {
    if (!node.parent) {
      issues.push({ code: 'slice-missing-parent', message: `slice node ${node.id} missing parent` })
    } else {
      const parentNode = nodeMap.get(node.parent)
      if (!parentNode || parentNode.kind !== 'package' || parentNode.package !== node.package) {
        issues.push({ code: 'slice-invalid-parent', message: `slice node ${node.id} has invalid parent ${node.parent}` })
      }
    }
  }

  // Static cycle detection across station-order and borrow edges
  const dagEdges = (nodes.edges || []).filter(
    (e) => (e.kind ?? 'station-order') === 'station-order' || e.kind === 'borrow'
  )
  const adj = new Map()
  for (const edge of dagEdges) {
    if (!adj.has(edge.from)) adj.set(edge.from, [])
    adj.get(edge.from).push(edge.to)
  }
  const visitState = new Map()
  for (const node of nodes.nodes) {
    visitState.set(node.id, 0)
  }
  let cycleFound = null
  const dfs = (nodeId) => {
    visitState.set(nodeId, 1)
    for (const next of adj.get(nodeId) || []) {
      const state = visitState.get(next) ?? 0
      if (state === 1) {
        cycleFound = `Cycle detected involving edge ${nodeId} -> ${next}`
        return true
      }
      if (state === 0) {
        if (dfs(next)) return true
      }
    }
    visitState.set(nodeId, 2)
    return false
  }
  for (const node of nodes.nodes) {
    if ((visitState.get(node.id) ?? 0) === 0) {
      if (dfs(node.id)) break
    }
  }
  if (cycleFound) {
    issues.push({ code: 'dag-cycle', message: cycleFound })
  }

  // Verify profiles: no revoked keys or modes, and all primary nodes covered
  for (const [profileId, profile] of Object.entries(profiles.profiles)) {
    if (profile.revoked !== undefined) {
      issues.push({
        code: 'profile-revoked-stale',
        message: `profile ${profileId} must not contain revoked property`,
      })
    }
    for (const [modeKey, modeVal] of Object.entries(profile.modes || {})) {
      if (modeVal === 'revoked' || modeKey === 'revoked') {
        issues.push({
          code: 'profile-revoked-stale',
          message: `profile ${profileId} must not contain revoked key or mode for ${modeKey}`,
        })
      }
      if (REVOKED_PACKAGES.includes(modeKey)) {
        issues.push({
          code: 'profile-revoked-not-deleted',
          message: `profile ${profileId} contains deleted revoked node ${modeKey}`,
        })
      }
    }
    for (const node of primaryNodes) {
      if (!profile.modes?.[node]) {
        issues.push({ code: 'profile-incomplete', message: `profile ${profileId} missing node ${node}` })
      }
    }
  }

  return { issues }
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const { issues } = check({ root: ROOT })
  if (issues.length === 0) {
    console.log('ablation-manifest: OK')
    process.exit(0)
  }
  console.error(`ablation-manifest: FAILED (${issues.length})`)
  for (const issue of issues) {
    console.error(`  [${issue.code}] ${issue.message}`)
  }
  process.exit(1)
}
