import * as bookkeeperTool from '../../../../dist/Repository/Knowledge/Casebook/OpenCode/BookkeeperToolSurface.js' // CUT-40: tool-binding entries split from BookkeeperSurface
import assert from 'node:assert/strict'
import * as casebook from '../../../../dist/Repository/Knowledge/Casebook/Surface.js'
import * as bookkeeper from '../../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js'
import * as bookkeeperRefresh from '../../../../dist/Repository/Knowledge/Casebook/BookkeeperRefreshSurface.js'
import * as lifecycle from '../../../../dist/Repository/Knowledge/Casebook/LifecycleSurface.js'

let programSequence = 0
const bookkeeperRequest = program => ({
  name: 'bookkeeper-scripted-' + (++programSequence),
  program,
  intent: 'Complete the bound Bookkeeper transaction and retain an explicit result if execution cannot finish.',
})

export const fileRead = (path, contentHash) => ({ kind: 'file-read', path, contentHash })
import { CANONICAL_Q, CANONICAL_A } from './canonical.mjs'
export { CANONICAL_Q, CANONICAL_A }

export const scriptedBookkeeperPort = () => {
  const createCalls = []
  const prompts = []
  const programCalls = []
  const terminals = new Set()
  let seq = 0
  const port = {
    CreateChildSession: async () => {
      throw new Error('Bookkeeper must not attach to a deleted physical parent')
    },
    CreateSiblingSession: async (_ownerSessionId, physicalParentId) => {
      seq += 1
      const child = `bk-child-${seq}`
      createCalls.push({ child, physicalParentId })
      return bookkeeper.acceptedSession(child)
    },
    AbortSession: async () => bookkeeper.aborted(),
    SubscribeTerminal: (_childId, callback) => {
      terminals.add(callback)
      return { Dispose: () => terminals.delete(callback) }
    },
    SendPrompt: async (childSession, text) => {
      prompts.push(text)
      const sid = bookkeeper.sessionValue(childSession)
      const tx = bookkeeper.txIdFor(sid)
      assert.notEqual(tx, '', 'SendPrompt must run against a bound Bookkeeper tx')
      const out = await bookkeeperTool.runProgram(
        sid,
        bookkeeperRequest(`class Js extends JsProgram { async run() { this.setQuestion(${JSON.stringify(CANONICAL_Q)}); this.setAnswer(${JSON.stringify(CANONICAL_A)}); return { changed: true }; } }`),
      )
      assert.equal(String(out).includes('changed = true'), true, out)
      programCalls.push(tx)
      for (const callback of terminals) callback(bookkeeper.sessionId(sid), bookkeeper.completed(sid))
      return bookkeeper.acceptedPrompt()
    },
  }
  return { port, createCalls, prompts, programCalls }
}

export const installBookkeeperRuntime = (port, ownerSessionIds) => {
  const installed = bookkeeper.setRuntime(
    port,
    ownerSessionIds.map((sessionId) => ({
      sessionId,
      logicalRunId: `bookkeeper-run-${sessionId}`,
      authorityRootUserMessageId: `bookkeeper-root-${sessionId}`,
      agent: 'engineer',
    })),
  )
  assert.equal(installed.ok, true, installed.error)
}

export const record = (sessionId, q, a, observations) => ({ sessionId, q, a, observations, lastAccessOrder: 0 })
