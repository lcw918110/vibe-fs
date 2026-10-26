import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { generate, generateRole } from '../../../dist/Repository/Programming/Js/GeneratorSurface.js'
import { execute, caseName } from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import { installDelegationHost, clearDelegationHost } from '../../../dist/Repository/Programming/Js/RuntimeSurface.js'

const program = (body) => 'class Js extends JsProgram { async run() { ' + body + ' } }'

test('WHAT[advisor-006] advisor is not a public JS member or capability', () => {
  const manager = generateRole('manager', 'en')
  assert.equal(manager.toolName, 'todowrite-for-manager')
  assert.equal(manager.capabilities.includes('Advisor'), false)
  assert.equal(manager.members.some((member) => member.memberName === 'advisor'), false)
  assert.doesNotMatch(manager.description, /advisor\(intent\)/)
  assert.doesNotMatch(manager.baseClassSource, /async advisor\(/)

  const forged = generate('Manager', ['Read', 'Advisor'], 'en')
  assert.equal(forged.members.some((member) => member.memberName === 'advisor'), false)
  assert.doesNotMatch(forged.baseClassSource, /_api\.advisor/)
})

test('WHAT[advisor-006] obsolete advisor calls cannot invoke a delegation callback', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-advisor-retired-'))
  const session = 'advisor-retired-member'
  let delegated = false
  const forbidden = async () => { delegated = true; throw Error('an obsolete callback was invoked') }
  try {
    installDelegationHost(session, forbidden, forbidden, forbidden)
    const result = await execute(
      dir,
      'Manager',
      'en',
      session,
      {
        name: 'reject-retired-advisor',
        program: program('return await this.advisor("review");'),
        intent: 'Verify that obsolete advice cannot be requested through the programming surface.',
      },
      3000,
      Date.now() + 60_000,
      1 << 20,
      null,
    )
    assert.equal(caseName(result), 'Failed')
    assert.equal(delegated, false)
  } finally {
    clearDelegationHost(session)
    rmSync(dir, { recursive: true, force: true })
  }
})
