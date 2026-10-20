import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import {
  REPO_ROOT,
  deriveExpectedClosure,
  parsePackResult,
  validateArchiveEntries,
  validateArtifact,
} from '../../../scripts/verify-package.mjs'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

const root = REPO_ROOT

const pkg = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'))

const exists = (relative) => fs.existsSync(path.join(root, relative))

const normalize = (entry) => String(entry).replace(/\\/g, '/').replace(/\/+$/, '')

const walkFs = (dir) => {
  const out = []
  if (!fs.existsSync(dir)) return out
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name)
    if (entry.isDirectory()) out.push(...walkFs(full))
    else if (entry.isFile() && entry.name.endsWith('.fs')) out.push(full)
  }
  return out
}

const PROVIDER_ROLES = [
  'engineer',
  'manager',
  'orchestrator',
  'devops',
  'plan',
  'bookkeeper',
]

test('WHAT[distribution-008] DISTRIBUTION_provider_resource_closure_is_language_complete', () => {
  const roleRoot = path.join(root, 'resources', 'provider', 'role')
  const roles = fs
    .readdirSync(roleRoot, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name)
  assert.ok(roles.length >= 1, 'provider role tree must contain at least one role')
  for (const role of roles) {
    assert.ok(exists(`resources/provider/role/${role}/en.md`), `missing Role Law ${role}/en.md`)
    assert.ok(
      exists(`resources/provider/role/${role}/zh-CN.md`),
      `missing Role Law ${role}/zh-CN.md`,
    )
  }
  for (const leaf of ['world/common-law', 'library/ingress', 'library/closing']) {
    assert.ok(exists(`resources/provider/${leaf}/en.md`), `missing provider asset ${leaf}/en.md`)
    assert.ok(
      exists(`resources/provider/${leaf}/zh-CN.md`),
      `missing provider asset ${leaf}/zh-CN.md`,
    )
  }
})

integrationTest('WHAT[distribution-008] repository runtime resources include both languages for active role laws and rulebook', () => {
  const providerDir = path.join(root, 'resources', 'provider')

  assert.ok(fs.existsSync(providerDir), 'resources/provider must exist')
  assert.equal(
    fs.existsSync(path.join(root, 'resources', 'prompts')),
    false,
    'legacy resources/prompts must be gone after Prompt Restoration cutover',
  )

  for (const role of PROVIDER_ROLES) {
    for (const locale of ['en.md', 'zh-CN.md']) {
      const full = path.join(providerDir, 'role', role, locale)
      assert.ok(fs.existsSync(full), `missing Role Law ${role}/${locale}`)
      const text = fs.readFileSync(full, 'utf8')
      assert.ok(text.trim().length > 0, `Role Law ${role}/${locale} must be non-empty`)
    }
  }
  assert.equal(PROVIDER_ROLES.length, 6)

  for (const leaf of ['world/common-law/en.md', 'world/common-law/zh-CN.md']) {
    const full = path.join(providerDir, leaf)
    assert.ok(fs.existsSync(full), `missing provider asset ${leaf}`)
  }
})

integrationTest(
  'WHAT[distribution-008] GAP-210: every declared resource is available from the exact installed artifact',
  async () => {
    const { execFileSync } = await import('node:child_process')
    const runDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wxs-package-008-'))
    try {
      const expectedClosure = deriveExpectedClosure({ root })
      const packJson = execFileSync(
        'npm',
        ['pack', '--json', '--ignore-scripts', '--pack-destination', runDir],
        { cwd: root, encoding: 'utf8' },
      )
      const filename = parsePackResult(packJson, 'wanxiangshu').filename
      const tarball = path.join(runDir, filename)
      assert.ok(fs.existsSync(tarball), `packed tarball must exist: ${filename}`)

      const validation = await validateArchiveEntries(tarball, expectedClosure)
      assert.deepEqual(
        validation.issues,
        [],
        `artifact closure issues: ${JSON.stringify(validation.issues.slice(0, 5))}`,
      )

      const artifact = await validateArtifact(tarball, expectedClosure)
      assert.equal(artifact.ok, true, 'the packed artifact must satisfy its declared closure')
    } finally {
      fs.rmSync(runDir, { recursive: true, force: true })
    }
  },
)
