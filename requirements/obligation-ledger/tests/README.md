# obligation-ledger 测试证明范围

本包守住编程面 `todowrite-for-<role>` 的 `view` 声明压缩 checkpoint 合同与宿主原生 `todowrite` 退役边界：原生 `todowrite` 不再暴露或触发 checkpoint，只有携带非空 `view` 的 `todowrite-for-*` 调用在 Host `completed` 终态后追加一条持久化 `ViewCheckpointCommitted`。

- 001：原生 `todowrite` 已从静态工具面、权限矩阵、调查估计分类和 ablation 映射退役；PluginHooks 仅在 `todowrite-for-*` 且 `state.input.view` 为非空对象时提交 `ViewCheckpointCommitted`。
- 002：checkpoint 窗口（`PhaseWindow`）与真实 `opencode serve` + 生产插件 canary（`support/run-native-todo-replacement-canary.mjs`）驱动带 `view` 的 `todowrite-for-engineer` 调用并验证 `ViewCheckpointCommitted` 落盘与无 `view` 对照组。
- 003–006：不维护第二份待办投影、`ViewCheckpointCommitted` 不影响非 checkpoint 投影切片、失败执行不形成 checkpoint 且重放/并发/WriterUnavailable 重试符合合同、声明无裁决权。
- 007：旧 Magic Todo decoder 当前明确拒绝；若未来提供历史审计读取，也不得激活旧门禁或写回。

旧施工迁移输入仍原样保留在 historical-tests/，不进入活动测试发现集。

局部命令：node --test requirements/obligation-ledger/tests/*.test.mjs。集成用例需 WXS_TIER_INTEGRATION=1。