# prefix-stability — WHAT


> **COMPAT（2026-10-29）现行口径**：Scope 生命周期入口是任意工具上的打平注解 `scope.kind` ∈ {`resetScope`,`keepLast`,`removeLast`,`meToo`} 与 retention（`wanxiangli/scope-retention` + 共同法 «Scope»）。**无独立 `resetScope` 宿主工具**；档案重锚 = 注解 `resetScope` → `ScopeReset.commit`。**无** `remainingScopeTurnsEstimate`／`turnBudget`（已退役；现行 retentionBudget）／KPI 倍率产品契约；Stall／ResetDue 估计路径已从宿主产品面退役。下文若仍写独占工具、估计必填或倒计时后缀，以本条为准。PairHint 屏障优先认 `scope.kind=resetScope`，旧工具名只读兼容。


## [001] 同 epoch append-only prefix law

同一 PrefixEpoch 内，未发生 `ScopeReanchored`、`ContextReanchored` 或 `TenureReanchored` 重锚时，前一次 provider wire 已保留的前缀必须是后一次的精确字节前缀。比较范围见 [013]；tools 必须完全一致，不以追加工具视为前缀稳定。请求级上下文清理统一由 `resetScope` 驱动并作为 `ScopeReanchored` 冷边界推进 epoch，不授权宿主在同 epoch 内改写留下的前缀。

## [002] 冷边界只有已提交证据与显式 view 取代

Epoch 只由 Host compaction 重锚（`ContextReanchored`）、Plan tenure 交接重锚（`TenureReanchored`）或显式 scope 重置重锚（`ScopeReanchored`）推进，且每次恰好加一。不得按容量或 Token 数由宿主主动估算切换。

- Scope 重锚：模型走 `scope.kind=resetScope` 产生 `ScopeReanchored` 事实（context-compression [035]），epoch 加一、清除旧 wire 快照；同一 `toolCallId` 不重锚两次。
- Plan tenure 交接重锚：Planner 任期交接触发 `TenureReanchored` 事件，epoch 加一、清除旧快照；同一 incumbencyId 不重锚两次。
- Host compaction 重锚：Host compaction 触发 `ContextReanchored` 事件，epoch 加一、清除旧快照；同一 compaction 不重锚两次。

## [003] 废除 Y 候选 probe 与 FrozenRecordPrefix

系统已彻底取消 Y 轴、`FrozenRecordPrefix`、`MemoryPreamble` 与 prefix probe 候选机制。历史按显式声明选择，当前回合按请求快照划界并保留，不另设独立的 Y 覆盖证明或 `PrefixRebaseCommitted` 候选流。

## [004] 唯一 epoch 真相源

`ActivePrefixEpoch` 是唯一持久化的前缀 epoch 合同，不另设阶段 epoch 或旁路状态机。

## [006] Host compaction 与 Plan tenure 重锚

Scope 重置通过 `ScopeReanchored` 收容：epoch 加一、清除旧快照。记录已处理 `toolCallId`，同一 scope reset 不重锚两次。
Plan tenure 交接通过 `TenureReanchored` 收容：epoch 加一、清除旧快照。记录已处理 incumbencyId，同一任期不重锚两次。Host compaction 只通过 `ContextReanchored` 收容：epoch 加一、清除旧快照。记录已处理 run，同一 compaction 不重锚两次。

## [007] 同 Life system prompt byte-identical

同一 Life 且全局语言不变期间，Office system prompt 保持逐字节一致，Persona 不重绑；交托、fallback、review 和 compaction 均不例外。全局语言变化是唯一允许 system prompt 随之整体变化的情形（provider-language-013）：切换后的第一次请求建立新前缀，此后恢复 byte-identical。

## [009] 稳定 Host 消息身份与 retry 行保真

消息裁剪与保留按 stable Host message identity 定位。

`ProviderRetryAttempt` 不进入业务主历史；同一 Current horizon 保留已进入 provider prefix 的 retry rows，仅新的重锚边界（`ScopeReanchored`、`ContextReanchored`、`TenureReanchored`）可按 stable Host id 退休旧 rows，且保留触发本次切换的当前 physical retry。

## [010] 标记注入原位、原字节 replay

所有 provider 共用统一呈现注入，不生成 synthetic skill 消息。

1. **工具结果倒计时后缀**：附于每个 terminal 工具结果（completed/error）的末尾，使用 `\0\uFEFF` 分隔拼接 `[Remaining turnBudget: N]`（context-compression [034]）。每条结果在同一 epoch 的首次请求呈现时**锁定自身的后缀字节**，不因后续请求的剩余预算变动而改变；同次首次呈现的多结果使用同一当时预算，不同时首次呈现的结果可不同。已呈现结果的后缀必须原样重放，不能重复拼接。
2. **用户消息尾巴提醒**：发往模型的请求副本中，每条 operator 用户消息末尾均拼接 scope 初始化提醒（`[System: Estimate the turnBudget...]`），同一消息已带该尾巴时不重复，持久化用户消息不改写。最新 operator 用户消息后尚未发生 reset 时，非 reset 工具结果重复该初始化提醒。
3. **零预算弱提醒**：在活跃 scope 内且预算为 0 时追加弱提醒用户消息（`steering: true`，仅存在于请求副本，不持久化）。

同一未重锚 horizon 按 durable occurrence 原位、原字节回放，不删除、过滤、去重、搬移或叠加第二后缀；placement 判定先剥离后缀，再精确重附。任何重锚事件（`ScopeReanchored`、`ContextReanchored`、`TenureReanchored`）退休旧 occurrence 的可见性：旧 occurrence 不再呈现，新 horizon 下同一位置承载新字节属于退休而非改写，不构成对未重锚回放律的违反。

## [011] 不用冷边界掩盖漂移

不得频繁切换 epoch 掩盖实现造成的历史字节漂移；历史 marker 重放时不得重新计算流逝时间。

## [012] 已提交前缀事实不可逆

合法提交的 `ScopeReanchored`、`ContextReanchored` 与 `TenureReanchored` 不因后续 provider Failed 或 Aborted 而撤销。

## [013] prefix identity 的比较范围

比较覆盖 provider、model、variant、完整 tools、system prompt 及 message 序列。身份、工具、system 或历史 message 的变更均不能视为追加；冷边界仍须满足 [002]。

唯一例外是全局语言变化（provider-language-002/013）：语言不同导致的 system prompt 与 message 语言整体差异不按前缀漂移处理，不要求 epoch 推进或冷边界声明；语言不变期间本条款照常强制。

## [014] 标记后缀正文不进入业务 trace

工具结果倒计时后缀（`[Remaining turnBudget: N]`）与辅助提示标记后缀只参与 provider 请求呈现，不进入业务消息快照、TOML 视图渲染或 compaction 输入。仅其 durable occurrence 投影参与恢复。


## [016] Scope 后缀不能随剩余预算回写历史前缀

同一 `ActivePrefixEpoch` 内，工具结果首次进入 provider 请求副本时确定的提示后缀必须以工具调用身份（无 ID 时以同 epoch 中稳定的消息／part 位置）固定并逐字重放。后续 `turnBudget`（已退役；现行 retentionBudget） 递减、新 operator steer、重复 provider transform、甚至同一输出之后新增工具结果，均不得重新计算已呈现结果的字节。新结果首次呈现时才使用当次剩余预算；agent side-request 不能提前冻结正常请求尚未呈现的结果。

只有确已提交的 `ScopeReanchored` 允许在新 epoch 重新生成保留的历史 reset 调用／结果后缀。不能因需要新预算而把单一历史工具结果从 `N` 改为 `N-1`：这会让前缀缓存从那条结果起失效，违反 [001]，不是合法的追加。此条保证请求热修正的重复呈现字节稳定，不声称独立进程重启后的冻结状态恢复已获证明，也不替代真实 provider 命中率观察。
