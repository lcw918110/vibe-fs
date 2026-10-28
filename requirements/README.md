# 功能说明书目录 (Requirement Packages)

> 本目录收录万象术各领域的**功能说明书与行为边界**。
> 一切机制皆为产品唯一总流程服务：**用户提出需求 → 合理交互与调查 → 稳妥修改代码 → 真实做成结束**。
> 说明书（SPEC）是功能浪花的描述，用于指导工程实现与日常自保测试，**不是凌驾于用户意志之上的唯一真理，不搞 proof ownership 证明宗教，亦不搞 AI 包办验收**。若说明书与当前实现存在差距，以文中标注的【程序待改】为准。

## 目录结构约定

每个功能包目录通常包含：
```text
WHY.md      本功能在总流程中的价值与存在理由
WHAT.md     具体的行为约定、边界与输入输出说明
tests/      开发自保与回归测试（NNN.test.mjs）
APPLIES-TO  （可选）该包关心的源码文件路径参考
```

待决事项与已知限制清单见 [GAP.md](GAP.md)；完整包清单见 [INDEX.md](INDEX.md)。

---

## 领域划分

### 1. 规范与消融
| Package | 职责说明 |
|---|---|
| [requirement-system](requirement-system/WHAT.md) | 说明书组织规则与包结构约定。 |
| [verification-system](verification-system/WHAT.md) | 分层测试调度（单元测试、集成测试与端到端回归）。 |
| [feature-ablation](feature-ablation/WHAT.md) | 功能消融与模块开关，支持渐进式关闭未审机制。 |
| [js-semantic-surface](js-semantic-surface/WHAT.md) | JS 语义入口约定，提供稳定的测试与调用表面。 |

### 2. 流程与时间
| Package | 职责说明 |
|---|---|
| [structured-workflow](structured-workflow/WHAT.md) | 业务流程由普通语言控制流表达，避免在领域层重复发明状态机。 |
| [time-capability](time-capability/WHAT.md) | 显式时间与时限能力，避免隐式环境时钟影响判定。 |
| [causal-wait](causal-wait/WHAT.md) | 结构化等待与可观测性，等待诊断不升级为业务特权。 |

### 3. 会话与宿主边界
| Package | 职责说明 |
|---|---|
| [session-ontology](session-ontology/WHAT.md) | 区分会话类别、所有权与角色人格，避免底层拓扑污染业务身份。 |
| [managed-session-lifecycle](managed-session-lifecycle/WHAT.md) | 子会话生命周期管理：创建、复用、取消与有序收束。 |
| [host-boundary](host-boundary/WHAT.md) | 外部宿主边界约定，将宿主信号与内部状态解耦。 |

### 4. 参与者与角色
| Package | 职责说明 |
|---|---|
| [participant-identity](participant-identity/WHAT.md) | Role（名分）、Persona（人格）与 ExecutionBinding（物理执行）分离。 |
| [execution-model-routing](execution-model-routing/WHAT.md) | 模型调度路由，根据角色与负载动态分配 ModelTarget。 |
| [office-capability](office-capability/WHAT.md) | 角色职位与权能定义，明确谁能产生何种物理后果。 |
| [capability-enforcement](capability-enforcement/WHAT.md) | 门禁与权限投影，确保模型看见的工具与运行时权限严格一致。 |
| [participant-horizon](participant-horizon/WHAT.md) | 视界管理，只传递会改变行动的关键上下文。 |
| [cognitive-environment](cognitive-environment/WHAT.md) | 长期认知背景与瞬时执行隔离。 |
| [cognitive-workspace](cognitive-workspace/WHAT.md) | 负向边界说明（旧画板与 TodoSink 已退役）。 |
| [attention-regulation](attention-regulation/WHAT.md) | 注意力调节边界（旧 enough/abandon 已退役，收拢至 assume 与 scope 文本）。 |
| [action-affordance](action-affordance/WHAT.md) | 动作可行性与参数语义说明。 |
| [provider-language](provider-language/WHAT.md) | 自然语言一致性（单会话语言固定，协议标识符不翻译）。 |
| [provider-projection](provider-projection/WHAT.md) | 语义意图向模型视角的投影呈现。 |

### 5. 交互、持久化与因果
| Package | 职责说明 |
|---|---|
| [concern-routing](concern-routing/WHAT.md) | 交互路由边界（旧邮箱已退役，统一归入编程面委托）。 |
| [interaction-authority](interaction-authority/WHAT.md) | 交互权限与逻辑任期推进规则。 |
| [managed-chat-execution](managed-chat-execution/WHAT.md) | 单物理消息的受理、执行与终态管理。 |
| [dispatch-protocol](dispatch-protocol/WHAT.md) | 消息派发协议，避免网络与宿主不稳定导致逻辑重复。 |
| [durable-events](durable-events/WHAT.md) | 不可变事件追加与确定性重放底座。 |
| [effect-accounting](effect-accounting/WHAT.md) | 外部副作用分型记账（请求、已知提交、未知结果隔离）。 |
| [durable-convergence](durable-convergence/WHAT.md) | 基于业务因果的状态收敛，不依赖时钟裁决。 |

### 6. 工作委托与协作
| Package | 职责说明 |
|---|---|
| [delegation](delegation/WHAT.md) | 任务委托（fork/resume/commission）的责任与后果转交。 |
| [advisor](advisor/WHAT.md) | 专席顾问交互说明，提供纯散文第二意见。 |
| [intra-participant-parallelism](intra-participant-parallelism/WHAT.md) | Fission 机制：同一 Engineer 的多路当下与统一收敛。 |
| [process-execution](process-execution/WHAT.md) | 外部进程执行与终端交互。 |
| [change-integration](change-integration/WHAT.md) | 工作树改动集成与短门提交流程。 |

### 7. 上下文与记忆
| Package | 职责说明 |
|---|---|
| [semantic-trace](semantic-trace/WHAT.md) | 原始语义轨迹记录与定位。 |
| [context-compression](context-compression/WHAT.md) | 上下文压缩与 resetScope 重置机制。 |
| [prefix-stability](prefix-stability/WHAT.md) | 前缀稳定性，确保历史上下文逐字节不变以维持缓存与因果稳定。 |

### 8. 故障恢复
| Package | 职责说明 |
|---|---|
| [execution-failure-policy](execution-failure-policy/WHAT.md) | 统一失败分类与处置策略。 |
| [provider-attempt-recovery](provider-attempt-recovery/WHAT.md) | 模型调用失败后的换绑与重试机制。 |
| [host-provider-failure-ownership](host-provider-failure-ownership/WHAT.md) | 接管宿主错误弹窗，统一故障处置所有权。 |
| [crash-reconciliation](crash-reconciliation/WHAT.md) | 进程意外崩溃后的事件重放与现场调和。 |

### 9. 任务与里程碑
| Package | 职责说明 |
|---|---|
| [obligation-ledger](obligation-ledger/WHAT.md) | 待办推进说明（编程面 todowrite 约定）。 |
| [work](work/WHAT.md) | Work 与 Plan 生命周期：阶段晋级、review 推进、长程连续性与结案。 |

### 10. 代码调查与编程面
| Package | 职责说明 |
|---|---|
| [repository-investigation](repository-investigation/WHAT.md) | 本地代码与事实调查规则（先查清再动手）。 |
| [knowledge-reuse](knowledge-reuse/WHAT.md) | 历史经验与案例复用（作为参考，不冒充当前事实）。 |
| [repository-programming](repository-programming/WHAT.md) | JS 编程面核心说明（cat/where/write 切片操作与事务沙箱）。 |

### 11. 其它
| Package | 职责说明 |
|---|---|
| [sphinx-v2](sphinx-v2/WHAT.md) | Sphinx 机制说明：MCP 单一入口与结构化问答。 |
| [distribution](distribution/WHAT.md) | npm 安装包构建与产物完整性约定。 |
