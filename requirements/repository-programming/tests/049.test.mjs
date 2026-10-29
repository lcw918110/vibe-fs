import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { z } from 'zod'
import {
  createRegistered,
  description,
  argumentNames,
  argumentSchema,
} from '../../../dist/Repository/Programming/Js/OpenCode/ToolHostSurface.js'

const withTool = (check, role = 'Coder') => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-049-schema-'))
  const tool = definition => definition
  tool.schema = z
  try {
    check(createRegistered({ tool }, role, 'en', dir, null))
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
}

const sample = 'class Js extends JsProgram { async run() { return { ok: true }; } }'

test('WHAT[repository-programming-049] todowrite requires name/program/intent; estimate field retired', () => {
  for (const role of ['Coder', 'Engineer', 'Manager', 'DevOps', 'Plan', 'Orchestrator']) {
    withTool(handle => {
      assert.deepEqual(argumentNames(handle), ['name', 'program', 'intent'], `${role} must not expose remainingScopeTurnsEstimate`)
      assert.equal(argumentSchema(handle, 'remainingScopeTurnsEstimate'), null, 'estimate retired from product schema')
      const program = argumentSchema(handle, 'program')
      assert.equal(program.safeParse(undefined).success, false, 'program is required')
      assert.equal(program.safeParse(null).success, false)
      assert.equal(program.safeParse(42).success, false)
      assert.equal(program.safeParse(sample).success, true)
      assert.match(program.description, /program|JsProgram/)
      const name = argumentSchema(handle, 'name')
      const intent = argumentSchema(handle, 'intent')
      for (const schema of [name, intent]) {
        assert.equal(schema.safeParse(undefined).success, false)
        assert.equal(schema.safeParse(42).success, false)
        assert.equal(schema.safeParse('value').success, true)
      }
      const modelSchema = z.object({ name, program, intent }).strict()
      assert.deepEqual(z.toJSONSchema(modelSchema).required, ['name', 'program', 'intent'])
      assert.equal(modelSchema.safeParse({}).success, false, 'an empty tool call must be schema-invalid')
      for (const retired of ['unpark', 'abandon', 'handle', 'remainingScopeTurnsEstimate']) {
        assert.equal(argumentSchema(handle, retired), null, retired + ' is not a Host tool argument')
      }
    }, role)
  }
})

test('WHAT[repository-programming-049] tool description leads with the three required string arguments', () => {
  withTool(handle => {
    const text = description(handle)
    assert.match(text.slice(0, 420), /program.*required|required.*program/i)
    assert.match(text.slice(0, 520), /class Js extends JsProgram/)
    assert.match(text, /async run\(\)/)
    assert.doesNotMatch(text.slice(0, 520), /optional.*program/i)
    assert.match(text, /remainingScopeTurnsEstimate|do not report|不要再报/i)
  })
})

test('WHAT[repository-programming-049] the actual OpenCode plugin exposes required name, program and intent to the LLM', async () => {
  const { withExecutablePlugin } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
  await withExecutablePlugin(async hooks => {
    for (const role of ['manager', 'engineer', 'devops', 'plan', 'orchestrator']) {
      const registered = hooks.tool[`todowrite-for-${role}`]
      assert.ok(registered, role + ' must expose a generated tool')
      const keys = Object.keys(registered.args)
      assert.ok(keys.includes('name') && keys.includes('program') && keys.includes('intent'), role + ' must expose three sandbox strings')
      assert.ok(!keys.includes('remainingScopeTurnsEstimate'), role + ' must not expose estimate')
      // host may inject `scope` annotation schema alongside the three strings
      const schema = z.toJSONSchema(z.object(registered.args))
      for (const k of ['name', 'program', 'intent']) assert.ok((schema.required || []).includes(k), role + ' requires ' + k)
      assert.ok(!(schema.required || []).includes('remainingScopeTurnsEstimate'))
      assert.equal(schema.properties.program.type, 'string')
      assert.equal(z.object(registered.args).safeParse({}).success, false)
      assert.match(registered.description.slice(0, 520), /program.*required|required.*program|唯一.*program|program.*必填|必填.*program/i)
    }
  })
})
