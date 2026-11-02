# 功能说明书目录

本目录按功能分包存放说明书与自保测试。

**写法。** 每包一份 `WHAT.md`（`verification-system` 用 `AXIOMS.md`），写操作员或组装方看得见的行为与边界；`WHY.md` 写理由；`tests/` 放该包的自保测试。说明只在规范文件里写一处，其余文件引用。

**口径。** 说明书服务总流程：用户打开软件，经过合理的交互，万象术了解需求，把事情做完、做成，结束。它不设验收栏、oracle 或 witness，也不把测试套件升格成宪法。与程序暂时对不上的地方写进 [GAP.md](GAP.md)（程序待改清单），由人按新说明改程序。

**分工。** 状态所有权迁往万象理 `wanxiangli/`；宿主翻译归万象通 `wanxiangtong/`；本仓只剩产品提示、工具资源与组装，向 empty 收敛。每篇 `WHAT.md` 先写清它在总流程上的浪花，再写边界；改措辞时保留真实测试。

**Scope 现行口径。** Scope 入口是任意工具上的 `scope.kind` 注解与 retention（`wanxiangli/scope-retention` + 共同法 «Scope»）。没有独立 `resetScope` 工具，也没有 `remainingScopeTurnsEstimate` 必填。个别 WHAT 若还留旧句，以共同法 «Scope» 为准，遇到时顺手改掉。

## 包清单

| 包 | 一句话 |
|---|---|
| [requirement-system](requirement-system/WHAT.md) | 本目录的组织规则。 |
| [verification-system](verification-system/AXIOMS.md) | 测试怎样写、怎样跑、怎样报告。 |
| [feature-ablation](feature-ablation/WHAT.md) | 模块开关与消融节点。 |
| [js-semantic-surface](js-semantic-surface/WHAT.md) | 测试访问产品的 JS 语义边界。 |
| [structured-workflow](structured-workflow/WHAT.md) | 业务流程用普通代码控制流表达。 |
| [time-capability](time-capability/WHAT.md) | 时间与时限能力的归属。 |
| [causal-wait](causal-wait/WHAT.md) | 结构化等待与诊断。 |
| [session-ontology](session-ontology/WHAT.md) | 会话类别、所有权与身份。 |
| [managed-session-lifecycle](managed-session-lifecycle/WHAT.md) | 托管子会话的创建、复用、取消与收束。 |
| [host-boundary](host-boundary/WHAT.md) | 宿主交互边界与物理能力。 |
| [participant-identity](participant-identity/WHAT.md) | 名分、人格与物理执行绑定分离。 |
| [execution-model-routing](execution-model-routing/WHAT.md) | 模型调度与容量。 |
| [office-capability](office-capability/WHAT.md) | 角色权能与后果权限。 |
| [capability-enforcement](capability-enforcement/WHAT.md) | 模型可见工具与运行时权限一致。 |
| [participant-horizon](participant-horizon/WHAT.md) | 视界：只传会改变行动的上下文。 |
| [cognitive-environment](cognitive-environment/WHAT.md) | 长期背景与瞬时执行隔离。 |
| [cognitive-workspace](cognitive-workspace/WHAT.md) | 已退役画板的负向边界。 |
| [attention-regulation](attention-regulation/WHAT.md) | 注意力调节边界。 |
| [action-affordance](action-affordance/WHAT.md) | 动作与参数语义。 |
| [provider-language](provider-language/WHAT.md) | 单会话语言固定与协议标识符。 |
| [provider-projection](provider-projection/WHAT.md) | 语义意图向模型视角的投影。 |
| [concern-routing](concern-routing/WHAT.md) | 交互路由边界。 |
| [interaction-authority](interaction-authority/WHAT.md) | 交互权限与逻辑任期。 |
| [managed-chat-execution](managed-chat-execution/WHAT.md) | 单条物理消息的受理与终态。 |
| [dispatch-protocol](dispatch-protocol/WHAT.md) | 消息派发与幂等。 |
| [durable-events](durable-events/WHAT.md) | 事件追加与重放底座。 |
| [effect-accounting](effect-accounting/WHAT.md) | 外部副作用分型记账。 |
| [durable-convergence](durable-convergence/WHAT.md) | 按因果收敛，不靠时钟裁决。 |
| [delegation](delegation/WHAT.md) | 委托：fork / resume / commission。 |
| [intra-participant-parallelism](intra-participant-parallelism/WHAT.md) | Fission：同一参与者多路并发与收敛。 |
| [process-execution](process-execution/WHAT.md) | 外部进程与终端。 |
| [change-integration](change-integration/WHAT.md) | 工作树改动集成与提交。 |
| [semantic-trace](semantic-trace/WHAT.md) | 原始语义轨迹。 |
| [context-compression](context-compression/WHAT.md) | 上下文重置与压缩边界。 |
| [prefix-stability](prefix-stability/WHAT.md) | 前缀逐字节稳定。 |
| [execution-failure-policy](execution-failure-policy/WHAT.md) | 失败分类与处置。 |
| [provider-attempt-recovery](provider-attempt-recovery/WHAT.md) | 模型调用失败后的换绑与重试。 |
| [latency-degeneration](latency-degeneration/WHAT.md) | 耗时监测、中断与续行。 |
| [host-provider-failure-ownership](host-provider-failure-ownership/WHAT.md) | 宿主失败的所有权。 |
| [crash-reconciliation](crash-reconciliation/WHAT.md) | 崩溃后的重放与调和。 |
| [obligation-ledger](obligation-ledger/WHAT.md) | 待办的消极边界。 |
| [work](work/WHAT.md) | Work / Review 生命周期与长程连续。 |
| [repository-investigation](repository-investigation/WHAT.md) | 本地事实调查。 |
| [knowledge-reuse](knowledge-reuse/WHAT.md) | 案例复用。 |
| [repository-programming](repository-programming/WHAT.md) | JS 编程面。 |
| [sphinx-v2](sphinx-v2/WHAT.md) | Sphinx 结构化问答。 |
| [distribution](distribution/WHAT.md) | 安装包与产物完整性。 |
| [epistemic-reasoning](epistemic-reasoning/WHAT.md) | 历史：旧认识论内核，已被 sphinx-v2 取代；仅存线索索引，不是现行条款。 |

包的边界就是 `requirements/` 下的目录本身，有没有 `WHY.md`、规范文件与 `tests/` 一眼可见。[INDEX.md](INDEX.md) 只是便于浏览的表格视图，不参与包发现，漏改不会让检查变红。
