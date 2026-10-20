import assert from 'node:assert/strict'
import test from 'node:test'
import { hint, warmStart, withWorkspace } from './support/warm-start.mjs'

test('WHAT[repository-investigation-007] keyword lines preserve whole queries with stable exact dedupe and the current finite cap', () => {
  const raw = ' auth handler\r\n\r\nbeta\nauth handler\nAuth handler\n gamma \n d\n e\n f\n g\n h\n i\n'
  assert.deepEqual(warmStart.normalizeKeywords(raw), ['auth handler', 'beta', 'Auth handler', 'gamma', 'd', 'e', 'f', 'g'])
  assert.equal(warmStart.maxKeywords, 8)
})

test('WHAT[repository-investigation-007] empty keywords perform zero searches even when the charge contains tempting query text', async (context) => {
  await withWorkspace(async (directory) => {
    let calls = 0
    const search = async () => { calls += 1; return [] }
    const charge = 'Find auth handlers.\r\nKeep this exact text.\n'
    for (const keywords of [undefined, '', ' \r\n\t']) {
      const result = await warmStart.prepareWithSearch(search, 'investigation-zero', 'Manager', directory, keywords, charge)
      assert.equal(result.ok, true)
      assert.equal(calls, 0)
      await context.test(`raw task bytes preserved for ${JSON.stringify(keywords)}`, { todo: 'GAP-084: current common renderer wraps instructions; clarify byte-equality boundary before changing it' }, () => {
        assert.equal(result.value, charge)
      })
    }
  })
})

test('WHAT[repository-investigation-007] repeated invocations actually search again and expose changed results', async () => {
  await withWorkspace(async (directory) => {
    let calls = 0
    const search = async (query) => { calls += 1; assert.equal(query, 'full query'); return [hint('src/a.fs', `revision-${calls}`)] }
    const first = await warmStart.prepareWithSearch(search, 'investigation-repeat', 'Engineer', directory, 'full query', 'Inspect.')
    const second = await warmStart.prepareWithSearch(search, 'investigation-repeat', 'Engineer', directory, 'full query', 'Inspect.')
    assert.equal(calls, 2)
    assert.equal(first.ok, true)
    assert.equal(second.ok, true)
    assert.ok(first.value.includes('revision-1'))
    assert.ok(second.value.includes('revision-2'))
  })
})
