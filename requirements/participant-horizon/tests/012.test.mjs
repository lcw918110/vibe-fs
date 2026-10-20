import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as warmStart from '../../../dist/Repository/Investigation/WarmStartSurface.js'

process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'

test('WHAT[participant-horizon-012] real warm-start admission permits repository-evidence roles and rejects others before search', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-horizon-warm-start-'))
  try {
    for (const role of ['engineer', 'devops', 'manager', 'orchestrator', 'bookkeeper', 'unknown']) {
      let searches = 0
      const search = async () => {
        searches += 1
        return [{ filePath: 'fixture.js', startLine: 1, endLine: 1, content: 'PRIVATE-HINT' }]
      }
      const result = await warmStart.prepareWithSearch(search, `warm-${role}`, role, directory, 'query', 'PUBLIC-CHARGE')
      if (['engineer', 'devops'].includes(role)) {
        assert.equal(result.ok, true, role)
        assert.equal(searches, 1, role)
        assert.match(result.value, /PRIVATE-HINT/)
      } else {
        assert.equal(result.ok, false, role)
        assert.equal(searches, 0, role)
        assert.ok(!JSON.stringify(result).includes('PRIVATE-HINT'))
      }
    }
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})
