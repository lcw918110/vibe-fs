# host-boundary 测试说明

规则见 [WHAT](../WHAT.md)。本目录的模拟端口、参数与辅助模块是测试设施，不是第二份产品合同。

## 三类检查

1. 信号解码、身份唯一性、类型化错误、快照决策、顺序转换和订阅释放：用例直接驱动生产 Surface。
2. 026/027/029/034/035 检查编译依赖与能力分界。部分源码与项目名锁定仍待整理，不能代替运行行为。
3. 真实 Host canary 要启动受支持的 Host，经公开 Hook/SDK 走实际场景。019 的受控端口只覆盖生产 transform 的条件分支，不能代替物理证据；全能力覆盖仍未做。

## 现状

- 003：调用实际注册的 hook——coarse abort 保留当前 execution，当前 exact cancelled assistant 才结算自己的 Cancelled；033 另证迟到旧 receipt 不动新 Human，后来的输入可替代旧 pending demand 而不发 Host abort。
- 015：工具注册到输出的完整断言，覆盖多字节文本、完整尾行、超大单行 Unicode 后缀、字节上限与重复限界稳定性。固定预算数值是当前兼容对象的设施配置，不代替真实 Host 版本能力核验。
- 019：清退已删除 MagicTodo 协议与源码词形检查。快照定位的有效案例归 012；实际 transform 分支通过只转换参数、记录端口调用的生产接缝验证，不再从 JS 拼装 Fable 内部对象。Replica 的真实物理路线仍须 Long Stroke 覆盖。
- 026：真实 Fable 编译诊断分片的用例，先暴露缺少 failure decision 依赖的错误，再补齐引用。全量构建包含类型定义，不能说明声明的局部依赖完整。
- 029：由真实子进程调用终止/诊断入口，父进程观察硬退出，并重开此前提交的 store 核对 durable 事实幸存。报告器抛错时，用未捕获异常观察器区分「异常退出」和「确实触发终止」；正常拒绝、stale callback、耗尽模式原先只打印成功就退出，这些假通过已删除，相应产品义务仍在，真实流程的正常退出证据待补。
- 032：参数恢复的若干用例（C15/C44–C53）检查评审字段的原始键序、重复 before 的边界与并发所有权。C54/C55 随 Predictor 槽位退役。真实 canary 按物理目标隔离正常、业务错误返回和取消三条路径，在同一 Host 生命周期内执行；安装版 Host 执行器抛错后自动调用 after 仍未观察。
- 033：HumanMessage/BusyAgentNudge 回归先接纳两份追加材料，验证旧请求的只读 params 仍合法；实际选择新材料的 transform 才交接单枚 token。安装版 OpenCode 1.18.29 canary 走用户输入的非中断合同：挂住实际 SSE 输出与 `todowrite-for-manager` 工具体，新 Human 抵达后确认没有 SDK abort、没有提前发新 provider 请求，释放后旧输出完整、下一次 wire 含新输入。
- 037：空闲续轮等宿主 runner 空闲后再发。复现 2026-10-29 Manager nudge 落进收尾中的 run、宿主不开新 loop 的事故；SDK 状态读取、端口等待、真实 ManagerWorkflow 顺序三段都调用 dist。

## 运行

先 `node scripts/build.mjs`，再运行本包用例：

```sh
TESTS_MJS_FILES="$(rg --files requirements/host-boundary/tests | rg '/[0-9]{3}\.test\.mjs$' | sort | paste -sd, -)" node requirements/verification-system/tests/run.mjs
```

019/029/032 仍有未完成项。023、032、033 需要真实 Host 与本机端口权限；026 的真实编译用例需要 integration tier。测试通过不等于产品做成。
