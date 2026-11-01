import { parse } from 'acorn'

// Test declarations found by syntax, not by regex: comments and example strings
// inside test bodies must not look like tests. `only`/`skip`/`todo` modifiers
// resolve to the same declaration kind as their base call.
const TEST_CALLEES = new Set(['test', 'it', 'integrationTest', 'e2eTest', 'releaseTest'])
const MODIFIERS = new Set(['only', 'skip', 'todo'])

function titleText(node) {
  if (node?.type === 'Literal' && typeof node.value === 'string') return node.value
  if (node?.type === 'TemplateLiteral') return node.quasis.map((part) => part.value.cooked).join('${…}')
  if (node?.type === 'BinaryExpression' && node.operator === '+') return titleText(node.left) + titleText(node.right)
  return ''
}

/**
 * @param {string} source module source text
 * @returns {{ kind: string, title: string, line: number }[]}
 */
export function testDeclarations(source) {
  const declarations = []
  const visit = (node) => {
    if (!node || typeof node !== 'object') return
    if (node.type === 'CallExpression') {
      let callee = node.callee
      if (callee?.type === 'MemberExpression' && MODIFIERS.has(callee.property.name)) callee = callee.object
      if (callee?.type === 'Identifier' && TEST_CALLEES.has(callee.name)) {
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
