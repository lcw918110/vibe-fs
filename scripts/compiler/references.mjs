import fs from 'node:fs'
import path from 'node:path'
import { digest } from './toolchain.mjs'

const file = root => path.join(root, '.fable-build/compiler/references.json')
export function readReferences(root) {
  try { return JSON.parse(fs.readFileSync(file(root), 'utf8')) }
  catch (error) {
    if (error.code === 'ENOENT') return []
    throw error
  }
}
export function currentReferences(references) {
  return references.map(reference => ({ path: reference.path,
    hash: fs.existsSync(reference.path) ? digest(fs.readFileSync(reference.path)) : null }))
}
export const referenceKey = references => digest(JSON.stringify(references.map(reference => [reference.path, reference.hash])
  .sort((a, b) => a[0].localeCompare(b[0]))))
export function recordReferences(root, references) {
  const entries = new Map(currentReferences(readReferences(root)).map(reference => [reference.path, reference]))
  for (const reference of references) entries.set(reference.path, reference)
  const current = [...entries.values()].filter(reference => fs.existsSync(reference.path)).sort((a, b) => a.path.localeCompare(b.path))
  fs.mkdirSync(path.dirname(file(root)), { recursive: true })
  const temporary = file(root) + '.' + process.pid
  fs.writeFileSync(temporary, JSON.stringify(current))
  fs.renameSync(temporary, file(root))
}
