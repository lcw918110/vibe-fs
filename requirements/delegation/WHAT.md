# delegation — WHAT

## [001] 委托的四项要素

委托必须明确 charge、允许产生的 office 后果、logical owner 和返回给调用方的 bounded 后果。以权能后果识别委托，不以 persona 名称或工具白名单代替。

## [002] Calling 别名不扩权

同一 Office 的 calling 别名不改变权限。fast/deep 仅作兼容参数，不独立路由或分档；每个 Role 对应单一 Persona。

## [003] 新道路与续做分开

`fork` 的 `calling` 可选，创建独立道路。省略时按 `name` 推导：`name` 为 `devops`（大小写不敏感）时推导为 `devops`，否则推导为 `engineer`；显式 `calling` 与推导不一致须 typed 拒绝。Manager 只能 fork `engineer`，`name` 为 `devops` 时不能用 `fork`。`resume` 必填 name，续做既有道路，复用该 participant 的完整历史和 binding；传入 calling 须 typed 拒绝。Manager 固定 DevOps 的 name 为 `devops`。同一目标的后续阶段、纠正和重试仍属同一道路，不另建道路。

## [004] 不同合同使用不同工具名

同一工具名在全系统只有一个确定语义。Orchestrator 的 `commission`、Manager 的 `fork` 和既有道路的 `resume` 不得混用。`commission` 的 `calling` 可选：Orchestrator 只委任 Manager，省略时推导为 Manager persona `lead`，显式 `calling` 与推导不一致须 typed 拒绝。`name` 未出现时新建道路；`name` 已出现时，省略 `calling` 续做既有道路，显式给出 `calling` 则拒绝重名新建。

## [005] 委托面不暴露机器拓扑

参数和返回结果不得包含 SessionId、AgentId、ManagerJobId、worktree、reused 等物理拓扑标识。跨 horizon 只传语义后果与 `js.deliver` 交回的 view。

## [006] 承接后果与绑定连续性

fork 成功只表示该 Byname 已承接 charge。续做按 Byname 找到既有 participant，沿用 execution binding 和模型档位，不得重选或篡改绑定。

## [007] SyncDelegate

同步委托依赖必须无环。Sphinx 程序内部受管调用标准 Engineer，完成当前工作项即返回调用点；沿用标准 OfficeCapability，不另禁写入或 Fission，不额外授予真实执行或 DevOps 差遣权限。

物理会话经 family root 压平，与当前 sub session 同级，不创建孙 session；结果接纳绑定本次调用的 exact terminal identity。

## [008] 批次由 Host 调用集合确定

同一 assistant 运行中指向同一 SyncDelegateRole 的调用构成一个批次，成员与顺序取自 Host tool-call 列表，不按到达时序或微任务猜测。合并 charges/prompts 后只发送一次。

## [009] 按直接调用方串行化

以直接调用方的 ReuseScope 为 key，同时至多一个 active batch；前批未完成时拒绝新请求。不同层级各占本层 scope，互不阻塞。

## [010] 模型绑定由系统决定

模型与执行配置由系统调度和有效配置决定，被委托模型不得自选 target。tier、fast/deep 仅作兼容参数，不授予模型选择权或额外权限；复用 child 沿用已绑定 managed agent。

## [011] 普通完成结束同步批次

不设独立 return 工具或双重 await。被委托方的普通 Assistant completion 结束批次，由宿主交回该次 `js.deliver` 的 view。

## [012] Canonical 正文与 sibling 引用

批次内仅首个 canonical 调用方获得完整 view，其余 sibling 只获得指向该结果的简要引用。此前已经交回过的内容，即使再次出现，也不再返回。

## [013] Join 有界消费

Join 只消费当前 owner 可用的 completion，批次受全局上限约束，稳定排序并逐项 CAS 消费。每个完成项呈现为该次已交付的 view，不以字段式 DTO 包装。

## [014] Commission Join

Orchestrator 的 commission 批量 join 按 FIFO 排空，遵守与 [013] 相同的批次上限。

## [015] Join 中断不取消 child

外部用户输入、操作员取消或超时结束 Join 等待时，返回 `Interrupted`，不作为 ForkError。外部输入只打断等待，不取消 child 或剥夺权限。

用户输入不得中断当前 LLM 输出或非 Join 工具，也不得为了唤醒 Join 而物理 abort 整个 session；输入留给下一次 LLM 请求。

Join 只在新输入已由 Host 保存、且属于该 owner 的已接纳物理消息时唤醒。重复可见回执不得打断下一次 Join，Join 尚未开始时收到的输入不预埋中断。Manager 听取输入后自行决定是否向 child 追加指导、取消工作或调整安排；系统不自动取消、重派或转发。

## [017] 返回不转移权威

返回的 view 或建议只是调用方的决策证据，不自动改变全局请求方向，不扩权，也不免除既定义务。

## [019] Typed 首提示词

fork 首 prompt 必须 typed 渲染，区分 Assignment 指令与 CommissionerRecord/Attachment 只读背景。父到子背景用 TOML 数据字段，子到父完成项用 `js.deliver` 交回的 view，不得混淆方向。

## [020] 语义不依赖物理工具名

工具改名或替换不改变委托的权能、所有权与生命周期合同。

## [021] Attachment 只作背景

attachment 只将指定同伴已经交回的 view 作为只读数据放入首 prompt，不将其中未竟工作转成新任务义务，不克隆 authority。

## [022] 建议性工具次数

调用方可提供 `expected_tool_calls` 估算。真实工具调用逐次递减，至零饱和；估算只帮助规划，归零不得阻断执行、改变权限或触发异常流。

## [023] 恢复耗尽才报告失败

单次尝试失败不立即交付父调用方；子会话所有恢复路径耗尽或确定性终结后，才交付最终失败。

## [024] Logical route 上的 work unit

复用 participant 的每个新工作是独立 direct-CE invocation，不建立 durable Stage/Phase/ActiveWorkUnit 或第二状态机。resume route 由 Byname 标识，SyncDelegate route 由 caller scope + dedicated role 标识；物理 SessionId 不拥有 handoff 连续性。

输入为新 charge（调用方在 charge 中显式提供上下文，SyncDelegate/dag 可直接拼接上游已交付的 view，且 ANCHOR 编号共享同一进程地址空间；父到子 `DelegationHandoff` delta tracking 已退役）。

同步调用等待自己完成，只返回本次 `js.deliver` 交回的 view。异步 fork/resume 按 [026] 完成 admission/dispatch 即返回 Byname 承接后果，不等待 completion，不直接返回该 view。完成结果只经 join/horizon 拉取。子到父的返回来自 `js.deliver` 交付格终值（未交付的子工作在结构化 join 记录中报告 `status = "undelivered"`）。S1/S2/S3/S4 接力不再主动清除上下文。

## [025] Completion 以因果身份认领

terminal 只有属于该 work unit 实际接受的 Authority Root/provider execution，才可完成或失败它。completion cell 单次赋值：首个因果有效的 proven terminal 可 claim，恢复 continuation 不可 claim，晚到 terminal 幂等忽略。

run-scoped Completed/Failed/Aborted 均须保留因果身份；只有 session-wide 物理故障可无 Authority Root。sticky terminal、旧 run 或旧 cache 不得结算新 work unit，订阅后的到达时序不能代替身份。

## [026] Effect truth 单向前进

新 assignment 以 direct F# CE 表达。同步为 `dispatch → await own completion`；异步 fork/resume 同步确认接收方的身份校验与持久接收，随后异步工作，不在 invocation 内等待 child 完成。发送前 claim 复用 PromptAuthority 事实，不另持久化程序状态。

仅 exact `AcceptedAssignment` 可宣告已承接，并发布为 join 可等待的运行任务；Submitted transport receipt 不足以证明接收。确定未发送或已拒绝时，返回调用失败并释放预留，不遗留运行中任务。

acceptance unknown 或确认超时时，保留 durable Pending 与原 PromptKey，不伪造失败、不自动重发；返回明确的 `DispatchUncertain`，说明可能已接受、不要重复委托，且不得发布为 join 等待任务。已经发生的 effect 不得被后置失败否认为“未放置”。

Join 只等待当前进程真正在跑或已产生可 join 结果的工作：真实 Host agent 运行经 `PendingRuns`/`PendingCompletionCount`/`PtyRuns` 可见，可 join 的 durable handle 经 journal 投影可见。重启后按 durable handle 重新收养的**空闲伴随句柄**（如 Manager 的 `devops`）只注册其身份、不代表有在跑任务，不得让 Join 误判为有工作而无限等待；无 journal 的纯 PTY 模式才以进程内 active run 计数作为 agent 工作信号。

## [027] Busy nudge 不承载新 assignment

同一 logical route 同时至多一个 active work unit。对忙碌 participant 调用 `resume` 时，charge 作为 `BusyAgentNudge` 追加到既有 LogicalRun，在 Host 保存后进入下一次尚未开始准备的 LLM 请求，不中断当前输出或工具。Engineer 与固定 DevOps 均允许此行为；保留原 work 的 Root、身份与完成订阅，不创建新 assignment，不因提示文本与旧 charge 相同而假装发送成功。

前一 work unit 完成后，`resume` 才承接下一项，不依赖 join 消费或重建 participant。忙碌提示的发送路径不得因异步准备期间任务结束而自动改走新 assignment。

## [028] 编译依赖按 effect 边界分层

委托合同只含 command/result、fact、typed payload、logical route、completion evidence 和 injected capability；fold 纯计算，durable composition 连接 canonical journal，各 workflow 消费合同与 fold。Host dispatch 和 PTY adapter 只由 composition root 绑定，合同的直接及传递依赖不得包含 Host、工具、PTY、process、EventStore runtime 或委托/恢复 workflow。

合同传递 production `.fs` 上限100；fold/runtime/sync/fork 目标185。Host/PTY adapter 因组装共享 durable/wait/signal/identity 能力，以全仓 production `.fs` 的60%为硬上限，超过须 full fallback；实测规模作 ratchet，增长须修订本条，当前 Host adapter 为312。owner compile 将所选 locality 的传递 ProjectReference 展成唯一计划，单次 Compile Include 保持拓扑偏序，不得退回目录通配。

## [029] 领域意图与物理能力单向注入

委托领域只产生 typed intent；linkage、estimate、child handle index 和 closed rejection 由委托纯 fold/decision 拥有。只有 durable composition 将意图包入外层持久事件并组合 projection。

各委托/恢复 workflow 只消费委托拥有的 append/query/wait/clock/PTY capability。PTY adapter 不得读取 Host/fork runtime、process 实现或其 Gate、Dictionary、registry、TCS 状态；Temporal、CausalWait、Process 物理实现只由 composition 构造最窄能力并注入。

## [030] Fatal 使用必填注入能力

sync invariant 违例由委托 owner 构造 typed incident；先保留已发生 effect 与 durable settlement，再调用必填的 fatal capability。runtime 不直接引用物理 fatal adapter，不设 optional/default/global fallback；同一 incident 至多一次 report 和 kill。

## [031] Checkpoint 保留已完成事实

（父到子 `DelegationHandoff` 及其 `HandoffCheckpointSettlement` 已随显式 charge 上下文传递与共享 ANCHOR 地址空间退役。）child 已完成的事实独立保留：同步调用连续复用同一 child 并各自交付终值 view，旧 authority root 的晚到完成不能认领新调用，父会话 supersede 或取消与完成在任意顺序下均精确结算一次，不遗留孤儿 claim，不重跑已完成 child。

## [032] Engineer 与 DevOps 不相互差遣

Engineer 完成工作或到达需 Manager 决定的边界即返回，不自行组织验证链，不直接、包装或转发任务给 DevOps。DevOps 不创建或差遣其他工程子代理。运行验证和进一步组织工作由 Manager 调度。
