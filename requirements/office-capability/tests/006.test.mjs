import assert from 'node:assert/strict'
import test from 'node:test'
import { CASES } from './eval/provider-office-boundary/corpus.mjs'
import { evaluateCase } from './eval/provider-office-boundary/oracles.mjs'

test('WHAT[office-capability-006] synthetic examples exercise the sample trace checkers, not production agents', () => {
  for (const example of CASES) {
    assert.equal(evaluateCase(example, example.pass_example).ok, true, example.id)
    assert.equal(evaluateCase(example, example.fail_example).ok, false, example.id)
  }
})

test('WHAT[office-capability-006] synthetic Manager checker rejects personal mutation even when delegation is also present', () => {
  const example = CASES.find((item) => item.id === 'manager-mixed-mission')
  for (const name of ['todowrite-for-engineer', 'todowrite-for-devops', 'fission']) {
    const invalid = {
      ...example.pass_example,
      toolCalls: [...example.pass_example.toolCalls, { name, args: { filePath: 'src/bug.ts' } }],
    }
    assert.equal(evaluateCase(example, invalid).ok, false, name)
  }
  assert.equal(evaluateCase(example, { ...example.pass_example, mutations: true }).ok, false, 'mutations=true')
})

test('WHAT[office-capability-006] synthetic Manager checker rejects retired coder delegation', () => {
  const example = CASES.find((item) => item.id === 'manager-mixed-mission')
  const invalid = {
    role: 'manager',
    toolCalls: [
      { name: 'fork', args: { calling: 'coder' } },
      { name: 'resume', args: { name: 'Op' } },
    ],
  }
  assert.equal(evaluateCase(example, invalid).ok, false)
})

test('WHAT[office-capability-006] synthetic repair checker requires repair followed by revalidation', () => {
  const example = CASES.find((item) => item.id === 'devops-inherent-repair')
  for (const toolCalls of [
    [{ name: 'todowrite-for-devops', args: { program: 'js.call' } }],
    [{ name: 'todowrite-for-devops', args: { program: 'js.call' } }, { name: 'todowrite-for-devops', args: { program: "this.write('src/bug.ts', 'fixed')" } }],
    [{ name: 'todowrite-for-devops', args: { program: 'js.call' } }, { name: 'todowrite-for-devops', args: { program: "this.write('src/bug.ts', 'fixed')" } }, { name: 'todowrite-for-devops', args: { program: 'js.call' } }, { name: 'fork' }],
  ]) {
    assert.equal(evaluateCase(example, { role: 'devops', toolCalls }).ok, false)
  }
})

test.todo('WHAT[office-capability-006] evaluate real provider traces and outcomes for office boundaries; synthetic checkers do not prove runtime compliance or distinguish product decisions from engineering choices')
