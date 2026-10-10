import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const rootDir = path.resolve(__dirname, '../../..')
const resourcesDir = path.join(rootDir, 'resources/provider')

test('WHAT[planning-014] plan role, stages, and tool resources exist in paired English and Simplified Chinese markdown files', () => {
  const resourceBases = [
    'role/plan',
    'planning/s1',
    'planning/s2',
    'planning/s3',
    'tool/js-plan',
    'tool/ask',
    'tool/resume',
    'tool/handoff',
    'tool/deliver',
  ]

  for (const base of resourceBases) {
    const enFile = path.join(resourcesDir, base, 'en.md')
    const zhFile = path.join(resourcesDir, base, 'zh-CN.md')

    // Both language files must exist on disk
    assert.ok(fs.existsSync(enFile), `Expected ${base}/en.md to exist`)
    assert.ok(fs.existsSync(zhFile), `Expected ${base}/zh-CN.md to exist`)

    // Both files must have non-empty content
    const enContent = fs.readFileSync(enFile, 'utf8').trim()
    const zhContent = fs.readFileSync(zhFile, 'utf8').trim()

    assert.ok(enContent.length > 0, `${base}/en.md must not be empty`)
    assert.ok(zhContent.length > 0, `${base}/zh-CN.md must not be empty`)
  }
})
