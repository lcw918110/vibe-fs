import assert from 'node:assert/strict'
import test from 'node:test'
import { writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { hint, warmStart, withWorkspace } from './support/warm-start.mjs'

test('WHAT[repository-investigation-008] actual consumer admission rejects other roles before search and admits Engineer and DevOps', async () => {
  await withWorkspace(async (directory) => {
    const calls = []
    const search = async (query, workspace) => { calls.push({ query, workspace }); return [hint('source.fs', 'candidate')] }
    for (const role of ['Manager', 'Orchestrator', 'Bookkeeper']) {
      const result = await warmStart.prepareWithSearch(search, 'investigation-role', role, directory, 'query', 'Inspect.')
      assert.equal(result.ok, false, role)
      assert.equal(calls.length, 0)
    }
    for (const role of ['Engineer', 'DevOps']) {
      const result = await warmStart.prepareWithSearch(search, 'investigation-role', role, directory, 'query', 'Inspect.')
      assert.equal(result.ok, true, role)
      assert.ok(result.value.includes('candidate'))
    }
    assert.deepEqual(calls, [{ query: 'query', workspace: directory }, { query: 'query', workspace: directory }])
  })
})

test('WHAT[repository-investigation-008] absent, blank, nonexistent and file-valued workspace paths skip without guessing a search directory', async () => {
  await withWorkspace(async (directory) => {
    const file = join(directory, 'file.txt')
    writeFileSync(file, 'not a directory')
    let calls = 0
    const search = async () => { calls += 1; return [] }
    const baseline = await warmStart.appendToBaseWithSearch(search, 'investigation-path', 'Engineer', directory, '', 'Base task')
    for (const workspace of [undefined, '', ' ', join(directory, 'missing'), file]) {
      const result = await warmStart.appendToBaseWithSearch(search, 'investigation-path', 'Engineer', workspace, 'query', 'Base task')
      assert.deepEqual(result, baseline)
      assert.equal(calls, 0)
    }
  })
})

test.todo('WHAT[repository-investigation-008] an existing but unrelated directory is not accepted as the task workspace; Directory.Exists alone does not establish provenance (GAP-083)')
