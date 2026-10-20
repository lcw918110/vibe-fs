import assert from 'node:assert/strict'
import test from 'node:test'
import { generateSurfaceForRole } from '../../../dist/Repository/Programming/Js/GeneratorSurface.js'

test('WHAT[repository-programming-027] both working roles receive the same generated members for equal capabilities', () => {
  const capabilities = ['Read', 'Write', 'Edit', 'Glob', 'Grep', 'Move', 'Remove']
  const engineer = generateSurfaceForRole('Engineer', capabilities)
  const devops = generateSurfaceForRole('DevOps', capabilities)
  assert.equal(engineer.toolName, 'todowrite-for-engineer')
  assert.equal(devops.toolName, 'todowrite-for-devops')
  assert.equal(engineer.baseClassSource, devops.baseClassSource)
  assert.deepEqual(
    engineer.members.map(m => m.memberName),
    ['write', 'rm', 'mv', 'cat', 'ls', 'rg', 'stat', 'mkdir', 'whereAll', 'where'],
  )
  assert.deepEqual(devops.members, engineer.members)
})

// Superseded note: direct Host filesystem tools (`read`/`write`/`edit`/`glob`/`grep`/`patch`)
// and plugin `mv`/`rm` ToolSpecs are retired; Engineer and DevOps share the unified js
// programming surface (`cat`/`where`/`write`/`ls`/`mv`/`rm`).
