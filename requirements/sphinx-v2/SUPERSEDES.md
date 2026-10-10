# sphinx-v2 supersedes 关系

本文件记录 sphinx-v2 对 `epistemic-reasoning` 的取代范围。它是 supersede 记录，不是删除指令。

## 取代范围

`sphinx-v2/WHAT.md` 的 36 条命题取代 `epistemic-reasoning/WHAT.md` 中描述旧内核的条款。旧条款的文本、旧测试、旧数据不删除：历史 inquiry 事件仍在权威日志中，可由原版本工具离线读取；旧测试文件保留为反例与设计对照，不进新 runner 的通过集。

2026-09-28迁移中的旧测试输入保存在 `epistemic-reasoning/historical-tests/`，使用非执行后缀并附解压后SHA-256清单；完整旧版本保留于Git备份 `1d7098a38`。归档不恢复旧生产接口，也不表示新测试已充分替代全部证明；当前范围见 `sphinx-v2/tests/README.md`。

## 逐条对应

| epistemic-reasoning 条款 | 状态 | sphinx-v2 对应 | 理由 |
|---|---|---|---|
| 001 认识状态为充分状态 | 取代 | 009、017 | 新状态的充分性由唯一 fold + canonical Current 保证，不再依赖 Legacy plugin 的 opaque payload |
| 002 Runtime 拥有提交权 | 取代 | 009、018 | 前提保留，但"默认 Legacy plugin 提供方法激活"整段退出 |
| 003 认识基底 Findings/Evidence/Hypotheses | 取代 | 005、006 | 该三元组移出 Core，成为插件语义类别 |
| 004 Pending Request 与 Observation 契约 | 取代 | 011、012、021 | 四阶段严格同型 request 换成 v2 工作身份与两阶段接纳 |
| 005 Proposal 与 Evidence 分层 | 取代 | 002、018 | 分层保留，语义类别由插件定义 |
| 006 Evidence 保留 Source 与 Dependency | 取代 | 003、014 | 等价类与去重规则改由 ballot cluster / attempt 承担 |
| 007 RootContract 分布与动态更新 | 取代 | 001、002 | QuestionForm 本体删除；目标只由用户修订 |
| 008 Legacy 根相对 Action Value 与 Gateway 价值 | 取代 | 002、013、029 | `0.72/(1+K)`、gateway 增益、期望根收益整类公式删除 |
| 009 Bayes exact refiner 仅接受合格因子 | 部分保留 | 025、026 | 合格因子条件保留；实现迁到 `Plugins/Bayes/Exact` 重写输入契约 |
| 010 三种经典精化的可验证退化 | 部分保留 | 025、026、027 | 标准算法退化保留；"SolverMode 互斥"与 coverage 包装删除 |
| 011 依赖感知的等价约简 | 取代 | 003、005 | 等价类不再是 Core 判重机制 |
| 012 拓扑精化闭包幂等 | 取代 | 027 | closure 声明域改为可证明条件 |
| 013 唯一原生插件工具 sphinx(question) | 取代 | 036 | 单工具换成 v2 七件套 MCP 白名单 |
| 014 唯一命令 /sphinx，无 MCP 接入 | 取代 | 035、036 | MCP 成为 v2 接入面之一 |
| 015 Core 认识论零硬编码 | 保留并加强 | 006、007 | 新 Core 只持 ID/envelope/图/槽/工作事实/事件 |
| 016 单一证书空间与分型精化保证 | 取代 | 005、026 | 证书改按 scope 地址寻址；保证分型细化 |
| 017 探究协议是可回放实验 | 取代 | 022、023 | 保留协议绑定字段，补 host-private label 与 missingness |
| 018 原生入口与回放语义等价 | 取代 | 008、016、020 | 三哈希分离，物理 binding 显式排除 |
| 019 Inquiry 以 canonical EventStore 为唯一 durable truth | 保留 | 009、010、019 | 不变；v2 batch 封装方式明确 |
| 020 Plugin manifest、依赖与 schema lock | 保留 | 015 | 加强为真实内容哈希 |
| 021 通用 WorkItem 拒绝非法生命周期 | 取代 | 003、011、012 | fence 与 attempt 分离；晚到结果按 ticket 归档 |
| 022 Scheduler 选择相容计算 | 取代 | 004、013 | 收益比较改为声明模型下的贡献估值 |
| 023 Split-ballot 随机化与问法效应 | 取代 | 022、023、024 | 保留随机化审计，ATE 解释前提补 SUTVA/positivity |
| 024 Borda 与 Bradley–Terry 适用域 | 取代 | 024、025 | 补 tie-aware 似然与完整协方差契约 |
| 025 Proper self-prediction 密封 | 退出默认 | — | SelfPrediction 移出 default profile，后续 Experimental 插件 |
| 026 固定点存在性与异步收敛 | 取代 | 027 | 收敛声明收窄到可证明条件 |
| 027 OpenCode Host 复用现有受管执行语义 | 保留 | 034 | 不变；receipt 必须来自实际 adapter |
| 028 Research export 区分可识别对象与外部真值 | 取代 | — | 由 `Persistence/Export` 与 full/summary 区分承担 |
| 029 Stop certificate 只覆盖已检验的决策域 | 取代 | 029、030 | answer.now 参加选择；缺 VOC 是缺失不是通过 |
| 030 Legacy Adapter 黄金轨迹 | 取代 | — | Legacy Adapter 退出生产构建 |
| 031 Sphinx 探究流程全程序控制 | 保留 | 009、018 | 不变 |
| 032 内部 Engineer 使用标准权限 | 保留 | 034 | 不变 |
| 033 结果接纳按工作身份幂等 | 取代 | 011 | key 增加 logicalFence |
| 034 取消全链贯穿 | 保留 | — | 由 Runtime/Recovery 承担，语义未变 |
| 035 原生调用交付与期望深度契约 | 取代 | — | `expectTurns` 价格模型删除，改为资源账本 |
| 036 期望回合转为持久化价格预算 | 取代 | 002、004 | `1.44/(expectTurns-1)^2`、`0.72/(1+K)`、`2*lambda` 购买规则整类删除 |

## 未被取代的共享要求

`epistemic-reasoning` 中不属于 Sphinx 内核的共享要求仍然有效。与 Sphinx 无关的 shared owner 约束、其它功能的事件类型登记、全仓 CI 门禁不因本 supersede 改变。

## 旧数据的处置

旧事件不删除、不自动转换为新语义、不执行目录清空。新程序必须明确拒绝把旧 inquiry 当作 v2 恢复：返回 `LEGACY_INQUIRY_UNSUPPORTED` 或经版本识别的同义错误，附不含敏感内容的恢复说明。不得悄悄返回空状态。

## 原生工具面残留的清理

2026-10-10（WP-039）：旧原生工具面的残留——权限词表 `ToolPermission.Sphinx`、静态工具名单、ablation 工具映射与 `resources/provider/tool/sphinx/` 文案资源——已清理。插件不提供 `sphinx` 原生工具面；生产接入仅 v2 七件套 MCP。历史事件与旧数据处置仍按上一节。
