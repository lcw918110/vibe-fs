import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { parse as parseToml } from 'smol-toml'
import { createRegistered, execute, argumentNames, argumentSchema } from '../../../dist/Repository/Programming/Js/OpenCode/ToolHostSurface.js'
import { pendingNames, closeAll } from '../../../dist/Repository/Programming/Js/Programs.js'
import * as language from '../../../dist/Participant/Provider/LanguageSurface.js'

// The physical Host ToolSpec validates the same three-field contract as execution.
const schemaOf = (state) => ({
  ...state,
  optional: () => schemaOf({ ...state, optional: true }),
  nullable: () => schemaOf({ ...state, nullable: true }),
  describe: text => schemaOf({ ...state, description: text }),
  int: () => schemaOf({ ...state, integer: true }),
  nonnegative: () => schemaOf({ ...state, nonnegative: true }),
  min: bound => schemaOf({ ...state, min: bound }),
  max: bound => schemaOf({ ...state, max: bound }),
})

const toolModule = () => {
  const tool = definition => definition
  tool.schema = {
    string: () => schemaOf({ type: 'string' }),
    any: () => schemaOf({ type: 'any' }),
    number: () => schemaOf({ type: 'number' }),
    array: item => schemaOf({ type: 'array', item }),
    record: (key, value) => schemaOf({ type: 'record', key, value }),
    object: shape => schemaOf({ type: 'object', shape }),
  }
  return { tool }
}

const WAIT = 'Give this named program enough time to reach the requested operation and return its actual result.'
const program = body => `class Js extends JsProgram { async run() { ${body} } }`
const parkProgram = program("return await this.park({ stage: 'host-schema' });")
const writeProgram = program("await this.write('probe.txt', 'ran'); return { wrote: true };")
const input = (name, source = writeProgram, intent = WAIT) => ({ name, program: source, intent })
const register = dir => createRegistered(toolModule(), 'Engineer', 'en', dir, null)
function fixture(t) {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-064-contract-'))
  t.after(async () => { await closeAll(); rmSync(dir, { recursive: true, force: true }) })
  return { dir, registered: register(dir) }
}
let previousLanguage
test.beforeEach(async () => {
  previousLanguage = process.env.WANXIANGSHU_PROVIDER_LANGUAGE
  process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'
  language.refreshGlobalLanguage()
  await closeAll()
})
test.afterEach(async () => {
  await closeAll()
  if (previousLanguage === undefined) delete process.env.WANXIANGSHU_PROVIDER_LANGUAGE
  else process.env.WANXIANGSHU_PROVIDER_LANGUAGE = previousLanguage
  language.refreshGlobalLanguage()
})
test('WHAT[repository-programming-064] the physical Host ToolSpec requires three sandbox strings; estimate retired', t => {
  const { registered } = fixture(t)
  assert.deepEqual(argumentNames(registered), ['name', 'program', 'intent'])
  for (const key of ['name', 'program', 'intent']) {
    const schema = argumentSchema(registered, key)
    assert.equal(schema.type, 'string')
    assert.notEqual(schema.optional, true)
  }
  assert.equal(argumentSchema(registered, 'remainingScopeTurnsEstimate'), null, 'estimate retired from product schema')
  for (const former of ['unpark', 'abandon', 'prefixUntil', 'carry', 'remainingScopeTurnsEstimate'])
    assert.equal(argumentSchema(registered, former), null)
})
test('WHAT[repository-programming-064] three top-level fields execute at the actual Host ToolSpec entry (estimate optional-ignored if present)', async t => {
  const { dir, registered } = fixture(t)
  const result = await execute(registered, input('write'), { sessionID: 'ses-064-write' })
  assert.equal(result.startsWith('# ok'), true, result)
  assert.equal(parseToml(result).data.wrote, true)
  assert.equal(existsSync(join(dir, 'probe.txt')), true)
})
for (const [label, args] of [
  ['empty', {}],
  ['source only', { program: writeProgram }],
  ['no intent', { name: 'bad', program: writeProgram }],
  ['empty intent', input('bad', writeProgram, '')],
  ['numeric intent', input('bad', writeProgram, 48000)],
  ['blank name', input(' ')],
  ['legacy unpark', { unpark: { handle: 'forged' } }],
  ['extra control', { ...input('bad'), abandon: { name: 'target' } }],
]) {
  test(`WHAT[repository-programming-064] ${label} is rejected before side effects even when schema parsing is bypassed`, async t => {
    const { dir, registered } = fixture(t)
    const result = await execute(registered, args, { sessionID: 'ses-064-invalid' })
    assert.ok(result.startsWith('# failed') || typeof parseToml(result).error === 'string', result)
    assert.equal(existsSync(join(dir, 'probe.txt')), false)
    assert.deepEqual(Array.from(pendingNames('ses-064-invalid')), [])
  })
}
test('WHAT[repository-programming-064] only a new program controls a retained target name; legacy fields cannot consume it', async t => {
  const { registered } = fixture(t)
  const context = { sessionID: 'ses-064-park' }
  const parked = await execute(registered, input('target', parkProgram), context)
  assert.equal(parked.startsWith('# parked'), true, parked)
  const data = parseToml(parked)
  assert.equal(data.name, 'target')
  assert.equal(data.handle, undefined)
  assert.deepEqual(Array.from(pendingNames(context.sessionID)), ['target'])
  const invalid = await execute(registered, { ...input('invalid'), unpark: { name: 'target', value: 'forged' } }, context)
  assert.ok(invalid.startsWith('# failed') || typeof parseToml(invalid).error === 'string', invalid)
  assert.deepEqual(Array.from(pendingNames(context.sessionID)), ['target'])
  const resumed = await execute(registered, input('controller', program("return await this.unpark('target', 'accepted');")), context)
  assert.equal(resumed.startsWith('# ok'), true, resumed)
  assert.equal(parseToml(resumed).data.state, 'completed')
  assert.equal(parseToml(resumed).data.value, 'accepted')
  assert.deepEqual(Array.from(pendingNames(context.sessionID)), [])
})
