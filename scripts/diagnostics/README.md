# 诊断工具套件 (Diagnostics Suite)

本目录提供面向万象术内核与 OpenCode 运行时的自动化诊断工具，用于在系统异常、卡死或崩溃时全息固化事故现场。

## 1. 全息事故现场采集脚本 (`collect-incident.mjs`)

### 运行方式
```bash
# 自动探测异常标签并全息采集
npm run diagnostics:collect

# 精准采集指定 Run 批次
npm run diagnostics:collect -- --run c7555bf6

# 指定自定义事故标签采集
npm run diagnostics:collect -- "provider-retry-loop"
```

### 输出规范
采集结果自动落盘至当前工程 `.diagnostics/` 目录（非 Git 仓库环境存至 `~/.local/state/wanxiang/diagnostics/`）：
```text
.diagnostics/incident-YYYYMMDD-HHmmss-[Tag]/
├── summary.md              # 事故全景诊断简报（核心指标、风险预警、对话流切片）
├── metadata.json           # 结构化事故元数据（RunID、Session、步数、Agent分布、系统环境）
├── process-snapshot.json   # 现场系统进程资源快照（PID、CPU%、RSS物理内存、运行时间）
├── chat-transcript.json    # 现场真实对话转录（原始 Prompt、大模型回复、工具调用入参）
├── run-scoped.log          # 异常周期的精准运行时日志（排除历史噪点）
├── events-tail.json        # 万象术内核最后 10 条状态机跳变事实
├── latest-events.ndjson    # 事故现场的万象术事件账本原始快照（支持跨工作区穿透）
├── wanxiangshu.mjs         # 当时生效的模型调度策略快照及语法校验结果
├── opencode-config.json    # 脱敏后的 Provider 与 Agent 物理配置快照
└── system-crash-*.ips      # 近 24 小时内的操作系统核心崩溃转储（macOS .ips）
```

## 2. 核心采集能力与保障
- **跨工作区账本穿透**：自动从运行时日志中解析当前任务关联的真实工作区（如外部测试仓），穿透提取真实的万象术不可篡改事件账本。
- **对话交互真实转录**：直连 OpenCode 底层 SQLite 数据库（`opencode.db`），提取真实用户 Prompt、大模型回复与系统注入消息，消除黑盒。
- **实时进程资源快照**：现场捕获 OpenCode 进程的 CPU 占用、RSS 物理内存使用量与执行时间，精准识别内存泄漏与濒死状态。
- **步数风暴与死循环预警**：自动监控单会话 Step 步数与角色分布，对超过阈值的异常递归（如 Spin-Lock / Loop-Storm）进行自动标记和警报。
- **精准生命周期切片**：根据 `run=<runId>` 精确切割当前执行周期日志。
- **机密数据安全脱敏**：采集配置时自动对所有 API Key 实施掩码处理（仅保留前 4 位和后 4 位）。
- **版本控制绝对隔离**：事故产物目录 `.diagnostics/` 已配置于 `.gitignore`，严防排查数据污染代码仓库。
