import assert from 'node:assert/strict'
import test from 'node:test'
import * as change from '../../../dist/Change/Surface.js'

test('WHAT[effect-accounting-004] actual worktree confirmation absorbs a repeated request and confirmation', () => {
  const requested = change.requestWorktree(change.empty(), 'worktree', '/repo/worktree', 'job')
  const confirmed = change.acceptWorktree(requested, 'worktree', '/repo/worktree', 'job')
  const repeated = change.acceptWorktree(change.requestWorktree(confirmed, 'worktree', '/repo/worktree', 'job'), 'worktree', '/repo/worktree', 'job')
  assert.equal(change.worktreeEffect(repeated, 'worktree'), 'Created')
  assert.equal(change.worktreeEffect(repeated, 'another-worktree'), null)
})
