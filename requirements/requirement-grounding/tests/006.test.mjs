import assert from 'node:assert/strict'
import { createHash, randomUUID } from 'node:crypto'
import { execFileSync, spawnSync } from 'node:child_process'
import { existsSync, mkdtempSync, mkdirSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'
import * as grounding from '../../../dist/Requirement/Grounding/Surface.js'
import * as host from '../../../dist/OpenCode/Host/RequirementGroundingSurface.js'
import * as eventStore from '../../../dist/Persistence/EventStore/Surface.js'
import * as eventCodec from '../../../dist/Persistence/EventStore/CodecSurface.js'
import * as retention from '../../../dist/Persistence/EventStore/RetentionSurface.js'

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wanxiang-grounding-delivery-'))
  mkdirSync(join(dir, 'requirements', 'alpha', 'tests'), { recursive: true })
  mkdirSync(join(dir, 'src'), { recursive: true })
  writeFileSync(join(dir, 'requirements', 'alpha', 'WHY.md'), 'why\n', 'utf8')
  writeFileSync(join(dir, 'requirements', 'alpha', 'WHAT.md'), 'what-v1\n', 'utf8')
  writeFileSync(join(dir, 'requirements', 'alpha', 'HOW.md'), 'how\n', 'utf8')
  writeFileSync(join(dir, 'requirements', 'alpha', 'APPLIES-TO'), '/src/**\n', 'utf8')
  writeFileSync(join(dir, 'requirements', 'alpha', 'tests', 'z.test.mjs'), 'z\n', 'utf8')
  writeFileSync(join(dir, 'requirements', 'alpha', 'tests', 'a.test.mjs'), 'a\n', 'utf8')
  writeFileSync(join(dir, 'src', 'main.fs'), 'source\n', 'utf8')
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}

const terminalRead = (path) => [{
  info: { id: 'r1', role: 'assistant', providerID: 'anthropic' },
  parts: [{ type: 'tool', tool: 'read', callID: 'source-read', state: { status: 'completed', input: { filePath: path }, output: 'source\n', time: { start: 0, end: 0 } } }],
}]

const readFacts = commonDir => {
  const events = join(commonDir, 'wanxiangshu', 'events')
  let names
  try { names = readdirSync(events) } catch (error) {
    if (error.code === 'ENOENT') return []
    throw error
  }
  return names
    .flatMap(name => readFileSync(join(events, name), 'utf8').trim().split('\n').filter(Boolean).map(JSON.parse))
    .flatMap(envelope => {
      const fact = envelope.payload?.Fact
      const hostFact = fact?.[0] === 'Agent' && fact[1]?.[0] === 'Host' ? fact[1][1] : null
      return hostFact?.[0] === 'RequirementGroundingReadObserved' ? [hostFact[1]] : []
    })
}

const digest = (path, bytes) => createHash('sha256').update(`${path}\0${bytes}`).digest('hex')

test('WHAT[requirement-grounding-006] an unclassified native read wrapper conservatively keeps the complete material eligible', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const snapshot = grounding.materializePackage(dir, 'alpha')
    assert.deepEqual(snapshot.materials.map((material) => material.path), [
      'requirements/alpha/HOW.md',
      'requirements/alpha/WHAT.md',
      'requirements/alpha/WHY.md',
    ])
    assert.equal(snapshot.materials.some((m) => m.path.includes('/tests/')), false)
    assert.equal(snapshot.materials.some((m) => m.path.endsWith('/APPLIES-TO')), false)

    const opened = await host.createJournal(dir)
    const selfPath = join(dir, 'requirements', 'alpha', 'WHAT.md')
    const requested = await host.observationDecision(
      opened.journal,
      dir,
      's-self-material',
      'read',
      { filePath: selfPath },
      'what-v1\n',
    )
    assert.equal(requested.needsGrounding, true)
    const projected = await host.projectWithJournal(opened.journal, 's-self-material', terminalRead(selfPath))
    assert.equal(projected.ok, true)

    const terminalOutput = projected.value.at(-1).parts[0].state.output
    assert.ok(terminalOutput.includes('requirement_source_path = "requirements/alpha/HOW.md"'))
    assert.ok(terminalOutput.includes('requirement_source_path = "requirements/alpha/WHY.md"'))
    assert.ok(terminalOutput.includes('requirement_source_path = "requirements/alpha/WHAT.md"'))
    assert.equal(terminalOutput.includes('/tests/'), false)
    assert.equal(terminalOutput.includes('/APPLIES-TO'), false)

    const later = await host.requestPaths(opened.journal, dir, 's-self-material', [join(dir, 'src', 'main.fs')])
    assert.equal(later.needsGrounding, false, 'the completed automatic material reads establish whole-file visibility')
    host.disposeJournal(opened.journal)
  } finally { cleanup() }
})

test('WHAT[requirement-grounding-006] deduplicates material content versions and re-grounds only the changed Markdown sibling', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const opened = await host.createJournal(dir)
    assert.equal(opened.ok, true)
    const journal = opened.journal
    const source = join(dir, 'src', 'main.fs')
    const first = await host.requestPaths(journal, dir, 's-dedupe', [source])
    assert.equal(first.needsGrounding, true)
    assert.equal(first.requested, 1)
    await host.projectWithJournal(journal, 's-dedupe', terminalRead(source))
    const same = await host.requestPaths(journal, dir, 's-dedupe', [source])
    assert.equal(same.needsGrounding, false)
    writeFileSync(join(dir, 'requirements', 'alpha', 'tests', 'a.test.mjs'), 'a-v2\n', 'utf8')
    const testOnlyChange = await host.requestPaths(journal, dir, 's-dedupe', [source])
    assert.equal(testOnlyChange.needsGrounding, false, 'package identity excludes tests/**')
    writeFileSync(join(dir, 'requirements', 'alpha', 'WHAT.md'), 'what-v2\n', 'utf8')
    const changed = await host.requestPaths(journal, dir, 's-dedupe', [source])
    assert.equal(changed.needsGrounding, true)
    assert.equal(changed.requested, 1)
    const changedProjection = await host.projectWithJournal(journal, 's-dedupe', terminalRead(source))
    const changedOutput = changedProjection.value.at(-1).parts[0].state.output
    assert.ok(changedOutput.includes('requirement_source_path = "requirements/alpha/WHAT.md"'))
    host.disposeJournal(journal)
  } finally { cleanup() }
})

test('WHAT[requirement-grounding-006] reanchor_resets_horizon_coverage_so_the_same_digest_must_ground_again', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const opened = await host.createJournal(dir)
    const source = join(dir, 'src', 'main.fs')
    const session = 's-reanchor-dedupe'
    await host.requestPaths(opened.journal, dir, session, [source])
    await host.projectWithJournal(opened.journal, session, terminalRead(source))
    assert.equal((await host.requestPaths(opened.journal, dir, session, [source])).needsGrounding, false)

    const reanchored = await host.appendContextReanchored(opened.journal, session, 0n, 1n, 'compaction-1')
    assert.equal(reanchored.ok, true, reanchored.error)
    const after = await host.requestPaths(opened.journal, dir, session, [source])
    assert.equal(after.needsGrounding, true)
    assert.equal(after.requested, 1)
    host.disposeJournal(opened.journal)
  } finally { cleanup() }
})

test('WHAT[requirement-grounding-006] a read returning old bytes does not mark the newer disk version visible', async () => {
  const { dir, cleanup } = sandbox()
  let opened
  try {
    opened = await host.createJournal(dir)
    const path = join(dir, 'requirements', 'alpha', 'WHAT.md')
    writeFileSync(path, 'what-v2\n')
    await host.observationDecision(opened.journal, dir, 's-stale-read', 'read', { filePath: path }, 'what-v1\n')
    const observation = readFacts(dir).find(fact => fact.SessionId[1] === 's-stale-read').Observation
    assert.equal(observation.Path, 'requirements/alpha/WHAT.md')
    assert.equal(observation.Digest, digest(observation.Path, 'what-v1\n'), 'the durable version must come from returned bytes')
    assert.notEqual(observation.Digest, digest(observation.Path, 'what-v2\n'))
    assert.equal(observation.Coverage, 'PartialFile')
    const result = await host.projectWithJournal(opened.journal, 's-stale-read', terminalRead(join(dir, 'src', 'main.fs')))
    assert.equal(result.ok, true)
    assert.ok(result.value.at(-1).parts[0].state.output.includes('what-v2'))
  } finally {
    if (opened?.ok) host.disposeJournal(opened.journal)
    cleanup()
  }
})

test('WHAT[requirement-grounding-006] a partial returned read does not mark the complete disk material visible', async () => {
  const { dir, cleanup } = sandbox()
  let opened
  try {
    opened = await host.createJournal(dir)
    const path = join(dir, 'requirements', 'alpha', 'WHAT.md')
    await host.observationDecision(opened.journal, dir, 's-partial-read', 'read', { filePath: path, offset: 1, limit: 1 }, 'what-')
    const observation = readFacts(dir).find(fact => fact.SessionId[1] === 's-partial-read').Observation
    assert.equal(observation.Digest, digest(observation.Path, 'what-'))
    assert.equal(observation.Coverage, 'PartialFile')
    host.disposeJournal(opened.journal)
    opened = await host.createJournal(dir)
    assert.equal(opened.ok, true, opened.error)
    assert.deepEqual(readFacts(dir).find(fact => fact.SessionId[1] === 's-partial-read').Observation, observation)
    const projected = await host.projectWithJournal(opened.journal, 's-partial-read', terminalRead(join(dir, 'src', 'main.fs')))
    assert.equal(projected.ok, true)
    assert.ok(projected.value.at(-1).parts[0].state.output.includes('requirement_source_path = "requirements/alpha/WHAT.md"'), 'a partial read cannot suppress the complete material')
    assert.ok(projected.value.at(-1).parts[0].state.output.includes('what-v1'))
  } finally {
    if (opened?.ok) host.disposeJournal(opened.journal)
    cleanup()
  }
})

test('WHAT[requirement-grounding-006] a non-content native observation protocol input does not mint a successful read fact', async () => {
  const { dir, cleanup } = sandbox()
  let opened
  try {
    opened = await host.createJournal(dir)
    const path = join(dir, 'requirements', 'alpha', 'WHAT.md')
    for (const output of [null, undefined, { ok: false, code: 'FILE_NOT_FOUND' }]) {
      const decision = await host.observationDecision(opened.journal, dir, 's-failed-read', 'read', { filePath: path }, output)
      assert.equal(decision.needsGrounding, false)
      assert.equal(decision.requested, 0)
    }
    assert.deepEqual(readFacts(dir), [])
    assert.equal((await host.requestPaths(opened.journal, dir, 's-failed-read', [join(dir, 'src', 'main.fs')])).needsGrounding, true)
  } finally {
    if (opened?.ok) host.disposeJournal(opened.journal)
    cleanup()
  }
})

test('WHAT[requirement-grounding-006] an actual program file-read failure cannot produce visible read facts', async () => {
  const workflow = await import('../../../dist/Repository/Programming/Js/WorkflowSurface.js')
  const { dir, cleanup } = sandbox()
  try {
    const observations = []
    const outcome = await workflow.runObserved(dir, 'Coder', 'en', "class Js extends JsProgram { async run() { const file = await this.file('requirements/alpha/missing.md'); return file.text(); } }", 2000, Date.now() + 60000, 1 << 20, null,
      async (reads, effects) => observations.push({ reads, effects }))
    assert.equal(workflow.caseName(outcome), 'Failed')
    assert.equal(workflow.failureCode(outcome), 'FILE_NOT_FOUND')
    assert.deepEqual(observations, [])
  } finally {
    cleanup()
  }
})

test('WHAT[requirement-grounding-006] a symlink replaced after the actual read preserves the read-time resolved path at observation', async () => {
  const workflow = await import('../../../dist/Repository/Programming/Js/WorkflowSurface.js')
  const { default: fs } = await import('node:fs')
  const { syncBuiltinESMExports } = await import('node:module')
  const { dir, cleanup } = sandbox()
  const root = realpathSync(dir)
  const oldPath = join(root, 'src', 'old.txt')
  const newPath = join(root, 'src', 'new.txt')
  const alias = join(root, 'src', 'alias.txt')
  writeFileSync(oldPath, 'same-bytes\n')
  writeFileSync(newPath, 'same-bytes\n')
  fs.symlinkSync(oldPath, alias)
  const originalRead = fs.readFileSync
  let replaced = false
  const observations = []
  try {
    fs.readFileSync = function(path, ...args) {
      const result = originalRead.call(this, path, ...args)
      if (!replaced && (path === oldPath || path === alias)) {
        replaced = true
        fs.unlinkSync(alias)
        fs.symlinkSync(newPath, alias)
      }
      return result
    }
    syncBuiltinESMExports()
    const outcome = await workflow.runObserved(root, 'Coder', 'en', "class Js extends JsProgram { async run() { const file = await this.file('src/alias.txt'); return file.text(); } }", 2000, Date.now() + 60000, 1 << 20, null,
      async (reads, effects) => {
        assert.equal(replaced, true, 'the real disk replacement precedes callback admission')
        assert.equal(realpathSync(alias), newPath)
        observations.push({ reads, effects })
      })
    assert.equal(workflow.caseName(outcome), 'Succeeded')
    assert.deepEqual(observations, [{ reads: [{ path: oldPath, resultBytes: 'same-bytes\n' }], effects: [] }])
    assert.equal(originalRead(newPath, 'utf8'), 'same-bytes\n', 'freshness equality cannot substitute path identity')
  } finally {
    fs.readFileSync = originalRead
    syncBuiltinESMExports()
    cleanup()
  }
})

test('WHAT[requirement-grounding-006] an actual file read through an outside-workspace symlink is denied without an observation', async () => {
  const workflow = await import('../../../dist/Repository/Programming/Js/WorkflowSurface.js')
  const { symlinkSync } = await import('node:fs')
  const { dir, cleanup } = sandbox()
  const foreign = mkdtempSync(join(tmpdir(), 'wanxiang-grounding-foreign-'))
  const outside = join(foreign, 'secret.txt')
  writeFileSync(outside, 'foreign-bytes\n')
  symlinkSync(outside, join(dir, 'src', 'foreign.txt'))
  const observations = []
  try {
    const outcome = await workflow.runObserved(dir, 'Coder', 'en', "class Js extends JsProgram { async run() { const file = await this.file('src/foreign.txt'); return file.text(); } }", 2000, Date.now() + 60000, 1 << 20, null,
      async (reads, effects) => observations.push({ reads, effects }))
    assert.equal(workflow.caseName(outcome), 'Failed')
    assert.equal(workflow.failureCode(outcome), 'PATH_DENIED')
    assert.deepEqual(observations, [])
    assert.equal(readFileSync(outside, 'utf8'), 'foreign-bytes\n')
  } finally {
    cleanup()
    rmSync(foreign, { recursive: true, force: true })
  }
})

integrationTest('WHAT[requirement-grounding-006] actual pinned native read output cannot honestly establish complete original file bytes', async t => {
  const runner = new URL('./support/run-native-read-canary.mjs', import.meta.url).pathname
  const run = args => {
    const env = { ...process.env }
    delete env.NODE_TEST_CONTEXT
    return spawnSync(process.execPath, [runner, ...args], { encoding: 'utf8', env, timeout: 45000 })
  }
  await t.test('WHAT[requirement-grounding-006] actual Host after-hook, SDK transcript, and next provider agree despite lossy native read formatting', () => {
    const child = run([])
    assert.equal(child.error, undefined, child.stderr)
    assert.equal(child.signal, null, child.stderr)
    assert.equal(child.status, 0, child.stderr)
    const line = child.stdout.split('\n').find(value => value.startsWith('native-read-canary: '))
    assert.ok(line, child.stdout)
    const receipt = JSON.parse(line.slice('native-read-canary: '.length))
    assert.equal(receipt.version, '1.18.29')
    assert.equal(receipt.cases, 7)
    for (const field of ['exactOutputAndMetadataOracle', 'afterHookMatchesSDK', 'nextProviderMatchesAfterHook', 'lineEndingCollision', 'longLineTruncationUnmarked']) assert.equal(receipt[field], true, field)
    assert.equal(receipt.rawFileBytesRecoverable, false)
    assert.equal(receipt.nativeByteCoverage, 'PartialFile')
  })
  await t.test('WHAT[requirement-grounding-006] an actual late provider callback failure prevents a successful native protocol receipt', () => {
    const child = run(['--late-provider-error'])
    assert.equal(child.error, undefined, child.stderr)
    assert.equal(child.signal, null, child.stderr)
    assert.equal(child.status, 1, child.stderr)
    const line = child.stderr.split('\n').find(value => value.startsWith('native-read-canary-failure: '))
    assert.ok(line, child.stderr)
    const failure = JSON.parse(line.slice('native-read-canary-failure: '.length))
    assert.equal(failure.requests, 2)
    assert.deepEqual(failure.failures, [])
    assert.deepEqual(failure.cleanupErrors, [], 'an expected provider refusal must not hide cleanup failure')
    assert.equal(failure.providerErrors.length, 1)
    assert.equal(failure.providerErrors[0].name, 'SyntaxError')
    assert.ok(failure.providerErrors[0].message.length > 0)
    assert.equal(child.stdout.includes('native-read-canary: '), false)
  })
})

test('WHAT[requirement-grounding-006] registered program grep keeps scan freshness without claiming explicit visible file contents', async () => {
  const { acceptAuthorityRoot, openIncumbency, withExecutablePlugin } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    mkdirSync(join(directory, 'requirements', 'alpha'), { recursive: true })
    writeFileSync(join(directory, 'requirements', 'alpha', 'WHAT.md'), 'GREPPED-MATERIAL\n')
    writeFileSync(join(directory, 'requirements', 'alpha', 'APPLIES-TO'), '/src/**\n')
    const sessionID = 'grounding-discovery-only'
    await acceptAuthorityRoot(runtime, sessionID, 'manager')
    await openIncumbency(runtime, sessionID)
    const args = { program: "class Js extends JsProgram { async run() { return await this.grep('GREPPED', 'requirements/alpha/WHAT.md'); } }" }
    const input = { tool: 'js-manager', sessionID, callID: 'grep-only' }
    await hooks['tool.execute.before'](input, { args })
    const output = await hooks.tool['js-manager'].execute(args, { sessionID, agent: 'manager' })
    assert.match(output, /text = "GREPPED"/)
    const events = join(directory, '.git', 'wanxiangshu', 'events')
    const persisted = readdirSync(events).map(name => readFileSync(join(events, name), 'utf8')).join('\n')
    assert.equal(persisted.includes('RequirementGroundingMaterialObserved'), false, 'grep must not mint a complete material observation')
    assert.equal(persisted.includes('RequirementGroundingReadObserved'), false, 'grep must not mint an explicit read observation')
    await hooks['tool.execute.after']({ ...input, args }, { title: 'grep', output, metadata: {} })
    const projected = { messages: [{ info: { id: 'grep-result', sessionID, role: 'assistant' }, parts: [{ type: 'tool', tool: input.tool, callID: input.callID, state: { status: 'completed', input: args, output } }] }] }
    await hooks['experimental.chat.messages.transform']({ sessionID }, projected)
    assert.equal(projected.messages[0].parts[0].state.output.includes('requirement_source_path ='), false, 'candidate discovery must not request grounding')
  })
})

test('WHAT[requirement-grounding-006] registered complete file reads preserve exact versions, deduplicate, replay, and reset after reanchor', async () => {
  const { acceptAuthorityRoot, openIncumbency, startPluginIncarnation } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
  const { dir, cleanup } = sandbox()
  let incarnation
  let opened
  const sessionID = 'grounding-explicit-complete'
  try {
    execFileSync('git', ['init', '--quiet', dir])
    incarnation = await startPluginIncarnation(dir)
    await incarnation.withRuntime(async runtime => {
      await acceptAuthorityRoot(runtime, sessionID, 'manager')
      await openIncumbency(runtime, sessionID)
      const args = { program: "class Js extends JsProgram { async run() { const file = await this.file('requirements/alpha/WHAT.md'); return file.text(); } }" }
      const execute = async callID => {
        const input = { tool: 'js-manager', sessionID, callID }
        await incarnation.hooks['tool.execute.before'](input, { args })
        const output = await incarnation.hooks.tool['js-manager'].execute(args, { sessionID, agent: 'manager' })
        await incarnation.hooks['tool.execute.after']({ ...input, args }, { title: 'read', output, metadata: {} })
        return output
      }
      const output = await execute('complete-first')
      assert.ok(output.includes('what-v1'))
      let observed = readFacts(join(dir, '.git'))
      assert.equal(observed.length, 1)
      assert.deepEqual(observed[0].SessionId, ['SessionId', sessionID])
      assert.equal(observed[0].Observation.Coverage, 'CompleteFile')
      assert.equal(observed[0].Observation.Digest, digest('requirements/alpha/WHAT.md', 'what-v1\n'))
      await execute('complete-repeated')
      assert.equal(readFacts(join(dir, '.git')).length, 1, 'the same actual returned version is not recorded twice')
      const projected = { messages: [{ info: { id: 'complete-result', sessionID, role: 'assistant' }, parts: [{ type: 'tool', tool: 'js-manager', callID: 'complete-first', state: { status: 'completed', input: args, output } }] }] }
      await incarnation.hooks['experimental.chat.messages.transform']({ sessionID }, projected)
      const enriched = projected.messages[0].parts[0].state.output
      assert.ok(enriched.includes('requirement_source_path = "requirements/alpha/HOW.md"'))
      assert.ok(enriched.includes('requirement_source_path = "requirements/alpha/WHY.md"'))
      assert.equal(enriched.includes('requirement_source_path = "requirements/alpha/WHAT.md"'), false)
      writeFileSync(join(dir, 'requirements', 'alpha', 'WHAT.md'), 'what-v2\n')
      assert.ok((await execute('complete-changed')).includes('what-v2'))
      observed = readFacts(join(dir, '.git'))
      assert.equal(observed.length, 2)
      assert.deepEqual(observed.map(fact => fact.Observation.Digest), [digest('requirements/alpha/WHAT.md', 'what-v1\n'), digest('requirements/alpha/WHAT.md', 'what-v2\n')])
    })
    await incarnation.hooks.dispose()
    incarnation = null
    opened = await host.createJournal(join(dir, '.git'))
    assert.equal(opened.ok, true, opened.error)
    const source = join(dir, 'src', 'main.fs')
    assert.equal((await host.requestPaths(opened.journal, dir, sessionID, [source])).needsGrounding, false, 'normal boot decodes the new read facts and restores full visibility')
    const reanchor = await host.appendContextReanchored(opened.journal, sessionID, 0n, 1n, 'read-reanchor')
    assert.equal(reanchor.ok, true, reanchor.error)
    assert.equal((await host.requestPaths(opened.journal, dir, sessionID, [source])).needsGrounding, true)
    const projected = await host.projectWithJournal(opened.journal, sessionID, terminalRead(source))
    assert.equal(projected.ok, true)
    assert.ok(projected.value.at(-1).parts[0].state.output.includes('what-v2'))
    assert.ok(projected.value.at(-1).parts[0].state.output.includes('requirement_source_path = "requirements/alpha/WHAT.md"'))
  } finally {
    await incarnation?.hooks.dispose()
    if (opened?.ok) host.disposeJournal(opened.journal)
    cleanup()
  }
})

test('WHAT[requirement-grounding-006] historical material observations retain their original decode and horizon semantics', async () => {
  const { dir, cleanup } = sandbox()
  let opened
  try {
    writeFileSync(join(dir, 'requirements', 'alpha', 'WHAT.md'), 'old\n')
    const legacy = readFileSync(new URL('./support/006-legacy-material-observation.ndjson', import.meta.url), 'utf8')
      .split('\n').filter(Boolean).map(JSON.parse)
    assert.equal(legacy[1].payload.Fact[1][1][0], 'RequirementGroundingMaterialObserved')
    legacy[1].payload.Fact[1][1][1].Observation.Workspace = realpathSync(dir)
    const frozen = legacy.map(JSON.stringify).join('\n') + '\n'
    const historicalEvents = legacy.map(event => {
      const decoded = eventCodec.decode(JSON.stringify(event) + '\n')
      assert.equal(decoded.ok, true, JSON.stringify(decoded.error))
      return decoded.event
    })
    const writerId = randomUUID()
    const resolveWriterFile = () => {
      const eventsDir = join(dir, 'wanxiangshu', 'events')
      const legacy = join(eventsDir, writerId + '.ndjson')
      if (existsSync(legacy)) return legacy
      return join(eventsDir, writerId + '-0000.ndjson')
    }
    const writerFile = resolveWriterFile()
    assert.equal(existsSync(writerFile), false, 'this process creates a new physical fixture writer')
    const activity = {
      id: 'd'.repeat(40),
      stream: 'grounding-fixture/activity',
      type: 'JobRequested',
      parents: [],
      payload: {},
      payloadRefs: [],
    }
    // The current fixture owns this fresh writer; it never resumes the sealed old producer.
    const writer = eventStore.create(dir, writerId)
    try {
      const appendedHistory = await eventStore.append(writer, historicalEvents)
      assert.equal(appendedHistory.ok, true, JSON.stringify(appendedHistory.error))
      assert.deepEqual(appendedHistory.cuts, [])
      assert.deepEqual(readFileSync(resolveWriterFile()), Buffer.from(frozen))
      for (const event of historicalEvents) assert.deepEqual(eventStore.read(writer, event.id), event)
      const appended = await eventStore.append(writer, [activity])
      assert.equal(appended.ok, true, JSON.stringify(appended.error))
      assert.deepEqual(appended.cuts, [])
      assert.deepEqual(eventStore.read(writer, activity.id), activity)
      assert.equal(eventStore.head(writer, activity.stream), activity.id)
    } finally {
      eventStore.dispose(writer)
    }
    const retained = readFileSync(resolveWriterFile())
    const prefix = Buffer.from(frozen)
    assert.deepEqual(retained.subarray(0, prefix.length), prefix, 'new activity must preserve every historical byte')
    const tail = retained.subarray(prefix.length).toString('utf8').trim().split('\n').map(JSON.parse)
    assert.equal(tail.length, 1)
    assert.equal(tail[0].event_id, activity.id)
    assert.equal(tail[0].event_type, activity.type)
    assert.deepEqual(retention.retainedWriterIdsAt(dir, Date.now()), [writerId], 'cold boot receives a nonempty retained historical fixture')
    opened = await host.createJournal(dir)
    assert.equal(opened.ok, true, opened.error)
    assert.equal(host.groundedIdentities(opened.journal, 'legacy').length, 1)
    await host.requestPaths(opened.journal, dir, 'legacy', [join(dir, 'src', 'main.fs')])
    const projected = await host.projectWithJournal(opened.journal, 'legacy', terminalRead(join(dir, 'src', 'main.fs')))
    assert.equal(projected.ok, true)
    const output = projected.value.at(-1).parts[0].state.output
    assert.equal(output.includes('requirement_source_path = "requirements/alpha/WHAT.md"'), false)
    assert.ok(output.includes('requirement_source_path = "requirements/alpha/WHY.md"'))
    assert.deepEqual(readFileSync(writerFile), retained, 'replay must not rewrite historical observations or new activity')
    assert.equal(readFacts(dir).length, 0, 'old observations are not reclassified as newly proven explicit reads')
  } finally {
    if (opened?.ok) host.disposeJournal(opened.journal)
    cleanup()
  }
})
