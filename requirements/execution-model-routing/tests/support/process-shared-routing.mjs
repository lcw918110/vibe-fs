import { execFileSync } from 'node:child_process'
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

export const createEnvironment = (initPlugin) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-routing-shared-'))
  const home = join(root, 'home')
  const routingDir = join(home, '.config', 'opencode')
  mkdirSync(routingDir, { recursive: true })
  writeFileSync(
    join(routingDir, 'wanxiangshu.mjs'),
    `export const routingProtocol = 2
export default function route(role, running) {
  if (role === 'engineer' || role === 'coder') {
    const occupied = running.filter((item) => item.model === 'provider/model-a' && item.reasoning === 'none').length
    return occupied === 0
      ? { model: 'provider/model-a', reasoning: 'none' }
      : { model: 'provider/model-b', reasoning: 'none' }
  }
  throw new Error('unexpected role: ' + role)
}\n`,
    'utf8',
  )

  return {
    home,
    createPlugin: async (name) => {
      const directory = join(root, name)
      mkdirSync(directory, { recursive: true })
      execFileSync('git', ['init', '--quiet', directory])
      return initPlugin({
        directory,
        client: {},
        events: { listen: () => () => {} },
      })
    },
    dispose: () => rmSync(root, { recursive: true, force: true }),
  }
}

export const managedConfig = () => {
  const agent = {}
  for (const role of ['orchestrator', 'manager', 'engineer', 'devops', 'plan']) {
    agent[role] = {}
  }
  return { agent }
}

export const messageOutput = (sessionID, agent, messageID = `msg_${sessionID}`) => ({
  message: {
    id: messageID,
    role: 'user',
    sessionID,
    agent,
    model: { providerID: 'host', modelID: 'placeholder' },
  },
  parts: [],
})

export const routeMessage = async (hooks, sessionID, agent = 'engineer', messageID = `msg_${sessionID}`) => {
  const output = messageOutput(sessionID, agent, messageID)
  await hooks['chat.message']({ sessionID, agent }, output)
  return output.message.model
}
