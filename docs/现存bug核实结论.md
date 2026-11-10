# 现存 Bug 清单核实结论（2026-11-09）

依据 `proposals/现存bug分批修复计划-2026-10-21.md`（63 条）逐条对照当时 HEAD `979d70719` 复核；核实结论沿用至今，`08b3c068a` 上仅 M-12 的归类经本轮修正（「真 bug」→「已修/防御性一致性」）。本文只记核实结果，不是待办账册。

## 一、6 条高危：0 条成立

| 原编号 | 原判断 | 核实结论 | 证据 |
| --- | --- | --- | --- |
| H-1 | Sphinx v2 用量结算整体覆盖 | **已修** | `Sphinx/V2/Core/Reducer.fs:687` `SettledUsage = accumulated` |
| H-2 | Sphinx v2 货币账恒 0 | **已修** | 同文件 `:688` `SettledMoneyMinor = target.SettledMoneyMinor + usage.MoneyMinor` |
| H-3 | Host 编解码把 provider 超时误判为中止 | **已修** | `HostEventCodec.fs:131` `"TimeoutError" -> ExecutionFailure.ProviderTransient`，已归入 provider 瞬时失败 |
| H-4 | parts 非数组强转 | **已修** | 文件已迁 `wanxiangtong/opencode/provider-request/MessageProjection.fs:45`，有 `Array.isArray` 守卫 |
| H-5 | 恢复闭包丢弃 AgentChild | **判断有误** | 去重走 `RecoveryNode.token`（`Recovery/Model.fs:51`），`AgentChild` 与 `ManagerJob` token 前缀不同（`A:` vs `M:`），不会混。`sortKey` 只用于排序 |
| H-6 | Pending 允许直接物理释放 | **判断有误** | 许可在准入时即绑定物理消息（`RoutingProtocol.fs:489`），用户撤回时许可尚未提交，按物理完成释放是该次执行的正常结束。测试 `permit/tests/001.test.mjs:113` 固化的是正确行为；错的是 `permit/AGENTS.md` 契约漏写该路径，已补 |

## 二、中危 14 条：全部核实，6 条已修、0 条真 bug

| 原编号 | 核实结论 |
| --- | --- |
| M-3 死 permit | **已修**（`SessionQuiescenceGate.fs` 已重写为薄壳，`TryConsume`/`TryRelease` 委托 `inner?TryConsume`，不再自行签发 permit；签 `serial=0` 的路径消失） |
| M-9 AsyncSemaphore 取消丢许可 | **已修**（`Foundation/Parallel.fs` 文件已不存在） |
| M-10 外部工具 fail-open | **判断有误（设计如此）**，详见下表 |
| M-13 `Option.get` | **防御性缺失，不改**（`DelegationProjectionBridge.fs:89`），前置 `admitWork` 已保证非 None |
| M-14 `Option.get` | **防御性缺失，不改**（`ExecutionFactFold.fs:61-62`），同上 |

### 中危其余 11 条逐条核实（2026-11-09 续）

> 下表含 M-10 的完整结论；第一表已注明「详见下表」，不重复计数。

| 原编号 | 原判断 | 核实结论 | 证据 |
| --- | --- | --- | --- |
| M-2 | abandon 清 occasion 但保留 PhysicalLandings | **判断有误（设计如此）** | `ClaimLaw.fs:136` `ClaimEvent.Abandon -> [ DropPending; ClearOccasion ]` 只清 occasion；`Run.fs:446` 的 `LandExact` 单独把落地证据写进 `PhysicalLandings`，abandon 不触碰它——保留落地证据是设计意图，不是遗漏 |
| M-4 | accepted 在 lock 外调 callback，重入竞态 | **已重构** | `PhysicalAcceptance.fs` 中已无对应 lock/callback 结构 |
| M-5 | Cancelled 完成一律拒绝 | **判断有误（设计如此）** | `LinkageProjection.fs:482` 注释 `EXEC-004: Abandoned is not a completion cell`，`joinable` 只认 `CompletedAwaitingJoin` |
| M-6 | `trySpendFullReplayBudget` 依赖 `loadedCommonDir=Some` | **已修** | `IntegratorEngine.fs` 从 400+ 行缩到 120 行，`trySpendFullReplayBudget` 与 `loadedCommonDir` 全仓无匹配 |
| M-7 | 空问题拒绝分支把角色与空问题耦合 | **已重构** | `Interaction/Authority/Fold.fs:180` 起为 `foldTerminalSource`，只校验 `source.FactId` 非空，原「空问题拒绝分支」已不存在 |
| M-8 | `readExactAt` 空块边界缺守卫 | **已修** | 已迁 `wanxiangli/src/segments/SegmentedReader.fs:109-119`，有 `if count <= 0 then failwith "unexpected EOF"` 显式守卫 |
| M-10 | `RoleFor=None` 分支落到 `()`，fail-open | **判断有误（设计如此）** | `ExternalToolAdmission.fs:60` 的 `\| None -> ()`：`capability-enforcement/tests/007.test.mjs:229-233` 要求无办公角色的宿主会话保留宿主自己的 MCP 面。已解析办公角色一律拒绝；`None` 不是漏洞，是「这不是办公会话」 |
| M-11 | `findRestoredRoot` 环路返回环内节点 | **已重构** | 函数已不存在，改为 `resolveFamilyRoot`（`Sessions.fs:133`）委托 JS 侧 `familyTree?resolveFamilyRoot` |
| M-12 | 未知 escape kind 解码语义不一致 | **已修，属防御性一致性** | 原判断：`Wait/Registry.fs:60` `\| _ -> WaitEscape.OpenEndedExternal` 静默降级，而 `Wait/Surface.fs:98` 抛异常。**核实后修正**：`Surface` 与库 `Wait.fs` 一直拒绝未知值，未知 escape 进不了注册表；`Registry.escapeOf` 只解码库返回的快照，该降级分支不可达，不存在可观测的错误行为（把 Registry 回退成旧写法重建，`causal-wait/tests/006` 仍全绿，即该分支不可测）。`9a10a48e8` 已让两处一致（`Registry.fs:65` `\| unknown -> invalidArg`）；本轮回补两个用例：边界拒绝未知 kind、五种已知 kind 往返保留 `at` 与 `owner` 载荷 |
| M-15 | `foldPromptClaimed` 孤儿代码 | **已修** | `Ledger.fs` 与 `Fold.fs` 中该符号均无匹配 |

**中危汇总：** 14 条 = 6 条已修（M-3/M-6/M-8/M-9/M-12/M-15）+ 6 条判断有误或已重构（M-2/M-5/M-10 设计如此，M-4/M-7/M-11 已重构）+ 2 条防御性缺失（M-13/M-14，前置已保证非 None）。**0 条真 bug。**

> M-12 原记为「真 bug」，本轮按红绿流程复核后修正：未知 escape 在 `Surface` 与库入口即被拒绝，`Registry` 的降级分支不可达（把 `Registry.fs` 回退成旧写法重建，`dist/Registry.js` 确实不含 `unknown wait escape`，测试仍全绿，即该分支不可测）。它只是两处解码器的语义对齐，没有可观测的错误行为。

## 三、低危 40 条（2026-11-09 部分核实）

文件已不存在：L-8、L-12、L-21、L-22、L-23、L-26、L-28、L-31、L-41。

| 原编号 | 核实结论 | 证据 |
| --- | --- | --- |
| L-1 | **真缺陷，已修** | `Recovery/Surface.fs` 与 `RecoveryClosureSurface.fs` 都把未知 kind 读成 `WorkSession`。合法工作会话的 kind 是 `work`。未知 kind 现在 `invalidArg`。`managed-session-lifecycle/tests/013.test.mjs` 覆盖 |
| L-6 | **判断有误** | `LastTransitionWasSuccess` 在投影、库状态和读回三处都有，不是死字段 |
| L-13 | **真缺陷，已修** | 库侧中断只有 `OperatorAbort`、`UserMessageArrived`、`DeadlineExpired`。宿主桥漏了第三种，并把其余未知值读成超时。现与库侧 `Wait.fs` 一致：三种显式映射，未知值拒绝 |
| L-32 | **真缺陷，已修** | `sedReadFile` 原先把第二个 `-e` 当成文件。`-e`、`-f` 现各带一个值，粘连写法整段是选项；没有它们时第一个非选项仍是位置脚本。库测试先红后绿 |
| L-8 | **名不符实，未改** | `tryConsumeSilentInterrupt` 只调用 `isSilentInterrupt`，不移除标记。改名牵动宿主表面和库，单独一批 |
| L-10 | **名不符实，不改** | `bindOrAbort` 只返回错误。真正中止在 `bindSessionStartedAt`。改名不改变行为 |
| L-7 | **判断有误** | `recordSubstantiveAccess` 只把库返回的路径记进追踪器，没有第二套口径 |
| L-19 | **现有规则，不改** | 显式 profile 未列出的节点标成消融。`feature-ablation-004` 按这个行为断言 |
| L-35 | **有意 no-op** | 认知事实只兼容读取，不恢复投影。`cognitive-workspace/WHAT.md` 写明 fold 必须为 no-op |
| L-37 | **不成立** | `Option.get` 前已用 `Work` 非空筛过候选 |
| L-38 | **不成立** | `failwith` 拒绝库返回的非法形状，不是静默降级 |
| L-40 | **判断有误** | `byname` 是函数参数，`Byname` 是句柄字段，不是同一字段的大小写口径 |
| L-4、L-5、L-16、L-17、L-20、L-25、L-29、L-30、L-33、L-36 | **符号已不在** | 原清单点名的字段、分支或函数已不在当前文件 |

## 四、结论

原清单「60 条仍成立」的判断已过期。**高危清零，中危 0 条真 bug**：6 条已修、6 条判断有误或已重构、2 条为防御性缺失（`Option.get` 前置已保证非 None，不是可触发的缺陷）。

原两份 bug 文档（`现存bug清单与测试用例-2026-10-21.md` 966 行、`现存bug分批修复计划-2026-10-21.md` 232 行）的判断基准已被本次核实覆盖。后续若要动中低危项，按本表残留项直接查代码，不再回读原清单。

## 五、本次实际改动

1. `wanxiangli/src/permit/AGENTS.md` LEMMA 3 补全生命周期第三路径 `Pending → Released(PhysicalCompletion)`，并写明理由。
2. M-12 由「真 bug」改判「已修，属防御性一致性」：`Surface` 与库入口一直拒绝未知 escape，`Registry` 的降级分支不可达。核实时把 `Registry.fs` 回退成旧写法重建，`dist/Registry.js` 确实不含 `unknown wait escape`，测试仍全绿——该分支不可测，不存在可观测的错误行为。
3. `requirements/causal-wait/tests/006.test.mjs` 回补两个用例：未知 escape kind 在等待边界被拒绝；五种已知 kind 经注册表往返后 `deadlineAt.at` 与 `cancelledBy.owner` 载荷无损。
