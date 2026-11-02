# latency-degeneration

按 session 的请求耗时看门狗。宿主为每个受管 session 维护一份在途请求测量；
当一次请求的耗时超过该 session 的正常水平（由库侧 RequestLatencyDetector 的
统计阈值决定）时，中断该请求一次，并发送一条保留既有产出的续行提示。

**中断不关闭 Session**：受管会话须调用 `ISessionHostPort.InterruptAttempt`，不得用
`AbortSession`（它还会摘除并中止子代理）。但 OpenCode 没有单次 Provider attempt 的中断，
`InterruptAttempt` 在宿主侧就是对该 session 的 abort：它停止整个 Session 循环（含正在执行的工具），
子会话本身保留。所以中断一旦受理，续跑由看门狗负责到底（见 [011]、[012]）。
中断真实获接纳后在同一个 Session 发起 continuation，保持已产生的思考与输出。该动作以 `PromptDispatcher.AwaitMode.Detached` fire-and-forget 提交，并注册 `ContinuationAcceptanceObserver`；调用端不等待 SDK promise 或物理接受，只有 Host 的 `PhysicalAccepted` 回调能宣布已续跑。已明确拒绝或抛错则向原受管子会话的 `TerminalOutcome.Failed` 返回，供父 Join 收到失败；单纯排队不是成功。原请求的中断回执也由独立 callback 处理，不串行阻塞事件 hook。不设置第二个任意的恢复超时。`tests/host/009.test.mjs` 通过真实 SQLite/AgentJournal 验证 Detached 返回与物理确认的因果区别。

统计、阈值与样本语义（完整样本、右删失样本、遗忘）由 wanxiangli/degeneration
的 RequestLatencyDetector 拥有；本域只承诺宿主侧状态机与接线行为。阈值是 p99、**无先验**，
前 100 条观测是新手保护期（阈值 +∞，不打断），见 [014]。

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
提示，要求保留已经产生的思考与输出并从中断处继续。续行提示的确定拒绝
不回滚已成立的删失观测，须向受管子会话发布失败终局并返回给父方，不得只记诊断。
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

**更正（见 [013]）**：OpenCode 1.18.29 在同一个 Provider step 内执行工具，`finish=tool-calls` 与 `time.completed` 要等该步全部工具返回后才写入。所以本条单靠这个事件拦不住长工具，真正的工具边界是 `tool.execute.before`。以下原文保留：

模型返回 `finish=tool-calls` 并带真实 `time.completed` 时，该 **Provider 请求已结束**，虽然整个 Session 仍将在工具执行后继续。按 `SessionId + Assistant ProviderRunId` 精确完成本轮耗时计量，立即撤销定时器；不得把 `resetScope`、其它工具的执行、下一次模型请求累加成旧 Provider 请求的延迟并错误中断 Engineer。确认失败或用户取消亦按同一 ProviderRunId 精确撤销，但不记成功样本；迟到的旧终态不得撤销新回合。

现场依据：工作树 `.Herdchestra/wanxiangshu/events/*.ndjson` 中 2026-10-27 02:38–02:47 UTC 多个 Engineer 的 `ScopeReanchored` 后出现 `ProviderRetryAttempt`，其 payload SHA-256 与 `resources/provider/runtime/latency-degeneration/zh-CN.md` 去尾换行后的文本完全一致，且取消的物理 Provider 用户请求之间持续存在不同 assistant run 的 `XTracePartAppended(tool_result)`，证明看门狗延迟超出了单次模型请求。`tests/008.test.mjs` 验证真实 Codec 工具终态与精确撤销。

## [009] 异步因果接力与 fast-check 无内部等待

中断计时器的回调只尝试为确切 ProviderRun 建立单次接力，随后立即返回，不 await 任意 Host Promise。中断已受理时派发带真实物理接纳观察者的 Detached continuation；物理接纳才确认已续跑，确定拒绝则发布原受管子会话 Failed 终局，同一代际最多结算一次。Provider 真正自行继续、Session 取消、迟到旧回执或新 ProviderRun 启动必须按会话与代际围栏清除旧接力，**同时 Dispose 相应物理接纳观察器**，不能复活或覆盖新运行。 若宿主明确拒绝 InterruptAttempt，原 Provider 保留继续运行的权利；但在没有精确 Provider terminal 的情况下 Session 随后转 idle，或 idle 先抵达然后中断被拒，则原会话已无人继续，必须按本次代际一次性向父方返回 Failed，而不是只注销监测并写诊断。

`tests/host/009.test.mjs` 进一步通过真实 HostSessionNudge、SQLite/AgentJournal 验证首次明确拒绝、迟到 Refused、真实 PhysicalAccepted 与 OutcomeUnknown 都由对应原生回执驱动。

`tests/009.test.mjs` 的 seeded fast-check 使用生产同构 Watchdog，随机交错中断受理／拒绝／抛错、继续提交／拒绝、真实物理接纳／迟到、两种 idle 与拒绝顺序以及新旧回合切换；不使用人为的等待超时给无回执请求制造假成功。外部宿主若永远不发送任何完成或拒绝回执，领域不能伪造其结果；保证的是插件事件循环不等待它，下一次可核验 Host 事件仍能正常前进，并只凭可观察事件判定终局。

## [010] 精确物理取消不等待中断 Promise 才开始接力

看门狗已针对一个确切 `SessionId + ProviderRunId` 触发
`InterruptAttempt` 后，宿主可能先报告该 Provider 的真实
`UserCancelled` 终态，而 `InterruptAttempt` 的宿主回执 Promise 仍悬而未决。
这条精确终态已经证明原 Provider 停止，不能再用通常的 `Discard`
把看门狗的恢复身份删掉并令受管子代理无声停止。

- 中断回执仍待定且终态属于原 Provider 时，由终态事件接力发送
  `ProviderRetryAttempt`；只记录一次删失样本，真正
  `PhysicalAccepted` 才确认续跑，确定拒绝则向父方返回失败。
- 宿主先明确拒绝中断，但同一 Provider 随后真实取消时，原请求已经不再
  运行，应明确返回失败，不能只释放测量。
- 迟到的成功、拒绝或异常中断回执不得撤销已经发起的续行，也不得双重结算。
  旧 Provider 的迟到终态不得影响新的 ProviderRun。
- 由 steer 引起的 `Superseded` 不等于 Host 中断已成立，不能
  借此再派发一次 degeneration 续行。没有任何外部物理回执时继续
  保留未知事实；不增设第二个超时来伪造无死锁。

验收：`tests/010.test.mjs` 的可复现反例和 seeded fast-check
验证两种回执顺序、未决 Promise、物理接纳幂等及新旧 Provider 的隔离。

## [011] 中断在途时请求先行结束，已受理的中断仍须续跑

`InterruptAttempt` 在宿主侧是 `AbortSession`：一旦受理，它停止整个 Session 循环，
包括中断回执待定期间开始的工具执行与新的模型请求。因此看门狗已发出中断
（Aborting）后，若同一 ProviderRun 先以工具边界（`finish=tool-calls`）、失败终态结束，
或同一 session 已开始新的 ProviderRun，**不得**随测量一起撤销恢复所有权。
该次中断被停放到回执到达：回执为受理则发送一次续行（或续行被拒时返回 Failed）；
新 ProviderRun 在回执前被精确物理取消时立即续行；回执为拒绝或抛错则说明中断
没有停止任何东西，静默结束恢复所有权。最终 `Completed`（stop/length/content-filter）
已交付工作，记完整样本、不补发续行；但若回执前同一 session 又开始了新的 ProviderRun（会话循环
仍在继续，例如排队消息），受理的中断停下的就是它，仍须续行，且不再记删失样本。看门狗每次触发中断都记一条
`latency-degeneration-interrupt` 诊断，带阈值，使打断原因可见。

旧实现在上述窗口里只做 `Discard`/`Complete` 并撤销恢复，回执到达时找不到测量即
静默返回：子会话被 Abort 后无人续跑，父 Join 视 `Aborted` 为观察而继续等待，形成死锁。

回归：`tests/011.test.mjs` 的 seeded fast-check 随机交错工具边界、失败终态、新 run
启动／取消／丢弃与 idle，断言受理的中断恰好续行或返回一次；旧实现的最小反例为
`[["step-end"]]`。

## [012] 看门狗自己的 Abort 不是 SyncDelegate 的答案

SyncDelegate 调用一见 `Aborted` 就立即失败（DidNotAnswer / "run aborted"）。
看门狗的中断就是一次宿主 Abort，随后由看门狗续跑或返回 `Failed`；把它当成答案，
等于还没续就把咨询判死，用户只看到「咨询被打断」而不知原因。宿主在发出中断
**之前**为该 session 记一次（`DegenerationInterrupts.record`，宿主拒绝则撤回），
上述消费者各持一个游标，每次中断只豁免一条 `Aborted`；续跑失败时看门狗返回的
`Failed` 带 `degeneration continuation unavailable: …` 原因，照常让咨询失败。
Fork 的 Join 本来就把 `Aborted` 当观察，不受影响。

## [013] 工具开始执行即结束模型请求计时（真机）

真机事件序（OpenCode 1.18.29，`/event` 实录）：assistant `message.updated`（开始）→ 工具 part
`pending → running → completed` → **之后**才有同一 assistant 的 `finish=tool-calls` + `time.completed`。
工具在 Provider step 内执行，[008] 依赖的终态事件要等工具全部返回才到。旧实现因此把 `join`、bash、
咨询（askPreviousScope）、`negotiate` 等长工具的执行时间都算成模型延迟；阈值随快速步骤收紧
（无地板，几秒级）后，长工具几乎必被看门狗 Abort——这就是「经常被打断」「咨询都被打断」的来源。

宿主 `tool.execute.before` 一到，就把该 session 的在途测量按工具边界结束（`LatencyWatchdog.ToolStarted`，
样本是模型自己的耗时）。中断已在途时仍按 [011] 停放、由回执决定续跑。同一步的并行工具、无测量时的
工具开始均为空操作；下一次模型请求由新的 assistant 开始重新计量。

验收：`tests/013.test.mjs`（状态机 + 生产接线）。真机对照：`tests/support/run-real-host-degeneration.mjs`
（`REPRO_MODE=slowtool`，配合 `tests/support/slow-tool-plugin.mjs` 让 `negotiate` 在 step 内阻塞）——
撤掉 `ToolStarted` 时 15s 的工具在 3.3s 被 Abort（`aborted=true`），接上后完整执行（`aborted=false`）。

## [014] 新手保护期不武装定时器

库侧阈值无先验，p99 至少要 100 条观测：之前 `ThresholdMs` 为 +∞。宿主照常测量与记样本（样本本身
结束保护期），但 +∞ 阈值不调度任何定时器、不中断；这类从未武装的测量在 idle 时也不返回失败。
验收：`tests/014.test.mjs`。

## [015] 两次中断同时在途时，受理的回执仍各得一次结算

第二次测量的定时器可能在第一次中断的宿主回执仍待定时到点，于是同一 session 同时挂着两条在途中断。
旧实现只留一个恢复槽和一个停放槽：第二次触发覆盖第一次的恢复登记，第二次停放又覆盖第一次的停放测量；
第一次中断的**受理回执**到达时既找不到在途测量也找不到自己的停放项，便静默返回——
受理的 AbortSession 已停掉 session 循环，无人续行、无人报败，父方 Join 永远等待。

- 停放表按代际保留每一条待回执测量，新停放不再销毁旧停放；每条受理回执记自己那次测量的删失样本。
- 恢复登记按代际各留一条；同一时刻至多一条续行在途：第一条派发的回执拥有循环续跑，
  其后受理的回执被吸收（只记自己的删失样本，不再发第二条物理续行）。
- 拒绝或抛错的回执照 [011] 静默结清；[010] 的立即续行窗口、未知回执保持未定、以及纯用户取消
  （无中断在途时不续行、不报败）全部不变。Cancel 先清停放再撤恢复（见成员注释）。

回归：`tests/015.test.mjs` 以两种回执到达顺序、受理/拒绝的四种组合断言「受理的中断恰好结算一次、
被吸收的回执恰好记一次删失样本」，另覆盖两次受理只发一条续行与无中断在途的普通用户取消。
