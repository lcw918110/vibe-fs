import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as grounding from '../../../dist/OpenCode/Host/RequirementGroundingSurface.js'
import * as pair from '../../../dist/OpenCode/Host/PairProgrammingThoughtSurface.js'
import * as trace from '../../../dist/Context/Trace/SemanticTraceSurface.js'
import { acceptAuthorityRoot, openIncumbency, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import './support/007-provider-serialization.integration.mjs'

// 语言阶梯最高优先级显式设为英文：本文件的注册组合证明逐字节断言 marker 文本与
// grounding 的顺序，不能随调用者 shell 的语言设置漂移。插件实例化时才解析该值。
process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wanxiang-grounding-opencode-'))
  mkdirSync(join(dir, 'requirements', 'alpha'), { recursive: true })
  mkdirSync(join(dir, 'src'), { recursive: true })
  writeFileSync(join(dir, 'requirements', 'alpha', 'WHAT.md'), 'ground truth\n', 'utf8')
  writeFileSync(join(dir, 'requirements', 'alpha', 'APPLIES-TO'), '/src/**\n', 'utf8')
  writeFileSync(join(dir, 'src', 'main.fs'), 'before\n', 'utf8')
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}

const toolBatch = (providerID, path) => [
  { info: { id: 'c1', role: 'assistant', providerID }, parts: [{ type: 'tool', tool: 'read', callID: 'source', state: { status: 'pending', input: { filePath: path }, time: { start: 0 } } }] },
  { info: { id: 'r1', role: 'assistant', providerID }, parts: [{ type: 'tool', tool: 'read', callID: 'source', state: { status: 'completed', input: { filePath: path }, output: 'before\n', time: { start: 0, end: 0 } } }] },
]

test('WHAT[requirement-grounding-007] actual pair and grounding transforms append result-only bytes for each tested provider without synthetic calls', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const sourcePath = join(dir, 'src', 'main.fs')
    assert.equal(grounding.cursorSeparator, '\0\uFEFF')
    for (const provider of ['anthropic', 'cursor', 'openai']) {
      const opened = await grounding.createJournal(dir)
      try {
        await grounding.requestPaths(opened.journal, dir, provider, [sourcePath])
        const input = toolBatch(provider, sourcePath)
        const paired = await pair.tryInject(provider, pair.text, input)
        assert.equal(paired.ok, true)
        const result = await grounding.projectWithJournal(opened.journal, provider, paired.value)
        assert.equal(result.ok, true)
        assert.equal(result.value.length, input.length)
        assert.deepEqual(result.value.map((message) => message.info), input.map((message) => message.info))
        assert.deepEqual(result.value[0], input[0], 'pending tool call remains untouched')
        const terminal = result.value.at(-1).parts[0].state.output
        assert.ok(terminal.startsWith('before\n'))
        assert.ok(terminal.includes('\0\uFEFFground truth'))
        assert.ok(terminal.includes('requirement_source_path = "requirements/alpha/WHAT.md"'))
        assert.equal(result.value.some((message) => message.info?.source === grounding.source), false)
        const pairAt = terminal.indexOf(pair.text.trim())
        if (paired.value.at(-1).parts[0].state.output.includes(pair.text.trim())) {
          assert.ok(pairAt >= 0)
          assert.ok(terminal.indexOf('requirement_source_path') > pairAt)
        }
        assert.deepEqual(input, toolBatch(provider, sourcePath), 'input is not mutated')
      } finally { grounding.disposeJournal(opened.journal) }
    }
  } finally { cleanup() }
})

test('WHAT[requirement-grounding-007] candidate discovery tools do not ground, while an explicit read does', async () => {
  const { dir, cleanup } = sandbox()
  const opened = await grounding.createJournal(dir)
  try {
    const sourcePath = join(dir, 'src', 'main.fs')
    for (const tool of ['grep', 'glob', 'list']) {
      const result = await grounding.observationDecision(opened.journal, dir, 'candidate', tool, { filePath: sourcePath, path: join(dir, 'src') }, `${sourcePath}:1:before\n`)
      assert.equal(result.ok, true)
      assert.equal(result.needsGrounding, false)
      assert.equal(result.requested, 0)
      assert.deepEqual(result.packages, [])
    }
    const read = await grounding.observationDecision(opened.journal, dir, 'candidate', 'read', { filePath: sourcePath }, 'before\n')
    assert.equal(read.ok, true)
    assert.equal(read.needsGrounding, true)
    assert.equal(read.requested, 1)
    assert.deepEqual(read.packages, ['alpha'])
  } finally {
    grounding.disposeJournal(opened.journal)
    cleanup()
  }
})

test('WHAT[requirement-grounding-007] original Markdown bytes survive the actual result suffix unchanged', async () => {
  const { dir, cleanup } = sandbox()
  const opened = await grounding.createJournal(dir)
  try {
    const content = 'first\r\n\r\nsecond  \r\n'
    writeFileSync(join(dir, 'requirements', 'alpha', 'WHAT.md'), content)
    const path = join(dir, 'src', 'main.fs')
    await grounding.requestPaths(opened.journal, dir, 'raw-bytes', [path])
    const result = await grounding.projectWithJournal(opened.journal, 'raw-bytes', toolBatch('anthropic', path))
    assert.equal(result.ok, true)
    assert.ok(result.value.at(-1).parts[0].state.output.includes(content))
  } finally {
    grounding.disposeJournal(opened.journal)
    cleanup()
  }
})

const originalMaterials = [
  ['crlf-blank-trailing', 'first\r\n\r\nsecond  \r\n'],
  ['bare-cr', 'first\rsecond\r'],
  ['empty', ''],
  ['no-final-lf', 'first\nlast  '],
  ['unicode', '中文 😀 e\u0301\n'],
  ['nul-bom', '\uFEFFhead\0middle\0\uFEFFtail'],
  ['marker-text', 'requirement_source_path = "requirements/alpha/WHAT.md"\n# Pair Programming: Language Anchor\n<system>literal</system>\0\uFEFFnot-a-presentation\n'],
]

const originalMaterialCarrier = body => `${body}\n\nrequirement_source_path = "requirements/alpha/WHAT.md"\n`

const sessionHostFacts = (directory, sessionID) => {
  const events = join(directory, '.git', 'wanxiangshu', 'events')
  return readdirSync(events).flatMap(name => readFileSync(join(events, name), 'utf8').split('\n').filter(Boolean).map(JSON.parse))
    .flatMap(event => {
      const fact = event.payload?.Fact
      const host = fact?.[0] === 'Agent' && fact[1]?.[0] === 'Host' ? fact[1][1] : null
      return host?.[1]?.SessionId?.[1] === sessionID ? [host] : []
    })
}

const withPresentationJournals = async (directory, body) => {
  const pairJournal = await pair.createJournal(join(directory, '.git'))
  assert.equal(pairJournal.ok, true)
  const groundingJournal = await grounding.createJournal(join(directory, '.git'))
  assert.equal(groundingJournal.ok, true)
  try { return await body(pairJournal.journal, groundingJournal.journal) }
  finally {
    grounding.disposeJournal(groundingJournal.journal)
    pair.disposeJournal(pairJournal.journal)
  }
}

for (const providerID of ['anthropic', 'cursor', 'openai']) {
  test(`WHAT[requirement-grounding-007] registered original results retain complete saved presentation text (${providerID})`, async () => {
    await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
      mkdirSync(join(directory, 'requirements', 'alpha'), { recursive: true })
      mkdirSync(join(directory, 'src'), { recursive: true })
      const sourcePath = join(directory, 'src', 'main.fs')
      const materialPath = join(directory, 'requirements', 'alpha', 'WHAT.md')
      writeFileSync(join(directory, 'requirements', 'alpha', 'APPLIES-TO'), '/src/**\n')
      writeFileSync(materialPath, 'seed material\n')
      const sessionID = `literal-presentation-${providerID}`
      await openIncumbency(runtime, sessionID)
      const produce = async (ordinal, output) => {
        writeFileSync(sourcePath, output)
        const args = { filePath: sourcePath }
        const input = { tool: 'read', sessionID, callID: `call-${ordinal}` }
        await hooks['tool.execute.before'](input, { args })
        const actual = readFileSync(sourcePath, 'utf8')
        await hooks['tool.execute.after']({ ...input, args }, { title: 'read', output: actual, metadata: {} })
        return { info: { id: `result-${ordinal}`, role: 'assistant', sessionID, providerID },
          parts: [{ id: `part-${ordinal}`, type: 'tool', tool: 'read', callID: input.callID,
            state: { status: 'completed', input: args, output: actual, time: { start: 0, end: 1 } } }] }
      }
      const seed = await produce('seed', 'seed source\n')
      await hooks['experimental.chat.messages.transform']({}, { messages: [structuredClone(seed)] })
      const facts = sessionHostFacts(directory, sessionID)
      const guidance = facts.find(fact => fact[0] === 'PairProgrammingGuidelineAnchored')[1].MarkerText
      const oldCarrier = facts.find(fact => fact[0] === 'RequirementGroundingAnchored')[1].Occurrence.Reads[0].CursorResultBytes
      const literal = grounding.cursorSeparator + guidance + grounding.cursorSeparator + oldCarrier
      const output = `raw-start\r\n${literal}\rMID\0\uFEFF中文 😀  \r\n${literal}`
      writeFileSync(materialPath, `${guidance}\r\nmaterial-start\r\n${literal}\rMID\n${literal}`)
      const next = await produce('literal', output)
      const raw = [seed, next]
      const projected = { messages: structuredClone(raw) }
      await hooks['experimental.chat.messages.transform']({}, projected)
      assert.ok(projected.messages.at(-1).parts[0].state.output.startsWith(output), 'the exact physical Host result must retain all complete known payloads inside and at its end')
      const saved = sessionHostFacts(directory, sessionID).filter(fact => fact[0] === 'RequirementGroundingAnchored').at(-1)[1].Occurrence.Reads[0]
      assert.equal(saved.ResultBytes, readFileSync(materialPath, 'utf8'))
      assert.equal(saved.CursorResultBytes, originalMaterialCarrier(saved.ResultBytes), 'complete prior payloads are opaque bytes inside the new material carrier')
      const canonical = await trace.currentProjection(runtime.journal, sessionID)
      assert.deepEqual(canonical.messages.flatMap(message => message.parts).filter(part => part.kind === 'tool-result'),
        [{ kind: 'tool-result', result: seed.parts[0].state.output }, { kind: 'tool-result', result: output }])
      const repeated = { messages: structuredClone(projected.messages) }
      await hooks['experimental.chat.messages.transform']({}, repeated)
      assert.deepEqual(repeated.messages, projected.messages, 'known payloads are data in original results and material bodies during projected replay')
      assert.deepEqual(await trace.currentProjection(runtime.journal, sessionID), canonical)
      for (const identity of ['missing-part', 'wrong-part', 'wrong-message', 'wrong-call']) {
        const unbound = structuredClone(next)
        if (identity === 'missing-part') delete unbound.parts[0].id
        if (identity === 'wrong-part') unbound.parts[0].id = 'part-seed'
        if (identity === 'wrong-message') unbound.info.id = 'unrecorded-result'
        if (identity === 'wrong-call') unbound.parts[0].callID = 'call-seed'
        const unboundMessages = identity === 'wrong-message' ? [...raw, unbound] : [seed, unbound]
        await withPresentationJournals(directory, async (pairJournal, groundingJournal) => {
          const paired = await pair.tryInjectWithJournal(pairJournal, sessionID, pair.text, unboundMessages)
          assert.equal(paired.ok, true, `${identity}: public pair projection completed`)
          assert.ok(paired.value.at(-1).parts[0].state.output.startsWith(output), `${identity}: another physical result cannot lend its captured original prefix`)
          const grounded = await grounding.projectWithJournal(groundingJournal, sessionID, paired.value)
          assert.equal(grounded.ok, true, `${identity}: public grounding projection completed`)
          assert.ok(grounded.value.at(-1).parts[0].state.output.startsWith(output), `${identity}: grounding never guesses where original bytes end`)
        })
      }
      const prefixBody = `${oldCarrier}new body\r\n\0\uFEFFtail  `
      writeFileSync(materialPath, prefixBody)
      const third = await produce('carrier-prefix', 'third actual source\r\n')
      const extended = { messages: structuredClone([...raw, third]) }
      await hooks['experimental.chat.messages.transform']({}, extended)
      assert.ok(extended.messages.at(-1).parts[0].state.output.endsWith(grounding.cursorSeparator + originalMaterialCarrier(prefixBody)))
      const extendedReplay = { messages: structuredClone(extended.messages) }
      await hooks['experimental.chat.messages.transform']({}, extendedReplay)
      assert.deepEqual(extendedReplay.messages, extended.messages, 'an older complete token that prefixes a new material carrier cannot split the new carrier')
    })
  })
}

test('WHAT[requirement-grounding-007] missing physical part IDs bind only to exact captured missing-part results', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    mkdirSync(join(directory, 'requirements', 'alpha'), { recursive: true })
    mkdirSync(join(directory, 'src'), { recursive: true })
    const sourcePath = join(directory, 'src', 'main.fs')
    const materialPath = join(directory, 'requirements', 'alpha', 'WHAT.md')
    const sessionID = 'captured-missing-part'
    writeFileSync(join(directory, 'requirements', 'alpha', 'APPLIES-TO'), '/src/**\n')
    writeFileSync(materialPath, 'first material\r\n')
    await openIncumbency(runtime, sessionID)
    const produce = async (ordinal, body) => {
      writeFileSync(sourcePath, body)
      const args = { filePath: sourcePath }
      const input = { tool: 'read', sessionID, callID: `missing-part-call-${ordinal}` }
      await hooks['tool.execute.before'](input, { args })
      await hooks['tool.execute.after']({ ...input, args }, { title: 'read', output: body, metadata: {} })
      return { info: { id: `missing-part-result-${ordinal}`, role: 'assistant', sessionID, providerID: 'anthropic' },
        parts: [{ type: 'tool', tool: 'read', callID: input.callID,
          state: { status: 'completed', input: args, output: body, time: { start: 0, end: 1 } } }] }
    }
    const first = await produce('first', 'first physical result\r\n')
    await hooks['experimental.chat.messages.transform']({}, { messages: [structuredClone(first)] })
    const firstFacts = sessionHostFacts(directory, sessionID)
    const guidance = firstFacts.find(fact => fact[0] === 'PairProgrammingGuidelineAnchored')[1].MarkerText
    const firstCarrier = firstFacts.find(fact => fact[0] === 'RequirementGroundingAnchored')[1].Occurrence.Reads[0].CursorResultBytes
    const literal = grounding.cursorSeparator + guidance + grounding.cursorSeparator + firstCarrier
    const body = `original missing-part body\r\n${literal}\0中文 😀\r${literal}`
    const materialBody = 'next missing-part material\r\n'
    writeFileSync(materialPath, materialBody)
    const next = await produce('next', body)
    const projected = { messages: structuredClone([first, next]) }
    await hooks['experimental.chat.messages.transform']({}, projected)
    const captured = trace.orderedSemanticParts(trace.snapshot(runtime.journal, sessionID)).filter(part => part.kind === 'tool_result')
    assert.deepEqual(captured.map(part => part.hostToolPartId), [null, null], 'the real capture has no physical part IDs to borrow or fabricate')
    assert.deepEqual(captured.map(part => part.toolCallId), [first.parts[0].callID, next.parts[0].callID])
    const nextGuidance = sessionHostFacts(directory, sessionID).filter(fact => fact[0] === 'PairProgrammingGuidelineAnchored').at(-1)[1].MarkerText
    assert.equal(projected.messages.at(-1).parts[0].state.output,
      body + grounding.cursorSeparator + nextGuidance + grounding.cursorSeparator + originalMaterialCarrier(materialBody))
    const replay = { messages: structuredClone(projected.messages) }
    await hooks['experimental.chat.messages.transform']({}, replay)
    assert.deepEqual(replay.messages, projected.messages, 'captured missing-part identity replays both owners once without stripping literal material')
    const thirdReplay = { messages: structuredClone(replay.messages) }
    await hooks['experimental.chat.messages.transform']({}, thirdReplay)
    assert.deepEqual(thirdReplay.messages, projected.messages, 'three projection passes retain the same whole presentation')
    const canonicalResults = (await trace.currentProjection(runtime.journal, sessionID)).messages.flatMap(message => message.parts).filter(part => part.kind === 'tool-result')
    assert.deepEqual(canonicalResults,
      [{ kind: 'tool-result', result: first.parts[0].state.output }, { kind: 'tool-result', result: body }],
      'three passes capture each actual result once and do not append request-local presentation')
    assert.equal(trace.orderedSemanticParts(trace.snapshot(runtime.journal, sessionID)).filter(part => part.kind === 'tool_result').length, 2)
    for (const identity of ['present-part', 'wrong-call', 'wrong-message']) {
      const unbound = structuredClone(next)
      if (identity === 'present-part') unbound.parts[0].id = 'invented-physical-part'
      if (identity === 'wrong-call') unbound.parts[0].callID = first.parts[0].callID
      if (identity === 'wrong-message') unbound.info.id = 'unrecorded-missing-part-message'
      await withPresentationJournals(directory, async (pairJournal, groundingJournal) => {
        const paired = await pair.tryInjectWithJournal(pairJournal, sessionID, pair.text, [first, unbound])
        assert.equal(paired.ok, true)
        assert.ok(paired.value.at(-1).parts[0].state.output.startsWith(body), `${identity}: missing-part evidence must not lend an original body to another identity`)
        const grounded = await grounding.projectWithJournal(groundingJournal, sessionID, paired.value)
        assert.equal(grounded.ok, true)
        assert.ok(grounded.value.at(-1).parts[0].state.output.startsWith(body), `${identity}: Grounding keeps the unbound physical result intact`)
      })
    }
    assert.deepEqual((await trace.currentProjection(runtime.journal, sessionID)).messages.flatMap(message => message.parts).filter(part => part.kind === 'tool-result'),
      [{ kind: 'tool-result', result: first.parts[0].state.output }, { kind: 'tool-result', result: body }])
  })
})

test('WHAT[requirement-grounding-007] ambiguous captured missing-part call identity is rejected by both presentation owners', async () => {
  await withExecutablePlugin(async (_hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ambiguous-missing-part'
    const raw = [{ info: { id: 'ambiguous-missing-part-result', role: 'assistant', sessionID, providerID: 'anthropic' },
      parts: ['first original', 'second original'].map(output => ({ type: 'tool', tool: 'read', callID: 'ambiguous-call',
        state: { status: 'completed', input: { filePath: 'README.md' }, output, time: { start: 0, end: 1 } } })) }]
    const captured = await trace.captureObservedMessages(runtime.journal, sessionID,
      raw.map(message => ({ hostMessageId: message.info.id, message })))
    assert.equal(captured.ok, true)
    const evidence = trace.orderedSemanticParts(captured.projection).filter(part => part.kind === 'tool_result')
    assert.equal(evidence.length, 2)
    assert.deepEqual(evidence.map(part => [part.hostToolPartId, part.toolCallId]), [[null, 'ambiguous-call'], [null, 'ambiguous-call']])
    await withPresentationJournals(directory, async (pairJournal, groundingJournal) => {
      const paired = await pair.tryInjectWithJournal(pairJournal, sessionID, pair.text, raw)
      assert.equal(paired.ok, false, 'a missing physical part ID does not authorize choosing between two captured results')
      assert.match(paired.error, /original Host tool result identity is ambiguous/)
      const grounded = await grounding.projectWithJournal(groundingJournal, sessionID, raw)
      assert.equal(grounded.ok, false)
      assert.match(grounded.error, /original Host tool result identity is ambiguous/)
    })
    assert.deepEqual(raw[0].parts.map(part => part.state.output), ['first original', 'second original'])
  })
})

test('WHAT[requirement-grounding-007] same-anchor versions cannot turn a complete guidance and read combination into one later material', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    mkdirSync(join(directory, 'requirements', 'alpha'), { recursive: true })
    mkdirSync(join(directory, 'src'), { recursive: true })
    const sourcePath = join(directory, 'src', 'main.fs')
    const materialPath = join(directory, 'requirements', 'alpha', 'WHAT.md')
    const firstBody = 'same-anchor material A\r\n'
    const output = 'actual source\r\n'
    const sessionID = 'same-anchor-combination'
    writeFileSync(sourcePath, output)
    writeFileSync(materialPath, firstBody)
    writeFileSync(join(directory, 'requirements', 'alpha', 'APPLIES-TO'), '/src/**\n')
    await openIncumbency(runtime, sessionID)
    const args = { filePath: sourcePath }
    const input = { tool: 'read', sessionID, callID: 'same-anchor-call' }
    await hooks['tool.execute.before'](input, { args })
    await hooks['tool.execute.after']({ ...input, args }, { title: 'read', output, metadata: {} })
    const raw = [{ info: { id: 'same-anchor-result', role: 'assistant', sessionID, providerID: 'anthropic' },
      parts: [{ id: 'same-anchor-part', type: 'tool', tool: 'read', callID: input.callID,
        state: { status: 'completed', input: args, output, time: { start: 0, end: 1 } } }] }]
    const first = { messages: structuredClone(raw) }
    await hooks['experimental.chat.messages.transform']({}, first)
    const guidance = sessionHostFacts(directory, sessionID).find(fact => fact[0] === 'PairProgrammingGuidelineAnchored')[1].MarkerText
    const firstCarrier = originalMaterialCarrier(firstBody)
    const firstExpected = output + grounding.cursorSeparator + guidance + grounding.cursorSeparator + firstCarrier
    assert.equal(first.messages[0].parts[0].state.output, firstExpected, 'the first projection has an independent complete-payload oracle')
    const secondBody = guidance + grounding.cursorSeparator + firstBody
    const secondCarrier = originalMaterialCarrier(secondBody)
    assert.equal(secondCarrier, guidance + grounding.cursorSeparator + firstCarrier, 'the next material is deliberately identical to the earlier two-owner combination')
    writeFileSync(materialPath, secondBody)
    const second = await withPresentationJournals(directory, async (_pairJournal, groundingJournal) => {
      const requested = await grounding.requestPaths(groundingJournal, directory, sessionID, [sourcePath])
      assert.equal(requested.ok, true)
      return grounding.projectWithJournal(groundingJournal, sessionID, first.messages)
    })
    assert.equal(second.ok, true)
    const occurrences = sessionHostFacts(directory, sessionID).filter(fact => fact[0] === 'RequirementGroundingAnchored').map(fact => fact[1].Occurrence)
      .sort((left, right) => left.Ordinal - right.Ordinal)
    assert.equal(occurrences.length, 2)
    assert.deepEqual(occurrences.map(occurrence => occurrence.ResultGap), [occurrences[0].ResultGap, occurrences[0].ResultGap], 'both content versions belong to this exact durable anchor')
    assert.deepEqual(occurrences.map(occurrence => occurrence.Reads[0].ResultBytes), [firstBody, secondBody])
    const fullExpected = firstExpected + grounding.cursorSeparator + secondCarrier
    assert.equal(second.value[0].parts[0].state.output, fullExpected)
    await withPresentationJournals(directory, async (pairJournal, groundingJournal) => {
      for (const previous of [first.messages, second.value, raw]) {
        const paired = await pair.tryInjectWithJournal(pairJournal, sessionID, guidance, structuredClone(previous))
        assert.equal(paired.ok, true)
        const priorReads = previous === raw ? '' : previous === first.messages ? grounding.cursorSeparator + firstCarrier : grounding.cursorSeparator + firstCarrier + grounding.cursorSeparator + secondCarrier
        assert.equal(paired.value[0].parts[0].state.output, output + grounding.cursorSeparator + guidance + priorReads, 'Pair removes only its typed presentation and restores guidance before every intact read')
        const grounded = await grounding.projectWithJournal(groundingJournal, sessionID, paired.value)
        assert.equal(grounded.ok, true)
        assert.equal(grounded.value[0].parts[0].state.output, fullExpected, 'Grounding removes complete accumulated read occurrences without penetrating the later material body')
      }
    })
    assert.deepEqual((await trace.currentProjection(runtime.journal, sessionID)).messages.flatMap(message => message.parts).filter(part => part.kind === 'tool-result'),
      [{ kind: 'tool-result', result: output }])
    await withPresentationJournals(directory, async (pairJournal) => {
      const reanchored = await pair.appendContextReanchored(pairJournal, sessionID, 0n, 1n, 'same-anchor-reanchor')
      assert.equal(reanchored.ok, true)
    })
    const visible = await withPresentationJournals(directory, async (pairJournal, groundingJournal) => {
      const paired = await pair.tryInjectWithJournal(pairJournal, sessionID, guidance, structuredClone(raw))
      assert.equal(paired.ok, true)
      const requested = await grounding.requestPaths(groundingJournal, directory, sessionID, [sourcePath])
      assert.equal(requested.ok, true)
      return grounding.projectWithJournal(groundingJournal, sessionID, paired.value)
    })
    assert.equal(visible.ok, true)
    const ambiguousExpected = output + grounding.cursorSeparator + guidance + grounding.cursorSeparator + secondCarrier
    assert.equal(visible.value[0].parts[0].state.output, ambiguousExpected)
    assert.equal(ambiguousExpected, output + grounding.cursorSeparator + guidance + grounding.cursorSeparator + guidance + grounding.cursorSeparator + firstCarrier,
      'the current one-read carrier also matches the complete historical two-guidance and first-read presentation')
    const beforeAmbiguousReplay = structuredClone(visible.value)
    await withPresentationJournals(directory, async (pairJournal, groundingJournal) => {
      const paired = await pair.tryInjectWithJournal(pairJournal, sessionID, guidance, visible.value)
      assert.equal(paired.ok, false, 'different valid owner boundaries must fail instead of trusting candidate order')
      assert.match(paired.error, /presentation ownership is ambiguous/)
      const grounded = await grounding.projectWithJournal(groundingJournal, sessionID, visible.value)
      assert.equal(grounded.ok, false, 'both owners reject the same evidence ambiguity')
      assert.match(grounded.error, /presentation ownership is ambiguous/)
    })
    assert.deepEqual(visible.value, beforeAmbiguousReplay, 'failed projection cannot mutate the ambiguous input')
  })
})

for (const damage of ['missing', 'changed']) {
  test(`WHAT[requirement-grounding-007] captured original result ${damage} blob fails both presentation owners`, async () => {
    await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
      mkdirSync(join(directory, 'requirements', 'alpha'), { recursive: true })
      mkdirSync(join(directory, 'src'), { recursive: true })
      const sourcePath = join(directory, 'src', 'main.fs')
      writeFileSync(sourcePath, 'physical original\r\n')
      writeFileSync(join(directory, 'requirements', 'alpha', 'WHAT.md'), 'material\r\n')
      writeFileSync(join(directory, 'requirements', 'alpha', 'APPLIES-TO'), '/src/**\n')
      const sessionID = `damaged-original-${damage}`
      await openIncumbency(runtime, sessionID)
      const args = { filePath: sourcePath }
      const input = { tool: 'read', sessionID, callID: `call-${damage}` }
      const output = readFileSync(sourcePath, 'utf8')
      await hooks['tool.execute.before'](input, { args })
      await hooks['tool.execute.after']({ ...input, args }, { title: 'read', output, metadata: {} })
      const raw = [{ info: { id: `result-${damage}`, role: 'assistant', sessionID, providerID: 'anthropic' },
        parts: [{ id: `part-${damage}`, type: 'tool', tool: 'read', callID: input.callID,
          state: { status: 'completed', input: args, output, time: { start: 0, end: 1 } } }] }]
      const projected = { messages: structuredClone(raw) }
      await hooks['experimental.chat.messages.transform']({}, projected)
      const captured = trace.orderedSemanticParts(trace.snapshot(runtime.journal, sessionID)).find(part => part.kind === 'tool_result')
      assert.equal(captured.hostToolPartId, `part-${damage}`)
      // durable-events-012: the ndjson line is the only payload carrier. Damage
      // means: the content address no longer matches the bytes it names.
      const eventsDir = join(directory, '.git', 'wanxiangshu', 'events')
      const writerFile = readdirSync(eventsDir).find((name) => name.endsWith('.ndjson'))
      const writerPath = join(eventsDir, writerFile)
      const lines = readFileSync(writerPath, 'utf8').trimEnd().split('\n')
      const handle = captured.textRef.slice('blobs/'.length)
      const index = lines.findIndex((line) => {
        const row = JSON.parse(line)
        return row.payload_refs?.includes(handle)
      })
      assert.ok(index >= 0, 'the captured result payload is embedded in an event line')
      const damaged = JSON.parse(lines[index])
      assert.equal(Buffer.from(damaged.payloads[handle], 'base64').toString('utf8'), output,
        'the fixture damages the actual captured result payload')
      if (damage === 'missing') {
        // No bytes remain under the reference: the line is no longer self-contained.
        delete damaged.payloads[handle]
      } else {
        // Bytes remain but contradict the address they are stored under.
        damaged.payloads[handle] = Buffer.from('changed payload\r\n', 'utf8').toString('base64')
      }
      lines[index] = JSON.stringify(damaged)
      writeFileSync(writerPath, lines.join('\n') + '\n')
      // Under the inline carrier the damaged line is not self-contained, so it is
      // a physical storage fault the canonical Integrator refuses while opening
      // history. Neither presentation owner can therefore be handed the evidence,
      // and no boot can smuggle the damaged line into a fold.
      const expected =
        damage === 'missing' ? /missing durable inline payload/ : /durable inline payload digest mismatch/
      for (const [owner, open] of [
        ['pair', () => pair.createJournal(join(directory, '.git'))],
        ['grounding', () => grounding.createJournal(join(directory, '.git'))],
      ]) {
        await assert.rejects(
          open,
          error => expected.test(error.message),
          `${owner} must refuse the ${damage} durable inline payload instead of presenting it`,
        )
      }
    })
  })
}

for (const providerID of ['anthropic', 'cursor', 'openai']) {
  test(`WHAT[requirement-grounding-007] registered native hook and actual JS read preserve original material bytes for ${providerID}`, async t => {
    await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
      mkdirSync(join(directory, 'requirements', 'alpha'), { recursive: true })
      mkdirSync(join(directory, 'src'), { recursive: true })
      const sourcePath = join(directory, 'src', 'main.fs')
      writeFileSync(sourcePath, 'SOURCE\r\n')
      writeFileSync(join(directory, 'requirements', 'alpha', 'APPLIES-TO'), '/src/**\n')
      for (const [name, body] of originalMaterials) {
        writeFileSync(join(directory, 'requirements', 'alpha', 'WHAT.md'), body)
        for (const tool of ['read', 'js-manager']) {
          await t.test(`WHAT[requirement-grounding-007] ${providerID} ${tool} ${name} exact content carrier and canonical history`, async () => {
            const sessionID = `original-${providerID}-${tool}-${name}`
            if (tool === 'js-manager') await acceptAuthorityRoot(runtime, sessionID, 'manager')
            await openIncumbency(runtime, sessionID)
            const args = tool === 'read' ? { filePath: sourcePath }
              : { program: "class Js extends JsProgram { async run() { const file = await this.file('src/main.fs'); return file.text(); } }" }
            const input = { tool, sessionID, callID: `call-${sessionID}` }
            await hooks['tool.execute.before'](input, { args })
            const output = tool === 'read' ? readFileSync(sourcePath, 'utf8')
              : await hooks.tool[tool].execute(args, { sessionID, agent: 'manager' })
            assert.ok(output.includes('SOURCE'))
            await hooks['tool.execute.after']({ ...input, args }, { title: tool, output, metadata: {} })
            const raw = [{ info: { id: `result-${sessionID}`, role: 'assistant', sessionID, providerID },
              parts: [{ id: `part-${sessionID}`, type: 'tool', tool, callID: input.callID,
                state: { status: 'completed', input: args, output, time: { start: 0, end: 1 } } }] }]
            const projected = { messages: structuredClone(raw) }
            await hooks['experimental.chat.messages.transform']({}, projected)
            const carrier = originalMaterialCarrier(body)
            const terminal = projected.messages[0].parts[0].state.output
            assert.ok(terminal.endsWith(grounding.cursorSeparator + carrier), 'the complete raw material carrier must be preserved exactly')
            const facts = sessionHostFacts(directory, sessionID)
            const savedRead = facts.find(fact => fact[0] === 'RequirementGroundingAnchored')[1].Occurrence.Reads[0]
            assert.deepEqual(Buffer.from(savedRead.ResultBytes), Buffer.from(body), 'durable original bytes match the file without normalization')
            assert.deepEqual(Buffer.from(savedRead.CursorResultBytes), Buffer.from(carrier), 'durable presentation uses the one raw-material owner')
            const guidance = facts.find(fact => fact[0] === 'PairProgrammingGuidelineAnchored')[1].MarkerText
            assert.equal(terminal, output + grounding.cursorSeparator + guidance + grounding.cursorSeparator + carrier, 'guidance precedes raw material without changing the original result')
            assert.deepEqual(projected.messages.map(message => message.info), raw.map(message => message.info))
            const canonical = await trace.currentProjection(runtime.journal, sessionID)
            assert.deepEqual(canonical.messages.flatMap(message => message.parts).filter(part => part.kind === 'tool-result'), [{ kind: 'tool-result', result: output }])
            const repeated = { messages: structuredClone(projected.messages) }
            await hooks['experimental.chat.messages.transform']({}, repeated)
            assert.deepEqual(repeated.messages, projected.messages, 'projected wire replay preserves the complete carrier once')
            assert.deepEqual(await trace.currentProjection(runtime.journal, sessionID), canonical)
          })
        }
      }
    })
  })
}

const assertRegisteredComposition = (messages, original, resultId, facts) => {
  assert.equal(messages.length, original.length, 'registered composition preserves message count')
  assert.deepEqual(messages.map(message => message.info), original.map(message => message.info))
  assert.deepEqual(messages[0], original[0], 'pending tool call and arguments are untouched')
  const output = messages.find(message => message.info.id === resultId).parts[0].state.output
  const originalOutput = original.find(message => message.info.id === resultId).parts[0].state.output
  assert.ok(output.startsWith(originalOutput), 'existing result and suffix bytes remain intact')
  const anchored = facts.find(fact => fact[0] === 'PairProgrammingGuidelineAnchored')
  assert.ok(anchored, 'registered guidance is present exactly once')
  const guidance = anchored[1].MarkerText
  const requirementSource = 'requirement_source_path = "requirements/alpha/WHAT.md"'
  assert.equal(output.split(guidance).length - 1, 1, 'registered guidance is present exactly once')
  assert.equal(output.split(requirementSource).length - 1, 1, 'registered grounding is present exactly once')
  assert.ok(output.indexOf(guidance) < output.indexOf(requirementSource), 'registered guidance precedes grounding')
  assert.equal(messages.some(message => message.info.source === pair.source || message.info.source === grounding.source), false)
}

const exerciseRegisteredComposition = async (providerID) => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    mkdirSync(join(directory, 'requirements', 'alpha'), { recursive: true })
    mkdirSync(join(directory, 'src'), { recursive: true })
    writeFileSync(join(directory, 'requirements', 'alpha', 'WHAT.md'), 'GROUNDING-ONLY-MATERIAL\n')
    writeFileSync(join(directory, 'requirements', 'alpha', 'APPLIES-TO'), '/src/**\n')
    const sourcePath = join(directory, 'src', 'main.fs')
    writeFileSync(sourcePath, 'before\n')
    for (const tool of ['read', 'write', 'edit', 'patch', 'apply_patch', 'mv', 'rm', 'js-manager', 'js-engineer']) {
      const sessionID = `composition-${providerID}-${tool}`
      if (tool === 'js-manager') await acceptAuthorityRoot(runtime, sessionID, 'manager')
      if (tool === 'js-engineer') await acceptAuthorityRoot(runtime, sessionID, 'engineer')
      await openIncumbency(runtime, sessionID)
      const callID = `call-${sessionID}`
      const args = tool === 'js-manager'
        ? { program: "class Js extends JsProgram { async run() { const file = await this.file('src/main.fs'); return file.text(); } }" }
        : tool === 'js-engineer'
          ? { program: "class Js extends JsProgram { async run() { await this.write('src/generated.fs', 'PROGRAM-MUTATION'); return 'PROGRAM-WRITTEN'; } }" }
          : tool === 'mv'
            ? { source: sourcePath, destination: join(directory, 'src', 'next.fs') }
            : { filePath: sourcePath, content: 'changed\n' }
      const input = { tool, sessionID, callID }
      await hooks['tool.execute.before'](input, { args })
      const actualOutput = tool.startsWith('js-')
        ? await hooks.tool[tool].execute(args, { sessionID, agent: tool.slice(3) })
        : 'NATIVE-RESULT\r\n'
      if (tool === 'js-manager') assert.match(actualOutput, /before/, 'registered program actually reads covered source')
      if (tool === 'js-engineer') assert.match(actualOutput, /PROGRAM-WRITTEN/, 'registered program actually mutates covered source')
      if (tool === 'js-engineer') assert.equal(readFileSync(join(directory, 'src', 'generated.fs'), 'utf8'), 'PROGRAM-MUTATION')
      const rawOutput = `${actualOutput}\0\uFEFFEXISTING-SUFFIX\n`
      await hooks['tool.execute.after']({ ...input, args }, { title: tool, output: rawOutput, metadata: {} })
      const original = [{
        info: { id: `pending-${sessionID}`, role: 'assistant', sessionID, providerID },
        parts: [{ id: `pending-part-${sessionID}`, type: 'tool', tool, callID,
          state: { status: 'pending', input: args, time: { start: 0 } } }],
      }, {
        info: { id: `result-${sessionID}`, role: 'assistant', sessionID, providerID },
        parts: [{ id: `part-${sessionID}`, type: 'tool', tool, callID,
          state: { status: 'completed', input: args, output: rawOutput, time: { start: 0, end: 1 } } }],
      }]
      const transformed = { messages: structuredClone(original) }
      await hooks['experimental.chat.messages.transform']({}, transformed)
      const facts = sessionHostFacts(directory, sessionID)
      assertRegisteredComposition(transformed.messages, original, original.at(-1).info.id, facts)
      assert.deepEqual(original.at(-1).parts[0].state.output, rawOutput)
      const canonical = await trace.currentProjection(runtime.journal, sessionID)
      assert.deepEqual(canonical.messages.flatMap(message => message.parts).filter(part => part.kind === 'tool-result'),
        [{ kind: 'tool-result', result: rawOutput }], 'canonical capture retains Host bytes but excludes both presentation additions')

      const replay = { messages: structuredClone(original) }
      await hooks['experimental.chat.messages.transform']({}, replay)
      assert.deepEqual(replay.messages, transformed.messages, 'raw Host replay reproduces the same anchored suffix once')
      const canonicalReplay = await trace.currentProjection(runtime.journal, sessionID)
      assert.deepEqual(canonicalReplay, canonical, 'presentation replay cannot append or contaminate canonical X')
    }
  })
}

for (const providerID of ['anthropic', 'cursor', 'openai']) {
  test(`WHAT[requirement-grounding-007] registered composition preserves result suffixes, orders guidance before grounding and keeps canonical X clean (${providerID})`, () =>
    exerciseRegisteredComposition(providerID))
}

const runCompositionMutation = mutation => {
  const env = { ...process.env, WANXIANGSHU_GROUNDING_COMPOSITION_MUTATION: mutation }
  delete env.NODE_TEST_CONTEXT
  return spawnSync(process.execPath, [
    '--loader', new URL('./support/007-composition-loader.mjs', import.meta.url).href,
    // The child's summary counters are read as TAP lines (`# pass 1`). Pin the
    // reporter: the default reporter is not TAP on every supported Node version.
    '--test-reporter=tap', '--test',
    '--test-name-pattern=registered composition preserves result suffixes.*anthropic',
    new URL(import.meta.url).pathname,
  ], {
    encoding: 'utf8', timeout: 30000,
    env,
  })
}

test('WHAT[requirement-grounding-007] registered composition keeps canonical suffix order when the production owner calls are reversed', () => {
  const result = runCompositionMutation('reversed-calls')
  assert.equal(result.error, undefined, result.stderr)
  assert.equal(result.signal, null)
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.match(result.stdout, /# pass 1/)
})

for (const mutation of ['missing-guidance', 'missing-grounding', 'reversed-delivery']) {
  test(`WHAT[requirement-grounding-007] registered composition proof rejects ${mutation} production wiring`, () => {
    const result = runCompositionMutation(mutation)
    assert.equal(result.error, undefined, `${mutation}: mutation child completed`)
    assert.equal(result.signal, null, `${mutation}: mutation child was not terminated`)
    assert.equal(result.status, 1, `${mutation}: the same registered composition assertion must fail`)
    assert.match(result.stdout, /registered (guidance is present exactly once|grounding is present exactly once|guidance precedes grounding)/)
  })
}
