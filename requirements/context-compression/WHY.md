# context-compression — WHY


> **COMPAT（2026-10-29）现行口径**：Scope 生命周期入口是任意工具上的打平注解 `scope.kind` ∈ {`resetScope`,`keepLast`,`removeLast`,`meToo`} 与 retention（`wanxiangli/scope-retention` + 共同法 «Scope»）。**无独立 `resetScope` 宿主工具**；档案重锚 = 注解 `resetScope` → `ScopeReset.commit`。**无** `remainingScopeTurnsEstimate`／`turnBudget`／KPI 倍率产品契约；Stall／ResetDue 估计路径已从宿主产品面退役。下文若仍写独占工具、估计必填或倒计时后缀，以本条为准。PairHint 屏障优先认 `scope.kind=resetScope`，旧工具名只读兼容。


模型能容纳多少历史并不可靠可知。围绕真实失败与已有证据恢复，可以避免容量猜测变成新的控制权。200 KiB 限制解决的是单次记录输入有界，不能拿来推断模型还剩多少空间。

上下文过度堆积会导致模型注意力涣散与推理恶化。传统的扁平累积让过程性工具输出永久驻留；`resetScope` 协议将长程任务组织为显式的细粒度状态闭环（State Closed-Loop）：每个闭环代表一个自洽的工作阶段（如探索定位、代码修改、验证复核等）。

`resetScope` 充当并发执行屏障与上下文垃圾回收（GC）边界。最新一次 `resetScope` 之前的过程性工具调用及其结果被彻底清理，而所有操作员用户消息（prompts 与 steers）始终保留且按原序置于两次 reset 之间，形成明确的因果脉络。旧 scope 中若有关键事实（函数签名、报错堆栈、精确行号等）在下一阶段仍需使用，模型通过将其逐字原样抄写进 `oldScopeVerboseVerbatimExperience` 跨闭环保留，保证认知连续性。

`turnBudget` 倒计时与提醒机制为模型提供软性节奏反馈，鼓励在预算耗尽前自主完成状态闭环并重置；预算归零时不粗暴阻断执行，避免破坏正在进行的工作。

跨会话交互统一采用 AXIOM 7 规范：散文作为 `# ...` 注释头承载意图/报告，父到子的背景由 post-scope 全视图作为 TOML 数据平面承载。子智能体（Engineer/DevOps）不再拥有 `deliver` 工具亦免除 nudge，以无未决调用的自然停机确定性交回末轮 assistant 散文（`# ...` 注释），而非其完整 post-scope 视图；Manager/Plan 则保留生命周期的 `deliver` 用于阶段晋级与接力。

并发、重试与迟到回调会同时看见同一个请求。durable 归属、物理 flight 和命令准入各自解决不同问题；将它们混为一谈，会让旧回调结算甚至终止新的工作。确切结算与共享失败使未知保持未知，也使失败不会重新获得一份预算。

## DEPENDS ON

- `semantic-trace`
- `provider-projection`

