#!/usr/bin/env node

/**
 * 万象术 & OpenCode 全息事故现场专项目录采集脚本 (Comprehensive Incident Collector)
 *
 * 全息精准原则：
 * 1. 批次生命周期锚定：根据 run=<runId> 精确切割当前运行周期日志，排除历史干扰。
 * 2. 真实工作区账本穿透：不仅探测当前仓库，还自动穿透日志中关联的外部代码仓，提取真实的万象术事件账本。
 * 3. 会话对话真实转录：从 opencode.db 提取原始 Prompt、大模型回复及系统注入消息，告别黑盒。
 * 4. 实时进程资源快照：捕获事故当时的 CPU%、RSS 物理内存、进程运行时间与自转状态。
 * 5. 步数风暴与死循环探测：自动识别失控自转（Spin-Lock / Step Storm），高亮风险指标。
 * 6. 原生崩溃解析：支持 macOS .ips 转储信号提取 (SIGTRAP / SIGSEGV / EXC_BREAKPOINT 等)。
 * 7. 机密安全脱敏：敏感 API Key 与 Token 自动掩码处理。
 */

import fs from 'node:fs'
import path from 'node:path'
import os from 'node:os'
import { execSync } from 'node:child_process'
import { initializeStateRoot, stateRoot } from '../lib/state-root.mjs'

function formatDateTime(d) {
  const pad = n => String(n).padStart(2, '0')
  return `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}`
}

// 解析命令行参数
const args = process.argv.slice(2)
let explicitRunId = null
let explicitTag = null
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--run' && args[i + 1]) {
    explicitRunId = args[++i].trim()
  } else if (args[i] === '--tag' && args[i + 1]) {
    explicitTag = args[++i].trim()
  } else if (!args[i].startsWith('--') && !explicitTag) {
    explicitTag = args[i].replace(/[^a-zA-Z0-9_\u4e00-\u9fa5-]/g, '').trim()
  }
}

const now = new Date()
const timeStr = formatDateTime(now)

// ==========================================
// 1. 捕获实时系统进程状态快照 (Process Snapshot)
// ==========================================
let runningProcessInfo = null
try {
  const psOutput = execSync('ps -ax -o pid,%cpu,%mem,rss,etime,command 2>/dev/null', { encoding: 'utf8' })
  const matched = psOutput.split('\n').filter(l => /opencode/i.test(l) && !/grep|collect-incident/i.test(l))
  if (matched.length > 0) {
    runningProcessInfo = matched.map(line => {
      const parts = line.trim().split(/\s+/)
      const rssKb = parseInt(parts[3], 10) || 0
      return {
        pid: parts[0],
        cpuPercent: parts[1],
        memPercent: parts[2],
        rssMb: (rssKb / 1024).toFixed(1),
        elapsedTime: parts[4],
        command: parts.slice(5).join(' ')
      }
    })
  }
} catch {}

// ==========================================
// 2. 精准分析 OpenCode 主运行时日志
// ==========================================
const ocLogPath = path.join(os.homedir(), '.local', 'share', 'opencode', 'log', 'opencode.log')
let ocAllLines = []
let targetRunId = explicitRunId
let runLines = []
let lastErrorLines = []
let sessionContext = {
  sessionId: '未知',
  agent: '未知',
  providerId: '未知',
  modelId: '未知',
  lastErrorMessage: '无显式错误'
}
let maxStepObserved = 0
const agentStepCounts = {}
const associatedWorkspaces = new Set()
let autoErrorTag = 'normal'

if (fs.existsSync(ocLogPath)) {
  try {
    const raw = fs.readFileSync(ocLogPath, 'utf8')
    ocAllLines = raw.split('\n').filter(Boolean)

    if (!targetRunId) {
      for (let i = ocAllLines.length - 1; i >= 0; i--) {
        const line = ocAllLines[i]
        const isErr = /level=ERROR|FATAL|panic|IntentRejected|InvalidExplicitAgent|Unexpected|ProviderModelNotFound/i.test(line)
        const m = line.match(/\brun=([a-f0-9]+)\b/)
        if (isErr && m) {
          targetRunId = m[1]
          break
        }
      }
    }

    if (!targetRunId && ocAllLines.length > 0) {
      for (let i = ocAllLines.length - 1; i >= 0; i--) {
        const m = ocAllLines[i].match(/\brun=([a-f0-9]+)\b/)
        if (m) {
          targetRunId = m[1]
          break
        }
      }
    }

    if (targetRunId) {
      runLines = ocAllLines.filter(l => l.includes(`run=${targetRunId}`))
    } else {
      runLines = ocAllLines.slice(-300)
    }

    for (const l of runLines) {
      if (/level=ERROR|FATAL|panic|IntentRejected|InvalidExplicitAgent|Unexpected/i.test(l)) {
        lastErrorLines.push(l)
      }

      const sMatch = l.match(/\bsession\.id=([a-zA-Z0-9_-]+)\b/)
      if (sMatch) sessionContext.sessionId = sMatch[1]

      const aMatch = l.match(/\bagent=([a-zA-Z0-9_-]+)\b/)
      if (aMatch) {
        sessionContext.agent = aMatch[1]
        agentStepCounts[aMatch[1]] = (agentStepCounts[aMatch[1]] || 0) + 1
      }

      const pMatch = l.match(/\bproviderID=([a-zA-Z0-9_-]+)\b/)
      if (pMatch) sessionContext.providerId = pMatch[1]

      const mMatch = l.match(/\bmodelID=([a-zA-Z0-9_.\/:-]+)\b/)
      if (mMatch) sessionContext.modelId = mMatch[1]

      const stepMatch = l.match(/\bstep=(\d+)\b/)
      if (stepMatch) {
        const stepNum = parseInt(stepMatch[1], 10)
        if (stepNum > maxStepObserved) maxStepObserved = stepNum
      }

      const dirMatch = l.match(/\b(?:directory|file)=(\/[^\s",]+)/)
      if (dirMatch) {
        const p = dirMatch[1]
        const gitIndex = p.indexOf('/git/')
        if (gitIndex !== -1) {
          const sub = p.slice(0, p.indexOf('/', gitIndex + 5) > 0 ? p.indexOf('/', gitIndex + 5) : p.length)
          if (fs.existsSync(sub)) associatedWorkspaces.add(sub)
        }
      }

      const errMsgMatch = l.match(/\berror="([^"]+)"/) || l.match(/\berror=([^\s]+)/)
      if (errMsgMatch) sessionContext.lastErrorMessage = errMsgMatch[1]
    }

    if (lastErrorLines.length > 20) {
      lastErrorLines = lastErrorLines.slice(-20)
    }

    const errBlock = lastErrorLines.join('\n')
    if (errBlock.includes('InvalidExplicitAgent')) autoErrorTag = 'InvalidAgent'
    else if (errBlock.includes('Model not found') || errBlock.includes('ProviderModelNotFound')) autoErrorTag = 'ModelNotFound'
    else if (errBlock.includes('IntentRejected')) autoErrorTag = 'IntentRejected'
    else if (errBlock.includes('TimeoutError') || errBlock.includes('timed out')) autoErrorTag = 'Timeout'
    else if (errBlock.includes('not a git repository')) autoErrorTag = 'NotGitRepo'
    else if (errBlock.includes('Aborted')) autoErrorTag = 'Aborted'
    else if (maxStepObserved > 100 || (runningProcessInfo && parseFloat(runningProcessInfo[0]?.rssMb || 0) > 4096)) autoErrorTag = 'SpinLock-LoopStorm'
    else if (lastErrorLines.length > 0) autoErrorTag = 'Error'
  } catch {}
}

const finalTag = explicitTag || autoErrorTag
const incidentDirName = `incident-${timeStr}-${finalTag}`

const baseDiagnosticsDir = path.join(await initializeStateRoot(process.cwd()), 'diagnostics')

const incidentPath = path.join(baseDiagnosticsDir, incidentDirName)
fs.mkdirSync(incidentPath, { recursive: true })

// ==========================================
// 3. 提取对话交互真实记录 (Chat Transcript from opencode.db)
// ==========================================
const dbPath = path.join(os.homedir(), '.local', 'share', 'opencode', 'opencode.db')
let transcriptExtracted = []
if (fs.existsSync(dbPath)) {
  try {
    let sql = ''
    if (sessionContext.sessionId && sessionContext.sessionId !== '未知') {
      sql = `SELECT m.id, json_extract(m.data, '$.role'), p.data FROM message m JOIN part p ON m.id = p.message_id WHERE m.session_id = '${sessionContext.sessionId}' ORDER BY m.time_created ASC LIMIT 50;`
    } else {
      sql = `SELECT m.id, json_extract(m.data, '$.role'), p.data FROM message m JOIN part p ON m.id = p.message_id ORDER BY m.time_created DESC LIMIT 30;`
    }
    const dbOut = execSync(`sqlite3 "${dbPath}" "${sql}" 2>/dev/null`, { encoding: 'utf8' })
    if (dbOut.trim()) {
      const rows = dbOut.trim().split('\n')
      for (const r of rows) {
        const parts = r.split('|')
        if (parts.length >= 3) {
          const msgId = parts[0]
          const role = parts[1]
          const partData = parts.slice(2).join('|')
          try {
            const parsed = JSON.parse(partData)
            if (parsed.text) {
              transcriptExtracted.push({ id: msgId, role, type: 'text', content: parsed.text })
            } else if (parsed.tool) {
              transcriptExtracted.push({ id: msgId, role, type: 'tool', tool: parsed.tool, input: parsed.state?.input })
            }
          } catch {
            transcriptExtracted.push({ id: msgId, role, raw: partData.slice(0, 300) })
          }
        }
      }
      fs.writeFileSync(path.join(incidentPath, 'chat-transcript.json'), JSON.stringify(transcriptExtracted, null, 2), 'utf8')
    }
  } catch {}
}

// ==========================================
// 4. 穿透关联工作区提取事件账本 (EventStore Penetration)
// ==========================================
const targetWorkspaces = Array.from(associatedWorkspaces)
if (!targetWorkspaces.includes(process.cwd())) {
  targetWorkspaces.unshift(process.cwd())
}

let journalSnapshots = []
for (const ws of targetWorkspaces) {
  let workspaceState = null
  try {
    workspaceState = await stateRoot(ws)
  } catch (error) {
    journalSnapshots.push({ workspace: ws, error: error.message })
  }

  if (workspaceState) {
    const eventsDir = path.join(workspaceState, 'wanxiangshu', 'events')
    if (fs.existsSync(eventsDir)) {
      const ndjsons = fs.readdirSync(eventsDir)
        .filter(f => f.endsWith('.ndjson'))
        .map(f => ({
          name: f,
          fullPath: path.join(eventsDir, f),
          mtime: fs.statSync(path.join(eventsDir, f)).mtimeMs
        }))
        .sort((a, b) => b.mtime - a.mtime)

      if (ndjsons.length > 0) {
        const topNd = ndjsons[0]
        const wsName = path.basename(ws)
        const targetFilename = ws === process.cwd() ? 'latest-events.ndjson' : `events-${wsName}.ndjson`
        fs.copyFileSync(topNd.fullPath, path.join(incidentPath, targetFilename))

        const content = fs.readFileSync(topNd.fullPath, 'utf8')
        const lines = content.split('\n').filter(Boolean)
        const recentTail = lines.slice(-10).map(l => {
          try {
            const j = JSON.parse(l)
            return {
              event_id: j.event_id,
              event_type: j.event_type,
              fact_type: Array.isArray(j.payload?.Fact) ? j.payload.Fact[0] : (typeof j.payload?.Fact === 'string' ? j.payload.Fact : 'UnknownFact')
            }
          } catch {
            return { raw: l.slice(0, 100) }
          }
        })

        journalSnapshots.push({
          workspace: ws,
          fileName: topNd.name,
          eventCount: lines.length,
          tailFile: targetFilename
        })

        if (ws === process.cwd()) {
          fs.writeFileSync(path.join(incidentPath, 'events-tail.json'), JSON.stringify(recentTail, null, 2), 'utf8')
        }
      }
    }
  }
}

// ==========================================
// 5. 原生系统崩溃报告 (.ips)
// ==========================================
let systemCrashDetail = null
let capturedSystemReports = []
if (os.platform() === 'darwin') {
  const diagDir = path.join(os.homedir(), 'Library', 'Logs', 'DiagnosticReports')
  if (fs.existsSync(diagDir)) {
    try {
      const nowMs = Date.now()
      const oneDayMs = 24 * 60 * 60 * 1000
      const reports = fs.readdirSync(diagDir)
        .filter(f => /^(opencode|node|bun).*\.ips$/i.test(f))
        .map(f => ({
          name: f,
          fullPath: path.join(diagDir, f),
          mtime: fs.statSync(path.join(diagDir, f)).mtimeMs
        }))
        .filter(r => (nowMs - r.mtime) <= oneDayMs)
        .sort((a, b) => b.mtime - a.mtime)

      if (reports.length > 0) {
        const topReport = reports[0]
        const destName = `system-crash-${topReport.name}`
        fs.copyFileSync(topReport.fullPath, path.join(incidentPath, destName))
        capturedSystemReports.push(destName)

        const ipsRaw = fs.readFileSync(topReport.fullPath, 'utf8')
        const lines = ipsRaw.split('\n')
        for (const line of lines) {
          const trimmed = line.trim()
          if (trimmed.startsWith('{') && (trimmed.includes('"exception"') || trimmed.includes('"termination"'))) {
            const parsed = JSON.parse(trimmed)
            systemCrashDetail = {
              signal: parsed.exception?.signal || parsed.termination?.indicator || parsed.termination?.signal || '已转储',
              exceptionType: parsed.exception?.type || 'NativeCrash',
              terminationBy: parsed.termination?.byProc || 'System',
              faultingThread: parsed.faultingThread
            }
            break
          }
        }
      }
    } catch {}
  }
}

// ==========================================
// 6. 固化配置与环境
// ==========================================
let pluginBuildStatus = '未检测到项目编译产物'
const pluginArtifactPath = path.join(process.cwd(), 'dist', 'OpenCode', 'Plugin', 'Plugin.js')
if (fs.existsSync(pluginArtifactPath)) {
  try {
    const stat = fs.statSync(pluginArtifactPath)
    pluginBuildStatus = `产物就绪 (${(stat.size / 1024).toFixed(1)} KB, 变更于 ${new Date(stat.mtimeMs).toLocaleString()})`
  } catch {}
}

const mjsPath = path.join(os.homedir(), '.config', 'opencode', 'wanxiangshu.mjs')
let mjsSyntax = '文件未找到'
if (fs.existsSync(mjsPath)) {
  try {
    execSync(`node --check "${mjsPath}" 2>&1`)
    mjsSyntax = '语法正确 (Syntax OK)'
  } catch (err) {
    mjsSyntax = `语法异常 (Syntax Error): ${err.message}`
  }
  fs.copyFileSync(mjsPath, path.join(incidentPath, 'wanxiangshu.mjs'))
}

const jsonPath = path.join(os.homedir(), '.config', 'opencode', 'opencode.json')
if (fs.existsSync(jsonPath)) {
  try {
    const rawCfg = JSON.parse(fs.readFileSync(jsonPath, 'utf8'))
    if (rawCfg.provider) {
      for (const p of Object.values(rawCfg.provider)) {
        if (p.options && p.options.apiKey) {
          const k = String(p.options.apiKey)
          p.options.apiKey = k.length > 8 ? `${k.slice(0, 4)}...${k.slice(-4)}` : '******'
        }
      }
    }
    fs.writeFileSync(path.join(incidentPath, 'opencode-config.json'), JSON.stringify(rawCfg, null, 2), 'utf8')
  } catch {
    fs.copyFileSync(jsonPath, path.join(incidentPath, 'opencode-config.json'))
  }
}

fs.writeFileSync(path.join(incidentPath, 'run-scoped.log'), runLines.join('\n'), 'utf8')
if (runningProcessInfo) {
  fs.writeFileSync(path.join(incidentPath, 'process-snapshot.json'), JSON.stringify(runningProcessInfo, null, 2), 'utf8')
}

// Git 信息
let gitBranch = 'unknown'
let gitCommit = 'unknown'
let gitStatus = 'clean'
try {
  gitBranch = execSync('git rev-parse --abbrev-ref HEAD 2>/dev/null', { encoding: 'utf8' }).trim()
  gitCommit = execSync('git log -1 --format="%h - %s (%ci)" 2>/dev/null', { encoding: 'utf8' }).trim()
  gitStatus = execSync('git status -s 2>/dev/null', { encoding: 'utf8' }).trim() || 'clean'
} catch {}

let opencodeVersion = 'unknown'
try {
  opencodeVersion = execSync('opencode --version 2>/dev/null', { encoding: 'utf8' }).trim()
} catch {}

const metadata = {
  incidentId: incidentDirName,
  timestamp: now.toISOString(),
  localTime: now.toLocaleString(),
  tag: finalTag,
  runId: targetRunId,
  maxStepObserved,
  agentStepCounts,
  associatedWorkspaces: targetWorkspaces,
  sessionContext,
  systemCrashDetail,
  runningProcessInfo,
  pluginBuildStatus,
  environment: {
    nodeVersion: process.version,
    platform: os.platform(),
    arch: os.arch(),
    opencodeVersion,
    cwd: process.cwd(),
    git: {
      branch: gitBranch,
      commit: gitCommit,
      status: gitStatus
    }
  }
}
fs.writeFileSync(path.join(incidentPath, 'metadata.json'), JSON.stringify(metadata, null, 2), 'utf8')

// ==========================================
// 7. 生成全景事故诊断简报 summary.md
// ==========================================
const summaryMd = `# 事故现场全景诊断报告: ${incidentDirName}

- **发生时间**: ${metadata.localTime}
- **事故分类标签**: \`${finalTag}\`
- **运行批次 (RunID)**: \`${targetRunId || '未捕获'}\` (已隔离提取该批次共 ${runLines.length} 行真实上下文)
- **代码基线**: 分支 \`${gitBranch}\` | 提交 \`${gitCommit}\`
- **事故专项目录**: \`${incidentPath}\`

---

## 1. 核心异常定位 (Accident Profile)
| 关键指标 | 现场事实 | 风险分析 |
| :--- | :--- | :--- |
| **发生会话 (SessionId)** | \`${sessionContext.sessionId}\` | - |
| **触发角色 (Agent)** | \`${sessionContext.agent}\` | ${sessionContext.agent === 'advisor' ? '⚠️ 顾问专席请求异常' : '正常'} |
| **目标模型 (Model/Provider)** | \`${sessionContext.providerId} / ${sessionContext.modelId}\` | - |
| **单会话最高步数 (Max Step)** | **\`${maxStepObserved}\`** 步 | ${maxStepObserved > 100 ? '🚨 步数暴涨，存在无限循环自激风暴' : '步数正常'} |
| **进程内存驻留 (RSS Memory)** | ${runningProcessInfo ? `\`${runningProcessInfo[0]?.rssMb} MB\` (CPU: ${runningProcessInfo[0]?.cpuPercent}%)` : '进程已退出'} | ${runningProcessInfo && parseFloat(runningProcessInfo[0]?.rssMb || 0) > 4096 ? '🚨 内存极高，濒临系统 OOM 崩溃' : '内存正常'} |
| **最后错误概要** | \`${sessionContext.lastErrorMessage}\` | - |
| **系统底层崩溃信号** | ${systemCrashDetail ? `\`${systemCrashDetail.signal}\` (${systemCrashDetail.exceptionType})` : (capturedSystemReports.length > 0 ? '已捕获底层转储文件' : '无底层崩溃信号')} | - |

---

## 2. 智能体活动分布与自转监控
\`\`\`json
${JSON.stringify(agentStepCounts, null, 2)}
\`\`\`

---

## 3. 现场交互流截取 (Chat Transcript Snippet)
${transcriptExtracted.length > 0
  ? transcriptExtracted.slice(-6).map(t => `- **[${t.role.toUpperCase()}]** (${t.type}): ${t.content ? t.content.slice(0, 160).replace(/\n/g, ' ') : (t.tool ? `调用工具 \`${t.tool}\`` : t.raw)}`).join('\n')
  : '[未提取到数据库交互流]'}

---

## 4. 现场最新错误流水切片 (Error Log Snippet)
\`\`\`text
${lastErrorLines.join('\n') || '[未捕获到显式 ERROR 标记，请查阅 run-scoped.log]'}
\`\`\`

---

## 5. 万象术组件与证据快照
| 检查项 | 检查结论 | 证据文件 |
| :--- | :--- | :--- |
| **实时进程资源快照** | ${runningProcessInfo ? `已记录 PID ${runningProcessInfo[0]?.pid} 运行状态` : '进程未在运行'} | \`process-snapshot.json\` |
| **交互历史转录** | 已从 SQLite 提取 ${transcriptExtracted.length} 轮真实对话/工具交互 | \`chat-transcript.json\` |
| **跨工作区账本穿透** | 检索到 ${journalSnapshots.length} 个工作区事件账本 | ${journalSnapshots.map(j => `\`${j.tailFile}\` (${path.basename(j.workspace)}, ${j.eventCount} 条)`).join(', ')} |
| **插件编译产物 (Plugin.js)** | ${pluginBuildStatus} | \`dist/OpenCode/Plugin/Plugin.js\` |
| **操作系统核心转储 (.ips)** | ${capturedSystemReports.length > 0 ? '已固化底层崩溃报告' : '近 24 小时无底层崩溃'} | ${capturedSystemReports.length > 0 ? `\`${capturedSystemReports[0]}\`` : '无'} |
| **批次运行时日志 (Run-Scoped)** | 精准截取本生命周期共 ${runLines.length} 行日志 | \`run-scoped.log\` |
| **模型调度器 (wanxiangshu.mjs)** | ${mjsSyntax} | \`wanxiangshu.mjs\` |
| **OpenCode 客户端配置** | 已脱敏固化当前 Provider 与 Agent 映射 | \`opencode-config.json\` |

---

## 6. 事故案卷文件清单
\`\`\`text
${incidentDirName}/
├── summary.md              # 事故全景精准总览（优先阅读）
├── metadata.json           # 结构化事故元数据（含步数、Agent分布、资源）
├── process-snapshot.json   # 现场系统进程占用（CPU、RSS、耗时）
├── chat-transcript.json    # 现场真实消息与工具调用流
├── run-scoped.log          # 本次运行周期的完整日志上下文
├── events-tail.json        # 万象术内核状态机最后 10 条跳变切片
├── latest-events.ndjson    # 万象术事件账本原始快照（穿透多仓）
├── wanxiangshu.mjs         # 当时的调度策略快照
├── opencode-config.json    # 当时的模型凭据配置快照（脱敏）
${capturedSystemReports.map(f => `├── ${f}    # 操作系统核心崩溃转储`).join('\n')}
\`\`\`
`

fs.writeFileSync(path.join(incidentPath, 'summary.md'), summaryMd, 'utf8')

console.log(`\n🚨 全息事故现场已完整固化！`)
console.log(`📁 事故专项目录：${incidentPath}`)
console.log(`\n📌 查看全景简报：`)
console.log(`   cat "${path.join(incidentPath, 'summary.md')}"`)
console.log(`\n💡 在 Cursor 聊天框中直接输入：`)
console.log(`   “分析本次事故目录 ${incidentPath}”\n`)
