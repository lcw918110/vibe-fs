import test from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'

const resource=path=>readFileSync(new URL('../../../resources/provider/'+path,import.meta.url),'utf8')
const local=path=>readFileSync(new URL('../../../'+path,import.meta.url),'utf8')

test('WHAT[cognitive-environment-019] no current model guidance suggests a second work lifecycle tool',()=>{
  const roots=[
    'tool/review/plan-promoted',
    'tool/deliver/blocked','tool/deliver/confirmation',
    'tool/deliver/no-authority','tool/deliver/non-manager-role',
    'tool/js-program/base-class-lifecycle','tool/js-program/run-rules',
  ]
  for(const root of roots)for(const language of ['en','zh-CN']){
    const text=resource(root+'/'+language+'.md')
    assert.doesNotMatch(text,/\bcall\s+deliver\b|调用\s*`?deliver`?|\bdeliver\s+is\s+Manager-only\b|\bdeliver\s+requires\b|\bbusiness deliver\b/i,
      root+' '+language+' must not teach an obsolete lifecycle tool')
  }
  for(const file of ['README.md','docs/功能导览.md','requirements/office-capability/WHAT.md']){
    assert.doesNotMatch(local(file),/\blifecycle\s+(?:host\s+)?tool\s+`?deliver`?|生命周期\s*(?:host\s*)?工具\s+`?deliver`?|调用\s*`?deliver`?/i,
      file+' must explain work lifecycle via review')
  }
  assert.match(resource('tool/deliver/en.md'),/wxs_deliver/,
    'the separate current material handback tool must not be lost')
  assert.match(resource('tool/deliver/zh-CN.md'),/wxs_deliver/)
})
