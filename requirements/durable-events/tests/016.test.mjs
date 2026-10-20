import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { readFileSync } = await import("node:fs");
const { default: test } = await import("node:test");
const journalCodec = await import("../../../dist/Persistence/Journal/CodecSurface.js");
const factCodec = await import("../../../dist/Persistence/Journal/FactCodecSurface.js");

const SESSION = 'ses_a'
const ROOT_ACCEPTED = {
  family: 'Prompt',
  case: 'AuthorityRootAccepted',
  payload: {
    SchemaVersion: 2,
    SessionId: SESSION,
    LogicalRunId: 'run_a',
    AuthorityRootUserMessageId: 'msg_root_a',
    AuthorityKind: 'HumanRoot',
    IdentitySeed: {
      kind: 'RootSelection',
      ownerSession: null,
      ownerLogicalRun: null,
      ownerAuthorityRoot: null,
      participantIdentity: { participant: 'engineer', role: 'engineer', persona: 'Engineer', personaCatalogVersion: 1, origin: 'ResolvedAtRoot' },
    },
  },
}
const env = (overrides = {}) => ({
  runtime: 'rt_a',
  seq: 1,
  observedAt: '2026-01-02T03:04:05Z',
  id: 'a'.repeat(32),
  stream: { kind: 'Session', id: SESSION },
  providerRun: null,
  fact: ROOT_ACCEPTED,
  ...overrides,
})
const readEnvelope = (value) => ({
  runtime: value.runtime,
  seq: Number(value.seq),
  event: value.id,
  stream: value.stream,
  providerRun: value.providerRun,
  fact: value.fact.case,
})
const mustOk = (result, label = 'result') => {
  assert.equal(result.ok, true, `${label} should be Ok: ${JSON.stringify(result.error)}`)
  return result.value
}

test('WHAT[durable-events-016] durable fact vocabulary has no OpenCode infrastructure dependency', () => {
  const readSource = (path) => readFileSync(new URL(`../../../${path}`, import.meta.url), 'utf8')
  const envelope = readSource('src/Wanxiangshu/Persistence/Journal/Envelope.fs')
  const fact = readSource('src/Wanxiangshu/Composition/Durable/Fact.fs')
  const hostFacts = readSource('src/Wanxiangshu/Host/Facts.fs')

  assert.doesNotMatch(envelope, /Wanxiangshu\.OpenCode/)
  assert.doesNotMatch(fact, /Wanxiangshu\.OpenCode/)
  assert.match(hostFacts, /namespace Wanxiangshu\.Host/)
  assert.doesNotMatch(hostFacts, /Wanxiangshu\.OpenCode/)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const eventCodec = await import("../../../dist/Persistence/EventStore/CodecSurface.js");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");

const A = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
const envelope = ({
  id = A,
  stream = 'job/main',
  eventType = 'JobRequested',
  parents = [],
  payload = { status: 'open' },
  payloadRefs = [],
} = {}) => ({
  id,
  stream,
  type: eventType,
  parents,
  payload,
  payloadRefs,
})

test('WHAT[durable-events-016] Git_contract_exposes_canonical_store_ref', () => {
  assert.equal(eventStore.canonicalStoreRef, 'refs/wanxiang/store')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { readFileSync } = await import("node:fs");
const { default: test } = await import("node:test");
const { CANONICAL_EVENT_READER_OWNER_PATHS, DUAL_WRITE_ALLOWLIST, GIT_BYPASS_ALLOWLIST, NON_STORE_SCHEMA_VERSION_SITES, PHYSICAL_HISTORY_OBSERVER_PATHS, SCANNER_IDS, collectProductionEntries, scanCanonicalSharedProgram, scanDualWrite, scanFeatureHistoryLoop, scanFeatureRef, scanFiles, scanGitBypass, scanPrivateDurableSubstrate, scanSchemaVersionInStoreContext, scanText } = await import("../../../scripts/checks/unified-store-gate.mjs");

const readFixture = (name) =>
  readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8')

test('WHAT[durable-events-016] scanner ids cover unified-store clean-break and history ownership rules', () => {
  assert.deepEqual([...SCANNER_IDS], [
    'feature-ref',
    'schema-version-in-store-context',
    'git-bypass',
    'dual-write',
    'feature-history-loop',
    'private-durable-substrate',
    'canonical-shared-program',
  ])
})
test('WHAT[durable-events-016] fixture unified-store-feature-ref.fs is RED for feature-ref', () => {
  const source = readFixture('unified-store-feature-ref.fs')
  const hits = scanFeatureRef(source, 'Domain/CasebookStore.fs')
  assert.ok(hits.length >= 1, 'expected feature-ref violation')
  assert.equal(hits[0].id, 'feature-ref')
  assert.match(hits[0].text, /refs\/wanxiang\/foo/)
  assert.equal(scanSchemaVersionInStoreContext(source).length, 0)
  assert.equal(scanGitBypass(source, 'Domain/CasebookStore.fs').length, 0)
})
test('WHAT[durable-events-016] fixture unified-store-git-bypass.fs is RED for git-bypass', () => {
  const source = readFixture('unified-store-git-bypass.fs')
  const hits = scanGitBypass(source, 'Domain/FeatureGit.fs')
  assert.ok(hits.length >= 1, 'expected git-bypass violation')
  assert.equal(hits[0].id, 'git-bypass')
  assert.match(hits[0].text, /FileName\s*=\s*"git"/)
  assert.equal(scanFeatureRef(source, 'Domain/FeatureGit.fs').length, 0)
  assert.equal(scanSchemaVersionInStoreContext(source).length, 0)
})
test('WHAT[durable-events-016] canonical refs/wanxiang/store is allowed only under Persist/Git ownership', () => {
  const source = 'let storeRef = "refs/wanxiang/store"'
  assert.equal(
    scanFeatureRef(source, 'Infrastructure/Persist/GitRawStore.fs').length,
    0,
  )
  assert.equal(scanFeatureRef(source, 'Infrastructure/Git/GitGateway.fs').length, 0)
  const red = scanFeatureRef(source, 'Domain/Casebook.fs')
  assert.ok(red.length >= 1)
})
test('WHAT[durable-events-016] owner remote-tracking store ref is allowed; other feature refs stay RED', () => {
  const remote = 'let r = "refs/wanxiang/remotes/origin/store"'
  assert.equal(
    scanFeatureRef(remote, 'Infrastructure/Persist/StoreTypes.fs').length,
    0,
  )
  assert.equal(scanFeatureRef(remote, 'Infrastructure/Git/GitGateway.fs').length, 0)
  assert.ok(scanFeatureRef(remote, 'Domain/Casebook.fs').length >= 1)

  const feature = 'let r = "refs/wanxiang/foo"'
  assert.ok(
    scanFeatureRef(feature, 'Infrastructure/Persist/StoreTypes.fs').length >= 1,
    'non-store refs/wanxiang/* remain RED even under Persist/Git ownership',
  )
  assert.ok(scanFeatureRef(feature, 'Domain/Casebook.fs').length >= 1)
})
test('WHAT[durable-events-016] git-bypass allowlist is empty; only Persist/Git ownership may invoke git', () => {
  assert.deepEqual([...GIT_BYPASS_ALLOWLIST], [])
  const source = 'let c = { FileName = "git"; Arguments = [] }'
  assert.equal(scanGitBypass(source, 'src/Wanxiangshu/Git/Subject.fs').length, 0)
  assert.equal(scanGitBypass(source, 'src/Wanxiangshu/Infrastructure/Persist/GitRawStore.fs').length, 0)
  assert.ok(scanGitBypass(source, 'src/Wanxiangshu/Domain/Sneaky.fs').length >= 1)
  assert.ok(scanGitBypass(source, 'src/Wanxiangshu/Journal/RuntimePath.fs').length >= 1)
})
test('WHAT[durable-events-016] production scan is GREEN under gate rules (empty git-bypass allowlist)', () => {
  const entries = collectProductionEntries()
  assert.ok(entries.length > 0, 'expected production .fs files')
  const violations = scanFiles(entries)
  const own = violations.filter((v) => v.id === 'feature-ref' || v.id === 'git-bypass')
  assert.deepEqual(
    own,
    [],
    own.map((v) => `[${v.id}] ${v.file}:${v.line} ${v.label}`).join('\n'),
  )
})
}
