# 前缀世代的产品连接

当前世代、重锚身份、上一 Scope 归档地址和提问回执由 `wanxiangli/prefix` 的 `PrefixHistory` 持有。`Epoch.fs` 只把产品身份与错误映射到公开入口；所有读者通过查询取值，不再维护第二份 Set/Map。Journal 重放依次把同一批事实交给历史对象，恢复归档地址与提问回执。

`PrefixReanchor` 读取 Journal 当前已提交的历史，选择一个压缩候选并追加 `ContextReanchored`。它不拥有宿主快照判定或内存去重；OpenCode 的配置、hooks 与快照生命周期在外层 `wanxiangtong/opencode/compaction/`。压缩、任期与 Scope 的旧拒绝顺序、归档缺失分支和回执冲突语义保持不变。

Scope 归档的 Blob 写入、产品事实编码、其他模块可见性退休仍由已有组合层负责。登记提问回执与实际回答送达是不同动作；主程序重开后也不能把回执当成送达。
