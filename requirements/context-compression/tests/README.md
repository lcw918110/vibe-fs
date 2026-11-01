# context-compression 测试

WHAT 拥有输入、提交与生命周期边界；本文只说明现有测试如何观察这些边界，不补充验收规则。

| 文件 | 已有证据与范围 |
|---|---|
| 001 | 原容量词汇扫描已撤下；真实依赖隔离待证。 |
| 002 | Host compaction 设置判别与重锚幂等性。 |
| 004、005、007 | 终态文本判别、失败预算及 typed RequestKind 分派（区分 WorkMain 与 InteractionRepair，clean-break 退役种类拒绝）。 |
| 006、008 | 角色固定性、连续失败预算消耗及主会话无压缩请求。 |
| 013 | 当前诊断 emit 的非致命行为。诊断失败或文本改变不影响真实决策仍待证；删除未进入产品的常量对象比较及在此重复的 Host fatal 展示。 |
| 017 | 真实用户消息保留规则（操作员用户消息永不删除且按序夹在 resetScope 之间，断言需重写适配 resetScope 语义）。 |
| 019 | 辅助注入的 epoch 退休与 tentative horizon。 |
| 023 | typed park 返回及取消；事件驱动依赖与 durable producer 判据仍待完整验证。 |
| 030 | assume 调用与结果原样保留，所有后续 resetScope 也不能删去；与 negotiate、askPreviousScope 同组 [046]。 |
| 031 | （条款已废止，编号永久空缺；原测试已删除） |
| 032 | （条款已废止，编号永久空缺；原测试已删除） |
| 033 | （待实现）resetScope 屏障执行、旧 scope 清理与操作员用户消息/成对 reset 结果保留。 |
| 034 | 进度估计与 ResetDue 归 `scope-progress`：中立计划不产生倒计时尾巴、不伪造首次估计提醒；已初始化后的新真人／代理消息不重置 Scope；工具结果后缀工具本身仍幂等。 |
| 035 | （待实现）ScopeReanchored 事实提交、epoch 推进与前缀缓存幂等清空。 |
| 036 | （待实现）跨会话只传当前指令与结构化材料的 TOML 渲染与传递。 |
| 037 | （待实现）子智能体自然停机完成、无 nudge，及末轮 assistant 散文以 `#` 注释交回父会话（无 `[[session]]` 表）。 |

| 038 | HNI 中立 Scope 计划：保留位与裁剪只由 PairHint 决定；估算强 steer 与工具结果后缀都不由它产生，原始 Host 消息不变。 |
| 039、040 | OpenCode 发往模型前的热修正：请求数组原地更新、缺估计时追加独立强 steer、reset 后只保留其调用与之后工作，已完成结果逐字不变；SnapshotScope 按 toolCallId 同时保留历史 reset 调用与结果，并保留同消息 assistant 散文。实体物理断言仍由 `requirements/host-boundary/tests/scope-hni-physical.test.mjs` 承担。 |
| 041 | 物理会话删除事件清理对应 ScopeSession，不牵连其他会话（同一 Host physical canary）。 |
| 051 | 冷重启历史包含 reset 但当前预算未恢复：持续独立强 steer 要求新 reset，绝不伪造余额 0 或零预算弱提醒；新 reset 后正常预算展示；见 host-boundary/scope-hni-physical.test.mjs。 |
| 053 | 旧 Scope 的 thinking/reasoning 随该 Scope 退役：双宿主请求副本实测旧 thinking 消失、assistant 正文与关键工具对保留、最新 reset 之后的 thinking 完整、原始消息对象与持久历史不被改写；见 pair-hint/016 与 Host physical canary。 |
| 050 | 真实 OpenCode 已注册 resetScope 工具双重锚 canary：第一 reset 不催 askPrevious，第二 reset 才要求；期间真人新消息预算继续、非 reset 工具输出不写 MUST；见 host-boundary/scope-hni-physical.test.mjs。 |
| 047–049 | OpenCode 真实 reset→archive→read-only BTW→真实 steer、持久 receipt 重放与异问题冲突见 Host physical canary；OMP 工具注册、无档案拒绝、旧 native Provider system/messages/model/tools 冻结及真实 side-turn 回放见 `omp-scope-seam` [048][049]；真实 OMP SDK + 本地 SSE Provider 出站、代理消息及重放见 opt-in `omp-negotiate-physical.test.mjs`。商业 Provider cache 命中与崩溃 Unknown 尚未证。 |
| 042 | （部分实施；见 [047]/[048] 未证范围）跨 Scope 回问接口（askPreviousScope）：紧邻上一 epoch 访问限制、首次 reset 前 NoPreviousScope、questions 非空形式校验、Host receipt 即解除 pending、回答作为 agent 来源 user steer 异步送达与 Stale/Unknown/重放幂等。 |
| 043 | （部分实施；见 [047]/[048] 未证范围）resetScope 档案与首问义务原子绑定：不可变内容地址 blob 先行持久化、同一 durable 提交绑定 ScopeReanchored 与首问义务、提交确证先于内存状态更新、原始返回文本不变，及 session.deleted / 再次 reset 在途咨询清理与 Stale 判定。 |
| 044 | （部分实施；见 [047]/[048] 未证范围）跨 Scope 回问与首问协商的对称 pending 状态：提示词与时序驱动（普通工具不被拦）、请求副本持续挂载 MUST 提醒（逐字重放）、预算与时序冻结（无预算后缀、不递减、初始化不前进、weak 关闭）、resetScope 自身自拒，及 PairHint 唯一状态归属。 |

完成节点后先正式构建，再通过 `requirements/verification-system/tests/run.mjs` 与 `TESTS_MJS_FILES` 运行本目录及受影响所有者。TODO 不计通过，GAP-104/105记录未闭合状态；旧发布清单的DONE不作当前证据。已通过本地 OMP SDK + 回环 SSE 假 Provider 的真实网络出站及人工可重复物理 canary；没有执行商用外部 Provider、cache hit/费用人工验收。
