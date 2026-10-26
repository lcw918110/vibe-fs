// Host-native `run` and the four PTY tools (`open-terminal`, `send-terminal`,
// `read-terminal`, `signal-terminal`) are retired; `this.call` is the sole
// execution tool (repository-programming [050]-[052]).
import { mkdtempSync, rmSync } from 'node:fs'
import { randomUUID } from 'node:crypto'
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

export const intent = 'If this command has not completed, preserve the running process and report the output collected so far. Check whether the command is still making progress before deciding to wait, interrupt, or retry. Do not infer an exit status from text printed by the child process; wait for the operating system to report its exit.'

export const runProgram = (dir, body, sessionId = 'ses-process-' + randomUUID(), requestIntent = intent, outputBoundBytes = 1 << 20) =>
  execute(dir, 'DevOps', 'en', sessionId, { name: 'process-' + randomUUID(), program: program(body), intent: requestIntent }, 20_000, Date.now() + 120_000, outputBoundBytes, null)

export const runText = async (dir, body, sessionId = 'ses-process-contract') =>
  render(await runProgram(dir, body, sessionId))

export const parseData = (outcome) => parse(render(outcome)).data

export { execute, caseName, render }
export const shellQuote = value => `'${value.replaceAll("'", "'\\''")}'`
export const nodeCommand = code => `${shellQuote(process.execPath)} -e ${shellQuote(code)}`
