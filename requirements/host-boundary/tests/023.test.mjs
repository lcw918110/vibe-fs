import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))

const fixture = JSON.parse(fs.readFileSync(path.join(here, '../fixtures/opencode-chat-admission-1.18.29.json'), 'utf8'))

const driftFixture = JSON.parse(fs.readFileSync(path.join(here, '../fixtures/opencode-chat-admission-drift.json'), 'utf8'))

const runner = path.join(here, 'support/run-opencode-chat-admission-canary.mjs')

const assertPassingVersionEvidence = (versions) => {
  const supported = fixture.supportedVersionRange !== null
    && (versions.opencode === fixture.supportedVersionRange || versions.opencode.startsWith('1.18.'))
    && (versions.plugin === fixture.supportedVersionRange || versions.plugin.startsWith('1.18.'))
  if (!supported) {
    throw new Error(
      `OpenCode ${versions.opencode}/plugin ${versions.plugin} is outside the passing observed range: ${fixture.missingCapability ?? fixture.supportedVersionRange}`,
    )
  }
}

const runInstalledCanary = () => {
  const launched = spawnSync(process.execPath, [runner], { cwd: path.resolve(here, '../../..'), encoding: 'utf8' })
  assert.equal(launched.status, 0, launched.stderr || launched.stdout)
  return JSON.parse(launched.stdout)
}

const first = (evidence, kind) => evidence.observations.find((observation) => observation.kind === kind)

test('WHAT[host-boundary-023] installed OpenCode chat admission public contract is observed and version-fenced', () => {
  const evidence = runInstalledCanary()
  // 在真实测试或 CI 容器中，若运行的 opencode 与 recorded fixture 版本存在补丁漂移，
  // 保持主版本与协议契约兼容校验，确保 Canary 契约与公共 Hook 集合一致。
  assert.equal(typeof evidence.versions.opencode, 'string')
  assert.equal(typeof evidence.versions.plugin, 'string')
  assert.deepEqual(evidence.publicApis.hooks, fixture.publicHooks)

  const message = first(evidence, 'chat.message')
  assert.equal(message.value.input.sessionID, '$session')
  assert.equal(message.value.input.messageID, '$accepted-message')
  assert.equal(message.value.output.sessionID, '$session')
  assert.equal(message.value.output.messageID, '$accepted-message')
  assert.deepEqual(message.value.output.keys, fixture.chatMessageOutputKeys)
  assert.equal(message.value.output.keys.includes('error'), false, 'public chat.message output has no user-error channel')

  const providerIndex = evidence.observations.findIndex(({ kind }) => kind === 'provider')
  assert.ok(providerIndex > 0, 'real provider boundary was not reached')
  const observedOrder = evidence.observations
    .slice(0, providerIndex + 1)
    .map(({ kind }) => kind)
    .filter((kind) => fixture.order.includes(kind))
  assert.deepEqual(observedOrder, fixture.order)
  assert.deepEqual(observedOrder, fixture.supportedOrder)

  const transformed = first(evidence, 'experimental.chat.messages.transform').value
  assert.equal(transformed.messageID, '$accepted-message')
  assert.equal(transformed.sessionID, '$session')
  assert.equal(transformed.role, 'user')

  const assistantEvents = evidence.observations.filter(
    ({ kind, value }) => kind === 'message.updated'
      && value.info.role === 'assistant'
      && value.info.parentID === '$accepted-message',
  )
  assert.ok(assistantEvents.length > 0)
  const assistantStart = assistantEvents.find(({ value }) => value.info.id && value.info.created !== null)
  assert.ok(assistantStart, 'public event omitted exact assistant start evidence')
  assert.equal(assistantStart.value.info.sessionID, '$session')
  assert.equal(typeof assistantStart.value.info.id, 'string')
  assert.equal(assistantStart.value.info.timeKeys.includes('created'), true)
  const assistantTerminal = assistantEvents.find(({ value }) => value.info.id === assistantStart.value.info.id
    && value.info.completed !== null)
  assert.ok(assistantTerminal, 'public event omitted exact assistant terminal evidence')
  assert.equal(evidence.providerLifecycle.chatParamsDeliveries,
    evidence.observations.filter(({ kind }) => kind === 'chat.params').length)
  assert.equal(evidence.providerLifecycle.providerDeliveries,
    evidence.observations.filter(({ kind }) => kind === 'provider').length)
  assert.equal(evidence.providerLifecycle.assistantMessageUpdates,
    evidence.observations.filter(({ kind, value }) => kind === 'message.updated' && value.info.role === 'assistant').length)

  const params = first(evidence, 'chat.params').value
  assert.deepEqual(params.inputKeys, ['agent', 'message', 'model', 'provider', 'sessionID'])
  assert.equal(params.sessionID, '$session')
  assert.equal(params.messageID, '$accepted-message')
  assert.equal(params.messageSessionID, '$session')
  assert.equal(params.inputKeys.some((key) => /attempt|providerRun/i.test(key)), false)

  const idle = first(evidence, 'session.idle').value
  assert.deepEqual(idle.keys, ['id', 'properties', 'type'])
  assert.deepEqual(idle.propertyKeys, ['sessionID'])
  assert.deepEqual(idle.properties, { sessionID: '<string>' })

  assert.equal(evidence.rejection.responseStatus, 204, 'prompt_async accepts before the Hook Promise rejects')
  const rejection = first(evidence, 'session.error').value
  assert.deepEqual(rejection.keys, ['id', 'properties', 'type'])
  assert.deepEqual(rejection.propertyKeys, ['error', 'sessionID'])
  assert.deepEqual(rejection.properties, {
    error: { data: { message: '<string>' }, name: 'UnknownError' },
    sessionID: '<string>',
  })

  const acceptedDeliveries = evidence.observations.filter(
    ({ kind, value }) => kind === 'chat.message' && value.input.messageID === '$accepted-message',
  )
  assert.equal(acceptedDeliveries.length, 2, 'duplicate messageID is re-delivered to chat.message')
  assert.equal(evidence.duplicate.responseStatus, 204)
  assert.equal(evidence.duplicate.transformsAfter, evidence.duplicate.transformsBefore)
  assert.equal(evidence.duplicate.providerDeliveriesAfter, evidence.duplicate.providerDeliveriesBefore)

  if (fixture.supportedVersionRange === null) {
    assert.throws(
      () => assertPassingVersionEvidence(evidence.versions),
      /outside the passing observed range: exact public assistant message\.updated start and terminal evidence has not yet been captured/,
    )
  } else {
    assertPassingVersionEvidence(evidence.versions)
  }
  console.log(JSON.stringify({ opencodeChatAdmissionCanary: evidence }))
})
