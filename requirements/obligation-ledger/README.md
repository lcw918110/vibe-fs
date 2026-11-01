# obligation-ledger — README

本包规定待办义务的消极边界：宿主原生 `todowrite` 退役，插件不维护第二份待办账本。规则正文见 `WHAT.md`；本文件只说明测试放在哪、哪些前态还缺入口，不新增规则。

## 001 / 002 — 原生 todowrite 退役与角色编程面待办

- `tests/001.test.mjs`：验证 `todowrite` 已从 `StaticTools.knownToolNames`、角色权限矩阵、`InvestigationEstimateContract.classifyTool`（返回 `Unreviewed`）和 `resources/ablation/tool-map.json` 中移除；验证原生 `todowrite` 不被放行。
- `tests/002.test.mjs`：验证 `todowrite-for-*` 角色编程面执行边界。

## 003 / 004 / 005 — 不维护第二份待办投影

`tests/004.test.mjs` 覆盖：

- **纯 fold 路径**（第一个用例）：companion、opening、trace parts、terminal、blog、prefix epoch、enforcement 在非空前态下逐值保留；同 call 重放幂等；跨 session 隔离；退役 API 拒绝（007）。
- **真实链路径**（integration 用例）：前态全部经真实 owner 入口建立；退役的 checkpoint 事实不影响其他投影。
- `Persistence/Journal/Surface.fs` 的 snapshot 暴露 `sessions`/`viewCheckpoints`/`sessionProjections` 字段。
- `Context/Companion/FoldSurface.fs` 的 `sessionToJs` 不再硬编码 null，转发全部 9 个 slice。
- 转发粒度：PromptAuthority profile 转发 session/logicalRun/authorityRoot/authorityKind（不含 identitySeed 完整结构，其保留由 interaction-authority 域测试承载）；ProviderFailures、Guidelines、DelegatedToolEstimate、Handles 等转发计数与 key 判别字段。

### 仍待补：前态没有共享 journal 入口

下列 slice 的非空前态在共享 journal 上没有公开入口（owner surface 的 journal handle 不配 `runtime.journal`，或 fact 族没有 JS 解码入口），要补覆盖得先由人决定是否为该 owner 开入口：

- ProviderFailures（TemporalSurface 的 journal handle 类型私有，纯 fold 无 checkpoint 路径）
- Guidelines / SessionStartedAt / DelegatedToolEstimate

这些 slice 现在只以空形状参与深比较（「checkpoint 后空 slice 仍空」有断言）；非空前态的检查等入口定了再补。

## 局部运行

`node --test requirements/obligation-ledger/tests/*.test.mjs`（构建须 `node scripts/build.mjs`；红绿由 DevOps 执行）。

