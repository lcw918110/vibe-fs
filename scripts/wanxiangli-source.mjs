import fs from 'node:fs'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm'

// npm run 把全局 .npmrc 的 allow-scripts 以 npm_config_allow_scripts 形式导出给
// 子进程；npm 12 在项目级安装里把它当 CLI 层配置并拒绝（EALLOWSCRIPTS）。
// 子进程的安装策略应归属各项目自身的 package.json/.npmrc，与全局 shell 配置无关。
function npmCommand(args, cwd) {
  const env = { ...process.env }
  delete env.npm_config_allow_scripts
  return execFileSync(npm, args, { cwd, env, stdio: 'inherit' })
}

function git(cwd, args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8' }).trim()
}

export function assertNoTrackedArchives(directory) {
  const tracked = execFileSync('git', ['ls-files', '-z'], { cwd: directory, encoding: 'utf8' })
  const archives = tracked.split('\0').filter(name => /\.tgz$/i.test(name))
  if (archives.length) throw new Error(`Tracked .tgz archives are forbidden: ${archives.join(', ')}`)
}

function sourceDirectory(directory) {
  const source = path.join(directory, 'wanxiangli')
  if (!fs.existsSync(path.join(source, 'package.json'))) {
    throw new Error('Initialize the source dependency: git submodule update --init -- wanxiangli')
  }
  const spec = JSON.parse(fs.readFileSync(path.join(directory, 'package.json'), 'utf8'))
  if (spec.dependencies?.wanxiangli !== 'file:wanxiangli') {
    throw new Error('wanxiangli must be consumed from file:wanxiangli, not an archive or a second checkout')
  }
  return source
}

export function checkSourceDependency(directory = root) {
  const source = sourceDirectory(directory)
  assertNoTrackedArchives(directory)
  assertNoTrackedArchives(source)
  const entry = git(directory, ['ls-files', '--stage', '--', 'wanxiangli'])
  if (!entry.startsWith('160000 ')) throw new Error('wanxiangli must be registered as a Git submodule')
  const branch = git(directory, ['config', '-f', '.gitmodules', '--get', 'submodule.wanxiangli.branch'])
  if (branch !== 'main') throw new Error('wanxiangli must track its main branch')
  return source
}

// 开发更新是显式 Git 操作；构建和运行不会悄悄联网换版本。
export function updateSourceDependency(directory = root) {
  let source = path.join(directory, 'wanxiangli')
  if (!fs.existsSync(path.join(source, 'package.json'))) {
    git(directory, ['submodule', 'update', '--init', '--', 'wanxiangli'])
  }
  source = checkSourceDependency(directory)
  if (git(source, ['status', '--porcelain'])) throw new Error('Commit or preserve local wanxiangli edits before updating')
  git(source, ['fetch', 'origin', 'main'])
  execFileSync('git', ['merge-base', '--is-ancestor', 'HEAD', 'origin/main'], { cwd: source })
  git(source, ['merge', '--ff-only', 'origin/main'])
  npmCommand(['install', '--package-lock-only', '--ignore-scripts', '--no-audit', '--no-fund'], directory)
  console.log(`wanxiangli now follows origin/main at ${git(source, ['rev-parse', 'HEAD'])}; review and commit the gitlink and lockfile.`)
}

export function buildSourceDependency(directory = root, { jobs = 1 } = {}) {
  if (!Number.isInteger(jobs) || jobs < 1 || jobs > 4) throw new Error('--jobs must be an integer from 1 to 4')
  const source = checkSourceDependency(directory)
  const installed = path.join(directory, 'node_modules', 'wanxiangli')
  if (!fs.existsSync(installed) || fs.realpathSync(installed) !== fs.realpathSync(source)) {
    throw new Error('Run npm ci to replace the old installed tarball with the wanxiangli source link')
  }
  installSourceToolchain(source)
  execFileSync(process.execPath, ['scripts/build.mjs', '--jobs', String(jobs)], { cwd: source, stdio: 'inherit' })
}

// npm ci 每次都会删掉并重装 wanxiangli/node_modules。只有安装输入（锁文件里除库自身版本号外的全部内容、
// Node 主版本）变了，或已装的包缺失时才需要重装：成功的 npm ci 之后在 node_modules 里记下输入摘要，下次一致即跳过。
// WANXIANGLI_FORCE_CI=1 强制重装。
const toolchainStamp = '.wanxiangshu-lock-stamp'
function sourceLockDigest(lock) {
  // 每次发版只改库自身版本号，不改依赖；版本号不参与摘要，免得每个新版本都白白重装一次。
  const { version: _, ...rest } = lock
  const { version: __, ...self } = lock.packages?.[''] ?? {}
  const inputs = { ...rest, packages: { ...lock.packages, '': self } }
  return createHash('sha256').update(`node${process.versions.node.split('.')[0]}\0`).update(JSON.stringify(inputs)).digest('hex')
}

export function installSourceToolchain(source) {
  const stamp = path.join(source, 'node_modules', toolchainStamp)
  const lock = JSON.parse(fs.readFileSync(path.join(source, 'package-lock.json'), 'utf8'))
  const digest = sourceLockDigest(lock)
  const required = Object.entries(lock.packages ?? {})
    .filter(([name, entry]) => name.startsWith('node_modules/') && !entry.optional)
  const intact = required.every(([name, entry]) => {
    try {
      return JSON.parse(fs.readFileSync(path.join(source, name, 'package.json'), 'utf8')).version === entry.version
    } catch {
      return false
    }
  })
  if (process.env.WANXIANGLI_FORCE_CI !== '1' && intact && fs.existsSync(stamp)
    && fs.readFileSync(stamp, 'utf8').trim() === digest) {
    console.log('[wanxiangli] node_modules matches package-lock.json; skipping npm ci')
    return false
  }
  npmCommand(['ci', '--ignore-scripts', '--no-audit', '--no-fund'], source)
  fs.writeFileSync(stamp, `${digest}\n`)
  return true
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    switch (process.argv[2]) {
      case 'check': checkSourceDependency(); break
      case 'update': updateSourceDependency(); break
      case 'build': buildSourceDependency(); break
      default: throw new Error('Usage: node scripts/wanxiangli-source.mjs check|update|build')
    }
  } catch (error) {
    console.error(error.message)
    process.exitCode = 1
  }
}
