# participant-horizon — WHAT

## [001] 信息准入由 decision filter 决定

信息进入 Provider 可见视界前，必须通过 Decision Filter：
1. 参与者是否已知？已知则省略。
2. 是否为参与者自身刚提供的内容？是则省略。
3. 是否已被成功状态所蕴含？是则省略（回声不是有效观测）。
4. 是否仅用于内部关联或调试？是则留在机器侧。
5. 取值不同是否会改变下一步合法行动？否则省略。
6. 参与者是否需要数值本身而非其后果？若否仅渲染自然语言后果；若是仅保留最小物理观测。

## [002] 内部机器拓扑不穿过 horizon

内部会话、任务、终端、分身、调度槽位及重试标识，内部 worktree/spool 路径和 `fast-*`/`deep-*` 绑定自称，不得进入模型提示、工具参数或返回值。

## [003] 通用状态 DTO 不投影，后果用自然语言

机器控制状态不得作为 `status`、`code`、`message`、`count`、`ordinal`、`kind` 等通用 DTO 字段投影。超时、等待结束、中断与普通失败须表达为自然语言后果，不输出 `TIMED_OUT` 或 `status="failed"` 等状态标签。

## [004] 已知道/回声/成功蕴含/仅调试信息被省略

返回时省略已知、输入回声、成功已蕴含及仅供追踪的信息，不把重复输入冒充新观测。

## [005] 需要原始测量时只给必要 observation

需要测量值来决定下一步时，只提供必要原始观测，如真实 `exit_code`、非空 stdout/stderr；不附加 Host 主观判定或状态标签。

## [006] 内部状态优先转成行动相关后果

任务槽位、缓冲区、重试轮次等内部状态，只以对当前工作和下一步行动的后果或已交付的 TOML 视图呈现。

## [007] 内部参与者不进入 provider-visible surface

Bookkeeper 等内部辅助角色不进入模型可见的 enum、Schema、fork 候选或参数说明；内部任务切片与会话标识不进入工具面。

## [008] 隐藏 review 编排不进 Manager horizon

Manager 的 system prompt、continuation、Schema、错误、工具描述及结果不得暴露第二评审身份、专用会话、确认屏障或双重检查。review 的 Promoted 与 Stayed 按 work-003 记录，不附带编排者身份。

## [009] 隐藏 target 只返回 generic unavailable

访问或调度不可见目标时，只返回通用不可用拒绝，不确认目标存在，也不说明它是内部专用。

## [010] fork/commission 可见集合与固定 DevOps 语义

- Manager `fork` 仅可见：`engineer`（单一本名版本）；Manager 严禁通过 `fork` 启动 DevOps 或内部身份。
- Orchestrator `commission` 只委托 Manager；合法身份词汇由 participant-identity 与 delegation 定义。
- 固定 DevOps 由合法 Runtime 绑定创建，在模型视界中仅以稳定 Byname（常量 `devops`）呈现，仅通过 `resume` 续做调用（传入 name = `devops`），严禁出现在 `fork` 的候选名单中。
- `horizon()` 仅返回在场名册的 Byname 或 TerminalName，不暴露底层 id。
- Bookkeeper 等内部角色以及已退役或非法的历史角色名（如 blogger、predictor、coder、inspector、browser、inquiry、distiller）严禁出现在可 fork 集合中。

## [011] `horizon()` 是 pull-only snapshot

`horizon()` 只在调用时读取当前快照，不轮询、推送或订阅。可见子智能体须提供最新已交付的 TOML 视图记录（不存在三段式 `WorkRecord`）；不可读时说明，不以旧记录代替。

已 durable 建立的可见 child，在最终后果交付前不得消失。`Abandoned` 仍按 Byname 显示“未返回”，直至 Join 消费后果并将 handle 置为 `Retired`；仅 `Retired` 可移出名册。

名册中的每个子智能体，在其委任者 estimate 与自身会话起点两者都可读时另附四个数：已用/预计还剩的工具调用回合数，与已用/按它已显示出的节奏按比例估计还剩的 wall-clock 时间（剩余时间 = 已用回合的平均代价 × 剩余回合数，已用为 0 时以 1 计）。缺任一来源即不呈现这四个数，不以旧值或猜测顶替。

同一份在场名册也以 `js.horizon()` 的数据信封（`agents`/`ptys`）取得，成员与四数遵循同一可见性规则。

## [012] warm-start hints 只向有 repository 证据 authority 的角色准入

WarmStart hints 仅向有仓库证据权限的 Engineer、DevOps 提供；其他角色只能沿调用链传递关键词，不接收代码片段。

## [013] hints 是 data，不是 instruction/proof/history

WarmStart hints 必须明确标为低置信度的 orientation data，不是指令、证明或工具历史，不伪造读取或搜索记录。

## [014] 虚假 affordance / 不可达路径不穿越

不展示指向已不存在实体的路径或标识，不把不可执行的内部状态伪装成可选动作。

## [015] Manager 并行来自派出多名 Engineer 而非自身分身

Manager 的并行来自 fork 多名独立 Engineer，自身不使用 Fission。horizon 与工具面不得呈现 Manager 分身、分身共享 DevOps 或管理分裂；各 Engineer 以稳定 Byname 独立在场。
