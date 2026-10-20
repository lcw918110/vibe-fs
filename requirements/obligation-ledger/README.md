# obligation-ledger — README

本包规定编程面（`todowrite-for-<role>`）的 ANCHOR `view` 声明压缩 checkpoint 边界与宿主原生 `todowrite` 的退役约束。规则正文见 `WHAT.md`；本 README 记录各条款的实际证明边界，不新增规则。

## 001 / 002 — 原生 todowrite 退役、view 声明门禁与 checkpoint 记录

- `tests/001.test.mjs`：验证 `todowrite` 已从 `StaticTools.knownToolNames`、角色权限矩阵、`InvestigationEstimateContract.classifyTool`（返回 `Unreviewed`）和 `resources/ablation/tool-map.json` 中移除；验证只有携带非空 `state.input.view` 的 `todowrite-for-*` 终态才产生 `ViewCheckpointCommitted`，缺省 `view`、空对象 `{}`、数组或原生 `todowrite` 均不产生 checkpoint。
- `tests/002.test.mjs`：验证 `PhaseWindow` checkpoint 窗口与跨 `todowrite-for-*` 编程面调用的 `ViewCheckpointCommitted` 累积，以及真实 `opencode serve` canary（`tests/support/run-native-todo-replacement-canary.mjs`）驱动带 `view` 的 `todowrite-for-engineer` 调用落盘 `ViewCheckpointCommitted`。

## 004 — ViewCheckpointCommitted 不影响非 checkpoint projection

### 已证明

- **纯 fold 路径**（`tests/004.test.mjs` 第一个用例，经 `Context/Companion/FoldSurface`）：companion、opening、trace parts、terminal、blog、prefix epoch、**enforcement** 在非空前态下逐值保留；同 call 重放幂等；跨 session 隔离；退役 API 拒绝（007）。enforcement 断言随观察口转发补入。
- **真实链路径**（`tests/004.test.mjs` integration 用例，005 模式）：前态全部经真实 owner 入口建立——PromptAuthority 经 dispatch surface 的 authority root acceptance、Handles 经生产 HandleLinked journal append、Relay 经真实 RelayTransaction append（openIncumbency）、Companion 与 XTrace terminal 经 journal surface 的 Companion facts；checkpoint 经 PluginHooks completed 事件（005 已证的链路）；`JournalSurface_snapshot` 的 `sessionProjections` 观察口逐 slice 转发实际 ProjectionSet，before/after 深比较，仅 `viewCheckpoints` 允许变化。handles 被清空、relay road 消失、authority profile 漂移等任何非 checkpoint 变异都会红。

### 观察口（Manager 裁决方案 A）

- 各 slice 的只读转发只读 public 字段与 owner 提供的访问器；private 类型不解构：`Relay.Fold.roads/view`（本次新增 `roads`）、`SessionStartedAtProjection.startedAt`（既有）。
- `Persistence/Journal/Surface.fs` 的 snapshot 暴露 `sessions`/`viewCheckpoints`/`sessionProjections` 字段。
- `Context/Companion/FoldSurface.fs` 的 `sessionToJs` 不再硬编码 null，转发全部 9 个 slice。
- 转发粒度：PromptAuthority profile 转发 session/logicalRun/authorityRoot/authorityKind（不含 identitySeed 完整结构，其保留由 interaction-authority 域测试承载）；ProviderFailures、Guidelines、DelegatedToolEstimate、Handles 等转发计数与 key 判别字段。

### 仍待裁决（前态无共享 journal 公开入口）

以下 slice 的非空前态在共享 journal 上没有公开入口（各 owner surface 的 journal handle 类型不配 `runtime.journal`，或 fact 族无 JS 解码入口），按裁决边界 2 列出，由 Manager 决定是否为该 owner 增加入口：

- ProviderFailures（TemporalSurface 的 journal handle 类型私有，纯 fold 无 checkpoint 路径）
- Guidelines / SessionStartedAt / DelegatedToolEstimate

这些 slice 目前以空形状参与深比较（「checkpoint 后空 slice 仍空」已被断言），非空前态的变异检测待入口裁决后补入。

## 局部运行

`node --test requirements/obligation-ledger/tests/*.test.mjs`（构建须 `node scripts/build.mjs`；红绿由 DevOps 执行）。
