# Long Stroke 委托 skip 修复草案（2026-10-10）

- 性质：设计记录。只给最小 diff 草案、条款与测试清单；不实施产品修复。
- 前置诊断：`proposals/W7-诊断-LongStroke-委托skip-2026-10-10.md`。
- 本文行号按 2026-10-10 工作树核对；诊断改动落盘后 Delegate.fs 已加长约 160 行，实施前必须重读当前源码。
- 诊断改动：本次已在 `Strength/OpenCode/Delegate.fs` 落盘两处 stderr 诊断（见第四节）。编译与测试未跑，待 DevOps 验证。

---

## 一、事实基础

### 1.1 现场两条失败

1. owner 会话：`resolveCompletedSourceBatch = None` → `no-completed-source-batch`。resolveSurface 成功，target 已绑定。
2. replica 会话：`observeBindableRun` 未得唯一绑定 → `owner provider run is not uniquely bound`（同一 transform 两阶段：capture 的 resolveSurface 与 recovery 的 resolveSurface 各一次）。

### 1.2 现有 capture 链

```
tryCaptureAndStart
  tryCapture → tryBind → tryExecuteCaptureOnBound
    resolveSurface
      tryResolvePhysicalUserMessage
      ports.Snapshots.GetMessages owner
      tryResolveAssistantRun            ← target 绑定（含 bounded reread）
      tryResolveAuthorityProfile
      planEvidence tryAttemptPlan owner target   ← RequestKind / HasPrefixProbe
      wire / anchor
    captureOnSurface → planCaptureRequest
      checkCaptureEligibility           ← 用 RequestKind / IsReplica / IsInternalLeaf
      tryResolveCaptureCallsAndBudget   ← 来源批次
      buildDelegationRequest
    executeCaptureRequest → evaluateCaptureDisposition → persistNewDelegationRequest
```

target 解析失败时，resolveSurface 整体返回 Error，`tryResolveCaptureCallsAndBudget` 完全没有机会执行。

### 1.3 关键时序（管线）

`PluginTransforms.fs` 的普通 transform stage 顺序：

- 步骤 11（L1003-1010）：`ProviderLifecycle.freezeProviderAttemptPlanForTransform`。它对每次带 physical user message 的 transform，按 `(sessionId, physicalUserMessageId)` 冻结 `PendingAttemptPlan`（`ProviderLifecycle.fs` L109-125、L131-149）。`PendingAttemptPlan` 携带 `RequestKind`、`ProjectionChoice`、`PhysicalUserMessageId`。
- 步骤 13.3（L1023-1032）：`StrengthDelegate.tryCaptureAndStart`，仅在 `frame.Horizon = Current` 执行。

因此 capture 时点，按 physical 的 pending plan 已经可用；`RequestKind` / prefix probe 的判定不必须依赖 target。

### 1.4 既有 start 恢复路径

`startPendingRequest`（Delegate.fs L1562-1603 旧行号）已经实现：扫描 durable projection 中属于当前 owner 与 logical run 的 `Requested`，逐个 `startRequest`。它由 `planSurfaceApplication → StartPending → applyOnSurface` 触发。

唯一障碍：`executeApplyOnBound` 的入口是 `resolveSurface`（含 target）；target 失败时，`startPendingRequest` 也进不去。

---

## 二、最小 diff 草案（只设计，不实施）

### D1 拆分请求面与绑定面

新增 `RequestSurface`（去掉 `Target`），`OwnerSurface` 变为「请求面 + target」：

```fsharp
type private RequestSurface =
    { Owner: SessionId
      Authority: PromptAuthority.AuthorityExecutionProfile
      Projections: ProjectionSet
      RawMessages: obj list
      HostMessages: SessionMessage list      // 未绑定时的初始快照，供 capture 与 tryResolveAssistantRun 复用
      Output: obj
      Ports: BoundPorts
      Wire: ProviderProjection.ProviderWireProjection
      AnchorDigest: string
      SourcePhysicalUserMessageId: PhysicalUserMessageId
      RequestKind: ProviderRequestKind
      HasPrefixProbe: bool
      IsInternalLeaf: bool
      DurableProjection: StrengthProjection }

type private OwnerSurface =
    { Request: RequestSurface
      Target: ProviderRunIdentity }
```

- `resolveRequestSurface`：构造 `RequestSurface`，不解析 target。
- `resolveSurface`：`resolveRequestSurface` + `tryResolveAssistantRun`，构造 `OwnerSurface`。
- capture 端函数改收 `RequestSurface`：`captureOnSurface`、`planCaptureRequest`、`tryResolveCaptureCallsAndBudget`、`buildDelegationRequest`、`executeCaptureRequest`、`persistNewDelegationRequest`、`tryAdmitDelegationRequest`、`evaluateCaptureDisposition`。
- start/consume 端函数保留 `OwnerSurface`；字段访问由 `surface.X` 变为 `surface.Request.X`（约 30 处，机械改动）。
- `tryExecuteCaptureOnBound` 改用 `resolveRequestSurface`；`tryStartBoundRequest`、`executeApplyOnBound` 继续用 `resolveSurface`。

### D2 RequestKind / prefix probe 判定改按 physical 查 pending plan

- `tryCaptureAndStart` 与 `executeApplyOnBound` 的 `tryAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option` 参数替换为 `tryPendingAttemptPlan: SessionId -> PhysicalUserMessageId -> PendingAttemptPlan option`；composition 接线到 `scope.Recovery.TryPendingAttemptPlan`（`PluginTransforms.fs` L746、L846 的 AttemptPlanCapability 已含该成员）。
- 判定：

```fsharp
let private planEvidenceFromPending tryPending owner physical =
    match tryPending owner physical with
    | Some pending -> pending.RequestKind, hasPrefixProbeChoice pending.ProjectionChoice
    | None -> ProviderRequestKind.WorkMain, false   // 保持现有默认，并保留注释说明
```

- 删除旧 `planEvidence tryAttemptPlan owner target`（唯一使用点即 resolveSurface）。

### D3 tryCaptureAndStart 的 Captured 分支保持「capture 后立即尝试 start」

- capture 成功（Requested 已落盘）后仍立即 `tryStartCapturedRequest`；target 解析失败则 emit 既有 `strength-start-surface-failed`，Requested 留在 durable。
- 下一次 transform target 可解析时：`decideTargetAction` 的 `tryDecisionForTarget` 找不到该 target 的 decision（未 Bound）→ `StartPending` → `startPendingRequest` → `startRequest`，按既有语义以当前请求的 target 写 Bound、冻结 mirror。
- 这使「Requested 的来源批次」与「Bound 的 target/mirror」分属两次请求，正是两阶段语义；`speculative-investigation-006` 已规定 Bound 固定「首次外发前」的 target 与 mirror。

### D4 保留但不扩大 bounded reread

- `projectionCatchupMaxReads = 6`、`projectionCatchupDelayMilliseconds = 10` 不变。
- 解耦后，等待只发生在 start/consume 的 target 读取上，服务「读取已发生投影的可见性延迟」，不再作为 capture 的业务前置。
- 不新增轮询、不延长预算、不把 future run 伪装成 lag（见第三节）。

### D5 诊断保留

本次落盘的两条诊断留在生产代码，服务下一层与 e2e 自证；待裁决是否长期保留。

---

## 三、与 host-boundary-008 的张力与条款

### 3.1 张力点

`host-boundary-008`：transform「不得把『当前 assistant run 已经存在』作为业务前置条件，也不得通过 bounded wait 把未来 run 伪装成 projection lag」。

现状中，`resolveSurface` 同时承担两件事，使 target 的可见性成为整个 capture 的前置；`tryResolveAssistantRun` 的 bounded reread 落在该禁区的边缘。

### 3.2 裁决与措辞

解耦后：

- 业务事实（Requested）不再以前 run 可见性为前置：来源判定只读 rawMessages、wire、durable projection 与 physical 冻结 plan。
- bounded reread 只服务「已 committed 的 projection 可见性」，且失败安全失败、不产生业务事实；不再是「等未来 run」。
- target 唯一命中仍是 Bound 的唯一准入；未命中保持 Requested，由后续合法请求处理，不伪造。

建议在 `speculative-investigation` 补一条（编号按实施时登记），措辞要点：

> **[新增] 捕获与目标绑定分相。**
> `DelegationRequested` 的判定只依赖来源完成批次、owner logical run、authority、durable projection 与 exact `PhysicalUserMessageId` 的冻结 attempt plan（`RequestKind`、`ProjectionChoice`），不得依赖当前 provider run 的绑定可见性。
> target 唯一命中只作为 `DelegationBound` 的准入。target 未命中时，已证明的来源不得丢弃：Requested 保持为 durable 未绑定事实；后续任一 target 可唯一命中的合法 WorkMain 请求经 `startPendingRequest` 以该请求的 target 与 mirror 完成 Bound 与启动。
> 不得为等待 target 出现而扩大 bounded 读取预算、引入轮询，或把未来 run 伪装成 projection lag；同一 physical 的重复 transform 复用首次冻结的 attempt plan，不得重算后以冲突终止。

同步在 `host-boundary-008` 补一句边界澄清（或引用新条款）：

> capture 的「来源冻结」不以当前 run 存在为前置；target 读取的 bounded reread 只读已提交投影，失败即安全失败。

### 3.3 需要明确的失败/补偿

- 来源批次不完整 → 不落 Requested（现状）。
- Requested 已落 + target 不可解析 → 保持 Requested；不建副本、不发 provider、不写 Bound、不占容量。
- 新用户输入或 authority 替代 → `Closed(Superseded)`（现有 `startRequested` 分支）。
- 进程重启 → Requested 保持；由后续 transform 的 start 路径消费（speculative-investigation-010）。

---

## 四、本次落盘的诊断（纯观察）

### 4.1 绑定失败

- 位置：`Strength/OpenCode/Delegate.fs` · `tryResolveAssistantRun` 最终失败分支（经 `emitProviderRunUnbound`）。
- operation：`strength-provider-run-unbound`。
- 字段：`session_id`、`physical_user_message_id`、`result`。
- result 编码：`<classification>;assistants=<n>;incomplete=<n>;parent_match=<n>;parent_completed=<n>;compaction_parent=<bool>;sequence_complete=<bool>`。
- classification ∈ `projection-not-visible-exhausted` | `rejected:no-bindable-run` | `rejected:ambiguous-run:<count>` | `rejected:not-latest-run` | `rejected:insufficient-sequence` | `bound`（不可达，穷尽匹配）。

区分能力：0 候选耗尽 vs 多候选 vs 最新非候选 vs 缺 CreatedAt vs compaction。

### 4.2 来源批次失败

- 位置：`tryResolveSourceCalls` 的 None 分支（经 `describeSourceBatchFailure`）。
- operation：`strength-source-batch-unresolved`。
- 字段：`session_id`、`physical_user_message_id`、`result`。
- result 编码：`tail=<completed|incomplete|absent>;raw_tools=<completed>/<total>;wire_tail=calls:<n>;results:<n|n/a|error>;calls=<yes|no>;run=<yes|no>;reason=<...>`。
- reason ∈ `incomplete-text-tail` | `incomplete-tool-tail` | `absent-wire-tail-incomplete` | `absent-no-assistant` | `absent-no-wire-batch` | `absent-no-provider-run` | `completed-wire-tail-incomplete` | `completed-raw-tools-incomplete` | `no-calls` | `no-provider-run`。

区分能力：尾部纯文本 vs 工具未完成 vs wire 尾部无完整批次 vs 配对缺 calls/run。

两条诊断只加在既有失败分支，返回值字节不变；字段全在 CTX-014 白名单（`session_id`、`physical_user_message_id`、`result`），未改白名单与测试。

---

## 五、failure-path 四问测试清单（实施时新增）

每个用例回答：什么失败；之后哪个 result/state 必须成立；哪些 cleanup/compensation 必须发生；哪些 side effect 绝不能发生。

1. **target 未绑定不阻塞 capture**（spec-inv 013，真实 transform）
   - 失败：`observeBindableRun` 0/多候选；来源批次完整且估计为正。
   - 必须成立：`DelegationRequested` 落盘；无 `DelegationBound`；`strength-provider-run-unbound` 存在。
   - cleanup：无副本、无 provider 请求、无容量占用。
   - 绝不：伪造 target、写 Bound、扩大读取预算。

2. **后续请求恢复 start**（spec-inv 010/013）
   - 失败：首次 start 时 target 不可解析。
   - 必须成立：下一请求 `DelegationBound` 的 target = 该请求 assistant run；mirror 为该请求最终出站字节；副本启动一次。
   - cleanup：Requested 幂等 Replay；不重跑只读工具。
   - 绝不：绑定旧 placeholder、重复 Requested、第二个副本。

3. **来源判定与 target 解耦（纯逻辑）**（spec-inv 013 或新单测）
   - 同一来源 + 有/无 target 两形态，`resolveCompletedSourceBatch` 结果一致；`owner provider run is not uniquely bound` 不再出现在 capture 决策。

4. **RequestKind / prefix probe 来自 physical 冻结 plan**（host-boundary 019/008）
   - pending plan `TentativeCold` → capture 按 policy 拒绝；WorkMain → 正常；无 plan → 既有默认。
   - 绝不：由 target 反查 plan、session-current 回退。

5. **Requested 的替代与关闭（回归）**（spec-inv 010）
   - 新用户输入/authority 替代 → `Closed(Superseded)`，不启动；重启后 Requested 保持，不自动消费、无 provider 请求。

6. **host-boundary-008 时序边界**
   - 预算耗尽 → start 安全失败；同一 physical 重复 transform 复用首次冻结 plan；后续 exact observation 一次性绑定。
   - 绝不：延长等待、把未来 run 当 projection lag、以冲突终止。

落点建议：`speculative-investigation/tests/010.test.mjs`、`013.test.mjs`、`host-boundary/tests/008.test.mjs`、`019.test.mjs`。文件号以实施时现有编号为准。

---

## 六、风险与未决

1. `OwnerSurface` 拆分是约 30 个调用点的机械重构。建议单独工作包：先重构（行为不变，套件全绿），再做 D2/D3 行为改动。两段各自原子提交。
2. `planEvidence` 改按 physical 依赖管线步骤 11 先于 13.3。需条款锚定 + 测试；若顺序调整，capture 的 RequestKind 判定退化。
3. 诊断结果先于修复实施。若 owner 的 reason 为 `incomplete-tool-tail` / `absent-wire-tail-incomplete`，则 D1-D3 不解决 owner 的 capture；需另查「transform 时点工具结果可见性」与来源批次的定义。
4. 若 replica 的 classification 为 `rejected:ambiguous-run` 或 `not-latest-run`，`bindableRun`/`observeBindableRun` 的候选规则本身可能需要另案裁决（当前合同不重试 identity 拒绝）。
5. 诊断改动未编译；未跑套件。待 DevOps 在 Node 22/25 下跑 `node scripts/build.mjs`、`speculative-investigation` 相关用例与 `node scripts/check.mjs`。
6. 本文件为新增 tracked `.md`；WP-042 已关自动 envelope 派生，不影响构建，但 degeneration-guard 手动 envelope 的更新流程仍需按流程执行。
