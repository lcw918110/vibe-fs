import assert from 'node:assert/strict'
import test from 'node:test'
import { parse } from 'smol-toml'
import { hint, warmStart, withWorkspace } from './support/warm-start.mjs'

test('bounded-map integration: reverse search completion retains keyword order in the actual prompt', { timeout: 5000 }, async () => {
  await withWorkspace(async directory => {
    const queries = ['first query', 'second query', 'third query']
    const gates = new Map(queries.map(query => [query, Promise.withResolvers()]))
    const started = Promise.withResolvers()
    const completions = []
    let calls = 0
    const search = async query => {
      if (++calls === queries.length) started.resolve()
      await gates.get(query).promise
      completions.push(query)
      return [hint(`${query}.fs`, `content for ${query}`)]
    }
    const pending = warmStart.prepareWithSearch(search, 'investigation-reverse', 'Engineer', directory, queries.join('\n'), 'Inspect.')
    try {
      await started.promise
      for (const query of [...queries].reverse()) {
        gates.get(query).resolve()
        await gates.get(query).promise
      }
      const result = await pending
      assert.equal(result.ok, true)
      assert.deepEqual(completions, [...queries].reverse())
      const parsed = parse(result.value)
      assert.deepEqual(parsed.repository_search.map(row => row.query), queries)
      assert.deepEqual(parsed.repository_hint.map(row => row.content), queries.map(query => `content for ${query}`))
    } finally {
      for (const gate of gates.values()) gate.resolve()
      await pending
    }
  })
})

test('bounded-map integration: one failed search remains empty while its siblings are rendered', async () => {
  await withWorkspace(async directory => {
    const calls = []
    const search = async query => {
      calls.push(query)
      if (query === 'unavailable') throw new Error('search unavailable')
      return [hint(`${query}.fs`, `content for ${query}`)]
    }
    const result = await warmStart.prepareWithSearch(search, 'investigation-failure', 'Engineer', directory, 'first\nunavailable\nthird', 'Inspect.')
    assert.equal(result.ok, true)
    assert.deepEqual(calls, ['first', 'unavailable', 'third'])
    const parsed = parse(result.value)
    assert.deepEqual(parsed.repository_search.map(row => row.query), ['first', 'unavailable', 'third'])
    assert.deepEqual(parsed.repository_hint.map(row => row.content), ['content for first', 'content for third'])
  })
})
