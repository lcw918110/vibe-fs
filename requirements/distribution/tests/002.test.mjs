import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..')

const packageResourcesUrl = pathToFileURL(
  path.join(root, 'dist/Resources/PackageResources.js'),
).href

const entryUrl = pathToFileURL(
  path.join(root, 'dist/OpenCode/Plugin/Plugin.js'),
).href

const RESOURCE_SAMPLES = [
  'provider/role/manager/en.md',
  'provider/role/manager/zh-CN.md',
  'provider/world/common-law/en.md',
  'provider/lifecycle/manager/work-activation/en.md',
  'provider/lifecycle/manager/work-activation/zh-CN.md',
]

test('WHAT[distribution-002] DISTRIBUTION_resource_reads_resolve_under_package_root_regardless_of_cwd', async () => {
  const previous = process.cwd()
  try {
    process.chdir('/')
    const { readText } = await import(packageResourcesUrl)
    for (const relative of RESOURCE_SAMPLES) {
      const text = readText(relative)
      assert.ok(
        text.trim().length > 0,
        `${relative} must be readable while process.cwd() is outside the package`,
      )
    }
  } finally {
    process.chdir(previous)
  }
})

test('WHAT[distribution-002] DISTRIBUTION_fresh_process_with_foreign_cwd_imports_entry_and_reads_resources', () => {
  // 证明进程工作目录不影响读取；仍导入仓库产物，不是独立安装验证。
  const script = `
    import { readText } from ${JSON.stringify(packageResourcesUrl)};
    const text = readText('provider/role/manager/en.md');
    if (!text.includes('Manager')) process.exit(2);
    if (!readText('provider/lifecycle/manager/work-activation/en.md').trim()) process.exit(3);
    await import(${JSON.stringify(entryUrl)});
    console.log('ok');
  `
  const result = spawnSync(process.execPath, ['--input-type=module', '--eval', script], {
    cwd: '/',
    encoding: 'utf8',
    timeout: 30_000,
  })
  assert.equal(result.status, 0, `child exited ${result.status}\n${result.stderr}`)
  assert.match(result.stdout, /ok/)
})
