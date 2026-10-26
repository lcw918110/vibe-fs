import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync } from 'node:fs'
import { admissionAuthority, capabilityToolNames, deliverDescription } from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'

test('WHAT[work-018] lifecycle deliver is not a capability tool, and suicide stays unregistered', () => {
  const names = capabilityToolNames('manager', 'work-main')
  assert.equal(names.includes('deliver'), false)
  assert.equal(names.includes('review'), true)
  assert.equal(names.includes('suicide'), false)
  assert.equal(admissionAuthority('deliver'), 'unknown')
  assert.equal(admissionAuthority('review'), 'office')
  assert.equal(admissionAuthority('suicide'), 'unknown')

  const description = deliverDescription()
  assert.equal(description.includes('suicide'), false)
  assert.doesNotMatch(description, /S1|S4/)
  assert.equal(existsSync('resources/provider/tool/suicide'), false)
})
