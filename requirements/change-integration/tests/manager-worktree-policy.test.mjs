import test from 'node:test'
import assert from 'node:assert/strict'
import {
  managerBranchOf,
  staleManagerWorktrees as staleWorktrees,
  staleManagerBranches as staleBranches,
} from '../../../dist/Change/Surface.js'

test('manager branch naming preserves the complete job identity', () => {
  assert.equal(managerBranchOf('job-7'), 'manager/job-7')
  assert.equal(managerBranchOf('nested/job'), 'manager/nested/job')
})

test('worktree cleanup selects only unowned manager names without a positional exemption', () => {
  const entries = [
    ['/first', 'refs/heads/manager/j1'],
    ['/main', 'refs/heads/main'],
    ['/detached', undefined],
    ['/second', 'manager/j2'],
    ['/other', 'refs/heads/x/manager/j3'],
    ['/case-sensitive', 'refs/heads/Manager/j4'],
  ]
  assert.deepEqual(staleWorktrees(entries, ['manager/j1']), ['/second'])
  assert.deepEqual(staleWorktrees(entries, ['manager/j1', 'manager/j2']), [])
  assert.deepEqual(staleWorktrees(entries, []), ['/first', '/second'])
  assert.deepEqual(staleWorktrees(entries, ['manager/j']), ['/first', '/second'])
  assert.deepEqual(staleWorktrees(entries, ['refs/heads/manager/j1']), ['/first', '/second'])
})

test('branch cleanup preserves listing order and only excludes owned short names', () => {
  const branches = ['manager/j1', 'refs/heads/manager/j2', 'manager/j3', 'feature/x', 'main']
  assert.deepEqual(staleBranches(branches, ['manager/j2']), ['manager/j1', 'manager/j3', 'feature/x', 'main'])
  assert.deepEqual(staleBranches(branches, ['manager/j']), branches)
  assert.deepEqual(staleBranches(['manager/j1', 'manager/j1'], []), ['manager/j1', 'manager/j1'])
})
