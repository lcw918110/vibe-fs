# advisor — WHAT

Advisor 是每个工作 Session 专用、持续复用的无工具咨询席位。工作者不能直接调用顾问。ScopeProgress 根据真实已结算的计费回合触发咨询，AdvisorFlow 通过隔离的短命工作者对话取材，AdvisorSeat 保存顾问自身持续增长的会话，最终由物理 steer 送回建议。宿主不向参与模型解释编排机制。

## [001] 专席身份与连续会话

同一工作 Session 只对应一个 dedicated Advisor。首次按需建立，之后继续同一席位的对话历史。物理子会话挂在家族根，不允许 Advisor 再派生 Advisor。重启后必须优先通过真实子会话列表认领相同席位，不得无条件新建。原始会话记录必须持久，系统 compaction 不能物理删除唯一来源。

## [002] 进度升级而不是主动工具

同一 Scope 内，只有真实 Provider 回合中被成功受理的 `todowrite` 才贡献计费估计。同回合所有估计取最小值，结算一次。当前最终估计不小于前一计费回合，或已经报告 0 而仍再次计费时，自动登记一个 Advisor 触发；没有 `todowrite` 的回合不触发。工作模型明知该规则，但不能调用 `this.advisor(intent)` 或独立 `advisor` 宿主工具。

## [003] 单飞、合并和身份

同一 Session 同时最多一个四段 Process。运行期间第一条新触发形成等待项，更多触发合并到该项，但保留所有触发报告身份。重复观察同一报告（包括已完成咨询后的重放）不能再次请求模型。每个 Process 绑定 Session、Scope 身份和真实触发；Scope 推进后，旧事件、迟到阶段、迟到 steer 都必须拒绝。

## [004] 隔离的四段咨询

A：通过当前 Session 唯一最新 transform 输出，在只读临时会话自然询问工作者为何进度变差，取得 prose1。
B：把 prose1 发往同一 dedicated Advisor，询问需要哪些背景，取得 prose2。
C：重新读取当前 Session 此刻最新一次 transform，向隔离工作者提出包含 prose1 和 prose2 的自然问题，取得 prose3。
D：把 prose3 发给同一 Advisor，由其给出 prose4，作为 agent-attributed steer 投递。各阶段先后顺序由执行器控制；参与模型只接收自然聊天文本，不看到临时副本、阶段号、archive、Advisor 编排、内部标记。查询不能写入真实工作会话，也不能替换实际来源模型。

模型返回的空答复、Provider 错误、异常、执行工具违规分别如实结算。只有明确的真实终局失败证明（端口标记 ConfirmedFailure）才可写 Finished(Failed)，而 SDK/容量/网络错误的结果未确证时保持 StageBegan Unknown 与全局单飞，不再追加模型提问或结算下一项。不得把工具租约、容量或网络异常作为新的用户消息传给模型，不自动重发结果未知的 Provider 请求。

## [005] 顾问零工具与物理送达

Dedicated Advisor 的 Host AgentConfig、Provider 请求工具目录和产品工具执行门禁一律无工具。执行前仍独立拒绝所有工具。工作者的临时咨询则保持原始 provider 工具 schema 不变，在执行层拒绝，不得为了禁工具破坏来源前缀。Advisor 建议没有操作员身份，不更改当前 Scope 估计或 ResetDue。只有真实物理接纳证据才算 steer 送达；发送前和接纳回执时都必须校验源 Scope。

Advisor 的双语 system prompt 只使用自身的聊天指引，不继承工作者 Common Law 中的交付与执行协议。提示词不提 `deliver`、`negotiate` 或 `resetScope`，不规定固定的回答分类；缺少事实时直接用自然语言提问。验收必须检查真实组装并经过 Provider transform 的完整提示词，而不只检查额外注入。

## [006] 废除旧成员

`wanxiangli/program`、JS 工具绑定、Host ToolRegistry 和模型可见双语模板不得再暴露 `this.advisor`。仅凭伪造 `Advisor` 能力标签不能重新注册它，模型从旧代码调用该函数也不得抵达专席或外部模型。

## [007] 原子受理与分阶段恢复边界

每一次因计费非递减触发的咨询，必须在调用任何短命 Provider 或专席模型之前，将 `(session,scope,providerRun)` 与原始问题交给 `wanxiangli/eventstore` 的真实 SQLite 追加事务。只有首次返回 `Committed`，AdvisorFlow 才可运行。`Replayed` 已代表同一报告的付费咨询被受理，不能再次启动；同身份异问题的 `Conflict`、存储异常、`NotWritten` 和提交 `Unknown` 均拒绝外部调用。受理事实属于 ScopeProgress 的原子身份，不是 Work 生命周期或 Host 的第二份业务账本。

`wanxiangli/src/scope-progress/tests/004.test.mjs` 以真实 SQLite 重新打开、SIGKILL、身份冲突和 Unknown 验证不重复派发；`requirements/advisor/tests/007.test.mjs` 在真实 OpenCode 插件中直接核对 SDK `session.create`／`session.prompt` 发生之前，SQLite 已经持久提交。

现在还会在同一 SQLite 会话流写入 `Finished(scope,reportIds,outcome)`：只有真实终局提交获得 Committed／Replayed 确证，才能解除前一咨询的全局单飞。不同进程对同一 Session 的新报告，即使各自拥有空内存队列，遇到未结算的持久受理事实也只能记为等待，不能启动第二条模型请求；正常同进程的合并等待项仍按顺序推进。完成身份异载荷冲突、Unknown 或存储失败不能释放下一项。`scope-progress/tests/004.test.mjs` 覆盖真实 OS 进程与独立队列共享 SQLite 的竞争、Finished 重放／冲突／Unknown、受理及终局的 RevisionConflict 有界重读，以及冷恢复未决报告的只读观察。Process ID 由 Session、Scope 和首次报告确定性 SHA-256 导出，防止进程重启后物理回执重复。

**物理完成边界**：OpenCode 的 `SendAdvisorSteer` 现在在 busy 和已闭合 Logical Run 两条路径上都使用 PromptDispatcher 的 `Await PhysicalAccepted`；只有 Dispatcher 真实确认物理用户消息，且源 Scope 在等待前后都仍有效，才把成功结果交给 `AdvisorFlow` 写 `Finished(Completed)`。普通 negotiate／askPreviousScope 仍使用 `Detached`。若 Host 提交返回 Unknown、Await 等待超时或抛错，AdvisorFlow 不将其误写为 `Finished(Failed)`，继续保留当前 Process 与 SQLite 未决报告，禁止自动重复计费/重发。`requirements/advisor/tests/008.test.mjs` 采用实际 Dispatcher/AgentJournal 区分 SDK 仅排队与 `PhysicalAccepted`，以及 Unknown 保存 Pending。`requirements/advisor/tests/009.test.mjs` 直接通过生产 BusyNudge Await 进行物理接纳；`010.test.mjs` 更经生产 ToolRuntimeScope 证明 Scope 在提交与确认之间推进时不得将旧建议标记为成功。注意：物理 Host 已经接纳的一条消息无法由后续 Scope 失效判定撤销，现有 fence 保证的是**不错误结算 Completed**，不是逆转先前 Host 物理副作用。跨进程强杀后按原物理 PromptKey 自动确认与相应清理仍未完成。

**阶段事实与有限冷恢复**：每段 A／B／C／D 的真实外部模型调用前，在同一 SQLite 流提交 `StageBegan(scope,process,phase,question)`；普通答复确证后才提交 `StageAnswered(...,prose)`。最终物理 steer 单独使用第 5 阶段，Host 真正 `PhysicalAccepted` 后才提交第 5 阶段的确认及 `Finished(Completed)`。相同阶段的确定性 requestId、已确认回答重放与不同内容冲突由 `wanxiangli/scope-progress` 领域状态机裁决；仅明确 `RevisionConflict` 可以有界重读，提交 Unknown 不重试。

`AdvisorFlow.resume` 只能按 SQLite `advisorPending` 最早未决报告、原问题和原报告的 `Replayed` 凭证认领 Process。已确认的 A／B／C／D 回答可以复用原 prose，跳过曾经收费的 Provider 请求，继续尚未预留的后续阶段；某阶段已经 `StageBegan` 却未有 `StageAnswered` 时始终保持 Unknown，不再发那条模型请求。OpenCode 在真实 `chat.params` 读取当前 Scope epoch 后主动尝试该恢复：优先使用 AgentJournal 已提交的 `PrefixEpoch.ReanchoredScopes` 身份；该持久身份尚不可读时，才沿用本进程 ScopeProgress 已观察的 epoch。**两者都不存在时，不得猜测或恢复。** AdvisorFlow 的恢复本身不凭空推断历史计费估计；同一真实 Scope 若已有独立 SQLite Recorded/Settled 计费事实，则 ScopeProgress 会从该持久账本重建上一付费估计及 ResetDue。验证包括 `scope-progress/tests/005–006.test.mjs` 的真实 SQLite 冷重开与未知阶段反例，`advisor/012.test.mjs` 的生产 PluginHooks 自动认领，以及 `advisor/013.test.mjs` 的真实 resetScope 提交 ScopeReanchored、清空进度内存、仍无需新 todowrite 即按持久身份续认。

**普通计费与多工作区隔离**：`DurableProgress` 为每个真实 `Session/Scope/ProviderRunId/ToolCallId` 提交 `Recorded`，在 Provider 请求边界只提交一次 `Settled`，用已确认事件重算 min、上回合估计、零预算锁存、未结算回合与 `AdvisorDue`。经真实 SIGKILL 冷恢复后仍可继续比较而不重复收费；初始 Scope 0 只在 SQLite 存在确切报告或结算事实时认领。OpenCode 按 SessionId 绑定到本工作区独立的 journal factory，没有 durable workspace 时不能借用另一个插件的数据库；`requirements/advisor/tests/014.test.mjs` 已经通过真实计费冷恢复，`015.test.mjs` 已经通过两个同时存活的插件跨工作区隔离。

**仍未闭合**：StageBegan 是可信的逻辑请求预留，不是 SDK 已返回的真实物理 ProviderRunId；结果未知的模型请求尚无可确证的物理回执来自动裁决。重启后若 ScopeProgress 尚无可信估计，但 AgentJournal 已有真实 ScopeReanchored 身份，依然可安全认领该 Scope 的咨询，不得顺势伪造旧预算；若 AgentJournal 和 ScopeProgress 均无 epoch，仍须等待真实工作事实。没有后续 Provider 边界时，Host 不会凭空启动恢复。已持久受理且依次排队的多个 Pending 报告，在前一 Process 的 Finished 确证、内存 active 释放后，会沿同一 SQLite 身份验证自动顺序接力，无须再等待下一次宿主请求；前项 StageBegan 结果 Unknown 则永久阻止后项抢跑。**已合并报告的冷恢复**：`StageBegan(1)` 同时持久保存实际 reportIds 及完整问题；`advisorPending` 只在存在该事实时返回 group，`AdvisorFlow.resume` 必须逐一读取原报告问题并完成 `Claim(Replayed)`，再用 Queue.restoreGroup 一次重建确切成员，不能拆分多行文本猜测分组。相同报告不能被两个不同 Process 预留。已确认的阶段回答只重用一次，正常 SQLite `Finished` 结算全部组员。历史缺少明确 members 的旧格式必须保持 Unknown、不可冒险猜测重发；商业 Provider canary 也不能被有限恢复冒称为 exactly-once。OMP 的原生 `todowrite(remainingScopeTurnsEstimate)` 已注册 essential tool：适配器以实际 `turn_start`／`turn_end` 为回合边界为同次回合建立唯一内部运行身份，按确切 toolCallId 记录最小估计并在 turn_end 结算；若进程在 turn_end 前退出，则在下一次宿主边界从 SQLite 结算已接受的报告。非递减或报告零后继续计费自动触发同一 `AdvisorFlow`，工作者通过原模型 SDK 的只读 ephemeral turn 问询（保留工具 schema），专席通过稳定 conversationKey＋持久历史摘要做零工具问询（`tools:false`），最终建议以 agent-attributed steer 投递，真实 `message_start` 观察到相同 Session/Scope/receipt 才结算第五阶段 PhysicalAccepted。`scope-progress-016/017` 已在 Node 与 Bun 的真实 OMP Adapter 下通过；`scope-progress-018` 进一步通过真实 Bun／OMP SDK + 本地 SSE Provider 网络验证自动 A/B/C/D 四次物理请求、A/C 原工具目录、B/D 零工具目录、D 重用 SQLite 已确认的专席上一组原问题与答复、真实 agent steer 消费后 SQLite 完成第 5 阶段与 Finished。原生 `negotiate`／`askPreviousScope` SSE Provider canary 也保持通过。SDK 的 `turnIndex` 在 `agent_start` 重新计数，供应商 responseId 又非必填，因此上述内部 run GUID 是受 OMP 实际回合生命周期约束的 best-effort 计费身份，不冒充供应商响应 ID；跨强杀的未决结果按 SQLite Unknown 保留，不盲目重推。

验收分别见 `wanxiangli/src/scope-progress/tests/001–003.test.mjs`、`requirements/advisor/tests/004.test.mjs`／`006.test.mjs`、`requirements/host-boundary/tests/scope-hni-physical.test.mjs` 的 `advisor-031` 和 `delegation-negotiation-005/035–037`。其中自治协议测试不能冒称真实 Provider 或重启恢复已经通过。

**已知边界（2026-10-26 评审记录）**：OMP 最终建议 steer 发出后 30 秒内没有观察到对应 `message_start`，宿主移除待确认回执并按 Unknown 返回；`AdvisorFlow` 保留当前 Process，因此本 Scope 后续触发合并为等待，直到 resetScope 或进程重启后恢复，迟到的 `message_start` 也不再补记第五阶段。这样不会重复计费，但会让本 Scope 余下时间没有新的 Advisor 咨询。专席历史取同一 Session 跨 Scope 最近 12 组已确认问答，作为一段散文前缀交给零工具请求，不是真正的多轮消息。投递回执 `[receipt:…]` 只用于宿主匹配物理接纳，OMP 在请求副本里从 agent steer 去掉，原始历史保留。
