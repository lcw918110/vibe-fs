import assert from 'node:assert/strict'
import test from 'node:test'
import * as compression from '../../../dist/Context/Companion/CompressionSurface.js'
import * as turns from '../../../dist/Interaction/Repair/CompletedTurnSurface.js'

// 固定抽取前主体的现状，不判断正文包含工具标记的产品策略是否合理。
// context-compression/004 的 GAP-105 仍未解决。
const samples = [
  { text: null, rejection: 'Empty' },
  { text: '', rejection: 'Empty' },
  { text: '\u00a0\u2003\u2028\u2029', rejection: 'Empty' },
  { text: 'Answer: <tool_call name=read> body', rejection: 'XmlOnly' },
  { text: 'before < INVOKE', rejection: 'XmlOnly' },
  { text: 'after </function_call> done', rejection: 'XmlOnly' },
  { text: '<call-sign>', rejection: 'XmlOnly' },
  { text: '修复完成。', rejection: null },
  { text: 'a < b; List<string>; <em>okay</em>', rejection: null },
  { text: '<tool_callback>', rejection: null },
]

const descriptions = { Empty: 'empty terminal', XmlOnly: 'XML-only terminal' }
const textParts = text => [{ type: 'text', text }]
const reasoning = text => ({ type: 'reasoning', text })
const toolCall = { type: 'tool-call', callID: 'response-text-read', tool: 'read', args: '{}' }
const expectedStop = rejection => rejection === null
  ? { kind: 'TurnCompleted', reason: null }
  : { kind: 'TurnNeedsContinuation', reason: `assistant stop with ${descriptions[rejection]}` }

test('terminal text checks preserve shape results and legacy diagnostics', () => {
  for (const { text, rejection } of samples) {
    assert.deepEqual(compression.terminalValidityCheck(text), rejection === null
      ? { ok: true }
      : { ok: false, error: rejection }, JSON.stringify(text))
    assert.deepEqual(compression.terminalValidity(text), {
      valid: rejection === null, rejection,
    }, JSON.stringify(text))
    assert.equal(compression.terminalValidityIsValid(text), rejection === null, JSON.stringify(text))
    if (rejection !== null) {
      assert.equal(compression.terminalValidityDescription(rejection), descriptions[rejection])
    }
  }
})

test('terminal text checks preserve actual stopped-turn classification for every shape', () => {
  for (const { text, rejection } of samples) {
    const parts = textParts(text)
    assert.deepEqual(turns.classifyOutcome(true, 'stop', null, parts), expectedStop(rejection), JSON.stringify(text))
    assert.equal(turns.formalContentUnusable(parts), rejection !== null, JSON.stringify(text))
  }
})

test('terminal whitespace retains the JavaScript character set', () => {
  for (const text of [null, '', ' ', '\t\r\n', '\v\f', '\u00a0', '\u1680', '\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a', '\u2028\u2029', '\u202f', '\u205f', '\u3000', '\ufeff']) {
    assert.deepEqual(compression.terminalValidityCheck(text), { ok: false, error: 'Empty' }, JSON.stringify(text))
  }
  for (const text of ['\u0000', '\u0085', '\u180e', '\u200b', '\u001c\u001d\u001e\u001f', ' \u200b ']) {
    assert.deepEqual(compression.terminalValidityCheck(text), { ok: true }, JSON.stringify(text))
  }
})

test('terminal tool tags retain partial matches, case and Unicode word boundaries', () => {
  for (const tag of ['tool_call', 'use_tool', 'call', 'function_call', 'invoke']) {
    for (const text of [`<${tag}>value</${tag}>`, `<${tag.toUpperCase()}>`, `</${tag}>`, `<${tag}`, `<${tag} name="tool"`, `< ${tag} />`, `</ ${tag} >`, `<${tag}\nattribute="value">`, `ordinary prose <${tag}> ordinary prose`, `中文 <${tag} 结尾`]) {
      assert.deepEqual(compression.terminalValidityCheck(text), { ok: false, error: 'XmlOnly' }, text)
    }
  }
  for (const text of ['<call_x>', '<calls>', '<call1>', '<tool_calling>', '<function_calls>', '<invoked>', '<callK>', '&lt;call&gt;', 'function_call without brackets']) {
    assert.equal(compression.terminalValidityIsValid(text), true, text)
  }
  for (const text of ['<call-x>', '<callé>', '<call\u0301>', '<call🙂>', '<CALL-x>', '<invoke:tool>', '<uſe_tool>']) {
    assert.deepEqual(compression.terminalValidityCheck(text), { ok: false, error: 'XmlOnly' }, text)
  }
})

test('terminal checks do not retain a regex cursor across calls', () => {
  for (let count = 0; count < 20; count++) {
    for (const text of ['<call>', '<call>', 'prefix <invoke']) {
      assert.deepEqual(compression.terminalValidityCheck(text), { ok: false, error: 'XmlOnly' })
    }
    assert.equal(compression.terminalValidityIsValid('ordinary'), true)
    assert.deepEqual(compression.terminalValidityCheck('  '), { ok: false, error: 'Empty' })
    assert.equal(compression.terminalValidityIsValid('List<string>'), true)
  }
})

test('WHAT[context-compression-004] provider errors and real tool calls keep their existing precedence over text shape', () => {
  for (const { text } of samples) {
    const parts = textParts(text)
    assert.deepEqual(turns.classifyOutcome(true, 'stop', 'UpstreamError', parts), {
      kind: 'TurnFailed', reason: 'UpstreamError',
    }, JSON.stringify(text))
    assert.deepEqual(turns.classifyOutcome(true, 'stop', null, [...parts, toolCall]), {
      kind: 'TurnCompleted', reason: null,
    }, JSON.stringify(text))
    assert.deepEqual(turns.classifyOutcome(true, 'stop', 'UpstreamError', [...parts, toolCall]), {
      kind: 'TurnInProgress', reason: null,
    }, JSON.stringify(text))
  }
})

test('WHAT[context-compression-004] reasoning and step markers do not become formal text or real tool calls', () => {
  for (const parts of [
    null,
    [],
    [reasoning('The task is complete.')],
    [{ type: 'step-start' }, reasoning('The task is complete.'), { type: 'step-finish' }],
  ]) {
    assert.deepEqual(turns.classifyOutcome(true, 'stop', null, parts), expectedStop('Empty'))
    assert.equal(turns.formalContentUnusable(parts), true)
    assert.equal(turns.hasRealToolCall(parts), false)
  }

  const invalidFormal = [...textParts('Answer: <tool_call>read'), reasoning('A normal final answer.')]
  assert.deepEqual(turns.classifyOutcome(true, 'stop', null, invalidFormal), expectedStop('XmlOnly'))
  const validFormal = [...textParts('A normal final answer.'), reasoning('<tool_call>private thinking')]
  assert.deepEqual(turns.classifyOutcome(true, 'stop', null, validFormal), expectedStop(null))
  assert.equal(turns.formalContentUnusable(validFormal), false)
})
