import test from 'node:test'
import assert from 'node:assert/strict'
import { planRequest, resetScopeState, isScopeInitialized, clearSessionState } from '../../../dist/Context/Scope/Surface.js'

const user = (key, timestamp) => ({ kind: 'user', operator: true, key, timestamp, parts: [] })
const tool = (id, name, terminalField = '') => ({ id, tool: name, terminalField })
const assistant = (...parts) => ({ kind: 'assistant', operator: false, parts })
const result = (id) => ({ kind: 'result', operator: false, resultFor: id, resultField: 'content', parts: [] })

test('WHAT[context-compression-038] HNI scope view plans retention without inventing a steer or suffix', () => {
  const session = 'ses_scope_038_user'
  clearSessionState(session)
  const messages = [user('100:do the task', 100)]
  const plan = planRequest(session, messages)
  assert.equal(plan.active, true)
  assert.deepEqual(plan.slots.map(x => x.index), [0])
  assert.deepEqual(plan.hints, [], 'the retired countdown owns no tool-result or user tail')
  assert.equal(plan.strong, null, 'the missing-estimate steer belongs to ScopeProgress')
  assert.equal(plan.weak, null)
  assert.equal(isScopeInitialized(session), false)
  assert.equal(messages[0].parts.length, 0, 'the plan cannot mutate raw Host messages')
  clearSessionState(session)
})

test('WHAT[context-compression-038] the HNI scope plan retains reset and active work, without deciding OpenCode output fields', () => {
  const session = 'ses_scope_038_tools'
  clearSessionState(session)
  const operator = user('100:task', 100)
  planRequest(session, [operator])
  resetScopeState(session, 5)
  const messages = [operator, assistant(tool('old', 'read', 'output')), assistant(tool('reset', 'resetScope', 'output')), assistant(tool('new', 'read', 'output'))]
  const plan = planRequest(session, messages)
  assert.deepEqual(plan.slots.map(x => x.index), [0, 2, 3])
  assert.deepEqual(plan.hints, [], 'tool results never carry the retired budget suffix')
  assert.deepEqual(messages.map(m => m.parts?.length ?? 0), [0, 1, 1, 1], 'raw parts stay untouched')
  clearSessionState(session)
})

test('WHAT[context-compression-038] a separate OMP toolResult uses the same plan, not a tool callback', () => {
  const session = 'ses_scope_038_omp'
  clearSessionState(session)
  const operator = user('10:task', 10)
  planRequest(session, [operator])
  resetScopeState(session, 2)
  const messages = [operator, assistant(tool('reset', 'resetScope')), result('reset'), assistant(tool('read', 'read')), result('read')]
  const plan = planRequest(session, messages)
  assert.deepEqual(plan.slots.map(x => x.index), [0, 1, 2, 3, 4])
  assert.deepEqual(plan.hints, [], 'the neutral plan writes no tool-result tail in either envelope')
  const replay = planRequest(session, [...messages, assistant(tool('next', 'read')), result('next')])
  assert.deepEqual(replay.hints, [], 'a repeated plan still writes no tail')
  clearSessionState(session)
})
