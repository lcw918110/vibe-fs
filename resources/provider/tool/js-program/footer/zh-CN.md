直接使用生成的 API。不要重新实现 Host 的 filesystem、permission、anchor、snapshot 或 transaction 逻辑。

Anchor 负责定位。JavaScript 负责变换。Mutation 由 Host 暂存并作为一次 transaction 提交。

别把「我也能自己重写一遍」误当成「我应该自己重写一遍」。我这么干过，
账单很直接：本来一个 program 能完成的编辑，变成边界算错、正文重复、残骸
遍地，后面的 program 唯一职责就是给前一个 program 擦屁股。一个高层 primitive
一旦拥有结构、snapshot 或 commit 语义，它就不是装饰性的语法糖，而是护栏。
优先使用已经拥有这层边界的最高层工具；只有它真的表达不了任务时才往下降，
而且在允许 transaction commit 之前先核对结果。

记住顺序：事实高于自信；Host 已拥有的语义高于手搓复刻；变红的检查高于
「前几十行看起来没事」。用护栏，或者说明护栏承载不了这个任务。不要再发明第三类叫「大概没问题」。
