# 执行失败策略的测试与证明范围

WHAT 决定允许的失败后果。本目录以编译后的策略、Host 解码、真实 admission/settlement owner 和标准 fatal 扫描器验证这些边界。

- 001/002/003/005/007/008/013：封闭输入、互斥决策、恢复条件、exact identity 与 commitment。有限矩阵确实调用生产策略；没有执行源码变异，不能称为 mutation proof。JS 接缝丢弃额外时间字段，仅证明适配输入规则，不证明整个解释器没有时钟依赖。
- 004：真实容量准入拒绝错消息和过期 lease；策略返回的字符串 fence 只是测试翻译，不能证明生产 fence 不可伪造，也不能替代实际 release 的反例。
- 006：真实 pre-provider 结算先 terminal、再 unbind/release；hook membrane 单独验证 fatal 所需证据。两段记录不拼成一个物理 fatal 顺序证明。append 未提交/未知时的实际容量观察，以及同一子进程的 durable settlement → fatal exit，仍是 TODO。
- 009/011：Host error 归类与真实 hook 原样抛出，未知异常不推定执行干净。单独的 messageID 字段不是末尾物理用户消息的证据。
- 010/014：当前仓库索引检查与受控新增/漂移/缺失回归反例。索引存在不代表每个 fatal 分支不可达或已被实际执行证明。

## Fatal 索引的当前载体

`../fatal-inventory.json` 的 entries 使用 WHAT[014] 定义的信息，字段为 id、ownerRequirement、sourceSymbol、operation、triggerBranch、inputSource、exactIdentity、phase、assertedInvariant、commitDisposition、scopeOfEffect、evidenceType、formalTestId、status。历史 F/T/C/X 编号保留；它们不是第二套产品分类。

标准入口 `scripts/check.mjs` 调用 `scripts/checks/fatal-inventory-gate.mjs`。当前扫描直接 FatalProcess/Diagnostic 调用，以及 TripFatal 点自由绑定、ReportFatalDiagnostic 的一跳转接；尚不是任意深度调用图证明。FixedWithRegression 必须有存在的 `.test.mjs` 文件；当前检查不验证 `::` 后的用例标题，也不证明该用例实际覆盖该分支。动态 forwarder、迁移条目和 ExcludedWithEvidence 的证据仍需人工审阅。详见 GAP-121。

已无对应 fatal 入口的 F05/F06/F08/F33/F34/C01/C02 移出当前索引，历史定位和未闭合的行为证据保留在[退役记录](../fatal-inventory-retired.md)。删除旧索引不等于证明替代路径。

## 运行

构建后，把本包 `001`—`014.test.mjs` 的实际路径传给 `requirements/verification-system/tests/run.mjs` 的 `TESTS_MJS_FILES`，启用所需 integration tier。联查 requirement-system、verification-system[021] 与 context-compression[025]。正式 runner 隔离配置目录，测试不得读取或修改日常 OpenCode 配置。TODO 是缺失证据，不是通过。
