# 执行失败策略的测试说明

WHAT 决定允许的失败后果。本目录用编译后的策略、Host 解码与真实 admission/settlement owner 检查这些边界。测试说明不另立规则。

- 001/002/003/005/007/008/013：封闭输入、互斥决策、恢复条件、exact identity 与 commitment。有限矩阵调用生产策略；没有做源码变异，所以不声称变异测试。JS 接缝丢弃额外时间字段，只验证适配输入规则，不说明整个解释器没有时钟依赖。
- 004：真实容量准入拒绝错消息和过期 lease；策略返回的字符串 fence 只是测试翻译，不代表生产 fence 不可伪造，也不能替代实际 release 的反例。
- 006：真实 pre-provider 结算先 terminal、再 unbind/release；hook membrane 单独验证 fatal 所需证据。两段记录不拼成一个物理 fatal 顺序。append 未提交/未知时的实际容量观察，以及同一子进程的 durable settlement → fatal exit，仍未做。
- 009/011：Host error 归类与真实 hook 原样抛出，未知异常不推定执行干净。单独的 messageID 字段不足以认定末尾物理用户消息。

## 运行

构建后，把本包现存 `NNN.test.mjs` 的实际路径传给 `requirements/verification-system/tests/run.mjs` 的 `TESTS_MJS_FILES`，启用所需 integration tier。联查 requirement-system、verification-system 与 context-compression。正式 runner 隔离配置目录，测试不得读取或修改日常 OpenCode 配置。TODO 是未完成事项，不算通过。
