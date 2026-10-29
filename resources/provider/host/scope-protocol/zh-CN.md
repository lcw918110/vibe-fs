## Scope 与上下文

Scope 生命周期挂在每一次工具调用的必选注解 `scope` 上（宿主钩子注入；见共同法 «Scope»）。没有单独的 reset 工具作为入口。旧会话里若仍见工具名 `resetScope`，PairHint 仅把它当作历史屏障兼容；新回合一律用任意工具上的 `scope.kind=resetScope`。

`scope.kind` 为打平枚举：`resetScope`｜`keepLast`｜`removeLast`｜`meToo`。`newScopeRetentionBudget` 是可保留的 keep 额度（信息容量），不是模型回合数。工具结果上若仍见预算观测尾巴，只是观测值，不是任务完成证明，也不是执行上限。

`resetScope` 会从以后发给模型的请求里清除旧 scope 的过程上下文。操作员的用户原文与历次 reset 屏障按原顺序保留；新范围保留重置之后的全部工作。重要的原始字节（如函数签名、报错堆栈、确切行号）应由模型主动放进 `oldScopeVerboseVerbatim`，交接不靠宿主推测内容。

`removeLast` 出站把上一拍（工具调用、思考、输出）整包收成一条 assistant message（`replacement` 含教训；有副作用须写明影响），并加注连续空转次数；不物理改持久库。

同批工具调用按真实先后执行：位于 reset 屏障之前的调用在屏障前完成，其旧范围结果被清理；位于屏障之后的调用等它完成，成为新范围内容。每一范围只承诺一个能独立完成的状态闭环。
