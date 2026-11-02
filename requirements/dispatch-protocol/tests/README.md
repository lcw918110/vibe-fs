# dispatch-protocol 测试说明

规则见本包 WHAT。本目录区分调度事实、Host 证据、准入交接与执行结果；函数存在不代表没有旁路。

- 001：实际 owner-root/continuation 在 Host 端观察注册好的 claim；全体合成消息来源仍要逐一验证。
- 002/005/006：真实生产 projection、sequence、hash 输入与兼容解码；005 单独改变六个组成要素，用注入 hash 观察确定性输入，不声称穷尽碰撞或跨进程故障。002 另覆盖 `ManagerWorkflow.observeIdle`：有效许可只发一次并被消费；旧 idle 暂停后新 Human 入场撤销许可，恢复旧调度只追加 `Abandoned(SupersededBeforePhysicalSend)`，SDK 零发送。002 还覆盖 claim-append 边界：journal 写者关闭时 SendPrompt 零调用，重开冷读无 pending claim；transport 入栈未完成时第二次发送不落。
- 003：实际 receipt 发送、关闭并重开 journal，两种外观的 receipt 都不授予权限。004/007/008 多为同进程额外 journal writer，不是 OS crash；保留真实拒绝、未知、查不到证据、不可读与并发 flight 的断言。
- 009：把 Host Promise 保持未决，直接看 Detached 是否返回，再释放端口，不用 120ms 竞争当完成事实。晚到 verdict 注入在 dispatcher 端口，不说明 SDK/HTTP 适配器自身的分类。A2-D0 另覆盖 root/continuation 两入口各四例：SDK 未决时早返回、真正 managed ingress 通知 exact physical、Unknown 不重发。
- 010/011：用实际接受的 root profile 续行，验证发送参数、完整 PromptKey、Origin 与 LogicalRun 元数据；profile 无 model 字段是投影事实，不代替全部调用者。010 的负编译按 durable-events/023 的既有模式落地：先由正例 probe 证明 root witness record、identity seed union 与 `SendOutcome` 回执值。010 的 managed assignment 回归在 journal 重开后继续 Manager 与 Engineer 子会话，run/root/participant 不变，副本工具权限不丢，continuation 不重取 root identity seed。
- 012：实际 claim acceptance 与独立 turn reconciliation，完整 handoff/容量所有权仍是 TODO。013 是 journal 重开与显式恢复，不说明真实插件构造无副作用。014 的自造 fatal descriptor 已撤。015 的载体属性测试与诱饵/getter/冲突输入保留，另加实际插件的非法 PromptKey 与非法 agent 反例，防止错误被折叠成 Missing 后取得新 authority。

`support/authority.mjs` 只准备真实 Persona、Journal 与 dispatch 入口需要的输入，并负责临时目录与 journal 重开，不在夹具里复制权限或恢复算法。

## 运行

先构建，再经 `verification-system/tests/run.mjs` 的 `TESTS_MJS_FILES` 选本目录与受影响的 authority、chat admission 测试。TODO 与跳过不算成功。实际插件用例用隔离的 fixture 配置，不改开发者的 OpenCode 配置；目前不用真实 provider。
