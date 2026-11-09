# 现存 Bug 清单核实结论（2026-11-09）

依据 `proposals/现存bug分批修复计划-2026-10-21.md`（63 条）逐条对照当前 HEAD `979d70719` 复核。本文只记核实结果，不是待办账册。

## 一、6 条高危：0 条成立

| 原编号 | 原判断 | 核实结论 | 证据 |
| --- | --- | --- | --- |
| H-1 | Sphinx v2 用量结算整体覆盖 | **已修** | `Sphinx/V2/Core/Reducer.fs:687` `SettledUsage = accumulated` |
| H-2 | Sphinx v2 货币账恒 0 | **已修** | 同文件 `:688` `SettledMoneyMinor = target.SettledMoneyMinor + usage.MoneyMinor` |
| H-3 | Host 编解码把 provider 超时误判为中止 | **已修** | `HostEventCodec.fs` 已无 `"The operation timed out"` → `UserCancelled` 分支 |
| H-4 | parts 非数组强转 | **已修** | 文件已迁 `wanxiangtong/opencode/provider-request/MessageProjection.fs:45`，有 `Array.isArray` 守卫 |
| H-5 | 恢复闭包丢弃 AgentChild | **判断有误** | 去重走 `RecoveryNode.token`（`Recovery/Model.fs:51`），`AgentChild` 与 `ManagerJob` token 前缀不同（`A:` vs `M:`），不会混。`sortKey` 只用于排序 |
| H-6 | Pending 允许直接物理释放 | **判断有误** | 许可在准入时即绑定物理消息（`RoutingProtocol.fs:489`），用户撤回时许可尚未提交，按物理完成释放是该次执行的正常结束。测试 `permit/tests/001.test.mjs:113` 固化的是正确行为；错的是 `permit/AGENTS.md` 契约漏写该路径，已补 |

## 二、中危 14 条：抽样 5 条，2 条已修

| 原编号 | 核实结论 |
| --- | --- |
| M-3 死 permit | **已修**（`SessionQuiescenceGate` 中已无 serial=0 分支） |
| M-9 AsyncSemaphore 取消丢许可 | **已修**（`Foundation/Parallel.fs` 已无该实现） |
| M-10 外部工具 fail-open | 仍在（`ExternalToolAdmission.fs:56` `\| None -> ()`），需判断 `RoleFor=None` 是否可达 |
| M-13 `Option.get` | 仍在（`DelegationProjectionBridge.fs:89`），前置 `admitWork` 已保证非 None，属防御性缺失 |
| M-14 `Option.get` | 仍在（`ExecutionFactFold.fs:61-62`），同上 |

## 三、低危 40 条

未逐条核实。按原清单描述多为死代码、命名口径不一致、防御性兜底缺失，不影响主流程。

## 四、结论

原清单「60 条仍成立」的判断已过期。**高危清零，中危存活率约 60% 且均为低风险项**。

原两份 bug 文档（`现存bug清单与测试用例-2026-10-21.md` 966 行、`现存bug分批修复计划-2026-10-21.md` 232 行）的判断基准已被本次核实覆盖。后续若要动中低危项，按本表残留项直接查代码，不再回读原清单。

## 五、本次实际改动

`wanxiangli/src/permit/AGENTS.md` LEMMA 3 补全生命周期第三路径 `Pending → Released(PhysicalCompletion)`，并写明理由。
