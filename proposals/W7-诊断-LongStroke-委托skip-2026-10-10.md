# Long Stroke 委托 skip 三层诊断结论（2026-10-10）

- 议题：e2e Long Stroke preflow 中，canary 委托的 `DelegationRequested` durable 断言失败（`0 !== 1`）。
- 性质：诊断记录。只写诊断结论与方向；产品修复未实施。
- 现场：`requirements/verification-system/tests/014.test.mjs` 的 `preFlowCanaries`。
- 诊断增强：`08b02ccd2`（失败时自动输出 `journalDiagnosis()`）。
- 本记录行号按 2026-10-10 工作树核对。

诊断分三层：观测层（第一节、第二节）、会话层（第三节）、代码层（第四节至第六节）。

---

## 一、现象

1. `preFlowCanaries` 的 durable 断言失败（`014.test.mjs` L379-383）：

   ```js
   assert.equal(
     factPayloads(scenario.host.workDir, 'DelegationRequested').length,
     1,
     `the canary decision must own exactly one DelegationRequested before recovery exists\n${journalDiagnosis()}`,
   )
   ```

   实际值 `0 !== 1`。其后的 `DelegationBound == 1` 断言（L384-388）未执行。

2. 其前的 Replica 启动层已通过：
   - `await scenario.provider.waitForExpectationAttempt('strength-canary-replica.0', 1, WAIT_FACT_WINDOW_MS)`（L241）；
   - `assert.ok(scenario.provider.matchCount('strength-canary-replica.0') >= 1, ...)`（L242-245）。
   - 按场景注释（`long-stroke.toml` L1293-1294），这一等待就是 canary companion 的首次物理交付。

3. 场景停在 preflow，未进入主链（L390 起的 humanroot 循环、join barrier、tool surface 断言均未执行）。

---

## 二、观测层：现场自证（字节确实不在）

诊断增强已入库 `08b02ccd2`；失败时自动输出 `journalDiagnosis()`（`014.test.mjs` L253-378）。现场结论：

1. `eventsPath` 解析正确：隔离 workspace 的 `.git/wanxiangshu/events`；唯一 writer 73412B。
2. 全量 fact 标签直方图（44 个标签）中无任何 Strength / Delegation 系列 fact。
3. 无其它 events / state 目录（legacy `wanxiang/`、worktree 副本、XDG state 均未发现）。
4. 判定：`DelegationRequested` / `DelegationBound` 的字节确实不在——观测侧排除，不是读取路径错误。

---

## 三、会话层：session 映射

stderr 仅三条 `strength-` 系列行，全为 `strength-delegation-skip`；无 `strength-delegation-requested`（该行只在 capture 成功分支 emit，`Delegate.fs` L1823-1828）。

| # | 会话 | journal | reason |
|---|---|---|---|
| 1 | owner 会话（测试登记 lane 内） | 28 行 | `no-completed-source-batch` |
| 2 | replica 会话（不在登记 lane 内） | 15 行，含 `Companion`、`ProviderStarted`、`XTracePartAppended`、`HostToolPartId` | `owner provider run is not uniquely bound` |
| 3 | 同 replica 会话 | — | `apply-surface-failed: owner provider run is not uniquely bound` |

第 2、3 条同属 replica 会话，构成同一次 transform 的两阶段（见第五条第 3 项）。

---

## 四、机制解释（回答「replica 匹配 ≥1 与 `DelegationBound = 0` 同真」）

replica 的物理启动（companion 会话创建 + provider 请求）不以保证委托事实已落盘为前提；strength 委托的两道准入（来源批次完成、owner provider run 唯一绑定）都没有通过，产品侧在准入处安全跳过，`DelegationRequested` / `DelegationBound` 从未落盘。

---

## 五、代码层：条件链（产品位置）

三条字符串都在 `src/Wanxiangshu/Strength/OpenCode/Delegate.fs`。

1. `no-completed-source-batch`（L799-804）。`tryResolveSourceCalls` 调 `resolveCompletedSourceBatch`（L696-702）；返回 None 的路径：
   - 尾部批次未完成（`tailBatchIsComplete` false，L643-647）：
     - 尾部 assistant 有 parts 但工具 part 未全 completed → `SourceAssistantTail.Incomplete`（L620-631）；
     - wire 尾部无完整批次（`checkWireTailComplete` false，L587-592）。
   - 配不上来源 run：`extractCompletedSourceBatch`（L683-690）里 `pairCompletedCallsWithRun` 缺 calls 或缺 `ProviderRun`（L649-655）。
   - 注意：`Incomplete` 明确不回退到更早的完成批次（L609-613 注释：`013 trailing-integrity`，宁可 fail closed，不取旧批次充数）。
2. `owner provider run is not uniquely bound`（L213-235，emit 于 L231）。`observeBindableRun`（`src/Wanxiangshu/OpenCode/Host/ProviderRunBinding.fs` L101-114）在预算内未得到唯一绑定：
   - 预算：`projectionCatchupMaxReads = 6`、`projectionCatchupDelayMilliseconds = 10`（L25-27）。首次读取立即；至多 5 次等待重读，每次 10ms，约 50ms 窗口。
   - 0 候选 `ProjectionNotVisibleYet` 耗尽后失败；或 `Rejected`（`NoBindableRun` / `AmbiguousRun count` / `NotLatestRun` / `InsufficientSequence`）——`Rejected` 类不重读。
3. `apply-surface-failed:`（L1647-1668，emit 于 L1661-1664）。capture 在 `tryCaptureAndStart`（L1770-1843）里 Skipped 后，同一次 transform 继续走 `tryBind` → `executeSkippedRecovery`（L1719-1738）→ `executeApplyOnBound`；surface 再失败即 emit 前缀 `apply-surface-failed:`。第 2、3 条因此是同一次 transform 的连续两阶段：capture 阶段 target 解析失败 → 跳过；recovery 阶段再次 target 解析失败 → 带前缀的 skip。

补充（本次落盘对照源码）：`tryExecuteCaptureOnBound`（L1000-1015）先 `resolveSurface`（含 target 绑定，L258）再 `captureOnSurface`；target 解析失败时，来源捕获（`tryResolveCaptureCallsAndBudget`，L806-831）完全没有机会执行。

---

## 六、修复方向（诊断建议，未实施）

`Delegate.tryCaptureAndStart`（L1770-1843）把「来源捕获」与「本次请求 target 绑定」耦合在单次 transform；capture 机会丢失后不可恢复。诊断观察到的 owner step 形态（step 0 来源＝无；step 2 来源＝`estimated-zero`；step 3＝纯文本 `Incomplete`）都无法补捕获。

方向：

1. 让 capture 的来源解析只依赖 `rawMessages + wire + durable projection`，不依赖 `surface.Target`。
2. 把 target 绑定失败留给既有 `startPendingRequest` 恢复路径（L1562-1603）：先落 `Requested`，Bound/start 阶段再解析 target。
3. 需在 `speculative-investigation` 补条款并加时序断言。
4. 与 `host-boundary-008` 存在张力，需一并评估：该条规定 `experimental.chat.messages.transform`「不得把『当前 assistant run 已经存在』作为业务前置条件，也不得通过 bounded wait 把未来 run 伪装成 projection lag」；当前 `resolveSurface → tryResolveAssistantRun` 的 50ms 重读与「run 存在才 capture/start」正落在这一禁区的边缘。

---

## 七、下一层取证点

1. 在 `Delegate.fs` 的上述判定点打印候选绑定集合与批次状态，区分 `ProjectionNotVisible` / `Ambiguous` / `NotLatest`；观察 owner 多轮请求对「唯一绑定」的影响。
2. 核对 canary owner 首请求是否有应有的 completed source batch。

---

## 八、边界

- 本记录只写诊断结论与方向，不做产品修复（超出现行分流）。
- 机制解释为诊断结论原样记录；本次落盘未重新运行复现。
- 第一节、第二节、第三节的现场数字（73412B、44 标签、三条 stderr 行、journal 行数）来自诊断现场；其解释逻辑（`journalDiagnosis()`）已对照当前工作树核实。
- 产品修复如需动工，应另开工作包，先补条款与时序断言，再改实现。

---

## 附：落盘核对附注（供下一层取证）

本次落盘对照源码核对了上述行号、断言原文与诊断输出逻辑，另核实一处调用点事实，供下一层取证同时解释：

- `SendPreparedPrompt` 在本仓有三个调用点：
  1. `Delegate.fs` L1248（`executeBoundReplica`，Bound append `Applied` 之后）；
  2. `Strength/Replica/Runtime.fs` L1521（`StartDecision` 单步入口：prepare 空 child 后直接 send，不写 Bound；本仓生产代码中未见调用者）；
  3. `Strength/Surface.fs` L2207（JS 面 `replicaSendPrepared`；本仓调用者仅有 `speculative-investigation/tests/support/replica-lifecycle-fixture.mjs`）。
- `Delegate` 的 e2e 路径走两阶段：`appendBoundAndExecute`（L1277-1310）只在 Bound append `Applied` 后执行 `executeBoundReplica`（L1241-1266）→ `SendPreparedPrompt`（L1248）。
- 因此下一层现场取证除绑定/批次状态外，还应核对：`strength-canary-replica.0` 匹配到的物理请求经由哪条路径发出，以及该路径对应的 durable 事实。这是回答「replica 请求发生与 `DelegationBound = 0` 同真」需要闭环的一环。
- 该附注不改变第四节的诊断结论，只补一条下一层必须同时解释的调用点事实。
