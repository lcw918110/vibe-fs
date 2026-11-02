import assert from 'node:assert/strict'
import test from 'node:test'
import * as trace from '../../../dist/Context/Trace/SemanticTraceSurface.js'

const unwrap = (result) => {
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.projection
}

test('WHAT[semantic-trace-005] canonical render is deterministic and omits provenance', () => {
  const items = [
    { cursor: trace.cursor(0), role: 'user', provenance: 'run-secret/msg-secret', part: trace.semanticText('Fix it.') },
    { cursor: trace.cursor(1), role: 'assistant', provenance: 'run-secret/msg-secret', part: trace.semanticReasoning('considered') },
    { cursor: trace.cursor(2), role: 'assistant', provenance: 'run-secret/msg-secret', part: trace.semanticToolCall('todowrite-for-engineer', '{}') },
  ]
  const first = trace.render(items)
  assert.equal(trace.render(items), first)
  assert.equal(trace.render(items.map((item) => ({ ...item, provenance: 'other-transport' }))), first)
  assert.notEqual(trace.render([{ ...items[0], part: trace.semanticText('Different task') }]), trace.render([items[0]]))
  assert.match(first, /user: Fix it\./)
  assert.match(first, /\[tool call\] todowrite-for-engineer \{\}/)
  assert.equal(first.includes('secret'), false)
  assert.equal(trace.render([]), '')
})

test('WHAT[semantic-trace-005] projection queries return detached opening and part views', () => {
  const part = {
    sequence: 1,
    role: 'assistant',
    provenance: 'g:0/msg:msg-1/host-part:part-1',
    turn: 0,
    partIndex: 0,
    kind: 'text',
    textRef: 'blob-1',
    textDigest: 'digest-1',
    providerRun: 'run-1',
  }
  let projection = unwrap(trace.appendOpening(trace.emptyProjection(), 'initial task', ['r1']))
  projection = unwrap(trace.appendPart(projection, part))

  const openingCopy = trace.openingEvidence(projection)
  openingCopy.assignmentText = 'mutated outside'
  openingCopy.authoritativeRequirements.push('mutated-req')
  assert.deepEqual(trace.openingEvidence(projection), {
    assignmentText: 'initial task',
    authoritativeRequirements: ['r1'],
    constitutiveBody: '',
  })

  const partsCopy = trace.orderedSemanticParts(projection)
  partsCopy[0].role = 'user'
  partsCopy.push({ ...part, sequence: 99 })
  assert.equal(trace.orderedSemanticParts(projection).length, 1)
  assert.equal(trace.orderedSemanticParts(projection)[0].role, 'assistant')
})
