import path from 'node:path'
import { primaryDirectory, checkStateDirectory, initializeStateDirectory } from 'wanxiangli/workspace-state'

// 诊断脚本直接消费公开库入口，无需加载应用 dist。
export async function stateRoot(workspace) {
  return checkStateDirectory(path.join(await primaryDirectory(workspace), '.Herdchestra'))
}

export async function initializeStateRoot(workspace) {
  return initializeStateDirectory(await stateRoot(workspace))
}
