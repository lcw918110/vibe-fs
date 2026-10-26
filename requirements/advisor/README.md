# Advisor 实现与验收位置

本目录的行为合同见 [WHAT.md](WHAT.md)。Advisor 由当前 Scope 的进度结算自动触发；工作模型不拥有 `this.advisor` 成员，也不能直接派发顾问咨询。

每个真实工作 Session 共享一个 dedicated Advisor 席位。宿主通过 `AdvisorSeat.consult` 创建或认领家族根下的顾问子会话，延续其自身对话，而不是每次新建一席。它与短命工作者问询是两种会话：前者保存持续的顾问讨论，后者借最近一次 transform 视图，只读提问，不向真实工作 Session 插入临时消息。

`wanxiangli/scope-progress` 中的 `ScopeProgress`、`AdvisorProcessQueue` 和 `AdvisorFlow` 分别拥有进度报告、单飞排队及四段顺序。两次工作者提问和两次顾问提问按 A→B→C→D 串行执行。顾问 A/C 阶段的取景由真实宿主的临时 Provider 能力提供；B/D 延续同一 Advisor 席位。最后的建议以带源 Scope 身份的 agent-attributed steer 送回。

Dedicated Advisor 的 Host 配置和产品执行准入均不允许任何工具；短命问询仍保留来源 Provider 原有工具 schema，仅在执行前拒绝工具调用，以保护来源前缀。Provider 错误、租约失败、容量错误与真正的回答必须分开处理。

进度触发先以 `wanxiangli/eventstore` 的 SQLite 原子事实持久受理；完成或失败后再写 `Finished`，经 Committed／Replayed 确证后才释放下一条咨询。这保证跨进程不重复受理和全局单飞，并通过真实 SIGKILL、OpenCode SDK 时序和 Node/Bun 产物验证。**四阶段各自的 Provider 请求与已确认 prose 的冷恢复、商业 Provider 端到端实测仍未验收。** 顾问席位能通过物理子会话列表认领，不等于未完成 Process 可以安全重试。具体范围与证据见 [tests/README.md](tests/README.md)。
