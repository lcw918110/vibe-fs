# OpenCode 压缩会话

长对话触发 OpenCode 压缩时，同一个 `OpenCodeCompaction` 接住压缩 hook、自动继续、历史变换和 Scheduler 快照。它拥有当前正在压缩的 session、临时只读咨询的溢出结果、配置缺口，以及插件实例的一次首轮观察；PluginHooks 和 PluginRuntimeScope 不再各留一份状态。

配置保持现状：`auto=true`、`prune=false`、`autocontinue=true`。普通压缩先标记 session，再向现有产品 Compaction 请求贡献；下一次属于该 session 的历史变换消费一次让行标记。自动继续采用产品返回值。配置无法建立时，到第一个已完成 assistant 的快照才报告，未完成消息不会提前消费这次观察。

临时只读咨询每次重投影同一份材料，压缩不会缩小它。溢出时记录结果并尽力中止宿主请求，不调用产品压缩贡献；咨询 runner 读取结果并在结束时清理。删除普通 session 只清自己的压缩标记。

快照独立于业务 Turn 观察，因为手动压缩可能没有 active logical run。没有 Journal 时不写事实；有 Journal 时，把已观测的压缩 run 交给产品侧 `Context/Prefix/PrefixReanchor`。该连接器从已提交 PrefixHistory 选择候选，最多追加一条 `ContextReanchored`，不复制去重状态。写入返回错误只记录诊断，下个快照仍能再观察。产品 Scope/Prefix 与宿主后续 execution 接纳策略均保持原状。
