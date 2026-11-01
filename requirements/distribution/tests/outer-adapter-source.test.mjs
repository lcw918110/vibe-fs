import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { readCompileShardInventory } from '../../../scripts/lib/compile-shards.mjs'
import { collectVerificationInputs, diffVerificationInputs } from '../../../scripts/lib/build-state.mjs'
import { collectTrackedInputs } from '../../../scripts/lib/owner-compile.mjs'

test('outer adapter source participates in compilation and freshness', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'outer-adapter-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  for (const name of ['src/Wanxiangshu', 'wanxiangtong/opencode', 'scripts', 'requirements', 'resources']) {
    fs.mkdirSync(path.join(root, name), { recursive: true })
  }
  const adapter = path.join(root, 'wanxiangtong/opencode/Request.fs')
  fs.writeFileSync(adapter, 'module Request\nlet value = 1\n')
  fs.writeFileSync(adapter + 'i', 'module Request\nval value: int\n')
  fs.writeFileSync(path.join(root, 'src/Wanxiangshu/Wanxiangshu.Owner.request.fsproj'),
    '<Project><ItemGroup><Compile Include="../../wanxiangtong/opencode/Request.fsi"/><Compile Include="../../wanxiangtong/opencode/Request.fs"/></ItemGroup></Project>')
  const inventory = readCompileShardInventory({ repositoryRoot: root })
  assert.deepEqual([...inventory.sourceProject.keys()], [adapter])
  const temporaryProject = path.join(root, 'src/Wanxiangshu', `Wanxiangshu.Impact.${'a'.repeat(64)}.fsproj`)
  fs.copyFileSync(path.join(root, 'src/Wanxiangshu/Wanxiangshu.Owner.request.fsproj'), temporaryProject)
  assert.ok(collectTrackedInputs({ root, projectDirectory: path.join(root, 'src/Wanxiangshu') }).includes(adapter))
  const before = collectVerificationInputs(root)
  assert.ok(!before.some(entry => entry.path.endsWith(path.basename(temporaryProject))))
  fs.writeFileSync(adapter, 'module Request\nlet value = 2\n')
  assert.equal(diffVerificationInputs(before, collectVerificationInputs(root)).equal, false)
  fs.writeFileSync(path.join(root, 'wanxiangtong/opencode/Unassigned.fs'), 'module Unassigned\n')
  assert.throws(() => readCompileShardInventory({ repositoryRoot: root }), /unassigned=.*wanxiangtong\/opencode\/Unassigned.fs/)
})
