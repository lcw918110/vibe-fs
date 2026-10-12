import assert from 'node:assert/strict'
import { execFileSync, spawnSync } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { existsSync, mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as host from '../../../dist/OpenCode/Host/RequirementGroundingSurface.js'
import * as eventCodec from '../../../dist/Persistence/EventStore/CodecSurface.js'
import * as eventStore from '../../../dist/Persistence/EventStore/Surface.js'
import * as retention from '../../../dist/Persistence/EventStore/RetentionSurface.js'

const originalWhat = '\uFEFFwhat-v1\r\n\rbare CR\0中文 😀  \r\nlast  '
const originalCarrier = body => `${body}\n\nrequirement_source_path = "requirements/alpha/WHAT.md"\n`

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

test('WHAT[requirement-grounding-012] a legacy inline carrier is restored verbatim instead of being rendered with the new representation', async () => {
  const { dir, cleanup } = sandbox()
  let opened
  try {
    const receipt = JSON.parse(readFileSync(new URL('./support/012-legacy-cursor-receipt.json', import.meta.url), 'utf8'))
    assert.equal(existsSync(receipt.originalWorkspace), false, 'the actual old producer workspace has been removed')
    const historicalBytes = readFileSync(new URL('./support/012-legacy-cursor-occurrence.ndjson', import.meta.url))
    const historicalEvents = historicalBytes.toString('utf8').trimEnd().split('\n').map(line => {
      const decoded = eventCodec.decode(line + '\n')
      assert.equal(decoded.ok, true, JSON.stringify(decoded.error))
      return decoded.event
    })
    assert.equal(historicalEvents.length, 3)
    const writerId = randomUUID()
    const resolveWriterFile = () => {
      const eventsDir = join(dir, 'wanxiangshu', 'events')
      const legacy = join(eventsDir, writerId + '.ndjson')
      if (existsSync(legacy)) return legacy
      return join(eventsDir, writerId + '-0000.ndjson')
    }
    const writerFile = resolveWriterFile()
    assert.equal(existsSync(writerFile), false, 'this process creates a new physical fixture writer')
    const activity = { id: randomUUID(), stream: 'grounding-fixture/activity', type: 'JobRequested', parents: [], payload: {}, payloadRefs: [] }
    const writer = eventStore.create(dir, writerId)
    try {
      // The current fixture owns this fresh writer; it never resumes the sealed old producer.
      const appendedHistory = await eventStore.append(writer, historicalEvents)
      assert.equal(appendedHistory.ok, true, JSON.stringify(appendedHistory.error))
      assert.deepEqual(appendedHistory.cuts, [])
      assert.deepEqual(readFileSync(resolveWriterFile()), historicalBytes, 'the real append preserves the actual old canonical bytes')
      for (const event of historicalEvents) assert.deepEqual(eventStore.read(writer, event.id), event)
      const appendedActivity = await eventStore.append(writer, [activity])
      assert.equal(appendedActivity.ok, true, JSON.stringify(appendedActivity.error))
      assert.deepEqual(appendedActivity.cuts, [])
      assert.deepEqual(eventStore.read(writer, activity.id), activity)
      assert.equal(eventStore.head(writer, activity.stream), activity.id)
    } finally {
      eventStore.dispose(writer)
    }
    const retained = readFileSync(resolveWriterFile())
    assert.deepEqual(retained.subarray(0, historicalBytes.length), historicalBytes)
    assert.equal(retained.subarray(historicalBytes.length).toString('utf8'), eventCodec.encode(activity), 'only the current owner activity extends the old bytes')
    assert.deepEqual(retention.retainedWriterIdsAt(dir, Date.now()), [writerId], 'cold boot receives a nonempty retained historical fixture')
    rmSync(join(dir, 'requirements', 'alpha', 'WHAT.md'))
    opened = await host.createJournal(dir)
    assert.equal(opened.ok, true, opened.error)
    assert.equal(host.groundedIdentities(opened.journal, receipt.sessionID).length, 1)
    const replay = await host.projectWithJournal(opened.journal, receipt.sessionID, receipt.raw)
    assert.equal(replay.ok, true)
    assert.deepEqual(replay.value, receipt.projected, 'the actual old producer carrier survives the real codec and cold replay unchanged')
    assert.ok(replay.value[0].parts[0].state.output.includes(host.cursorSeparator + '# genuine-old-body'))
    assert.equal(replay.value[0].parts[0].state.output.includes(originalCarrier(receipt.originalBody)), false, 'old facts are not upgraded to the new carrier')
    assert.deepEqual(readFileSync(writerFile), retained, 'cold replay never modifies the closed fixture writer')
  } finally {
    if (opened) host.disposeJournal(opened.journal)
    cleanup()
  }
})

test('WHAT[requirement-grounding-012] reopening the durable journal replays exact messages and appends changed content only to a new terminal result', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const source = join(dir, 'src', 'main.fs')
    let opened = await host.createJournal(dir)
    await host.requestPaths(opened.journal, dir, 's-restart', [source])
    const first = await host.projectWithJournal(opened.journal, 's-restart', terminalRead(source))
    assert.equal(first.ok, true)
    // Universal cursor mode produces no synthetic read pairs; grounding rides the terminal tool result.
    assert.equal(first.value.some((m) => m.info?.source === host.source), false)
    const frozen = first.value.at(-1).parts[0].state.output
    assert.ok(frozen.includes('requirement_source_path = "requirements/alpha/WHAT.md"'))
    assert.ok(frozen.includes('what-v1'))
    host.disposeJournal(opened.journal)

    // Restart must replay the durable occurrence bytes without rereading the files:
    // WHAT.md changed on disk, but a bare re-projection keeps the frozen bytes.
    writeFileSync(join(dir, 'requirements', 'alpha', 'WHAT.md'), 'what-v2\n', 'utf8')
    opened = await host.createJournal(dir)
    const replay = await host.projectWithJournal(opened.journal, 's-restart', terminalRead(source))
    assert.equal(replay.ok, true)
    assert.equal(replay.value.at(-1).parts[0].state.output, frozen)
    assert.equal(replay.value.at(-1).parts[0].state.output.includes('what-v2'), false)
    assert.deepEqual(replay.value, first.value)

    // A fresh request grounds the changed digest by appending after the frozen prefix.
    const changed = await host.requestPaths(opened.journal, dir, 's-restart', [source])
    assert.equal(changed.needsGrounding, true)
    assert.equal(changed.requested, 1)
    const nextRead = terminalRead(source)
    nextRead[0].info.id = 'r2'
    nextRead[0].parts[0].callID = 'source-read-2'
    const transcript = [...terminalRead(source), ...nextRead]
    const appended = await host.projectWithJournal(opened.journal, 's-restart', transcript)
    assert.equal(appended.ok, true)
    const grown = appended.value.at(-1).parts[0].state.output
    assert.deepEqual(appended.value.slice(0, first.value.length), first.value)
    assert.ok(grown.includes('what-v2'))
    const repeated = await host.projectWithJournal(opened.journal, 's-restart', appended.value)
    assert.deepEqual(repeated, appended)
    host.disposeJournal(opened.journal)
  } finally { cleanup() }
})

test('WHAT[requirement-grounding-012] separate registered producer and restarted processes replay inline occurrence bytes at their original anchors', async t => {
  const { dir, cleanup } = sandbox()
  const processes = new Set()
  const receipts = {}
  const receiptPath = phase => join(dir, `${phase}-receipt.json`)
  const run = phase => {
    const env = { ...process.env }
    delete env.NODE_TEST_CONTEXT
    const child = spawnSync(process.execPath, [new URL('./support/012-process.fixture.mjs', import.meta.url).pathname, dir, phase, receiptPath(phase), receiptPath('produce'), receiptPath('append')], { encoding: 'utf8', env, timeout: 30000 })
    assert.equal(child.error, undefined, `${phase}: the independent process completed: ${child.stderr}`)
    assert.equal(child.signal, null)
    assert.equal(child.status, 0, child.stderr)
    const receipt = JSON.parse(readFileSync(receiptPath(phase), 'utf8'))
    assert.notEqual(receipt.pid, process.pid)
    assert.equal(processes.has(receipt.pid), false, 'each phase crosses an independent OS process')
    processes.add(receipt.pid)
    assert.throws(() => process.kill(receipt.pid, 0), error => error.code === 'ESRCH', 'the prior process has really exited')
    return receipt
  }
  try {
    execFileSync('git', ['init', '--quiet', dir])
    writeFileSync(join(dir, 'requirements', 'alpha', 'WHAT.md'), originalWhat)
    await t.test('WHAT[requirement-grounding-012] the registered producer anchors a real covered read and persists its inline occurrence', () => {
      const first = run('produce')
      assert.equal(first.occurrences, 1)
      assert.ok(first.projected.at(-1).parts[0].state.output.includes('what-v1'))
      assert.ok(first.projected.at(-1).parts[0].state.output.includes('requirement_source_path = "requirements/alpha/WHAT.md"'))
      assert.deepEqual(first.projected.map(message => message.info), first.raw.map(message => message.info))
      receipts.produce = first
    })
    assert.ok(receipts.produce, 'a failed producer cannot authorize later test phases')
    rmSync(join(dir, 'requirements', 'alpha', 'WHAT.md'))
    await t.test('WHAT[requirement-grounding-012] an independent normal boot replays exact prior messages while the original material is absent', () => {
      const replay = run('replay')
      assert.equal(replay.occurrences, 1)
      assert.deepEqual(replay.projected, receipts.produce.projected, 'deleted source cannot be reread to replace frozen bytes')
      assert.deepEqual(replay.raw, receipts.produce.raw)
    })
    writeFileSync(join(dir, 'requirements', 'alpha', 'WHAT.md'), 'what-v2\n')
    await t.test('WHAT[requirement-grounding-012] a fresh actual read in an independent process appends changed material after the frozen prefix', () => {
      const appended = run('append')
      assert.equal(appended.occurrences, 2)
      assert.deepEqual(appended.projected.slice(0, receipts.produce.projected.length), receipts.produce.projected)
      assert.ok(appended.projected.at(-1).parts[0].state.output.includes('what-v2'))
      assert.equal(appended.projected[0].parts[0].state.output.includes('what-v2'), false)
      assert.deepEqual(appended.repeated, appended.projected)
      receipts.append = appended
    })
    assert.ok(receipts.append, 'a failed fresh-read phase cannot authorize its replay')
    rmSync(join(dir, 'requirements', 'alpha'), { recursive: true })
    await t.test('WHAT[requirement-grounding-012] another normal process replays both saved inline versions with stable anchors and no source package', () => {
      const replay = run('replay-appended')
      assert.equal(replay.occurrences, 2)
      assert.deepEqual(replay.projected, receipts.append.projected)
      assert.deepEqual(replay.raw, receipts.append.raw)
    })
    const eventsDirectory = join(dir, '.git', 'wanxiangshu', 'events')
    const writer = readdirSync(eventsDirectory).map(name => join(eventsDirectory, name)).find(file => readFileSync(file, 'utf8').split('\n').some(line => line.includes('RequirementGroundingAnchored') && line.includes('what-v1')))
    assert.ok(writer, 'the actual producer wrote a typed grounding occurrence to its durable writer')
    const lines = readFileSync(writer, 'utf8').split('\n')
    const index = lines.findIndex(line => line.includes('RequirementGroundingAnchored') && line.includes('what-v1'))
    const event = JSON.parse(lines[index])
    assert.deepEqual(event.payload_refs, [], 'grounding occurrence result bytes are inline, without an invented grounding blob')
    const fact = event.payload.Fact
    assert.ok(Array.isArray(fact))
    assert.equal(fact.length, 2)
    assert.equal(fact[0], 'Agent')
    const agent = fact[1]
    assert.ok(Array.isArray(agent))
    assert.equal(agent.length, 2)
    assert.equal(agent[0], 'Host')
    const grounding = agent[1]
    assert.ok(Array.isArray(grounding))
    assert.equal(grounding.length, 2)
    assert.equal(grounding[0], 'RequirementGroundingAnchored')
    const occurrence = grounding[1].Occurrence
    for (const gap of [occurrence.CallGap, occurrence.ResultGap]) {
      assert.deepEqual(gap, ['After', ['TranscriptMessageAddress', receipts.produce.raw[0].info.id]])
    }
    const reads = occurrence.Reads
    assert.ok(Array.isArray(reads))
    assert.ok(reads.length > 0)
    assert.equal(new Set(reads.map(read => read.CallId[1])).size, reads.length)
    for (const read of reads) {
      assert.equal(typeof read.CallId[1], 'string')
      assert.ok(read.CallId[1])
      assert.deepEqual(read.CallId, ['ToolCallId', read.CallId[1]])
      assert.deepEqual(JSON.parse(read.ArgsJson), { filePath: read.Path })
    }
    const what = reads.find(read => read.Path === 'requirements/alpha/WHAT.md')
    assert.ok(what)
    assert.equal(what.ResultBytes, originalWhat)
    assert.equal(what.CursorResultBytes, originalCarrier(originalWhat), 'cold replay carries the entire independently specified original body and source footer')
    assert.ok(receipts.produce.projected[0].parts[0].state.output.includes(what.CursorResultBytes))
    lines[index] = '{BROKEN-GROUNDING-OCCURRENCE'
    writeFileSync(writer, lines.join('\n'))
    assert.throws(() => JSON.parse(readFileSync(writer, 'utf8').split('\n')[index]), SyntaxError)
    await t.test('WHAT[requirement-grounding-012] corruption of the actual persisted occurrence prevents a fresh normal boot from reporting replay success', () => {
      const rejected = run('corrupt')
      assert.equal(rejected.rejected, true)
      assert.equal(rejected.failure, 'MalformedEnvelope')
      assert.match(rejected.error, /^local EventStore boot failed: local event history read failed: MalformedEnvelope/)
      assert.equal(rejected.projected, undefined)
    })
  } finally { cleanup() }
})
