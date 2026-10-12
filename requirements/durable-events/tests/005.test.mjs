import test from 'node:test'

// WHAT[durable-events-005] — writer 段序列合同（红阶段）。
// 本文件承载无需注入段上限的用例：
//   1) 既有断言按新合同改写为 <WriterId>-<seq>.ndjson 段名形态，
//      在旧实现（无后缀单文件）上因「文件名形态」而红；
//   2) append 续写最高 seq 段：旧实现把 append 写到无后缀文件而红；
//   3) 旧形态兼容锚：在旧实现上绿，新实现必须保持绿（旧形态不得 leave-unread）。
// 需要注入 64KiB 同构小上限的滚动 / 重组 / 故障注入用例见 026.test.mjs。

{
const { default: assert } = await import("node:assert/strict");
const { readFile, readdir } = await import("node:fs/promises");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");

const id = (n) => n.toString(16).padStart(40, '0')
const event = (id, parents = []) => ({
  id,
  stream: 'append/law',
  type: 'JobRequested',
  parents,
  payload: { id },
  payloadRefs: [],
})
const withTemp = (fn) => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-append-law-'))
  return fn(base)
}

test('WHAT[durable-events-005] small_history_stays_one_segment_named_with_seq_suffix', async () => {
  const dir = withTemp((base) => base)
  const store = eventStore.create(dir, 'one-file-law')
  try {
    let parent = null
    for (let n = 1; n <= 128; n += 1) {
      const next = id(n)
      await eventStore.append(store, [event(next, parent ? [parent] : [])])
      parent = next
    }
    const files = await readdir(join(dir, 'wanxiangshu', 'events'))
    assert.deepEqual(files, ['one-file-law-0000.ndjson'],
      'history below the segment cap stays one segment named <WriterId>-0000.ndjson')
  } finally {
    eventStore.dispose(store)
    rmSync(dir, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { mkdtemp, readFile, readdir, rm } = await import("node:fs/promises");
const { tmpdir } = await import("node:os");
const { default: path } = await import("node:path");
const { default: test } = await import("node:test");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");

const hexId = (n) => n.toString(16).padStart(40, '0')
const event = (id, n, parents = []) => ({
  id,
  stream: 'proof/local',
  type: 'JobRequested',
  parents,
  payload: { n },
  payloadRefs: [],
})
const commonDir = async () => path.join(await mkdtemp(path.join(tmpdir(), 'wanxiang-local-log-')), '.git')
const remove = async (dir) => rm(dir, { recursive: true, force: true })

test('WHAT[durable-events-005] DURABLE_EVENTS_005_one_process_is_one_segment_series_and_appends_preserve_prior_bytes', async () => {
  const gitCommonDir = await commonDir()
  const store = eventStore.create(gitCommonDir, 'writer-proof-a')
  try {
    const first = Array.from({ length: 4 }, (_, i) => event(hexId(i + 1), i + 1))
    assert.equal((await eventStore.append(store, first)).ok, true)
    const file = path.join(gitCommonDir, 'wanxiangshu', 'events', 'writer-proof-a-0000.ndjson')
    const prefix = await readFile(file)

    const many = Array.from({ length: 160 }, (_, i) => event(hexId(i + 100), i + 100))
    assert.equal((await eventStore.append(store, many)).ok, true)
    const after = await readFile(file)

    assert.equal(after.subarray(0, prefix.length).equals(prefix), true, 'append must preserve every prior byte')
    assert.equal(path.basename(file), 'writer-proof-a-0000.ndjson')

    const files = await readdir(path.join(gitCommonDir, 'wanxiangshu', 'events'))
    assert.deepEqual(files, ['writer-proof-a-0000.ndjson'],
      'history below the segment cap must stay one segment named <WriterId>-0000.ndjson')
  } finally {
    eventStore.dispose(store)
    await remove(path.dirname(gitCommonDir))
  }
})
test('WHAT[durable-events-005] DURABLE_EVENTS_005_each_process_writer_id_names_a_distinct_segment_series', async () => {
  const gitCommonDir = await commonDir()
  const a = eventStore.create(gitCommonDir, 'writer-a')
  const b = eventStore.create(gitCommonDir, 'writer-b')
  try {
    assert.equal((await eventStore.append(a, [event(hexId(0xa1), 1)])).ok, true)
    assert.equal((await eventStore.append(b, [event(hexId(0xb1), 2)])).ok, true)

    const files = (await readdir(path.join(gitCommonDir, 'wanxiangshu', 'events'))).sort()
    assert.deepEqual(files, ['writer-a-0000.ndjson', 'writer-b-0000.ndjson'])
  } finally {
    eventStore.dispose(a)
    eventStore.dispose(b)
    await remove(path.dirname(gitCommonDir))
  }
})
}

{
// 手写段 fixture：证明 append 续写最高 seq 段（红），与旧形态兼容读取（锚）。
const { default: assert } = await import("node:assert/strict");
const { readdir, readFile } = await import("node:fs/promises");
const { mkdirSync, mkdtempSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { dirname, join } = await import("node:path");
const { default: test } = await import("node:test");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");

const hexId = (n) => n.toString(16).padStart(40, '0')
const event = (id, n, parents = []) => ({
  id, stream: 'proof/segment', type: 'JobRequested', parents, payload: { n }, payloadRefs: [],
})
const canonicalLine = (event) => JSON.stringify({
  event_id: event.id, event_type: event.type, parents: [...event.parents].sort(),
  payload: event.payload, payload_refs: [...event.payloadRefs].sort(), stream_id: event.stream,
}) + '\n'
const commonDir = () => join(mkdtempSync(join(tmpdir(), 'wxs-segment-law-')), '.git')
const eventsDir = (dir) => join(dir, 'wanxiangshu', 'events')

test('WHAT[durable-events-005] append_continues_the_highest_segment_instead_of_opening_a_suffixless_file', async () => {
  const dir = commonDir()
  const first = event(hexId(1), 1)
  mkdirSync(eventsDir(dir), { recursive: true })
  // fixture 先落盘再 create：装载重放必须看到段 0 的事件。
  writeFileSync(join(eventsDir(dir), 'cont-law-0000.ndjson'), canonicalLine(first))
  const store = eventStore.create(dir, 'cont-law')
  try {

    const second = event(hexId(2), 2, [hexId(1)])
    const result = await eventStore.append(store, [second])
    assert.equal(result.ok, true, JSON.stringify(result.error))

    const names = (await readdir(eventsDir(dir))).sort()
    assert.deepEqual(names, ['cont-law-0000.ndjson'],
      'append must extend the highest segment, never open <WriterId>.ndjson')
    assert.equal(await readFile(join(eventsDir(dir), 'cont-law-0000.ndjson'), 'utf8'),
      canonicalLine(first) + canonicalLine(second))
    assert.equal(eventStore.read(store, hexId(2)) != null, true)
  } finally {
    eventStore.dispose(store)
    rmSync(dirname(dir), { recursive: true, force: true })
  }
})

test('WHAT[durable-events-005] legacy_suffixless_file_is_still_read_as_one_writer_stream', async () => {
  // 兼容锚：本用例在旧实现上绿；新实现必须保持绿——
  // 无段后缀的旧形态文件按其完整内容读取为单条逻辑流，不得 leave-unread。
  const dir = commonDir()
  const e1 = event(hexId(1), 1)
  const e2 = event(hexId(2), 2, [hexId(1)])
  mkdirSync(eventsDir(dir), { recursive: true })
  // fixture 先落盘再 create：装载重放必须看到旧形态文件。
  writeFileSync(join(eventsDir(dir), 'legacy-law.ndjson'), canonicalLine(e1) + canonicalLine(e2))
  const store = eventStore.create(dir, 'legacy-law')
  try {

    assert.deepEqual(eventStore.heads(store, 'proof/segment'), [hexId(2)])
    assert.equal(eventStore.read(store, hexId(1)) != null, true)
  } finally {
    eventStore.dispose(store)
    rmSync(dirname(dir), { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import('node:assert/strict')
const { execFileSync } = await import('node:child_process')
const { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } = await import('node:fs')
const { tmpdir } = await import('node:os')
const { join } = await import('node:path')
const eventStore = await import('../../../dist/Persistence/EventStore/Surface.js')
const retention = await import('../../../dist/Persistence/EventStore/RetentionSurface.js')

const CAP = 64 * 1024

const hexId = (n) => n.toString(16).padStart(40, '0')
const canonicalLine = (value) => JSON.stringify({
  event_id: value.id, event_type: value.type, parents: [...value.parents].sort(),
  payload: value.payload, payload_refs: [...value.payloadRefs].sort(), stream_id: value.stream,
}) + '\n'

const lineOf = (n, totalBytes, parents = []) => {
  const probe = { id: hexId(n), stream: 'proof/segment-cap', type: 'JobRequested', parents, payload: { fill: '' }, payloadRefs: [] }
  const skeleton = canonicalLine(probe).length
  assert.ok(totalBytes >= skeleton, 'requested line is smaller than its canonical skeleton')
  return { ...probe, payload: { fill: 'x'.repeat(totalBytes - skeleton) } }
}

const withRepo = async (fn) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-segcap-'))
  execFileSync('git', ['init', '-q', root])
  const commonDir = join(root, '.git')
  try { await fn(root, commonDir) } finally { rmSync(root, { recursive: true, force: true }) }
}
const eventsDir = (commonDir) => join(commonDir, 'wanxiangshu', 'events')
const writeLegacy = (commonDir, writerId, events) => {
  mkdirSync(eventsDir(commonDir), { recursive: true })
  const path = join(eventsDir(commonDir), writerId + '.ndjson')
  writeFileSync(path, events.map(canonicalLine).join(''))
  return path
}
const chain = (count, lineBytes) => {
  const events = []
  let parent = null
  for (let i = 1; i <= count; i += 1) {
    const e = lineOf(i, lineBytes, parent ? [parent] : [])
    parent = e.id
    events.push(e)
  }
  return events
}

test('WHAT[durable-events-005] appending_past_the_cap_rolls_to_the_next_segment', async () => {
  await withRepo(async (repo, commonDir) => {
    const store = eventStore.createWithSegmentCap(commonDir, 'roll-law', CAP)
    try {
      const events = chain(10, CAP / 8)
      for (const e of events) {
        const result = await eventStore.append(store, [e])
        assert.equal(result.ok, true, JSON.stringify(result.error))
      }
      const names = readdirSync(eventsDir(commonDir)).sort()
      assert.deepEqual(names, ['roll-law-0000.ndjson', 'roll-law-0001.ndjson'])
      assert.ok(statSync(join(eventsDir(commonDir), 'roll-law-0000.ndjson')).size <= CAP,
        'no segment may exceed the cap')
      const reassembled = Buffer.concat(names.map(name => readFileSync(join(eventsDir(commonDir), name))))
      assert.equal(reassembled.equals(Buffer.concat(events.map(e => Buffer.from(canonicalLine(e))))), true,
        'the logical stream (segments concatenated by seq) is byte-for-byte the append order')
      const observer = eventStore.create(commonDir, 'roll-observer')
      try {
        assert.deepEqual(eventStore.heads(observer, 'proof/segment-cap'), [events.at(-1).id])
      } finally {
        eventStore.dispose(observer)
      }
    } finally {
      eventStore.dispose(store)
    }
  })
})

test('WHAT[durable-events-005] an_append_landing_exactly_on_the_cap_stays_in_the_current_segment', async () => {
  await withRepo(async (repo, commonDir) => {
    const store = eventStore.createWithSegmentCap(commonDir, 'exact-law', CAP)
    try {
      const half = CAP / 2
      const first = lineOf(1, half)
      const second = lineOf(2, half, [first.id])
      for (const e of [first, second]) {
        const result = await eventStore.append(store, [e])
        assert.equal(result.ok, true, JSON.stringify(result.error))
      }
      const names = readdirSync(eventsDir(commonDir)).sort()
      assert.deepEqual(names, ['exact-law-0000.ndjson'],
        'size + text landing exactly on the cap does not roll; only strictly exceeding rolls')
      assert.equal(statSync(join(eventsDir(commonDir), 'exact-law-0000.ndjson')).size, CAP)
    } finally {
      eventStore.dispose(store)
    }
  })
})

test('WHAT[durable-events-005] a_single_line_larger_than_the_cap_occupies_its_own_segment', async () => {
  await withRepo(async (repo, commonDir) => {
    const store = eventStore.createWithSegmentCap(commonDir, 'oversize-law', CAP)
    try {
      const big = lineOf(1, CAP * 2)
      const small = lineOf(2, 256, [big.id])
      assert.equal((await eventStore.append(store, [big])).ok, true)
      assert.equal((await eventStore.append(store, [small])).ok, true)

      const names = readdirSync(eventsDir(commonDir)).sort()
      assert.deepEqual(names, ['oversize-law-0000.ndjson', 'oversize-law-0001.ndjson'],
        'an oversized line takes its own segment; the next append must roll')
      assert.equal(statSync(join(eventsDir(commonDir), 'oversize-law-0000.ndjson')).size, CAP * 2)
      assert.equal(statSync(join(eventsDir(commonDir), 'oversize-law-0001.ndjson')).size, canonicalLine(small).length)
      assert.equal(eventStore.read(store, small.id) != null, true)
    } finally {
      eventStore.dispose(store)
    }
  })
})

test('WHAT[durable-events-005] a_failed_roll_keeps_sealed_segment_bytes_and_leaves_the_event_uncommitted', async () => {
  await withRepo(async (repo, commonDir) => {
    const base = eventStore.createWithSegmentCap(commonDir, 'roll-fail-law', CAP)
    try {
      for (const e of chain(8, CAP / 8)) {
        assert.equal((await eventStore.append(base, [e])).ok, true)
      }
      const sealed = join(eventsDir(commonDir), 'roll-fail-law-0000.ndjson')
      const before = readFileSync(sealed)

      const observed = []
      const failing = eventStore.createAppendFailureStore(base, {
        code: 'CommitUnknown',
        phase: 'PhysicalAppend',
        cause: new Error('segment roll physical failure'),
      }, (append) => observed.push(append))
      const ninth = chain(9, CAP / 8).at(-1)
      const result = await eventStore.append(failing, [ninth])
      assert.equal(result.ok, false, 'the rolled append must surface the injected typed failure')
      assert.equal(observed.length, 1, 'a failed append is never retried with a fresh event identity')

      assert.equal(readFileSync(sealed).equals(before), true,
        'the sealed segment bytes are never rewritten by a failed roll')
      assert.equal(eventStore.read(base, ninth.id) != null, false,
        'the uncommitted event stays unreadable after the failed roll')
    } finally {
      eventStore.dispose(base)
    }
  })
})

test('WHAT[durable-events-005] materialization_reorganizes_an_oversized_legacy_file_into_equal_segments', async () => {
  await withRepo(async (repo, commonDir) => {
    const events = chain(6, CAP / 4)
    const legacy = writeLegacy(commonDir, 'reorg-law', events)
    const original = readFileSync(legacy)

    const snap = await retention.syncAtWithSegmentCap(repo, commonDir, null, Date.now(), CAP)
    assert.equal(snap.ok, true, JSON.stringify(snap.error))
    assert.equal(existsSync(legacy), false,
      'the legacy file is removed only after every segment has landed')
    const names = readdirSync(eventsDir(commonDir)).sort()
    assert.deepEqual(names, ['reorg-law-0000.ndjson', 'reorg-law-0001.ndjson'])
    for (const name of names) {
      assert.ok(statSync(join(eventsDir(commonDir), name)).size <= CAP)
    }
    const reassembled = Buffer.concat(names.map(name => readFileSync(join(eventsDir(commonDir), name))))
    assert.equal(reassembled.equals(original), true,
      'reorganization reassigns event lines byte-for-byte; the logical stream is unchanged')
    const observer = eventStore.create(commonDir, 'reorg-observer')
    try {
      assert.deepEqual(eventStore.heads(observer, 'proof/segment-cap'), [events.at(-1).id])
    } finally {
      eventStore.dispose(observer)
    }
  })
})

test('WHAT[durable-events-005] reorganization_is_idempotent_across_repeated_syncs', async () => {
  await withRepo(async (repo, commonDir) => {
    const events = chain(6, CAP / 4)
    const legacy = writeLegacy(commonDir, 'reorg-idem-law', events)
    const now = Date.now()

    const first = await retention.syncAtWithSegmentCap(repo, commonDir, null, now, CAP)
    assert.equal(first.ok, true, JSON.stringify(first.error))
    const segmentZero = join(eventsDir(commonDir), 'reorg-idem-law-0000.ndjson')
    const statBefore = statSync(segmentZero)

    const second = await retention.syncAtWithSegmentCap(repo, commonDir, first.root, now, CAP)
    assert.equal(second.ok, true, JSON.stringify(second.error))
    assert.equal(second.root, first.root, 'an already-reorganized store re-materializes to the identical root')
    assert.equal(statSync(segmentZero).mtimeMs, statBefore.mtimeMs,
      'repeat sync must not touch reorganized segment bytes or stats')
    assert.equal(existsSync(legacy), false)
  })
})

test('WHAT[durable-events-005] an_interrupted_reorganization_recovers_by_prefix_validation', async () => {
  await withRepo(async (repo, commonDir) => {
    const events = chain(6, CAP / 4)
    const legacy = writeLegacy(commonDir, 'reorg-recover-law', events)
    const original = readFileSync(legacy)

    const partial = join(eventsDir(commonDir), 'reorg-recover-law-0000.ndjson')
    writeFileSync(partial, events.slice(0, 4).map(canonicalLine).join(''))

    const snap = await retention.syncAtWithSegmentCap(repo, commonDir, null, Date.now(), CAP)
    assert.equal(snap.ok, true, JSON.stringify(snap.error))
    assert.equal(existsSync(legacy), false, 'recovery completes the interrupted reorganization')
    const names = readdirSync(eventsDir(commonDir)).sort()
    assert.deepEqual(names, ['reorg-recover-law-0000.ndjson', 'reorg-recover-law-0001.ndjson'])
    const reassembled = Buffer.concat(names.map(name => readFileSync(join(eventsDir(commonDir), name))))
    assert.equal(reassembled.equals(original), true)
  })
})

test('WHAT[durable-events-005] divergent_partial_segments_fail_closed_without_touching_bytes', async () => {
  await withRepo(async (repo, commonDir) => {
    const legacy = writeLegacy(commonDir, 'reorg-divergent-law', chain(6, CAP / 4))
    const original = readFileSync(legacy)

    const divergent = chain(6, CAP / 4).map((e) => ({ ...e, stream: 'proof/segment-divergent' }))
    const divergentPath = join(eventsDir(commonDir), 'reorg-divergent-law-0000.ndjson')
    writeFileSync(divergentPath, divergent.slice(0, 4).map(canonicalLine).join(''))
    const partialBytes = readFileSync(divergentPath)

    const snap = await retention.syncAtWithSegmentCap(repo, commonDir, null, Date.now(), CAP)
    assert.equal(snap.ok, false, 'coexisting forms without a prefix relation must fail closed')
    assert.equal(readFileSync(legacy).equals(original), true,
      'fail-closed must not silently delete or rewrite the legacy file')
    assert.equal(readFileSync(divergentPath).equals(partialBytes), true,
      'fail-closed must not silently rewrite the partial segment')
  })
})

test('WHAT[durable-events-005] line_continuation_convergence_appends_legacy_lines_onto_the_series_tail', async () => {
  await withRepo(async (repo, commonDir) => {
    const aChain = chain(4, CAP / 8)
    const bChain = [
      lineOf(0x21, CAP / 8, [aChain.at(-1).id]),
      lineOf(0x22, CAP / 8, [hexId(0x21)]),
    ]
    const segmentPath = join(eventsDir(commonDir), 'continuation-law-0000.ndjson')
    mkdirSync(eventsDir(commonDir), { recursive: true })
    writeFileSync(segmentPath, aChain.map(canonicalLine).join(''))
    const legacy = join(eventsDir(commonDir), 'continuation-law.ndjson')
    writeFileSync(legacy, bChain.map(canonicalLine).join(''))
    const legacyBytes = readFileSync(legacy)

    const snap = await retention.syncAtWithSegmentCap(repo, commonDir, null, Date.now(), CAP)
    assert.equal(snap.ok, true, JSON.stringify(snap.error))
    assert.equal(existsSync(legacy), false, 'line-continuation convergence removes the legacy name')
    assert.deepEqual(readdirSync(eventsDir(commonDir)).sort(), ['continuation-law-0000.ndjson'],
      'the tail headroom fits every legacy line, so no fresh segment rolls')

    const tail = readFileSync(segmentPath)
    assert.equal(tail.subarray(tail.length - legacyBytes.length).equals(legacyBytes), true,
      'the legacy lines land verbatim at the series tail')

    const observer = eventStore.create(commonDir, 'continuation-observer')
    try {
      for (const e of [...aChain, ...bChain]) {
        assert.equal(eventStore.read(observer, e.id) != null, true, 'event must be readable after convergence')
      }
      assert.deepEqual(eventStore.heads(observer, bChain.at(-1).stream), [bChain.at(-1).id])
    } finally {
      eventStore.dispose(observer)
    }
  })
})

test('WHAT[durable-events-005] line_continuation_with_a_half_line_still_fails_closed', async () => {
  await withRepo(async (repo, commonDir) => {
    const aChain = chain(2, CAP / 8)
    const b1 = lineOf(0x21, CAP / 8, [aChain.at(-1).id])
    const segmentPath = join(eventsDir(commonDir), 'halfline-law-0000.ndjson')
    mkdirSync(eventsDir(commonDir), { recursive: true })
    writeFileSync(segmentPath, aChain.map(canonicalLine).join(''))
    const legacy = join(eventsDir(commonDir), 'halfline-law.ndjson')
    const halfLine = canonicalLine(b1) + '{"event_id":"truncated'
    writeFileSync(legacy, halfLine)
    const segmentBytes = readFileSync(segmentPath)

    const snap = await retention.syncAtWithSegmentCap(repo, commonDir, null, Date.now(), CAP)
    assert.equal(snap.ok, false, 'a half line is not a line continuation and must fail closed')
    assert.equal(readFileSync(legacy, 'utf8'), halfLine, 'fail-closed must not touch the legacy file')
    assert.equal(readFileSync(segmentPath).equals(segmentBytes), true, 'fail-closed must not touch the series')
  })
})

test('WHAT[durable-events-005] line_continuation_with_overlapping_ids_still_fails_closed', async () => {
  await withRepo(async (repo, commonDir) => {
    const aChain = chain(4, CAP / 8)
    const overlapChain = [lineOf(0x31, CAP / 8, [aChain.at(-1).id]), aChain[1]]
    const segmentPath = join(eventsDir(commonDir), 'overlap-law-0000.ndjson')
    mkdirSync(eventsDir(commonDir), { recursive: true })
    writeFileSync(segmentPath, aChain.map(canonicalLine).join(''))
    const legacy = join(eventsDir(commonDir), 'overlap-law.ndjson')
    writeFileSync(legacy, overlapChain.map(canonicalLine).join(''))
    const legacyBytes = readFileSync(legacy)
    const segmentBytes = readFileSync(segmentPath)

    const snap = await retention.syncAtWithSegmentCap(repo, commonDir, null, Date.now(), CAP)
    assert.equal(snap.ok, false, 'overlapping event ids are divergence, not continuation')
    assert.equal(readFileSync(legacy).equals(legacyBytes), true)
    assert.equal(readFileSync(segmentPath).equals(segmentBytes), true)
  })
})

test('WHAT[durable-events-005] readStreams_reads_line_continuation_as_series_plus_legacy_lines', async () => {
  await withRepo(async (repo, commonDir) => {
    const aChain = chain(2, CAP / 8)
    const bChain = [
      lineOf(0x21, CAP / 8, [aChain.at(-1).id]),
      lineOf(0x22, CAP / 8, [hexId(0x21)]),
    ]
    const segmentPath = join(eventsDir(commonDir), 'read-cont-law-0000.ndjson')
    mkdirSync(eventsDir(commonDir), { recursive: true })
    writeFileSync(segmentPath, aChain.map(canonicalLine).join(''))
    const legacy = join(eventsDir(commonDir), 'read-cont-law.ndjson')
    writeFileSync(legacy, bChain.map(canonicalLine).join(''))
    const legacyBytes = readFileSync(legacy)

    const observer = eventStore.create(commonDir, 'read-cont-observer')
    try {
      for (const e of [...aChain, ...bChain]) {
        assert.equal(eventStore.read(observer, e.id) != null, true, 'the continuation form must stay readable')
      }
      assert.deepEqual(eventStore.heads(observer, bChain.at(-1).stream), [bChain.at(-1).id])
      assert.equal(existsSync(legacy), true, 'the read layer must not converge physically')
      assert.equal(readFileSync(legacy).equals(legacyBytes), true)
    } finally {
      eventStore.dispose(observer)
    }
  })
})

test('WHAT[durable-events-005] rolling_a_new_segment_only_blobs_the_new_segment', async () => {
  await withRepo(async (repo, commonDir) => {
    const store = eventStore.createWithSegmentCap(commonDir, 'blob-reuse-law', CAP)
    try {
      for (const e of chain(8, CAP / 8)) {
        assert.equal((await eventStore.append(store, [e])).ok, true)
      }
      const now = Date.now()
      const first = await retention.syncAtWithSegmentCap(repo, commonDir, null, now, CAP)
      assert.equal(first.ok, true, JSON.stringify(first.error))

      for (const e of chain(10, CAP / 8).slice(8)) {
        assert.equal((await eventStore.append(store, [e])).ok, true)
      }
      const second = await retention.syncAtWithSegmentCap(repo, commonDir, first.root, now, CAP)
      assert.equal(second.ok, true, JSON.stringify(second.error))

      const blobOid = (root, name) =>
        execFileSync('git', ['-C', repo, 'rev-parse', root + ':writers/' + name], { encoding: 'utf8' }).trim()
      assert.equal(
        blobOid(second.root, 'blob-reuse-law-0000.ndjson'),
        blobOid(first.root, 'blob-reuse-law-0000.ndjson'),
        'durable-convergence-010: the sealed segment blob OID is reused; only the new segment is blobbed')
    } finally {
      eventStore.dispose(store)
    }
  })
})
}
