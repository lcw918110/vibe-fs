import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const read = path => readFileSync(new URL('../../../resources/provider/' + path, import.meta.url), 'utf8')

test('WHAT[cognitive-environment-017] permanent host prompts carry no state-specific command or hidden-reasoning markup', () => {
  for (const locale of ['en', 'zh-CN']) {
    const guideline = read('host/pair-programming-guideline/' + locale + '.md')
    const scopeProtocol = read('host/scope-protocol/' + locale + '.md')
    assert.doesNotMatch(guideline, /\[System:|<think>|<\/think>/)
    assert.doesNotMatch(scopeProtocol, /\[System:|Then call resetScope immediately|single exception|唯一例外/)
    assert.doesNotMatch(scopeProtocol, /When .*resetScope|每当.*resetScope|如果.*resetScope/)
    // [PRODUCT-RETIRED standalone-resetScope] guideline names the `scope` annotation.
    assert.match(guideline, /«Scope»[\s\S]*`scope`/)
    assert.match(scopeProtocol, /resetScope/)
  }
})

test('WHAT[cognitive-environment-017] role and stage resources do not ask the model to resolve known Host gates', () => {
  for (const locale of ['en', 'zh-CN']) {
    const plan = read('role/plan/' + locale + '.md')
    const s3 = read('planning/s3/' + locale + '.md')
    assert.doesNotMatch(plan, /when the user enters a command asking to execute|若用户再次输入指令要求执行交付/)
    assert.doesNotMatch(plan, /When live blocking resources exist|存在活跃阻塞资源/)
    assert.doesNotMatch(s3, /Whenever the user in `post-S3`|此后若用户在 `post-S3`/)
    const deliver = read('tool/deliver/description/' + locale + '.md')
    assert.doesNotMatch(deliver, /The first call returns|第一次调用返回|call deliver again|再次调用 deliver/)
    assert.match(deliver, /一次调用|one call/i)
  }
})
