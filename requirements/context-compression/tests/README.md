# context-compression 测试

WHAT 拥有输入、提交与生命周期边界；本文只说明现有测试如何观察这些边界，不补充验收规则。

| 文件 | 已有证据与范围 |
|---|---|
| 001 | 原容量词汇扫描已撤下；真实依赖隔离待证。 |
| 002 | Host compaction 设置判别与重锚幂等性。 |
| 004、005、007 | 终态文本判别、失败预算及 typed RequestKind 分派（区分 WorkMain 与 InteractionRepair，clean-break 退役种类拒绝）。 |
| 006、008 | 角色固定性、连续失败预算消耗及主会话无压缩请求。 |
| 013 | 当前诊断 emit 的非致命行为。诊断失败或文本改变不影响真实决策仍待证；删除未进入产品的常量对象比较及在此重复的 Host fatal 展示。 |
| 017 | 真实用户消息与 view 取代规则（物理用户消息原样保留）。 |
| 019 | 辅助注入的 epoch 退休与 tentative horizon。 |
| 023 | typed park 返回及取消；事件驱动依赖与 durable producer 判据仍待完整验证。 |
| 030 | assume 调用与结果原样保留（未点名移除时原样保留）。 |
| 031 | ANCHOR 编号稳定、视图声明一次性消费、前缀保留/尾部丢弃/引用重加、无标识消息保守保留、token 经济计数与生成工具的 view 参数捕获。真实 transform 管线内的注入块端到端断言仍待补（当前由 400 例既有 transform 用例证明无回归）。 |

完成节点后先正式构建，再通过 `requirements/verification-system/tests/run.mjs` 与 `TESTS_MJS_FILES` 运行本目录及受影响所有者。TODO 不计通过，GAP-104/105记录未闭合状态；旧发布清单的DONE不作当前证据。未执行真实 provider 或人工验收。
