import { spawnSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'

/**
 * 校验指定二进制是否能在当前宿主机器架构上正常执行。
 * 返回 true 表示可用；返回 false 表示失配或不可执行（例如 Linux ELF 在 macOS 上报 ENOEXEC）。
 */
function isBinaryExecutable(binPath) {
  if (!fs.existsSync(binPath)) return false
  try {
    const res = spawnSync(binPath, ['--version'], {
      encoding: 'utf8',
      timeout: 3000,
    })
    return res.status === 0 && !res.error
  } catch {
    return false
  }
}

/**
 * 确保 node_modules 中的 opencode 原生二进制与当前宿主机系统/CPU架构匹配。
 * 若检测到不存在或架构失配（如 macOS 出现 Linux ELF 报 ENOEXEC），自动调用 postinstall 自愈。
 *
 * @param {{ root?: string, quiet?: boolean }} [options]
 * @returns {boolean} 自愈/检查是否成功
 */
export function ensureNativeOpencode({ root = process.cwd(), quiet = false } = {}) {
  const localBin = path.join(root, 'node_modules', '.bin', 'opencode')
  const packageDir = path.join(root, 'node_modules', 'opencode-ai')
  const postinstallScript = path.join(packageDir, 'postinstall.mjs')

  if (!fs.existsSync(packageDir) || !fs.existsSync(postinstallScript)) {
    return true
  }

  if (isBinaryExecutable(localBin)) {
    return true
  }

  if (!quiet) {
    console.warn(`[ensure-native-opencode] 检测到 opencode 二进制缺失或与当前架构 (${process.platform}-${process.arch}) 失配，正在执行自动自愈...`)
  }

  try {
    const res = spawnSync(process.execPath, [postinstallScript], {
      cwd: packageDir,
      encoding: 'utf8',
      timeout: 60000,
      stdio: quiet ? 'ignore' : 'inherit',
    })
    if (res.status !== 0 && !quiet) {
      console.error(`[ensure-native-opencode] 自动自愈失败，postinstall 退出码: ${res.status}`)
    }
  } catch (err) {
    if (!quiet) {
      console.error(`[ensure-native-opencode] 执行 postinstall 遇到异常:`, err)
    }
  }

  const ok = isBinaryExecutable(localBin)
  if (ok && !quiet) {
    console.warn(`[ensure-native-opencode] opencode 原生二进制自愈完成并已通过验证。`)
  }
  return ok
}
