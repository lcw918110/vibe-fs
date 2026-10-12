import assert from 'node:assert/strict'
import test, { afterEach } from 'node:test'
import { call, decode } from './support/cycle.mjs'

// Let Node deliver completed journal work between the synchronous steps.
afterEach(() => new Promise(resolve => setImmediate(resolve)))

test('WHAT[behavior-diagnosis-009] actual cycle decoder accepts one completed call and refuses zero or two', () => {
  const single = decode([call()])
  assert.equal(single.decodedCalls, 1)
  assert.deepEqual(single.decision, {
    ok: true,
    value: {
      text: 'resolve the current question work result settled continue on the settled path',
      evidence: '',
      ruleId: 'primitive-obsession',
      toolCallIds: ['call-1'],
    },
  })
  for (const parts of [[], [call(), call({ callID: 'call-2' })]]) {
    assert.equal(decode(parts).decision.ok, false)
  }
})

test('WHAT[behavior-diagnosis-009] raw cardinality precedes decode filtering on the decoder surface', () => {
  // One decodable call plus one completed-but-undecodable chronicle call:
  // the raw cardinality of the step is two, and the breach stands no matter
  // how many of those calls decode.
  const undecodable = call({
    callID: 'call-2',
    state: { status: 'completed', input: { charge: 'only a charge, nothing else' } },
  })
  const mixed = decode([call(), undecodable])
  assert.equal(mixed.chronicleCallCount, 2, 'raw cardinality counts undecodable calls too')
  assert.equal(mixed.decodedCalls, 1, 'decode filtering alone would hide the second call')
  assert.equal(mixed.decision.ok, false, 'a two-call step never forms a valid cycle')

  // 0 / 1 / 2 legal-call controls: only exactly one raw call yields a cycle.
  const zero = decode([])
  assert.equal(zero.chronicleCallCount, 0)
  assert.equal(zero.decodedCalls, 0)
  assert.equal(zero.decision.ok, false)

  const one = decode([call()])
  assert.equal(one.chronicleCallCount, 1)
  assert.equal(one.decodedCalls, 1)
  assert.equal(one.decision.ok, true)

  const two = decode([call(), call({ callID: 'call-2' })])
  assert.equal(two.chronicleCallCount, 2)
  assert.equal(two.decodedCalls, 2)
  assert.equal(two.decision.ok, false)
})

{
const { default: assert } = await import('node:assert/strict')
const { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } = await import('node:fs')
const { tmpdir } = await import('node:os')
const { basename, dirname, join } = await import('node:path')
const blog = await import('../../../dist/Enforcer/BlogSurface.js')
const journal = await import('../../../dist/Persistence/Journal/Surface.js')
const runtime = await import('../../../dist/Context/Companion/RuntimeSurface.js')
const resources = await import('../../../dist/Resources/PromptSurface.js')
const ownership = await import('../../verification-system/tests/support/blogger-ownership.mjs')

resources.runtimeInstallFromPackage()

let ownerCounter = 0
const withOwner = async (action) => {
  ownerCounter += 1
  const n = ownerCounter
  const ids = {
    main: `ses-main-bd009-${n}`,
    blogger: `ses-blogger-bd009-${n}`,
    request: `req-blog-bd009-${n}`,
    root: `msg-root-blog-bd009-${n}`,
    physical: `msg-phys-blog-bd009-${n}`,
  }
  const dir = mkdtempSync(join(tmpdir(), 'wxs-bd009-'))
  let opened
  let scope
  let actionFailure
  const drains = []
  try {
    opened = await journal.JournalSurface_bootWithWriterId(
      dir,
      `writer-bd009-${n}`,
      `rt-bd009-${n}`,
      4242,
      '2026-01-01T00:00:00Z',
    )
    assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
    const durable = opened.journal.journal
    await ownership.linkBlogger(opened.journal, ids.main, ids.blogger)
    const profile = await ownership.rootBlogger(opened.journal, ids.blogger, ids.root)
    // Real process-local owner scope (isolates the shared flight registry).
    scope = runtime.createScope()
    const request = runtime.main({
      requestId: ids.request,
      mainSession: ids.main,
      bloggerSession: ids.blogger,
      toml: 'bd-009-toml',
    })
    assert.equal(runtime.claimCurrentRequest(scope, ids.blogger, request), 'Claimed')
    await ownership.ownRequest({
      handle: opened.journal,
      durable,
      scope,
      bloggerSession: ids.blogger,
      profile,
      request,
      physical: ids.physical,
    })
    return await action({
      ids,
      durable,
      scope,
      request,
      dir,
      trackPending: pending => {
        drains.push(Promise.allSettled([pending]))
        return pending
      },
      writerFile: join(dir, 'wanxiangshu', 'events', `writer-bd009-${n}.ndjson`),
    })
  } catch (error) {
    actionFailure = { error }
    throw error
  } finally {
    const failures = []
    try {
      if (scope) runtime.dispose(scope)
    } catch (error) {
      failures.push(error)
    }
    try {
      if (scope) await runtime.drainRepairEpisodes(scope)
    } catch (error) {
      failures.push(error)
    }
    for (const drain of drains) {
      const [outcome] = await drain
      if (outcome.status === 'rejected') failures.push(outcome.reason)
    }
    try {
      if (opened?.ok) journal.JournalSurface_dispose(opened.journal)
    } catch (error) {
      failures.push(error)
    }
    try {
      rmSync(dir, { recursive: true, force: true })
    } catch (error) {
      failures.push(error)
    }
    if (failures.length) {
      const causes = actionFailure ? [actionFailure.error, ...failures] : failures
      throw new AggregateError(causes, 'Blogger observation fixture cleanup failed', { cause: actionFailure ? actionFailure.error : failures[0] })
    }
  }
}

const ownedTerminal = (ids, run, parts = []) => [
  ownership.userMessage(ids.physical),
  ownership.assistantMessage(run, ids.physical, parts),
]

// BlogObservationCommitted is the single durable fact that atomically carries
// frame append, RecordCoverage advance, tip and identities (WHAT-012), so the
// count of committed facts in the real writer file is the coverage verdict.
// A writer line is canonical JSON (durable-events-003): the journal envelope
// rides in payload, and its fact is the union path
// Agent → Context → BlogObservationCommitted.
const isObservationLine = line => {
  const parsed = JSON.parse(line)
  const fact = parsed?.payload?.Fact
  return Array.isArray(fact)
    && fact[0] === 'Agent'
    && Array.isArray(fact[1])
    && fact[1][0] === 'Context'
    && Array.isArray(fact[1][1])
    && fact[1][1][0] === 'BlogObservationCommitted'
}

const countFile = (file) => {
  return readFileSync(file, 'utf8')
    .split('\n')
    .filter(line => line.trim() !== '')
    .filter(isObservationLine)
    .length
}

const committedCount = (writerPrefix) => {
  const dir = dirname(writerPrefix)
  const base = basename(writerPrefix)
  const writerId = base.endsWith('.ndjson') ? base.slice(0, -'.ndjson'.length) : base
  if (existsSync(writerPrefix)) {
    return countFile(writerPrefix)
  }
  if (!existsSync(dir)) {
    const error = new Error(`ENOENT: no such file or directory, open '${writerPrefix}'`)
    error.code = 'ENOENT'
    throw error
  }
  const files = readdirSync(dir)
    .filter(name => name === `${writerId}.ndjson` || name.startsWith(`${writerId}-`))
    .map(name => join(dir, name))
  if (files.length === 0) {
    // If testing explicit path that doesn't exist, throw ENOENT
    const error = new Error(`ENOENT: no such file or directory, open '${writerPrefix}'`)
    error.code = 'ENOENT'
    throw error
  }
  let total = 0
  for (const file of files) {
    total += countFile(file)
  }
  return total
}

const undecodableChronicle = callID => ({
  type: 'tool',
  tool: 'chronicle',
  callID,
  state: { status: 'completed', input: { charge: 'only a charge, nothing else' } },
})

test('WHAT[behavior-diagnosis-009] GAP-112 real raw two-call terminal with only one decodable call commits nothing and advances no coverage', async () => {
  await withOwner(async ({ ids, durable, scope, writerFile }) => {
    const parts = [
      ownership.chroniclePart('call-1', 'primitive-obsession', 'one decodable observation'),
      undecodableChronicle('call-2'),
    ]

    const outcome = await blog.continueTransform(
      scope,
      durable,
      ids.blogger,
      ownedTerminal(ids, 'run-mixed', parts),
    )
    assert.ok(['ProjectMessages', 'StopPhysicalRun'].includes(outcome.kind))

    // The raw cardinality breach commits no BlogObservationCommitted, and
    // coverage only ever advances through that single fact — zero facts means
    // zero coverage advance.
    assert.equal(committedCount(writerFile), 0)
    // No commit, no release: the exact flight still holds the open request.
    assert.equal(runtime.tryGetFlight(scope, ids.blogger).requestId, ids.request)
  })
})

test('WHAT[behavior-diagnosis-009] a single fully decodable call still commits exactly one observation through the same real chain', async () => {
  await withOwner(async ({ ids, durable, scope, writerFile, trackPending }) => {
    const parts = [ownership.chroniclePart('call-1', 'primitive-obsession', 'one decodable observation')]

    // A successful commit parks the continuation waiting for new material, so
    // the durable effect is the assertion target — not the parked return.
    trackPending(blog.continueTransform(
      scope,
      durable,
      ids.blogger,
      ownedTerminal(ids, 'run-single', parts),
    ))
    const deadline = Date.now() + 5000
    while (committedCount(writerFile) === 0 && Date.now() < deadline) {
      await new Promise(resolve => setTimeout(resolve, 20))
    }
    assert.equal(committedCount(writerFile), 1)
    assert.equal(runtime.tryGetFlight(scope, ids.blogger), null)
  })
})

test('WHAT[behavior-diagnosis-009] a missing unreadable or malformed writer log cannot count as zero committed observations', () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-bd009-log-'))
  try {
    assert.throws(() => committedCount(join(dir, 'missing.ndjson')), { code: 'ENOENT' })
    assert.throws(() => committedCount(dir), { code: 'EISDIR' })
    const malformed = join(dir, 'malformed.ndjson')
    writeFileSync(malformed, '{broken journal line}\n')
    assert.throws(() => committedCount(malformed), SyntaxError)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[behavior-diagnosis-009] cleanup cancels and drains a publicly claimed repair episode before retiring its owner resources', async () => {
  let disposedScope
  let identity
  let directory
  await withOwner(async ({ ids, scope, dir }) => {
    identity = [ids.request, ids.physical, ids.main, ids.blogger]
    assert.equal(runtime.claimRepairEpisode(scope, ...identity), 'Claimed')
    disposedScope = scope
    directory = dir
  })
  assert.equal(runtime.claimRepairEpisode(disposedScope, ...identity), 'Error:Blogger runtime is shutting down')
  assert.equal(existsSync(directory), false)
})

for (const failure of [new Error('controlled Blogger fixture action failure'), null]) {
  test(`WHAT[behavior-diagnosis-009] owner cleanup preserves the original ${failure === null ? 'null' : 'Error'} action failure`, async () => {
    let directory
    const [outcome] = await Promise.allSettled([withOwner(async ({ dir }) => {
      directory = dir
      throw failure
    })])
    assert.equal(outcome.status, 'rejected')
    assert.equal(outcome.reason, failure)
    assert.equal(existsSync(directory), false)
  })
  test(`WHAT[behavior-diagnosis-009] a rejected pending task preserves the original ${failure === null ? 'null' : 'Error'} as the cleanup cause`, async () => {
    let directory
    const drainFailure = new Error('controlled pending-task failure')
    const [outcome] = await Promise.allSettled([withOwner(async ({ dir, trackPending }) => {
      directory = dir
      trackPending(Promise.reject(drainFailure))
      throw failure
    })])
    assert.equal(outcome.status, 'rejected')
    assert.ok(outcome.reason instanceof AggregateError)
    assert.equal(outcome.reason.cause, failure)
    assert.deepEqual(outcome.reason.errors, [failure, drainFailure])
    assert.equal(existsSync(directory), false)
  })
}
}
