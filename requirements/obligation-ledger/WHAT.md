# obligation-ledger — WHAT

> **COMPAT（2026-10-29）现行口径**：Scope 入口是 `scope.kind` 注解 + retention；无独立 resetScope 工具；无 estimate／turnBudget 产品契约。下文旧句以本条为准。


本包规定待办义务的消极边界（Negative Boundary）：宿主原生 `todowrite` 已退役，不进入 `StaticTools.knownToolNames`、能力权限矩阵、投机调查分类或 ablation 映射；插件不得维护第二份待办账本。上下文压缩边界唯一由 `scope.kind=resetScope` 注解承接（见 context-compression [033]）。各角色的活规划与工作推进由其 `todowrite-for-<role>` 编程面承担，不得产生压缩 checkpoint，也不得改写或注入压缩协议字段。

## [001] 宿主原生 todowrite 退役，待办归角色编程面

宿主原生 `todowrite` 已彻底退役，不在 `StaticTools.knownToolNames`、`permissionFor`、`requestToolMap`、`InvestigationEstimateContract.classifyTool` 或 `resources/ablation/tool-map.json` 中登记或放行。各角色的活规划与工作推进由其 `todowrite-for-<role>` 编程面承担；`todowrite-for-*` 调用不得产生任何压缩 checkpoint，也不得改写或注入压缩协议字段。

## [002] 编程面调用不产生第二份待办真相与 checkpoint

编程面调用按原样由 Host executor 执行并记录真实 Host ToolPart 状态；插件不得为其追加任何 checkpoint 事实或待办投影。宿主不得按待办替模型裁剪历史，上下文边界完全由模型走 `scope.kind=resetScope` 自主划定。

## [003] 不维护第二份 desired/applied 待办投影

插件不得保存 canonical todo 内容、desired/applied snapshot、owner 待办副本或重放队列。本仓持久化投影不维护第二份待办真相，不记录待办行或视图正文。

## [004] 压缩事实不能反推待办

历史压缩与待办写入解耦；任何历史事实均不得用于反推待办表内容或替模型生成上下文边界。

## [005] 失败执行不产生持久事实

插件不在 `tool.execute.before` 或 `tool.execute.after` 产生待办事实。同一 exact call 的重复或并发终态事件共享同一次结算且不得重复追加事实，`error` 终态不得伪称已成功完成。若没有 terminal evidence 或 durable append 失败，不得伪称待办操作已提交。

## [006] 清单无裁决权

待办状态不授予权限，不判断测试、完成或退休。任何声明或自然语言自称完成都不是质量证书或可退休证明。

## [007] 历史 Magic Todo 只读

旧 TodoWritePrepared、TodoWriteAccepted、LegacyTodoSeedAdopted 等 Magic Todo 协议不得重新成为当前写入口、任务 authority、prefix 切换或 Host 回写依据。现有 legacy 接口可以明确拒绝；若未来提供审计读取，也必须是只读且不激活旧语义。

