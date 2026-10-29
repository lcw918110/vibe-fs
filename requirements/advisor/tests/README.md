# Advisor 测试

行为见 [WHAT.md](../WHAT.md)，测试编写遵循 [测试规则](../../verification-system/AXIOMS.md)。目录按所驱动的边界区分，文件编号与用例锚点对应现行条款；008–015 没有独立条款，不再以历史文件编号充当规则。

| 入口 | 观察范围 |
| --- | --- |
| `004.test.mjs` | 四段问答、同一专席、自然提示、重复报告与过期 Scope |
| `006.test.mjs` | 真实 JS 生成与执行中不再暴露旧 advisor 成员 |
| `007.test.mjs` | 插件在 SDK 调用前持久受理与预留阶段；未知回复不重发 |
| `dispatch/005.test.mjs` | PromptDispatcher/AgentJournal 区分提交、物理接纳与 Unknown |
| `busy-nudge/005.test.mjs` | 生产发送包装、可配置接纳预算与超预算的迟到接纳 |
| `tool-runtime/005.test.mjs` | 生产 ToolRuntimeScope 送达与 Scope 围栏 |
| `flow/007.test.mjs` | 四段 Flow、真实 SQLite 与物理 steer 确认后的终局 |
| `plugin-recovery/007.test.mjs` | PluginHooks 认领当前 Scope 的未决报告，拒绝未知请求重发 |
| `scope-recovery/007.test.mjs` | resetScope 持久身份在清除内存后仍能用于恢复 |
| `progress-recovery/007.test.mjs` | 计费估计与 ResetDue 冷恢复；错误程序不计费 |
| `workspace-isolation/007.test.mjs` | 同时存活的插件工作区之间不串用数据库 |

这些用例使用真实产品入口和相应存储，部分 Host/Provider 端口受控，不能代替商业 Provider 验收。库侧 `scope-progress/tests` 另覆盖 SQLite、CAS、跨实例和 SIGKILL；本仓 `host-boundary/tests/scope-hni-physical.test.mjs` 与 `omp-negotiate-physical.test.mjs` 覆盖相应宿主适配。

供应商请求的强杀后重新认领、无新工作边界时的独立恢复调度、完整合并等待项冷恢复、商业 Provider 端到端运行仍须单独验证。本次移动与验证结果见 [记录](../../../evidence/test-updates-20261028/README.md)，入口存在不代表所有场景已经通过。
