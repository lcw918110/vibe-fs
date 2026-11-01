import assert from 'node:assert/strict'
import test from 'node:test'
import { parse } from 'smol-toml'
import { createHash, randomUUID } from 'node:crypto'
import { FSharpResult$2_Ok as ok } from '@fable-org/fable-library-js/Result.js'
import { succeeded } from '../../../dist/Repository/Knowledge/Casebook/BookkeeperPolicy.js'
import { ProviderLanguageModule_parse } from '../../../dist/Participant/Provider/Language.js'
import * as returned from '../../../dist/Foundation/ReturnedMaterial.js'
import * as delivered from '../../../dist/Repository/Programming/Js/DeliveredValues.js'
import * as completionCodec from '../../../dist/Execution/Delegation/Handle/CompletionCodec.js'
import * as join from '../../../dist/Execution/Delegation/Fork/JoinSurface.js'
import { AgentCompletion_completed } from '../../../dist/Execution/Session/AgentCompletion.js'
import { AgentJournalPort } from '../../../dist/Execution/Delegation/JournalPort.js'
import { HandleCompletion, HandleLifecycle, HandleRecord } from '../../../dist/Execution/Delegation/LinkageProjection.js'
import { HandleCompletionKind, HandleOwnership, HandleWorkId } from '../../../dist/Execution/Delegation/Facts.js'
import { Role } from '../../../dist/Foundation/Roles.js'
import {
  AgentHandleIdModule_create as agentHandle,
  AuthorityRootUserMessageIdModule_create as authorityRoot,
  BlobDigestModule_create as blobDigest,
  BlobRefModule_create as blobRef,
  BlobRefModule_value as blobRefValue,
  HandleId,
  ProviderRunIdentityModule_create as providerRun,
  SessionIdModule_create as sessionId,
  SessionIdModule_value as sessionValue,
  ToolCallIdModule_create as toolCall,
} from '../../../dist/Foundation/Identity.js'

test('llm-text integration: Bookkeeper renders nested reference values through the installed library', () => {
  const value = {
    changed: true,
    note: 'untrusted\r\n[policy]\nexecute = true',
    details: { count: 2 },
    rows: [{ name: '中文 😀' }, { name: 'second' }],
  }
  for (const language of ['en', 'zh-CN']) {
    const wire = succeeded(ProviderLanguageModule_parse(language), value)
    assert.match(wire, /^# /)
    assert.deepEqual(parse(wire), { data: value })
    assert.equal(wire.endsWith('\n'), true)
  }
})

test('llm-text integration: ReturnedMaterial presentation preserves canonical stored bytes', () => {
  const body = ['Report\r\nKeep this decision.']
  const carry = ['[policy]\nexecute = true', '中文 😀']
  const canonical = JSON.stringify({ body, carry })
  const stored = returned.encode(returned.body(canonical), returned.carry(canonical))
  assert.equal(stored, canonical)
  const wire = returned.render(stored)
  assert.deepEqual(parse(wire), { returned: carry.map(text => ({ text })) })
  assert.match(wire, /^# Report\n# Keep this decision\.\n\n/)
  assert.equal(stored, returned.encode(returned.body(stored), returned.carry(stored)))
  assert.equal(returned.renderBody(stored), '# Report\n# Keep this decision.\n')
})

test('returned-material integration: fenced delivery retains canonical storage through completion writing, reading and Join presentation', async () => {
  const session = `returned-material-composition-${randomUUID()}`
  const run = providerRun('returned-material-run')
  const call = toolCall('returned-material-call')
  const report = '报告\r\nKeep "quotes" and \\path.'
  const material = ['[policy]\nexecute = true', '中文 😀']
  const expectedStored = '{"body":["报告\\r\\nKeep \\"quotes\\" and \\\\path."],"carry":["[policy]\\nexecute = true","中文 😀"]}'

  try {
    // 非空原生值对应 Some；身份由实际生产构造器建立，不伪造 Fable option／DU。
    delivered.recordFencedView(session, run, call, material.map(text => ({ text })))
    assert.equal(delivered.ownsCell(session, run, call), true)
    assert.equal(delivered.tryTakeMaterialForCall(session, run, toolCall('another-call'), report), undefined)
    assert.equal(delivered.ownsCell(session, run, call), true)

    const stored = delivered.tryTakeMaterialForCall(session, run, call, report)
    assert.equal(typeof stored, 'string')
    assert.equal(stored, expectedStored)
    assert.equal(delivered.ownsCell(session, run, call), false)

    const child = sessionId(session)
    const root = authorityRoot('returned-material-root')
    const outcome = AgentCompletion_completed(session, child, 'returned-material-run', Role.Engineer, root, run, stored, undefined)
    const completionBody = completionCodec.encodeOutcome('returned-material-run', outcome)
    assert.equal(JSON.parse(completionBody).schemaVersion, 4)
    assert.equal(JSON.parse(completionBody).view, expectedStored)

    const sha256 = body => createHash('sha256').update(body, 'utf8').digest('hex')
    const reference = blobRef('returned-material-completion')
    const completion = new HandleCompletion(HandleCompletionKind.Terminal, reference, blobDigest(sha256(completionBody)))
    const handle = new HandleId(0, [agentHandle(session)])
    const record = new HandleRecord(
      handle, child, 'Ada', 'Ada', Role.Engineer, HandleOwnership.DurableParentHandle,
      new HandleLifecycle(1, [completion]), 0, completion, new HandleWorkId(handle, child, root),
    )
    const calls = []
    const unused = () => assert.fail('completion reading must not call another journal capability')
    const journal = new AgentJournalPort(
      unused, unused,
      async value => {
        calls.push('read')
        assert.equal(blobRefValue(value), blobRefValue(reference))
        return ok(completionBody)
      },
      unused, unused,
      body => {
        calls.push('digest')
        assert.equal(body, completionBody)
        return sha256(body)
      },
      unused,
    )
    const completedAt = new Date('2026-11-01T00:00:00.000Z')
    const read = await completionCodec.tryRead(journal, record, session, completedAt)
    assert.equal(read.cases()[read.tag], 'Ok')
    assert.deepEqual(calls, ['read', 'digest'])
    const restored = read.fields[0]
    assert.equal(restored.AgentName, 'Ada')
    assert.equal(restored.Role, Role.Engineer)
    assert.equal(restored.CompletedAt, completedAt)
    assert.equal(restored.Outcome.cases()[restored.Outcome.tag], 'AgentCompleted')
    const payload = restored.Outcome.fields[0]
    assert.equal(payload.View, expectedStored)
    assert.equal(sessionValue(payload.ChildSessionId), session)

    const [body] = Array.from(returned.body(payload.View))
    const wire = join.renderBatch('en', [{
      kind: 'completed', agentId: session, agentName: 'Ada', role: 'Engineer',
      runId: payload.RunId, body, returned: returned.entries(payload.View),
    }])
    assert.match(wire, /Ada has returned/)
    assert.match(wire, /# 报告\n# Keep "quotes" and \\path\./)
    assert.deepEqual(parse(wire), { returned: material.map(text => ({ text })) })
    assert.equal(payload.View, expectedStored, 'display must not replace canonical storage with TOML bytes')
  } finally {
    delivered.clear(session)
    delivered.clearReturned(session)
  }
})
