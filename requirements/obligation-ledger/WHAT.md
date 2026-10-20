# obligation-ledger — WHAT

本包规定编程面（`todowrite-for-<role>`）的 ANCHOR `view` 声明与上下文压缩 checkpoint 的确认边界。宿主原生 `todowrite` 已退役，不进入 `StaticTools.knownToolNames`、能力权限矩阵、投机调查分类或 ablation 映射；插件也不维护第二份待办账本。唯一产生压缩 checkpoint 的来源是携带非空 `view` 声明并成功完成的编程面调用：每次 exact 调用确认后追加一条持久化 `ViewCheckpointCommitted` 事实，进入 checkpoint 窗口；裁剪边界由 `view` 声明自身决定。

## [001] 宿主原生 todowrite 退役，编程面 view 声明为唯一 checkpoint 来源

宿主原生 `todowrite` 已彻底退役，不在 `StaticTools.knownToolNames`、`permissionFor`、`requestToolMap`、`InvestigationEstimateContract.classifyTool` 或 `resources/ablation/tool-map.json` 中登记或放行。各角色的活规划与工作推进由其 `todowrite-for-<role>` 编程面承担；唯有前缀为 `todowrite-for-` 且 `state.input.view` 为非空对象的调用具备压缩 checkpoint 资格。未声明 `view`、空对象 `{}`、非对象或非 `todowrite-for-*` 工具的终态事件均不产生 `ViewCheckpointCommitted`。

## [002] 编程面 view 声明驱动真实 Host 终态与 checkpoint 记录

编程面调用携带的 `view` 参数按原样进入执行与 Host ToolPart 状态；当 Host 发出 `message.part.updated` 的 `completed` 终态且 `state.input.view` 非空时，插件为该 exact `ToolCallId` 追加一条 `ViewCheckpointCommitted`，进入 `ViewCheckpoints` 窗口。窗口只记录成功声明的 checkpoint 身份与顺序；上下文的保留与裁剪由 `view` 声明本身决定，宿主不再按固定计数替模型裁剪历史。

## [003] 不维护第二份 desired/applied 待办投影

插件不得保存 canonical todo 内容、desired/applied snapshot、owner 待办副本或重放队列。本仓 durable projection 只在 `ViewCheckpoints` 中记录压缩 checkpoint 的 `ToolCallId`，不记录待办行或视图正文。

## [004] 压缩事实不能反推待办

`ViewCheckpointCommitted` 只证明某次携带非空 `view` 的编程面调用成功建立了上下文压缩 checkpoint；它只更新 `ViewCheckpoints` 窗口，不得改动会话的任何非 checkpoint 投影切片，也不能用于恢复、重建或反推任何待办表内容。

## [005] 失败执行不形成 checkpoint

插件不在 `tool.execute.before` 或 `tool.execute.after` 产生 `ViewCheckpointCommitted`。Host 在 after 时 ToolPart 仍可能是 running，因此 checkpoint 的确认边界是随后 exact `message.part.updated` 的 `completed` 状态（且工具名为 `todowrite-for-*`、`state.input.view` 为非空对象）；同一 exact call 的重复或并发终态事件共享同一次追加且不得追加第二条持久化事实，`error` 只关闭候选而不推进压缩。`WriteUnknown`/`FactRejected` 保持已结算而不重追加，`WriterUnavailable`（未尝试）释放结算标记以允许后续重试。若没有 terminal evidence 或 durable append 失败，不得伪称压缩 checkpoint 已提交。

## [006] 清单无裁决权

视图声明或待办状态不授予权限，不判断测试、完成或退休。任何声明或自然语言自称完成都不是质量证书或可退休证明。

## [007] 历史 Magic Todo 只读

旧 TodoWritePrepared、TodoWriteAccepted、LegacyTodoSeedAdopted 等 Magic Todo 协议不得重新成为当前写入口、任务 authority、prefix 切换或 Host 回写依据。现有 legacy 接口可以明确拒绝；若未来提供审计读取，也必须是只读且不激活旧语义。
