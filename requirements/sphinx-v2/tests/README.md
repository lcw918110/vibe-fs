# sphinx-v2 测试说明

本目录是取代旧 epistemic-reasoning 的活动测试。WHAT 保持新上游 36 条合同；旧价格公式、阶段工具和内核不能借测试迁回来。对应关系见 [SUPERSEDES](../SUPERSEDES.md)。

## 当前生产路径

七件套工具各自解码；start/status/export 走共享 Commands，cancel 仍在 MCP 内调 Admission、Codec 与同一个 store 的 Append，跨入口先刷新。`work_next` / `work_submit` / `goal_amend` 仍是具名 unsupported，JS 的 submitResults 也拒绝，不返回空 receipts 当成功。036 覆盖了现存 inquiry 的读取与导出；完整业务 driver、Host 取消、模型与 lease 观察器仍未接通。源码存在不等于业务可用。

每个 canonical envelope 带完整 TransitionBatch，Integrator 从 exact accepted parent 恢复基态后整体应用；合法 fork 保留多个 heads，canonical Current 返回 `DomainConflict`，不挑一个分支当真相。019 覆盖真实创建 → 请求取消 → 取消终态 → 新 writer 重开。`CancelRequested` 只是请求。

`Tool.unsupported` 是剩余实现限制，不是已完成业务。无配置的 start 会具名拒绝，这不代替配置后的创建正例。claim/submit/amend 要去掉 unsupported，得先真正接通 Runtime 与 durable 合同，不能在 `bodyOf` 里加个 case 或拿空 trace 顶替。

MCP 启动需要 `SPHINX_COMMON_DIR`；可选 `SPHINX_START_CONFIG` 是显式 JSON：`commandNamespace`、`createdBy`、`profileRef="sphinx.default@2"`、`executionMode="delegated"|"independent"`、非空 `resourceSpecs` 与 `renderReserve`。资源项形如 `{"name":"calls","kind":{"case":"consumed","payload":"calls"},"authorizedLimit":0}`；零额度也要显式给。缺配置只能读，start 返回 `CONFIG_REQUIRED`；非法配置启动失败。

## 真实 SDK stdio 适配器合同

036 走一条真实协议路径。进程 fixture 就地放在测试文件里，不新增公共测试入口，也不改共用 support。所有协议子测共享一次 ServeEntry 生命周期：已安装的 SDK Client/StdioClientTransport 直接以 Node 启动最终产物 `dist/Sphinx/V2/ServeEntry.js`，不使用 eval/import wrapper、不调用私有 handler、不替换 Server/schema/SDK。ServeEntry 源码和当前产物都有 runIfEntryPoint，直接执行它会走生产 serveDefault。每次用 mkdtemp 创建隔离目录，将它同时作为 cwd 和 SPHINX_COMMON_DIR；transport 只继承 SDK 的默认安全环境，不继承父测试的 WANXIANGSHU_NO_FATAL_EXIT、NODE_OPTIONS 等旁路。

Client.connect 完成 initialize 并发出 initialized，然后 listTools 检查七件套的精确名单。独立 metadata 子测检查 goalAmend 的 replacementText 不在 required 中、expectedRevision 在 required 中；metadata 红灯不吞掉后面的协议反例。接着用真实 callTool 观察：

- 合法 work_submit 形状目前落到 unsupported 分支，这是 ingress 正对照，不是结果接纳；schema hash 用给定 canonical schema 文档的真实 SHA-256，但这不代表该 schema 已注册或匹配真实 work ticket。
- 同一合法形状分别携带 certificatePatches、budgetDebit、events、goalRevision、goalAmendment：必须收到 WORK_RESULT_EXCEEDS_ROLE，path 精确为各字段。SDK 剥字段后落入 unsupported 分支会红，不能只靠 parser 测试给它开绿灯。
- status 与 summary/full export 分别携带 command、commands 及上述五类变动，必须收到 INVALID_SCHEMA 和精确字段 path。干净未知inquiry的status/export均为UNKNOWN_INQUIRY；历史trace-unavailable对照已替换。未知分支只说明拒绝可达；已有 inquiry 的读取/导出与业务零写入由配置创建、跨入口与冷进程轨迹另外覆盖。
- goalAmend 使用显式合法版本串 expectedRevision='0'。省略 replacementText 和提供非空文本应越过 parser；当前未接业务时是 unsupported 对照。replacementText 是 optional、非 nullable：显式 null 与空白文本均不得当作省略。直接公开 Tool parser 对两者仍严格断言 INVALID_SCHEMA/refusal.path=replacementText，真实 callTool 的空白文本也保留此具名业务拒绝。null 可由 SDK 类型 schema 拒绝：接受公开 McpError/InvalidParams，或原生 isError=true 的正式结果，但必须有非空可读错误，指出 replacementText 并说明类型或 null 原因；空错误、其它字段错误、进程失败和其它协议错误仍使测试失败。无需把 SDK 参数错误转换成业务 JSON，不能为此 advertise nullable 或绕过 SDK 类型检查。WHAT018/034/036 完整正文没有要求所有 schema 错型均返回业务 JSON。

Tool 业务拒绝的响应检查要求原生 MCP result、isError=true，以及 structuredContent 内的 apiVersion/outcome/refusal.code/path/message，或 content 中一份非空、可读的同合同 JSON。它不读取 DU tag/fields，不接受只有空 content 的拒绝或 isError=false 的假成功；纯正负对照让空内容、非 JSON、缺字段和空文案成为反例。上述 null schema 错型测项单独遵守 SDK 参数错误合同，不将这条宽容分支用于 work_submit 五类夹带写字段或 status/export 明确写意图；后两类仍必须到达 Tool 并返回精确 code/path，SDK 通用参数错误不能代替。unsupported只识别剩余未接通的操作，不是已完成业务的永久承诺；trace-unavailable已删除，后续owner接通时保留全部ingress拒绝断言。

具名失败码保留定位信息：MCP_ENTRY_UNAVAILABLE 是最终产物缺失；MCP_BOOT_OR_REGISTRATION_FAILED 带 initialize 等待点、原始 cause 与 stderr 尾部，可定位注册阶段的 Emit 参数错误；MCP_INGRESS_REFUSAL_MISMATCH 报告字段拒绝未到达（包括 strip 后落入 unsupported/unknown）；MCP_RESPONSE_LAYOUT 表示 SDK 已交回 result 但业务 JSON/refusal 布局不合合同；MCP_PROTOCOL_RESPONSE_FAILED 保留 SDK 本身对非法 MCP response 的拒绝。它们不把所有启动错误都猜成 Emit，也不把所有错码都断言为 strip。

挂死监测复用 verification-system/tests/e2e/support/watchdog.js，不改默认静默窗口：只有 initialize、listTools、callTool 真正完成才续期，stderr、诊断与后台噪音不续期，也没有另加总墙时或放大 timeout。成功和失败都在 finally 里关闭 Client/transport，再等预先订阅的 transport.onclose；SDK 1.30.0 的 client/stdio.js 在底层 ChildProcess close 后才调用它，shared/protocol.js 的 connect 会保留旧回调。SDK close 在 SIGKILL 后可能先返回，因此只等 close() 或 pid=null 不算实际退出。cleanup 用同一默认 Watchdog 有界等待，未观测退出即 MCP_SERVER_EXIT_UNOBSERVED 并保留目录，不伪报收束；外部执行监督器仍负责异常不可回收进程组。

复核这块可跑 `node --test requirements/sphinx-v2/tests/018.test.mjs requirements/sphinx-v2/tests/034.test.mjs requirements/sphinx-v2/tests/036.test.mjs`；正式交付仍走受监督入口。这个 fixture 只覆盖 MCP stdio 适配器，不是 Host/provider Long Stroke，也没有第二条昂贵 E2E。进程退出已有公开观察点，不另造生产 entry 的替身。

036 的现存 inquiry 重复读取：实际 start 与 cancel 作为真实业务写入的正对照，查询前后完整 journal 字节不变。这只说明查询本身不写业务；lease、dispatch、模型调用还缺能产生它们的前向对照。036 的两个 TODO 与 034 的 Host receipt/终止等待/迟到结果继续保留，不能拿永远为零的观察器当作零 effect 已完成。


## 运行

本目录完整局部套件是 `node --test requirements/sphinx-v2/tests/*.test.mjs`；正式交付走 verification-system 的受监督入口。036 仍有两个完整 TODO（含真实 lease/model 正向观察器），034 仍有 Host 合同 TODO。v2 MCP 是独立接入端口，Host 原生 `/sphinx question` 适配器不启动也不注入 MCP，两者可以并存；工具名单存在不代表 Host 配置要注入 MCP。
