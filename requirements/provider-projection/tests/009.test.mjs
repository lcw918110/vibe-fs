import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const join = await import("../../../dist/OpenCode/JoinResultRendererSurface.js");


test('WHAT[provider-projection-009] MISC_join_render_batch_empty_view_no_comment', () => {
  const wire = join.renderAgentCompletion('english', 'x', '')
  assert.match(wire, /# x has returned\./)
  assert.equal(wire.trim().split('\n').length, 1)
})
test('WHAT[provider-projection-009] MISC_join_render_batch_child_to_parent_view_stays_entry_local_comment', () => {
  const wire = join.renderAgentCompletion('english', 'coder', 'Work summary\ndid the thing\n\nRecent work\nok')
  assert.match(wire, /# coder has returned\./)
  assert.match(wire, /^# Work summary$/m)
  assert.match(wire, /^# did the thing$/m)
  assert.equal(wire.includes('work_record ='), false)
  assert.equal(wire.includes("= '''"), false)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const toolModule = await import("@opencode-ai/plugin/tool");

const codec = await import('../../../dist/OpenCode/Codec/ToolHostSurface.js')
const {
  makeArguments: makeArgs,
  argumentText,
  argumentOptionalText,
  argumentOptionalTexts,
  argumentOptionalNumber,
  schemaString,
  schemaStringDescribed,
  schemaNumber,
  schemaEnum,
  schemaEnumDescribed,
  schemaOptionalEnum,
  schemaOptionalEnumDescribed,
  schemaManagedOrHandle,
  schemaOptionalString,
  schemaOptionalStringDescribed,
  schemaOptionalNumber,
  schemaOptionalNonNegativeIntegerDescribed,
  schemaOptionalStringArray,
  registryNames,
  hide,
  contextDecode,
  contextView,
  contextAttachAbort,
  tomlObject,
  tomlObjectWithInstructions,
  tomlTable,
  looksLikeHandleId,
  digest,
} = codec

test('WHAT[provider-projection-009] CODEC_toml_object_with_instructions_prepends_them', () => {
  const text = tomlObjectWithInstructions(['do this first'], [{ name: 'name', value: 'demo' }])
  assert.ok(text.indexOf('do this first') < text.indexOf('name = "demo"'))
})
}
