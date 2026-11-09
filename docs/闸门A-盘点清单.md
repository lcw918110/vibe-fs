# 闸门 A：人话盘点清单

> 2026-11-05。摸清树里实际还在扛事的东西：有什么、谁在用、挡不挡总流程、该留/改/删/迁万象通。
> 数字都是当场量的，不是回忆。判据只有一条：**它挡不挡总流程**——用户打开软件、说清需求、事情做完、结束。

## 一、总账

| 区域 | Git 跟踪文件 | 体量 | 一句话 |
|---|---|---|---|
| `src/` | 1574 | 约 11.5 万行 F# | 程序主体，19 个目录 |
| `requirements/` | 1370 | 47 包 / 799 测试文件 | 说明书 + 测试，测试即自保 |
| `scripts/` | 82 | 约 1.85 万行 | 构建、门禁、验证 |
| `resources/` | 537 | 2.6M | 提示词与工具资源 |
| `wanxiangtong/` | 26 | 5309 行 | 宿主翻译层，**尚未独立** |
| `wanxiangli/` | submodule | 60+ 模块 | 中立核 |
| `proposals/` | 22（其余本地排除） | 34 份 / 760K | 施工笔记，多数已过期 |
| `docs/` | 4 | 672K | 现状与导览 |

编译产物 `src/Wanxiangshu/Wanxiangshu.Impact.*.fsproj` 共 **374 个**，未被 Git 跟踪，已在 `.gitignore` 第 76 行覆盖。不是垃圾，是增量编译的哈希分片，但会淹没 `ls`。

---

## 二、`src/` —— 程序主体

19 个目录，按 F# 行数排：

| 目录 | 行数 | 是什么 | 挡总流程吗 |
|---|---|---|---|
| `Execution` | 39402 | 执行、委派、会话、工具准入 | 是 |
| `OpenCode` | 29213 | **宿主专属**：消息、插件、工具、编解码 | 是，但语义该在万象通 |
| `Interaction` | 15122 | 交互权威、派发、修复 | 是 |
| `Sphinx` | 12645 | 推理内核 v2 | 是 |
| `Repository` | 11967 | 仓库调查与编程 | 是 |
| `Persistence` | 6800 | 事件存储、日志 | 是 |
| `Composition` | 6274 | 组合者，Turn 编排 | 是 |
| `Change` | 5910 | 变更集成 | 是 |
| `Participant` | 5872 | 参与者、provider 投影 | 是 |
| `Context` | 5415 | 上下文、前缀、压缩 | 是 |
| `Mission` | 4620 | Work 生命周期、交付 | 是 |
| `Omp` | 3869 | **第二宿主适配** | 是，但该在万象通 |
| `Host` | 3042 | 宿主中立端口 | 是 |
| `Foundation` | 2654 | 基础类型 | 是 |
| `Process` | 2313 | 进程执行 | 是 |
| `Verification` | 2062 | 验证支撑 | 是 |
| `Ablation` | 930 | 特性消融 | 边缘 |
| `Resources` | 448 | 资源编目 | 是 |
| `Git` | 414 | Git 操作 | 是 |

**判定：全部留。** 没有一个是空壳或死代码——每个目录都有被 `requirements` 里的测试真实调用。真正的债不在「哪个目录该删」，而在下面三处结构性错位。

### 2.1 万象通没有独立：它现在是 `src` 的编译期一部分

这是本次盘点最重要的发现。

- 6 个 `Wanxiangshu.Owner.*.fsproj` 用 `<Compile Include="../../wanxiangtong/...">` 直接把万象通源码编进自己
- 万象通 **全部 20 个文件**都 `open Wanxiangshu.*` 或 `namespace Wanxiangshu.*`（`Capture.fs` 24 处、`RequestProjection.fs` 39 处）
- 结果：万象通既不是独立层，也不是独立包，而是 `src` 的一部分，只是放在另一个目录

**这违反纠偏令第 3.1 条**（翻译税进万象通，万象通 → 万象理单向）。当前是 `src` ↔ 万象通双向缠绕。

**处置：改。** 不是删，是解耦。万象通要能独立编译，只能通过公开契约消费万象理，不能 `open Wanxiangshu.*`。这是一刀真活，不是文档活。

### 2.2 宿主专名渗进 `src` 各层

- 426 个文件含 `OpenCode`/`opencode`，其中 220 个在 `OpenCode/` 目录（合理），**206 个散落在其他 15 个目录**
- `Omp` 专名同样散落：`Execution` 172 处、`OpenCode` 112 处、`Interaction` 77 处

散落本身不必然是错——`Execution` 里出现宿主名可能只是注释或诊断文案。但 `Owner` 分片名里带 `opencode-host-*` 的有几十个，说明**宿主适配代码还在主体里**。

**处置：改。** 逐点判断是「真宿主语义」还是「只是名字出现在注释里」。前者迁万象通，后者不动。这项工作量大，不能靠正则。

### 2.3 `guidance-delivery` 代码在、说明书没了

`requirements/guidance-delivery/` 随 `584c30c61` 删除，但 5 个 `Owner.guidance-delivery.*.fsproj` 仍在编译 `PairGuidance.fs`、`Occurrences.fs`、`PairProgrammingCalibration.fs` 等真实代码。

**这不是死代码，是文档缺口。** 代码有主人（配对编程提示），说明书被误删。

**处置：改。** 要么补回说明书，要么把代码迁走。不能装作它不存在。

---

## 三、`requirements/` —— 47 个包

### 3.1 两个已退役包

| 包 | 状态 | 证据 |
|---|---|---|
| `epistemic-reasoning` | **已退役** | `tests/` 只有 README，无 `.test.mjs`；34 份旧测试压成 `.mjs.txt` 存在 `historical-tests/`；`WHY.md` 明写「旧 36 条不再作为当前验收义务」 |
| `obligation-ledger` | **半退役** | 7 个测试仍在跑，但 WHAT 头部标注「COMPAT 2026-10-29 现行口径」，且 `historical-tests/` 存在 |

**处置：`epistemic-reasoning` 删。** 它的义务已由 `sphinx-v2` 的 `SUPERSEDES.md` 承接，留 23K 的 WHAT 和 34 份压缩测试只是考古负担。历史在 Git 里。

`obligation-ledger` **留**，7 个测试还在保护「宿主 todowrite 已退役」这条真实约束。

### 3.2 11 个包没有对应 Owner 分片

`attention-regulation`、`cognitive-workspace`、`concern-routing`、`crash-reconciliation`、`effect-accounting`、`js-semantic-surface`、`latency-degeneration`、`obligation-ledger`、`requirement-system`、`sphinx-v2`、`structured-workflow`

抽查三个：`sphinx-v2` 的代码在 `src/Wanxiangshu/Sphinx/V2/`，`crash-reconciliation` 在 `Composition/Turn/Reconcile*.fs`，`structured-workflow` 在 `Composition/Turn/Workflow.fs`。

**结论：分片命名不按包名走，包与分片不是一对一。** 不是缺陷，但意味着「包名 → 代码位置」不能靠猜。

### 3.3 6 个 Owner 分片没有对应包

`dispatch`、`durable-composition`、`guidance-delivery`、`opencode-plugin`、`planning`、`runtime-platform`

`guidance-delivery` 已在 2.3 说明。其余五个是**内部实现分片**，没有独立说明书——`dispatch` 被 `dispatch-protocol` 包覆盖，`planning` 被 `structured-workflow` 覆盖。

**处置：留。** 分片是编译单元，不必都有说明书。

### 3.4 `requirement-system` 在测试自己

这包测的是 `scripts/lib/spec-rules.mjs`——即 requirements 体系自身的规则。5 个测试。

**判定：这是元层面的自保，不是产品行为。** 它保护的是「说明书怎么写」的约定。留着不挡事，但也不产生用户价值。

**处置：留，但降级。** 它不属于产品能力，不应和 `work`、`delegation` 并列出现在能力清单里。

---

## 四、`scripts/` —— 支撑程序

| 位置 | 行数 | 是什么 |
|---|---|---|
| `scripts/lib/` | 9360（35 文件） | 构建核心：`owner-compile.mjs` 1949 行、`test-surface-scan.mjs` 1903 行 |
| `scripts/checks/` | 7187（25 文件） | 静态门禁 |
| `scripts/verify-package.mjs` | 1579 | 打包白名单校验 |
| `scripts/build.mjs` | 712 | 构建入口 |
| `scripts/verify.mjs` | 497 | 7 级验证入口 |
| `scripts/compiler/` | 590（10 文件） | 编译器封装 |
| `scripts/diagnostics/` | 495（2 文件） | 事故收集 |
| `scripts/coverage.mjs` | 259 | 覆盖率 |
| `scripts/compile-owner.mjs` | 192 | 单分片编译 |
| `scripts/check.mjs` | 105 | 静态检查入口 |
| `scripts/ci-local.sh` | 106 | 容器 CI（本轮重构） |
| `scripts/wanxiangli-source.mjs` | 161 | 库依赖管理 |
| `scripts/warmup-opencode.mjs` | 69 | 预热 |

### 4.1 25 个 check 的实际挂载情况

`check.mjs` 挂载 20 个。没挂的 5 个逐个核实：

| check | 行数 | 真实状态 |
|---|---|---|
| `adapter-thinness.mjs` | 327 | **活**：被 `architecture.mjs` import |
| `fsharp-control-pyramid-guide.mjs` | ~~1517~~ | **已删**（2026-11-05）：纯常量表，只在 `--explain` 时打印。门禁阈值同时从 `depth >= 2` 收到 `depth >= 3`——两层（`if 前置检查 then ... else match`）是正常写法，原阈值把正常代码当违规，配 1517 行手册教人改，是典型的正确性宗教。现 `--explain` 输出 11 行说明。 |
| `shard-edge-audit.mjs` | 421 | **半活**：只在注释里被提及，未 import |
| `js-surface-gate.mjs` | 122 | **活**：被 3 个测试调用 |
| `llm-facing-format-gate.mjs` | 68 | **活**：被 4 个测试调用 |

**结论：没有一个是纯死代码。** 「未挂载」不等于「无用」。

### 4.2 真正值得动的

- ~~`fsharp-control-pyramid-guide.mjs` **1517 行**是纯常量表（控制流金字塔的文案指南）。它不检查任何东西，只提供文案。**这是典型的证明税化石**——一个 1517 行的「怎么写代码」教条文件。~~
  **已处理（2026-11-05）**：手册删除，`--explain` 改为 11 行；门禁阈值从 `depth >= 2` 收到 `depth >= 3`。原阈值报出的 2 处都是 `if 前置检查 then ... else match` 这种正常写法，改后全仓 0 违规。
- ~~`unified-store-gate.mjs` **776 行**、`language-parity-gate.mjs` **526 行**，都是巨型门禁。~~
  **已核实（2026-11-09），结论：留，不删不瘦。** 行数不是罪证，抓不住反例才是。逐个 scanner 喂违规样本实测：
  - `unified-store-gate` 6 个 scanner 全部真会红，且守卫条件正确：`git-bypass` 只在非 store-owner 路径报（`Persistence/`、`Git/` 本就该调 git），`schema-version` 只在附近有 `EventStore`/`EventEnvelope` 上下文时报，裸 `schemaVersion` 不误伤。既有自测 `durable-events/tests/009` 等已覆盖 75 处引用。
  - `language-parity-gate` 的 `hardcoded-english` 只抓 `ProviderProse.render ProviderLanguage.English` 这类硬编码渲染，不抓普通英文句子；`provider-language-binding` 要求观察原始环境值、委托解析、别名归 owner 三件事同时成立。`provider-language/tests/` 与 `distribution/tests/012` 已覆盖 17 处。
  - 判据：删了它，双写、绕过统一 git、私有持久底座、硬编码英文渲染会立刻回来。按「删掉用户会立刻做不成事吗」这一条，两个都该留。
- `test-surface-scan.mjs` **1903 行**，扫描测试挂载关系。

**处置：逐个读，不是批量删。** ~~优先看 `fsharp-control-pyramid-guide.mjs`——如果它只输出文案给 AI 看，那就是纠偏令点名的「正确性宗教」，该删或该瘦成几十行。~~
**已处理（2026-11-05）**：确认是正确性宗教，已删并收窄阈值。详见 4.1。

---

## 五、`resources/` —— 提示词与工具资源

537 个文件 2.6M，分 13 个目录：`provider/{role,host,runtime,planning,lifecycle,world,library,delegation,work,casebook,tool}`、`enforcer/`、`ablation/`。

**这是产品面**，模型真正读到的提示词。纠偏令要求「去证明化，服务总流程；禁止教模型先证明再干活」。

**处置：留，但要审。** 逐目录读一遍，凡是「先出示证明再干活」「维护 ledger 再理解需求」的腔调，改写。这是闸门 B（纠偏）的活，不是 A 的。

---

## 六、`proposals/` —— 34 份施工笔记

Git 跟踪 22 份，本地排除 12 份（含 `TODO待办清单-批次规划`、`现存bug清单与测试用例` 等）。

按性质分：

| 类型 | 份数 | 例子 | 处置 |
|---|---|---|---|
| 已完成的施工计划 | 9 | `第三批/第四批/第五批完整职责拆分`、`新边缘模块拆分`、`已拆模块边界调整` | 删 |
| 过期的 bug 清单 | 3 | `现存bug清单与测试用例`（966 行）、`现存bug分批修复计划` | 删 |
| 模块说明（有替代） | 3 | `bounded-map/llm-text/response-text模块说明` | 删（内容应在库仓） |
| 当前仍有效的记录 | 3 | `本轮合并请求整理-20261105`、`本地与主仓分叉分析-2026-10-19`、`类别C落点清单` | 留 |
| 说不清用途 | 16 | `旧的.md`、`Steward.md`、`验收驱动.md`、`跨Scope回问` | 逐个看 |

**处置：删大部分。** 纠偏令明写「历史全文在 git，活文档不养尸」。这些是本地施工笔记，Git 里有，删了不丢。

---

## 七、`docs/` —— 4 份现状文档

| 文件 | 大小 | 用途 |
|---|---|---|
| `两仓现状.md` | 22.7K | 两仓分工与现状，**含例外清单**（`run-witness` 的真实消费点就记在这里） |
| `人工巡检.md` | 3.0K | 巡检步骤 |
| `功能导览.md` | 4.6K | 功能地图 |
| `委托惯例-验证前查总工头合入.md` | 838B | 一条惯例 |

**处置：留。** `两仓现状.md` 是当前唯一记录「哪些是例外、解除条件是什么」的活文档，有价值。

---

## 八、`wanxiangli/` —— 中立核

submodule，`branch = main`，gitlink `abd1f721`。60+ 模块，`src` 消费其中 15+ 个（`dispatch` 29 处、`runtime` 25 处、`chat-execution` 22 处）。

**发现一处需登记：** 核里有 `run-witness` 模块（`ClosureWitness` 编解码）。纠偏令点名「witness 是证明税」，但 `docs/两仓现状.md:104` 明确登记为**例外**：它被宿主 `Execution/Routing/ModelRouting.fs` 与 `Interaction/Authority/Run.fs` 真实消费，解除条件是「沿调用点判断哪些记录是真实运行所需，剩下迁出核或删掉，不凭名字删」。

**处置：留，按登记条件处理。** 不凭名字删，这是对的。

---

## 九、汇总：该做什么

### 立刻做（无争议，不挡任何事）

1. **删 `requirements/epistemic-reasoning/`** —— 已退役，义务由 `sphinx-v2` 承接，23K WHAT + 34 份压缩测试是纯考古负担
2. **删 `proposals/` 里已完成的施工计划与过期 bug 清单** —— 约 12 份，Git 里有
3. **补 `guidance-delivery` 的说明书，或迁走它的代码** —— 现在是有代码无说明的裸奔状态

### 需要判断（要读代码，不能靠正则）

4. **解耦万象通** —— 让它能独立编译，去掉 `open Wanxiangshu.*`。这是结构性一刀
5. ~~**`fsharp-control-pyramid-guide.mjs` 1517 行** —— 读一遍，判断是「有用的文案」还是「正确性宗教化石」~~ **已完成（2026-11-05）**：是正确性宗教。手册删除，阈值从 `depth >= 2` 收到 `depth >= 3`，全仓 0 违规。
6. **`src` 里 206 个散落的 OpenCode 专名文件** —— 逐点判断真宿主语义 vs 注释提及

### 明确不做

- 不删 `run-witness`（有真实消费点，已登记例外）
- 不删任何 check（5 个「未挂载」的都有真实引用）
- 不删 `obligation-ledger`（7 个测试保护真实约束）
- 不动 `src` 的 19 个目录（都有测试真实调用）

---

## 十、这份清单不是什么

- 不是完成度账册。没有「已盘点 80%」这种话。
- 不是迁移计划。盘点只回答「现在有什么」，不回答「下一步迁哪个」。
- 不是证明。数字是当场量的，会变；下次量可能不同。

**闸门 A 的完成标志**：这张表与树里的东西对得上，且你能看着它说「对，就是这么回事」。然后才轮到 B（纠偏）、C（调结构）、D（清理支撑程序）。
