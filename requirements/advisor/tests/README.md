# Advisor 测试与证明范围

行为合同位于 [../WHAT.md](../WHAT.md)。

`004.test.mjs`：经公开 `wanxiangli/scope-progress` 接口验证四段问答、同一顾问席位延续对话、自然提示内容、建议 steer、重复报告去重和过期 Scope 拒绝。它验证组合协议的调用次序，**不是**真实商业 Provider 或跨进程恢复的证明。

`006.test.mjs`：经真实 JS 生成与执行产物验证旧 `this.advisor` 不在模型可见和运行成员中，伪造 `Advisor` 能力不会启用它，遗留程序调用不能抵达专席。

`../host-boundary/tests/scope-hni-physical.test.mjs`（从仓库根计为 `requirements/host-boundary/tests/scope-hni-physical.test.mjs`）：`delegation-negotiation-005` 验证专席零工具的真实角色配置与产品准入；`035`—`037` 验证临时问询保持原工具 schema、租约隔离以及失败不转成聊天内容；`advisor-031` 验证顾问不能再次派生顾问。

`wanxiangli/src/scope-progress/tests/001–003.test.mjs` 验证计费回合聚合、队列合并、四段顺序、迟到与失败释放。`004.test.mjs` 经真实 SQLite 验证受理和终结的 Committed／Replayed／Conflict／Unknown、SIGKILL 重读、跨两个独立 AdvisorFlow 队列与独立 OS 进程的全局单飞、确定性修订号竞争重试、终局抛错保护、跨强杀只读 Pending 和完成后恢复准入。

`007.test.mjs` 通过真实 OpenCode 插件确认 SQLite 受理与阶段预留先于 SDK session.create／prompt；SDK Provider 错误若没有确定终局回执，只留下 Admitted／StageBegan，禁止 Finished(Failed) 及重复付费请求；同报告重放不追加新受理。 `008.test.mjs` 使用真实 PromptDispatcher/AgentJournal 的 Await 发送与物理接纳观测，证明 SDK 提交不能冒充可见落地、物理结果 Unknown 保留持久 Pending；同时确认 Advisor 才使用 Await 而普通协商不变。 `009.test.mjs` 更直接调用生产 `HostForkBusyNudge.sendObservedAwait`，以真实 Host Session port 和 AgentJournal 验证只有 `PhysicalAccepted` 才返回 F# Ok，送达未知保留 PromptKey。 `010.test.mjs` 直接经生产 `ToolRuntimeScope.SendAdvisorSteer` 测试排队不完成、物理落地成功、等待期间 Scope 推进拒绝旧建议成功、Host Unknown 保留原始未决声明。`011.test.mjs` 通过真实 AgentJournal／ToolRuntimeScope／SQLite 完成 A／B／C／D＋物理 steer 纵切：先五个 StageBegan/StageAnswered，最终落地后才有 Finished。`012.test.mjs` 测试真实 OpenCode chat.params 只有已证明的当前 Scope epoch 才能认领旧 SQLite Pending，已发但回复未知的阶段不再调用 Provider。`013.test.mjs` 经注册的 OpenCode resetScope 真正提交 ScopeReanchored 后清空全部内存计费状态，证明下一宿主请求直接用 AgentJournal 身份认领旧 Pending，未确认外部阶段不重发、未知余额不伪造。`wanxiangli/src/scope-progress/tests/005–006.test.mjs` 另测真实 SQLite 冷重开时复用已确认 prose、未知请求禁止重发及物理 steer 独立预留。 `wanxiangli/src/scope-progress/tests/007–008.test.mjs` 进一步以真实 SQLite 的 CAS、跨实例与 SIGKILL 测试保存普通 Provider 计费回合的最小估计、重启后上一回合的比较基线、ResetDue 与已结算但尚未派发的 AdvisorDue outbox。`014.test.mjs` 在最新官方 Fable 编译后通过真实 OpenCode todowrite→Recorded/Settled→清内存→恢复旧估计与 ResetDue 的纵切；`015.test.mjs` 在两个同时存活的真实 OpenCode 插件工作区之间交替执行 todowrite，证明按 Session 绑定的 SQLite 不会被第二插件初始化覆盖。 `006.test.mjs` 还验证三个不同 Pending 报告只需一次恢复就依次完成、原合并 Process 的精确持久 reportIds 冷恢复、首项 Unknown 禁止抢跑、SDK CapacityQueueFull 一类未确证结果不得错误提交 Failed；`005.test.mjs` 验证同一报告不能分配给两个不同 Process；`007.test.mjs` 的真实 OpenCode 物理 SDK 失败也只保存 StageBegan，不发第二次付费咨询。

`requirements/host-boundary/tests/omp-negotiate-physical.test.mjs` 的本机 Bun + OMP SDK + SSE 假 Provider canary 已重新通过。 SSE 测试接收器现在跨 TCP chunk 持续解码 UTF-8（“到得早” 严格保真）；修复后保留完整前缀与工具定义逐字段相等断言，连续 8 次真实 Bun 本地 Provider canary 通过。

**尚缺证明**：实际供应商 ProviderRunId 与 SDK 物理请求结果的强杀后重新认领（当前只有阶段逻辑请求预留及已确认 prose）；没有新工作边界时的独立恢复调度；完整合并等待项冷恢复；商业 Provider 端到端 canary。OMP 的原生 negotiate/askPreviousScope 本地 Provider canary 已通过，但 OMP Adapter 目前尚无有效 todowrite 计费与自动 AdvisorFlow 触发的完整接线，此项仍未验收。单次持久受理与全局单飞不能冒充整个四段流程 exactly-once。
