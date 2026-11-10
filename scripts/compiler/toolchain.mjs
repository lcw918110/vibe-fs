import fs from 'node:fs'
import path from 'node:path'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const source = path.dirname(fileURLToPath(import.meta.url))
const command = (root, args) => execFileSync('dotnet', args, { cwd: root, encoding: 'utf8', env: { ...process.env, DOTNET_CLI_UI_LANGUAGE: 'en' } }).trim()
// The default root is only a fallback for a stale override, so a dotnet invocation that cannot
// run here must not fail the build: an empty answer just means this candidate is unavailable.
const commandWithoutOverrides = (root, args) => {
  const env = { ...process.env, DOTNET_CLI_UI_LANGUAGE: 'en' }
  delete env.NUGET_PACKAGES
  try {
    return execFileSync('dotnet', args, { cwd: root, encoding: 'utf8', env, stdio: ['ignore', 'pipe', 'ignore'] }).trim()
  } catch {
    return ''
  }
}
export const digest = value => createHash('sha256').update(value).digest('hex')

function files(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const file = path.join(directory, entry.name)
    return entry.isDirectory() ? files(file) : [file]
  }).sort()
}

export function toolchain(root) {
  const version = JSON.parse(fs.readFileSync(path.join(root, '.config/dotnet-tools.json'))).tools.fable.version
  const sdkVersion = command(root, ['--version'])
  const sdkLine = command(root, ['--list-sdks']).split('\n').find(line => line.startsWith(sdkVersion + ' '))
  if (!sdkLine) throw new Error(`Cannot locate .NET SDK ${sdkVersion}`)
  const sdk = path.join(sdkLine.match(/\[(.*)\]/)[1], sdkVersion)

  // `dotnet nuget locals` echoes whatever NUGET_PACKAGES names, even a directory that no longer
  // exists or never did. Sandboxed runs hand us a temp root that gets cleaned underneath us, and
  // restore short-circuits on its own cache without repopulating that root. Resolve the first
  // NUGET_PACKAGES candidate that actually holds the restored tool, falling back to the user's
  // own global-packages, so a stale override costs one fallback instead of a raw ENOENT.
  const reported = command(root, ['nuget', 'locals', 'global-packages', '--list']).replace(/^global-packages:\s*/, '')
  // Ask dotnet for the root it would use without the override, rather than rebuilding that path
  // from HOME — test runners and sandboxes hand us a temporary HOME, and guessing `.nuget` under
  // it pointed at an empty directory while the real restored tool sat elsewhere. The second query
  // is allowed to fail: a compiler service started under a stripped environment must keep using
  // the reported root instead of aborting the compile.
  const fallback = commandWithoutOverrides(root, ['nuget', 'locals', 'global-packages', '--list']).replace(/^global-packages:\s*/, '')
  const candidate = base => path.join(base, 'fable', version)
  const carries = packageRoot => fs.existsSync(path.join(packageRoot, 'tools'))

  let packageRoot = [reported, fallback].filter(Boolean).map(candidate).find(carries)

  if (!packageRoot) {
    // Nothing holds the tool anywhere we know. Let dotnet restore into its own default root,
    // which means clearing the stale override rather than writing through it.
    const restoreEnv = { ...process.env, DOTNET_CLI_UI_LANGUAGE: 'en' }
    delete restoreEnv.NUGET_PACKAGES
    execFileSync('dotnet', ['tool', 'restore'], {
      cwd: root,
      encoding: 'utf8',
      env: restoreEnv,
    })
    const postRestore = commandWithoutOverrides(root, ['nuget', 'locals', 'global-packages', '--list']).replace(/^global-packages:\s*/, '')
    const restored = [postRestore, fallback, reported].filter(Boolean).map(candidate).find(carries)
    if (!restored) throw new Error(
      `Fable ${version} is not restored in either reported global-packages (${reported}) or the user default (${postRestore || fallback || '<unavailable>'}); ` +
      `run dotnet tool restore after unsetting NUGET_PACKAGES.`,
    )
    packageRoot = restored
  }

  const tools = path.join(packageRoot, 'tools')
  const frameworks = fs.readdirSync(tools).filter(name => fs.existsSync(path.join(tools, name, 'any/fable.dll')))
  if (frameworks.length !== 1) throw new Error('Expected one restored Fable tool target; run dotnet tool restore')
  const target = frameworks[0]
  const payload = path.join(tools, target, 'any')
  const dotnetRoot = path.resolve(sdk, '../..')
  const packs = path.join(dotnetRoot, 'packs/Microsoft.NETCore.App.Ref')
  const referenceVersion = fs.readdirSync(packs).filter(name => fs.existsSync(path.join(packs, name, 'ref', target)))
    .sort((a, b) => a.localeCompare(b, undefined, { numeric: true })).at(-1)
  if (!referenceVersion) throw new Error(`Missing SDK reference pack for ${target}`)
  const references = path.join(packs, referenceVersion, 'ref', target)
  const inputs = [...files(packageRoot).filter(file => /\.(dll|json|js)$/.test(file)),
    ...files(source).filter(file => /\.(mjs|fs)$/.test(file)),
    ...['global.json', '.config/dotnet-tools.json', 'package-lock.json'].map(name => path.join(root, name))]
  const hash = createHash('sha256').update(JSON.stringify({ sdk, references, node: process.execPath, nodeVersion: process.version,
    // `packageRoot` already pins which restored tool tree this build consumes, so the raw
    // NUGET_PACKAGES override carries no extra identity — only churn. A sandbox that hands out a
    // fresh temp root per run would otherwise invalidate every cached compile while resolving to
    // the same bytes.
    environment: Object.fromEntries(Object.entries(process.env).filter(([key]) => /^(DOTNET_|COMPlus_|MSBUILD|NUGET_|FABLE_|WXS_CHECKER_)/i.test(key) && key.toUpperCase() !== 'NUGET_PACKAGES')) }))
  for (const file of inputs) hash.update(file).update('\0').update(fs.readFileSync(file))
  hash.update(fs.readFileSync(path.join(sdk, 'FSharp/fsc.dll')))
  return { key: hash.digest('hex'), sdk, payload, packageRoot, references }
}

export function prepare(root, identity) {
  const output = path.join(root, '.fable-build/compiler/tools', identity.key)
  const entry = path.join(output, 'Checker.dll')
  if (fs.existsSync(path.join(output, '.ready'))) return entry
  fs.mkdirSync(output, { recursive: true })
  for (const name of fs.readdirSync(identity.payload).filter(name => name.endsWith('.dll'))) {
    fs.copyFileSync(path.join(identity.payload, name), path.join(output, name))
  }
  fs.cpSync(path.join(identity.packageRoot, 'fable-library-js'), path.join(output, 'fable-library-js'), { recursive: true })
  fs.copyFileSync(path.join(identity.payload, 'fable.runtimeconfig.json'), path.join(output, 'Checker.runtimeconfig.json'))
  const args = ['--target:exe', '--targetprofile:netcore', '--noframework', '--simpleresolution', '--nocopyfsharpcore', '--optimize+', '--out:' + entry,
    ...fs.readdirSync(identity.references).filter(name => name.endsWith('.dll')).map(name => '-r:' + path.join(identity.references, name)),
    ...['FSharp.Core.dll', 'FSharp.Compiler.Service.dll', 'Fable.AST.dll', 'Fable.Transforms.dll', 'Fable.Compiler.dll', 'fable.dll'].map(name => '-r:' + path.join(output, name)),
    path.join(source, 'Checker.fs')]
  execFileSync('dotnet', [path.join(identity.sdk, 'FSharp/fsc.dll'), ...args], { cwd: root, stdio: 'pipe' })
  fs.writeFileSync(path.join(output, '.ready'), identity.key)
  return entry
}
