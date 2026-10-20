import assert from 'node:assert/strict'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import {
  scanProviderProjectionRepo,
  scanProjectionCoreSource,
} from '../../../scripts/checks/provider-projection-boundary.mjs'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '../../..')

const FIXTURE_FILE = 'src/Wanxiangshu/Participant/Provider/Projection/Fixture.fs'

const violationFor = (source) => scanProjectionCoreSource(FIXTURE_FILE, source)

const expectedViolation = (rule, line, text) => ({
  id: 'provider-projection-owner',
  rule,
  file: FIXTURE_FILE,
  line,
  text,
})

test('WHAT[provider-projection-007] generic decode projection and writeback remain allowed', () => {
  const source = `namespace Wanxiangshu.Participant.Provider.Projection

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.LlmFacing
open Wanxiangshu.OpenCode

module GenericProjection =
    type ProjectionMessageBase =
        { Key: string
          Rows: obj list }
    type ProjectionSnapshot = { CurrentProjection: obj }
    type ProjectionIntent =
        | ReplaceMessageBase of ProjectionMessageBase
        | InsertMessageRows of obj list
    let decode raw = ProviderWireDecode.decodePart raw
    let render document = LlmFacing.render document
    let projectionSnapshot currentProjection = { CurrentProjection = currentProjection }
    let replaceMessageBase cutoffDigest rows = ReplaceMessageBase { Key = cutoffDigest; Rows = rows }
    let renderMessagesWithHostIds snapshot baseMessages intents =
        snapshot.CurrentProjection, baseMessages, intents
    let renderMessagesWithIntents snapshot baseMessages intents =
        renderMessagesWithHostIds snapshot baseMessages intents
    let tryApplyRenderedMessages sessionId sha256 rendered =
        ProjectionMessageEdit.tryApplyRenderedMessages sessionId sha256 rendered
    let tryApplyRenderedInsertionsPreservingBase sessionId sha256 raw insertions =
        ProjectionMessageEdit.tryApplyRenderedInsertionsPreservingBase sessionId sha256 raw insertions
`

  assert.deepEqual(violationFor(source), [])
})

test('WHAT[provider-projection-007] Strength namespace imports are rejected precisely', () => {
  assert.deepEqual(
    violationFor('namespace Fixture\nopen Wanxiangshu.Strength.Prediction\n'),
    [expectedViolation('strength-import', 2, 'open Wanxiangshu.Strength.Prediction')],
  )
})

test('WHAT[provider-projection-007] each foreign owner reference is rejected precisely', () => {
  for (const [source, reference] of [
    ['open Wanxiangshu.Context.Prefix', 'Wanxiangshu.Context.Prefix'],
    ['open Wanxiangshu.Interaction.Dispatch', 'Wanxiangshu.Interaction.Dispatch'],
    ['open Wanxiangshu.Execution.Session.Recovery', 'Wanxiangshu.Execution.Session.Recovery'],
  ]) {
    assert.deepEqual(
      violationFor(`module Fixture\n${source}\n`),
      [expectedViolation('foreign-owner-reference', 2, reference)],
    )
  }
})

test('WHAT[provider-projection-007] each foreign materialization is rejected precisely', () => {
  for (const name of [
    'ActivatePrefixEpoch',
    'InsertRepair',
    'SuppressTransportOnly',
    'ReanchorAfterCompaction',
    'ProjectionConstants.RepairInstruction',
  ]) {
    assert.deepEqual(
      violationFor(`module Fixture\nlet value = ${name}\n`),
      [expectedViolation('foreign-materialization', 2, name)],
    )
  }
})

test('WHAT[provider-projection-007] Strength writeback API names are rejected precisely', () => {
  assert.deepEqual(
    violationFor('module Fixture\nlet tryApplyStrengthRenderedMessages value = value\n'),
    [expectedViolation('strength-api', 2, 'tryApplyStrengthRenderedMessages')],
  )
})

test('WHAT[provider-projection-007] production provider projection owners are policy-free', () => {
  assert.deepEqual(
    scanProviderProjectionRepo(ROOT),
    [],
    'provider-projection-owner violations must move to their policy owner; this proof remains RED until the production cutover',
  )
})
