// Host-native `run` and the four PTY tools (`open-terminal`, `send-terminal`,
// `read-terminal`, `signal-terminal`) are retired; `js.call` is the sole
// execution tool (repository-programming [050]-[052]).
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { parse } from 'smol-toml'
import { execute, caseName, render } from '../../../../dist/Repository/Programming/Js/WorkflowSurface.js'

export const sandbox = (prefix = 'wxs-proc-run-') => {
  const dir = mkdtempSync(join(tmpdir(), prefix))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}

export const program = (body) => `class Js extends JsProgram {
  async run() {
${body}
  }
}`

export const runProgram = (dir, body, sessionId = 'ses-process-contract') =>
  execute(dir, 'DevOps', 'en', sessionId, { program: program(body) }, 20_000, Date.now() + 120_000, 1 << 20, null)

export const runText = async (dir, body, sessionId = 'ses-process-contract') =>
  render(await runProgram(dir, body, sessionId))

export const parseData = (outcome) => parse(render(outcome)).data

export { execute, caseName, render }
export const shellQuote = value => `'${value.replaceAll("'", "'\\''")}'`
export const nodeCommand = code => `${shellQuote(process.execPath)} -e ${shellQuote(code)}`
