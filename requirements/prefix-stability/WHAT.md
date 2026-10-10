# prefix-stability — WHAT

## [001] 同 epoch append-only prefix law

同一 PrefixEpoch 内，前一次 provider wire 必须是后一次的精确字节前缀。比较范围见 [013]；tools 必须完全一致，不以追加工具视为前缀稳定。

## [002] 冷边界只有四种已提交证据

Epoch 只由成功 prefix probe、Host compaction 重锚、Plan tenure 交接重锚或 context-compression-028/029 规定的阶段窗口 rebase 推进，且每次恰好加一。不得按容量或 Token 数主动切换。Plan tenure 交接重锚：Planner 任期交接触发 TenureReanchored 事件，epoch 加一、清除 Snapshot、PrefixCoverage 归零。同一 incumbencyId 不重锚两次。

## [003] candidate 不等于 committed

候选前缀只进入当前 attempt 的不可变 profile，不修改已提交 epoch；失败直接丢弃，不产生前缀提交或回滚事实。

## [004] 唯一 epoch 真相源

ActivePrefixEpoch 与 PrefixRebaseCommitted 是唯一前缀 epoch 合同。阶段窗口 rebase 共用该合同，不另设阶段 epoch 或旁路状态机。

## [005] rebase 先于下一 attempt seal

PrefixRebaseCommitted 须在下一次真实 provider attempt 的 seal 绑定前原子提交。已提交事实的不可逆性见 [012]。

## [006] Host compaction 重锚

Plan tenure 交接通过 TenureReanchored 收容：epoch 加一、清除 Snapshot、PrefixCoverage 归零。记录已处理 incumbencyId，同一任期不重锚两次。Host compaction 只通过 ContextReanchored 收容：epoch 加一、清除 Snapshot、PrefixCoverage 归零。记录已处理 run，同一 compaction 不重锚两次。

## [007] 同 Life system prompt byte-identical

同一 Life 且全局语言不变期间，Office system prompt 保持逐字节一致，Persona 不重绑；交托、fallback、review 和 compaction 均不例外。全局语言变化是唯一允许 system prompt 随之整体变化的情形（provider-language-013）：切换后的第一次请求建立新前缀，此后恢复 byte-identical。

## [008] FrozenRecordPrefix 是 low-trust context

FrozenRecordPrefix 必须明确标记为 low-trust context，不伪装成人类或系统指令；同一 epoch 内内容完全冻结。

## [009] canonical cutoff 与精确写回

Cutoff 只落在 current-generation canonical XTrace 的完整 semantic turn 边界。coverage digest 的生成和验证使用同一 canonical projection；请求级呈现变化不得造成假失配，真实历史失配须 fail closed。写回按 stable Host message identity 定位，不把 cutoff 当 provider 数组下标。

ProviderRetryAttempt 不进入 X/Y/digest；定位当前 retry 的 request-start 时，可从与 XTrace capture 相同的可解码 Host 消息范围取得 turn 坐标。同一 Current horizon 保留已进入 provider prefix 的 retry rows；仅新的 TentativeCold 可按 stable Host id 退休旧 rows，且保留触发本次切换的当前 physical retry。

## [010] guidance 原位、原字节 replay

所有 provider 共用 cursor 呈现，不生成 synthetic skill 消息。guidance 只能附在**本次请求最新一条真实消息**上，且只有该消息本身承载呈现时才有载体：用户消息附于其末尾并使用无 name 属性的 `<system>` 包装；其余角色附于其 terminal 工具结果（completed/error）的 NUL+BOM 后缀。最新消息不承载载体时，本轮不生成新 occurrence，也不留下任何无法呈现的 durable 事实；不得把 occurrence 记在渲染器改不动的位置（`Before(user)`、`After(assistant 文本)` 等）上。

同一未重锚 horizon 按 durable occurrence 原位、原字节回放，不删除、过滤、去重、搬移或叠加第二后缀；placement 判定先剥离后缀，再精确重附。ContextReanchored 退休旧 occurrence 的可见性：旧 occurrence 不再呈现，新 occurrence 以新序号追加到新的载体；新 horizon 下同一位置承载新字节属于退休而非改写，不构成对未重锚回放律的违反。


## [011] 不用冷边界掩盖漂移

不得频繁切换 epoch 掩盖实现造成的历史字节漂移；历史 marker 重放时不得重新计算流逝时间。

## [012] 已提交前缀事实不可逆

合法提交的 ContextReanchored 与 PrefixRebaseCommitted 不因后续 provider Failed 或 Aborted 而撤销。

## [013] prefix identity 的比较范围

比较覆盖 provider、model、variant、完整 tools、system prompt 及 message 序列。身份、工具、system 或历史 message 的变更均不能视为追加；冷边界仍须满足 [002]。

唯一例外是全局语言变化（provider-language-002/013）：语言不同导致的 system prompt 与 message 语言整体差异不按前缀漂移处理，不要求 epoch 推进或冷边界声明；语言不变期间本条款照常强制。

## [014] guidance 正文不进入 trace

guidance 后缀正文只参与 provider 呈现，不进入 XTrace、Companion decode、Blogger delta、WorkRecord 或 compaction 输入。仅其 durable occurrence 投影参与 guidance 恢复。

## [015] synthetic identity 确定性

Synthetic ID 由 SealRoot、frameEpoch、ordinal 等持久化身份材料确定性派生，不用 GUID、随机数、时间戳或临时运行时 ID。
