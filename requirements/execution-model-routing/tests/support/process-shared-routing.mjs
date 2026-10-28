import { execFileSync } from 'node:child_process'
import { mkdtempSync, mkdirSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

// WHAT[execution-model-routing-001]/[005]: the wanxiangshu.mjs scheduler is
// retired. Model selection belongs to the host; the plugin passes the
// host-selected model through untouched, so the environment only provides two
// workspace directories whose host configs differ.
export const createEnvironment = (initPlugin) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-routing-shared-'))
  const home = join(root, 'home')

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

export const messageOutput = (sessionID, agent, model, messageID = `msg_${sessionID}`) => ({
  message: {
    id: messageID,
    role: 'user',
    sessionID,
    agent,
    model,
  },
  parts: [],
})

export const routeMessage = async (hooks, sessionID, agent = 'engineer', messageID = `msg_${sessionID}`) => {
  const model = { providerID: 'host', modelID: sessionID.endsWith('_a') ? 'root-model' : 'worktree-model' }
  const output = messageOutput(sessionID, agent, model, messageID)
  await hooks['chat.message']({ sessionID, agent }, output)
  return output.message.model
}
