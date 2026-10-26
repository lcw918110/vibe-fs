import assert from 'node:assert/strict'
import { mkdtempSync, rmSync, readFileSync, writeFileSync, existsSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { randomUUID } from 'node:crypto'
import { parse } from 'smol-toml'
import { execute, caseName, failureCode, parkedName, parkedPayload, parkedSource, render, created, rewritten } from '../../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import { closeSession, pendingNames } from '../../../../dist/Repository/Programming/Js/Programs.js'
export { assert, readFileSync, writeFileSync, existsSync, join, caseName, failureCode, parkedName, parkedPayload, parkedSource, render, created, rewritten, pendingNames }
export const WAIT = 'Wait for the named program to finish; preserve its original state and command result if the request expires.'
export const source = body => 'class Js extends JsProgram {\n  async run() {\n' + body + '\n  }\n}'
export const data = outcome => parse(render(outcome)).data
export const literal = value => JSON.stringify(value) ?? 'undefined'
export function fixture(t, label, role = 'Coder') {
  const root = mkdtempSync(join(tmpdir(), 'wxs-named-' + label + '-'))
  const session = label + '-' + randomUUID()
  let sequence = 0
  t.after(async () => {
    await closeSession(session)
    rmSync(root, { recursive: true, force: true })
  })
  const run = (name, body, options = {}) => execute(root, options.role ?? role, 'en', options.session ?? session,
    { name, program: source(body), intent: options.intent ?? WAIT }, options.executionBudgetMs ?? 2000, Date.now() + 60000, 1 << 20, options.store ?? null)
  return {
    root, session, run,
    file: name => join(root, name),
    unpark: (name, value, options = {}) => run('resume-' + (++sequence), 'return await this.unpark(' + literal(name) + ', ' + literal(value) + ');', options),
    abandon: (name, options = {}) => run('abandon-' + (++sequence), 'return await this.abandon(' + literal(name) + ');', options),
  }
}
