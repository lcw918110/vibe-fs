# effect-accounting — WHAT

## [001] 意图与确认分型

每个外部副作用须分别持久化请求意图（Requested/Claimed）与物理确认（Accepted/Created/Published），使用不同的强类型事实，不得压成单一布尔字段或状态枚举。旧的通用副作用类型在解码时拒绝。

## [002] Requested-only 是未知结果

有请求、无确认只表示结局未知，不得视为未发生而重发，也不得视为成功而推进。修复预算耗尽时须以明确失败结束处理，副作用的未知事实仍须保留，不得无限挂起或伪造结局。

## [003] 先记账后行动

副作用意图须先持久化，再更新权威内存状态或执行物理动作；不得事后补记。

## [004] 确认不回退

已确认效果不得因重放或重试退回 Requested。重复确认须幂等，不得改变已确认状态或产生第二次副作用。

## [005] 先核对再重试

恢复 Requested-only 效果时，须先按 effect identity 核对外部物理证据。仅在证实尚未发生且领域合同允许幂等重试时才可重试；否则保留未决记账，不得猜测或盲目重发。

## [006] 显式表达不确定性

事实追加或副作用执行遇到不确定异常时，须返回明确的 unknown 或 pending 类型；不得伪称 committed，也不得把缺失回执当成未发生。

## [007] Aborted 不是终态

取消信号不构成业务终态，也不证明 provider 崩溃。Agent 终态仅为 Completed、Failed 或 Abandoned，不得将取消包装成完成。

## [008] 效果家族

工作区创建与分支发布分别使用 WorktreeCreateRequested/WorktreeCreated、PublishClaimed/Published 表达意图与确认。编程面调用（`todowrite-for-<role>`）的待办与视图写入由 Host executor 完成；插件不改写其 provider 参数，不在 before/after 产生 checkpoint。只有 Host 随后的 `message.part.updated` 明确给出该 exact call 的 `completed` ToolPart（且 `state.input.view` 为非空对象），才追加 `ViewCheckpointCommitted`；`error` 只关闭候选而不推进压缩。各领域负责具体证据。

## [010] 拒绝旧通用事实

解码须明确拒绝 DurableEffectRequested 与 DurableEffectAccepted 并给出迁移指引，不得将其作为当前词汇或混合双读。

## [012] 后续阶段以确认证据为前提

只有上一阶段的确认事实完成，才可准备下一阶段调用。发布 Claim 须基于已完成变基与双重评审的不可变见证，不得凭空发起。
