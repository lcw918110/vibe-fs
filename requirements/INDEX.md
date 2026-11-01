# 规范包索引 (Package Index)

包清单只用于浏览与分组，不是条款、不是进度表，也不是包发现依据：包的边界就是 `requirements/` 下的目录本身。新增或删除包时顺手更新本表即可，漏改不会让检查变红，也不会让谁做不成事。

| Package | 说明 |
|---|---|
| `requirement-system` | 本目录的组织规则。 |
| `verification-system` | [测试规则](verification-system/AXIOMS.md)：怎样写、跑、报告。 |
| `feature-ablation` | 模块开关与消融节点。 |
| `js-semantic-surface` | 测试访问产品的 JS 语义边界。 |
| `structured-workflow` | 业务流程用普通代码控制流表达。 |
| `time-capability` | 时间与时限能力。 |
| `causal-wait` | 结构化等待与诊断。 |
| `session-ontology` | 会话类别、所有权与身份。 |
| `managed-session-lifecycle` | 托管子会话的创建、复用、取消与收束。 |
| `host-boundary` | 宿主交互边界与物理能力。 |
| `participant-identity` | 名分、人格与物理执行绑定分离。 |
| `execution-model-routing` | 模型调度与容量。 |
| `office-capability` | 角色权能与后果权限。 |
| `capability-enforcement` | 模型可见工具与运行时权限一致。 |
| `participant-horizon` | 视界：只传会改变行动的上下文。 |
| `cognitive-environment` | 长期背景与瞬时执行隔离。 |
| `cognitive-workspace` | 已退役画板的负向边界。 |
| `attention-regulation` | 注意力调节边界。 |
| `action-affordance` | 动作与参数语义。 |
| `provider-language` | 单会话语言固定与协议标识符。 |
| `provider-projection` | 语义意图向模型视角的投影。 |
| `concern-routing` | 交互路由边界。 |
| `interaction-authority` | 交互权限与逻辑任期。 |
| `managed-chat-execution` | 单条物理消息的受理与终态。 |
| `dispatch-protocol` | 消息派发与幂等。 |
| `durable-events` | 事件追加与重放底座。 |
| `effect-accounting` | 外部副作用分型记账。 |
| `durable-convergence` | 按因果收敛，不靠时钟裁决。 |
| `delegation` | 委托：fork / resume / commission。 |
| `intra-participant-parallelism` | Fission：同一参与者多路并发与收敛。 |
| `process-execution` | 外部进程与终端。 |
| `change-integration` | 工作树改动集成与提交。 |
| `semantic-trace` | 原始语义轨迹。 |
| `context-compression` | 上下文重置与压缩边界。 |
| `prefix-stability` | 前缀逐字节稳定。 |
| `execution-failure-policy` | 失败分类与处置。 |
| `provider-attempt-recovery` | 模型调用失败后的换绑与重试。 |
| `latency-degeneration` | 耗时监测、中断与续行。 |
| `host-provider-failure-ownership` | 宿主失败的所有权。 |
| `crash-reconciliation` | 崩溃后的重放与调和。 |
| `obligation-ledger` | 待办的消极边界。 |
| `work` | Work / Review 生命周期与长程连续。 |
| `repository-investigation` | 本地事实调查。 |
| `knowledge-reuse` | 案例复用。 |
| `repository-programming` | JS 编程面。 |
| `sphinx-v2` | Sphinx 结构化问答。 |
| `distribution` | 安装包与产物完整性。 |
| `epistemic-reasoning` | 历史：旧认识论内核，已被 `sphinx-v2` 取代；不是现行条款。 |
