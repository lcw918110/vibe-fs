import fs from 'node:fs'
import path from 'node:path'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const source = path.dirname(fileURLToPath(import.meta.url))
const command = (root, args) => execFileSync('dotnet', args, { cwd: root, encoding: 'utf8', env: { ...process.env, DOTNET_CLI_UI_LANGUAGE: 'en' } }).trim()
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
  const cache = command(root, ['nuget', 'locals', 'global-packages', '--list']).replace(/^global-packages:\s*/, '')
  const packageRoot = path.join(cache, 'fable', version)
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
    environment: Object.fromEntries(Object.entries(process.env).filter(([key]) => /^(DOTNET_|COMPlus_|MSBUILD|NUGET_|FABLE_|WXS_CHECKER_)/i.test(key))) }))
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
