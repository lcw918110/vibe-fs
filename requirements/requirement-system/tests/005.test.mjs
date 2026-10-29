import assert from 'node:assert/strict'
import test from 'node:test'
import {
  clauseDefinitionHeadings,
  duplicateClauseDefinitions,
} from '../../../scripts/lib/spec-rules.mjs'

test('WHAT[requirement-system-005] formal clause definitions can only live in the package rule document', () => {
  const entries = [
    {
      file: 'requirements/README.md',
      pkg: 'requirement-system',
      text: '# README\n\n## [002] illegal clause in navigation file\n',
    },
    {
      file: 'AGENTS.md',
      pkg: 'requirement-system',
      text: '# AGENTS\n\n## [003] illegal clause in root doc\n',
    },
    {
      file: 'CHANGELOG.md',
      pkg: 'requirement-system',
      text: '# CHANGELOG\n\n## [004] illegal clause in changelog\n',
    },
    {
      file: 'requirements/requirement-system/WHAT.md',
      pkg: 'requirement-system',
      text: '# requirement-system — WHAT\n\n## [001] legal clause\n',
    },
  ]

  const findings = duplicateClauseDefinitions(entries)
  assert.equal(findings.length, 3, 'must flag all 3 non-WHAT definitions')
  assert.ok(findings.some((f) => f.file.includes('README.md') && f.msg.includes('非 WHAT 文件严禁定义正式条款')))
  assert.ok(findings.some((f) => f.file.includes('AGENTS.md') && f.msg.includes('非 WHAT 文件严禁定义正式条款')))
  assert.ok(findings.some((f) => f.file.includes('CHANGELOG.md') && f.msg.includes('非 WHAT 文件严禁定义正式条款')))
})

test('WHAT[requirement-system-005] clauseDefinitionHeadings surfaces ## [NNN] definitions from any file for detection', () => {
  assert.deepEqual(
    clauseDefinitionHeadings([
      '# README',
      '## [002] a clause defined in a navigation file',
    ].join('\n')),
    [{ id: '002', line: 2 }],
  )
})

test('WHAT[requirement-system-005] migrated test rules accept AXIOMS and reject active clauses in the old file', () => {
  const rules = { file: 'requirements/verification-system/AXIOMS.md', pkg: 'verification-system',
    text: '## AXIOM\n1. Truth matters.\n## SPEC\n### [001] Observable rule\n' }
  const navigation = { file: 'requirements/verification-system/WHAT.md', pkg: 'verification-system',
    text: '# Rules moved\n[Current rules](AXIOMS.md)\n' }
  assert.deepEqual(duplicateClauseDefinitions([rules, navigation]), [])
  const findings = duplicateClauseDefinitions([rules, { ...navigation, text: '## [002] Competing rule\n' }])
  assert.equal(findings.length, 1)
  assert.equal(findings[0].file, navigation.file)
  assert.match(findings[0].msg, /AXIOMS/)
  const wrongOwner = { ...rules, file: 'requirements/sample/AXIOMS.md', pkg: 'sample' }
  assert.equal(duplicateClauseDefinitions([wrongOwner]).length, 1)
  const outsideSpec = { ...rules, text: '## AXIOM\n### [002] Misplaced rule\n## SPEC\n### [001] Valid rule\n' }
  const misplaced = duplicateClauseDefinitions([outsideSpec])
  assert.equal(misplaced.length, 1)
  assert.match(misplaced[0].msg, /SPEC/)
  assert.equal(misplaced[0].line, 2)
  const repeated = duplicateClauseDefinitions([{ ...rules, text: rules.text + '### [001] Duplicate\n' }])
  assert.equal(repeated.length, 1)
  assert.match(repeated[0].msg, /重复定义/)
})

