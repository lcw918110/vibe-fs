import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs'
import { basename, join, relative } from 'node:path'
import { parse } from 'acorn'
import { clauseDefinitionHeadings } from '../../../../scripts/lib/spec-rules.mjs'

export function packageProblems(directory) {
  return [['WHY.md', 'file'], ['WHAT.md', 'file'], ['tests', 'directory']].flatMap(([name, kind]) => {
    const path = join(directory, name)
    const present = existsSync(path) && (kind === 'file' ? statSync(path).isFile() : statSync(path).isDirectory())
    return present ? [] : [`${name}: expected ${kind}`]
  })
}

export function testFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) return testFiles(path)
    return entry.name.endsWith('.test.mjs') ? [path] : []
  }).sort()
}

function titleText(node) {
  if (node?.type === 'Literal' && typeof node.value === 'string') return node.value
  if (node?.type === 'TemplateLiteral') return node.quasis.map((part) => part.value.cooked).join('${…}')
  if (node?.type === 'BinaryExpression' && node.operator === '+') return titleText(node.left) + titleText(node.right)
  return ''
}

export function testDeclarations(source) {
  const declarations = []
  const visit = (node) => {
    if (!node || typeof node !== 'object') return
    if (node.type === 'CallExpression') {
      let callee = node.callee
      if (callee?.type === 'MemberExpression' && ['only', 'skip', 'todo'].includes(callee.property.name)) callee = callee.object
      if (callee?.type === 'Identifier' && ['test', 'it', 'integrationTest', 'e2eTest', 'releaseTest'].includes(callee.name)) {
        declarations.push({ kind: callee.name, title: titleText(node.arguments[0]), line: node.loc.start.line })
      }
    }
    for (const value of Object.values(node)) {
      if (Array.isArray(value)) value.forEach(visit)
      else if (value && typeof value === 'object') visit(value)
    }
  }
  visit(parse(source, { ecmaVersion: 'latest', sourceType: 'module', locations: true }))
  return declarations
}

export function testOwnershipProblems(source, owner) {
  const allowed = owner === 'context-compression-034' ? [owner, 'context-compression-033'] : [owner]
  return testDeclarations(source).flatMap(({ title, line }) => {
    const anchors = [...title.matchAll(/WHAT\[([^\]]+)\]/g)].map((match) => match[1])
    return allowed.some((expected) => anchors.includes(expected)) ? [] : [{ line, anchors, expected: owner }]
  })
}

export function testFileProblems(files, liveClauses) {
  const knownTransitionFiles = new Set([
    'requirements/delegation/tests/034.test.mjs',
    'requirements/repository-programming/tests/054.test.mjs',
  ])

  // 物理 canary：检查 Fable union 表征与真实宿主 provider 请求改写，不属于某个编号包语义。
  const isPhysicalCanary = (file) => file.endsWith('-physical.test.mjs') || file.endsWith('tenure-barrier.test.mjs')

  return files.flatMap((file) => {
    if (file.endsWith('-seam.test.mjs') || isPhysicalCanary(file) || knownTransitionFiles.has(file)) return []
    const number = /^(\d{3})\.test\.mjs$/.exec(basename(file))?.[1]
    const problems = []
    if (!number) return [`${file}: expected NNN.test.mjs`]
    if (!liveClauses.has(number)) problems.push(`${file}: clause ${number} is absent; notify the user to resolve its tests`)
    return problems
  })
}

export function inspectRequirements(root) {
  const requirements = join(root, 'requirements')
  const index = readFileSync(join(requirements, 'INDEX.md'), 'utf8')
  const packages = [...index.matchAll(/^\| `([a-z][a-z0-9-]*)` \|/gm)].map((match) => match[1])
  const problems = []
  for (const entry of readdirSync(requirements, { withFileTypes: true })) {
    if (entry.isDirectory() && entry.name !== 'proposals' && !packages.includes(entry.name)) {
      problems.push(`${entry.name}: package missing from INDEX.md`)
    }
  }
  for (const pkg of new Set(packages)) {
    const directory = join(requirements, pkg)
    const incomplete = packageProblems(directory)
    problems.push(...incomplete.map((message) => `${pkg}/${message}`))
    if (incomplete.length) continue
    const clauses = new Set(clauseDefinitionHeadings(readFileSync(join(directory, 'WHAT.md'), 'utf8')).map(({ id }) => id))
    const files = testFiles(join(directory, 'tests'))
    problems.push(...testFileProblems(files.map((file) => relative(root, file)), clauses))
    for (const file of files) {
      const number = /^(\d{3})\.test\.mjs$/.exec(basename(file))?.[1]
      if (!number) continue
      for (const issue of testOwnershipProblems(readFileSync(file, 'utf8'), `${pkg}-${number}`)) {
        problems.push(`${relative(root, file)}:${issue.line}: expected exactly WHAT[${issue.expected}], got ${JSON.stringify(issue.anchors)}`)
      }
    }
  }
  return problems
}
