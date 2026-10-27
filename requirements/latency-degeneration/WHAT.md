# latency-degeneration

按 session 的请求耗时看门狗。宿主为每个受管 session 维护一份在途请求测量；
当一次请求的耗时超过该 session 的正常水平（由库侧 RequestLatencyDetector 的
统计阈值决定）时，中断该请求一次，并发送一条保留既有产出的续行提示。

**中断的是正在生成的 Provider attempt，而非关闭 Session**：受管会话须调用
`ISessionHostPort.InterruptAttempt`，不得用 `AbortSession` 结束整个子会话后再向已结束会话投递。
中断真实获接纳后在同一个 Session 发送 continuation，使模型接着已保留的思考与输出继续；拒绝中断不谎报成功。该 continuation 由既有 `HostSessionNudge.sendContinuationResult` 经 `PromptDispatcher.AwaitMode.Await` 和真实 `ContinuationAcceptanceObserver` 发送，等待 Host `PhysicalAccepted` 后才报告续行成功（接受观察预算 30 秒）；单纯 SDK 排队回执不能冒充续行。`requirements/latency-degeneration/tests/002.test.mjs` 验证生产 managed child 中断后 Session 仍活跃、port 实际接线以及 SQLite/AgentJournal 上排队和物理接纳的区别。

统计、阈值与样本语义（完整样本、右删失样本、遗忘）由 wanxiangli/degeneration
的 RequestLatencyDetector 拥有；本域只承诺宿主侧状态机与接线行为。

## [001] 按 session 统计完整样本，完成按请求身份精确归属

一次受管请求正常完成时，宿主把该次请求的完整耗时记入该 session 的统计。
完成事件按请求身份与该 session 的在途测量配对：身份不一致的迟到完成事件
不得归入在途的新请求，也不得打扰它；任一侧缺失请求身份、无法建立唯一归属
时，放弃该样本，不记入统计。同一 session 内新请求开始而旧测量的完成事件
缺失时，旧测量被放弃，不记样本。被打断后到达的残缺完成事件不得记为完整
样本。同一请求不得同时记入一个完整样本和一个删失样本。

## [002] 右删失样本只增保守性

一次请求被看门狗中断成立时，宿主把该次请求按删失样本记入该 session 的
统计，删失时长不小于该次测量使用的阈值。中断未被接受（宿主拒绝）时，
不记删失样本，该次测量保留在途；若该请求随后真实完成，仍记完整样本。

## [003] 超过 session 阈值触发中断

每个受管 session 至多一条在途测量。请求开始时按该 session 的当前阈值
武装一次一次性定时器；到点且测量仍在途、代际一致时，向宿主发出该
session 的一次中断。重复的请求开始不重置起算点，也不重复武装。

## [004] 中断成立后发送续行提示且保留已有输出

中断被宿主接受并记入删失样本后，宿主以该 session 的语言发送一条续行
提示，要求保留已经产生的思考与输出并从中断处继续。续行提示的发送
失败不回滚已成立的删失观测，只记诊断。
中断回执尚未返回时收到 idle，不得丢弃该次中断；接受回执到达后仍须发送
续行提示。会话删除或已证明的正常完成仍可取消该次续行。

## [005] 只有受管 session 生效

非受管 session 的请求完全不启动测量：不武装定时器、不记任何样本、
不中断、不发送续行提示。session 删除时，在途测量直接丢弃，不记样本，
该 session 的统计被遗忘。

## [006] 中断成立后不重复中断同一请求

一次测量只发出一次中断：定时器触发后立即解除武装，不再重排定时器。
中断未被接受时同样不再重排，避免中断风暴。迟到或代际不符的定时器
回调不产生任何效果。

## 接线缺口（如实记录，非承诺）

宿主 idle 信号（session.status idle / session.idle）由 PluginHooks 观察；
若宿主未推送 idle，此类既无完成
事件也无 idle 信号的残缺测量，只能由 session 删除（Cancel）或插件
dispose 收尾。

## [008] 工具边界终结模型请求计时

模型返回 `finish=tool-calls` 并带真实 `time.completed` 时，该 **Provider 请求已结束**，虽然整个 Session 仍将在工具执行后继续。按 `SessionId + Assistant ProviderRunId` 精确完成本轮耗时计量，立即撤销定时器；不得把 `resetScope`、其它工具的执行、下一次模型请求累加成旧 Provider 请求的延迟并错误中断 Engineer。确认失败或用户取消亦按同一 ProviderRunId 精确撤销，但不记成功样本；迟到的旧终态不得撤销新回合。

现场依据：工作树 `.Herdchestra/wanxiangshu/events/*.ndjson` 中 2026-10-27 02:38–02:47 UTC 多个 Engineer 的 `ScopeReanchored` 后出现 `ProviderRetryAttempt`，其 payload SHA-256 与 `resources/provider/runtime/latency-degeneration/zh-CN.md` 去尾换行后的文本完全一致，且取消的物理 Provider 用户请求之间持续存在不同 assistant run 的 `XTracePartAppended(tool_result)`，证明看门狗延迟超出了单次模型请求。`requirements/latency-degeneration/tests/003.test.mjs` 验证真实 Codec 工具终态与精确撤销。
