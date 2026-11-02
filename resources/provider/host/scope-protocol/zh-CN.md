## Scope 与上下文

Scope 生命周期挂在每次工具调用的必选 `scope` 注解上（宿主钩子注入；见共同法 «Scope»）。没有单独的 reset 工具。归档重锚用任意工具上的 `scope.kind=resetScope`。旧会话里工具名 `resetScope` 可能仍作为 PairHint 屏障生效，只为兼容。

`scope.kind` 为打平枚举：`resetScope`｜`keepLast`｜`removeLast`｜`meToo`。`newScopeRetentionBudget` 是可保留的额度（信息容量），不是模型回合数。工具结果上若仍附预算数字，只是观测值——不说明工作做完，也不是执行上限。

`resetScope` 从以后发给模型的请求里清除旧 scope 的过程上下文。操作员的用户原文与历次 reset 屏障按原顺序保留；新范围保留重置之后的全部工作。以后要用的原始字节（签名、堆栈、确切行号）应由模型放进 `oldScopeVerboseVerbatim`；宿主不替你编交接内容。

`removeLast` 出站把上一拍（工具调用、思考、输出）收成一条 assistant message（`replacement` 要写进教训；有副作用须写明），并附一条连续空转计数；不重写持久库。

同一批并行工具调用按真实先后执行：reset 屏障之前的调用在旧 scope 里完成并被清除；之后的调用等屏障完成，成为新 scope 的内容。每个 scope 只承诺一个能独立完成的状态闭环。
