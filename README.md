# 万象术 Herdchestra：让一万只大象一起跳舞

> Orchestration for agents that don't like being orchestrated.

写程序这件事，长久以来是一个人的手艺。一个工程师，一台电脑，一条思路，从需求走到代码，饿了吃饭，困了睡觉。后来 AI 来了，我们忽然有了一群不会疲倦的程序员。它们读代码，查资料，写功能，补测试，做代码审查；来自不同的厂商，用着不同的模型，脾气也各不相同——有的谨慎，有的激进；有的写起代码来像钟表匠，有的像推土机。而且，每一个都还在变聪明。

于是有了一个新问题：如果一个智能体能写程序，一百个、一千个、一万个一起写，会怎么样？

这件事，有点像让一万只大象跳舞。

大象并不蠢。恰恰相反，它们聪明、有力、记性惊人，问题只在于太重。一只大象往左挪一步，那只是一步；一万只大象同时往左挪一步，地都要震。工程里也是如此：这边刚改完一个文件，那边的智能体已经把它覆盖了；这个还在查问题，那个已经按自己的理解动了手；有的以为任务做完了，有的还欠着测试；有的掉线，有的重试，有的忘了自己干过什么。要是再闯进来一个带着别家模型习惯的，场面就更热闹了。

对付这样的场面，喊是喊不动的，给每头象拴一根更长的绳子也没用。现在不少智能体编程的做法，说到底还是一个指挥站在台中央，拼命挥棒子，指望几十头越来越有主见的象恰好踩在同一个小节上。两三头的时候，这法子看着不错；十头，开始手忙脚乱；一万头，指挥棒就成了道具。

人们爱说，站在风口上，猪也能飞。风确实起来了：模型越来越强，价格越来越低，速度越来越快智能体越来越多。可是风托得起一只猪，托不起一万头没有队形的象。到了那个规模，能不能飞，不再取决于哪头象最聪明，而取决于地上有没有路，有没有红绿灯，有没有账本、分工和规矩；有人跌倒了，队伍还走不走；有人走散了，还能不能找回来；各支队伍能不能踩着自己的步子，在同一个世界里把同一件事做完。

这就是我们做万象术的原因。“万象”，既是一万头象，也是世间万象。我们不想去造世界上最聪明的那头象，也不想把所有的象驯成同一个姿势。我们想做的事更笨一些，也更基础一些：让不同的智能体各尽其长，又能共同成事。今天最好的模型能来，明天更好的也能来。

它们性格不同，能力不同，上下文不同，干活的法子也不同。但只要进了同一个工程，就知道自己是谁，正在做什么，能做什么、不能做什么；知道哪些事已经发生，哪些事还没有完成；知道什么时候可以并行，什么时候必须等待；知道怎样把手里的活交给另一个智能体—也知道，万一哪个智能体消失了，世界不该跟着失忆。

这里没有永远正确的总指挥，也没有一根拴住所有象的绳子。有的只是一套基础设施，让协作这件事本身变得可靠。

于是，一头象可以跑，一百头象可以协作，一万头象可以跳舞。而且跳的不是同一种舞：爵士、芭蕾、街舞，还有没人见过的新舞步——各有各的节奏，各有各的长处，谁也不踩谁的脚。

这就是万象术：不是驯服智能的法术，而是驾驭智能规模的技术。我们相信，AI 编程的下一个时代，不属于拥有最强智能体的人，而属于能让无数不同的智能体自由加入、可靠协作、把真正复杂的工作做完的人——就像今天的进程、服务和机器那样。当智能成为充沛的资源，协同就是新的稀缺品。万象术想做的，就是这一层。

让一万头大象一起跳舞。更快，更好，更省。

万象皆可用，众智自成事。

---

万象术以 OpenCode 插件形式落地。它不替换 Host 的对话模型，而是在其上叠加一层结构化编排：Orchestrator 统筹全局，Manager 分解任务并管理子会话，Engineer 调查事实与修改源码（独占 Fission），DevOps 管控进程与执行并拥有固有局部自修授权。每个角色有自己的工具面与权限边界，Companion 在会话级提供认知上下文，Fallback 与 Review 各有明确写入口。智能体不必彼此信任，只需遵守同一套事实与边界。

Wanxiangshu is proprietary commercial software.
Use, copying, modification, and distribution are governed by LICENSE.

## 用户指南

### 产品简介

万象术作为 OpenCode 插件加载后，为 Host 会话提供多角色协作、任务分叉与汇合、审阅与恢复。公开入口：

```text
import "wanxiangshu"
→ dist/OpenCode/Plugin/Plugin.js
```

`package.json` 的 `main` / `exports["."]` 指向同一路径。npm 包含 `dist/` 与 `resources/`，不含源码与测试树。

### 系统要求

- Node.js `>= 20`（`engines.node`）
- OpenCode Host，peer 依赖 `@opencode-ai/plugin`（`>= 1.17.4`）
- 从源码构建时：.NET SDK（`global.json`）与 `dotnet tool restore`（Fable、Fantomas）

`packageManager` = `npm@11.12.1`。安装依赖使用 `npm ci`（已提交 `package-lock.json`）。

### 获取与安装

`private: true` 商业软件，不从公共 npm 默认源分发。从 tarball 或私有 registry 安装：

```bash
npm install ./wanxiangshu-0.9.0.tgz
# 或
npm install wanxiangshu --registry <your-private-registry>
```

版本以 `package.json` 的 `version` 为准（当前 **0.9.0**）。

### OpenCode 配置

1. 在可解析 peer 插件 API 的环境中安装本包。
2. 按 Host 的 plugin 配置挂载入口（包名 `wanxiangshu` 或已安装包的 `main`）。
3. 启动 Host。插件初始化时加载 `resources/` 下 system prompt 与 Enforcer catalog；资源缺失或非法则启动失败（fail fast），无代码内置副本兜底。

配置以 Host 文档与 `peerDependencies` 为准。角色与 Prompt 语义以 [requirements/README.md](requirements/README.md) 为高级参考；安装与挂载不依赖阅读条款正文。

部分常用可选环境变量（非全集）：

| 变量 | 作用 |
|------|------|
| `WANXIANGSHU_SKIP_AUTO_INJECTED=1` | 跳过 HOST-013 新的 `auto-injected` 伪工具注入；已落盘历史 pair 仍会 replay |
| `WANXIANGSHU_PROCESS_HARD_LIMIT_SECS` | executor 单进程硬超时上限（秒） |
| `WANXIANGSHU_NO_FATAL_EXIT=1` | 抑制 fatal 的物理进程退出（`SIGKILL` / `process.exit`）；测试用 |
| `WANXIANGSHU_PROVIDER_LANGUAGE` | provider 语言偏好显式设置（`en` / `zh-CN`），位于全局语言阶梯最高优先级 |
| `WANXIANGSHU_ADMISSION_TIMEOUT_MS` | prompt 物理 acceptance 等待超时（毫秒，默认 10000） |
| `WANXIANGSHU_DIAG=1` | 让内部诊断记录经 stderr 可见；只观测，不改变任何决策 |
| `WANXIANGSHU_ABLATION_PROFILE` | feature-ablation 拓扑 profile 选择（配 `resources/ablation/`） |
| `WANXIANGSHU_ABLATION_<node>` | feature-ablation 单节点三态覆盖（`ablated` / `borrowed` / `active`）；仍过 DAG 校验 |
| `SPHINX_COMMON_DIR` | Sphinx MCP 独立进程的 durable workspace；缺失即启动失败 |
| `SPHINX_START_CONFIG` | Sphinx MCP start 配置 JSON；缺失时只允许读取类工具（start 返回 `CONFIG_REQUIRED`） |

### 升级：只读委托（调度协议 2）

只读委托由模型调度配置驱动，没有独立开关。升级只做两件事：旧的环境变量已全部失效；模型调度配置需要升到协议 2。

**旧环境变量已失效。** `WANXIANGSHU_STRENGTH_MODE`、`WANXIANGSHU_STRENGTH_DRY_RUN_BUDGET`、`WANXIANGSHU_STRENGTH_HOST_CANARY`、`WANXIANGSHU_STRENGTH_K1_MARGIN`、`WANXIANGSHU_STRENGTH_K2_MARGIN`、`WANXIANGSHU_STRENGTH_K2_MIN_EVIDENCE`、`WANXIANGSHU_STRENGTH_CONTROL_BPS`、`WANXIANGSHU_STRENGTH_POLICY_VERSION`、`WANXIANGSHU_STRENGTH_SAVED_DEEP_*` 等一律不再读取。残留旧值不会阻止运行，也不会启用任何功能；从 shell 环境里删掉即可。

**模型调度配置升级。** 配置位于 `~/.config/opencode/wanxiangshu.mjs`，需要导出：

- `export const routingProtocol = 2`：调度 ABI 的稳定契约版本，不是运行开关。
- `export default function route(role, running, previous, purpose)`：`purpose` 取 `"normal"`（角色常规执行）或 `"readonly-delegate"`（只读委托，从 Predictor 模型池选择）。用途不改变角色与参与者身份。
- `export const hasTheoreticalCapacity = (role, purpose) => …`。
- `export const predictorConfiguration = () => ({ state: 'configured' | 'unconfigured' | 'invalid', reason })`：Predictor 槽位存在且候选非空即已配置；容量与 provider 健康不参与这个判断。

随包模板 `resources/wanxiangshu.mjs` 已是协议 2 的参照形状，对照修改自己的配置即可。旧的三参数配置会被加载器明确拒绝并给出可操作错误；运行时不会覆盖用户已有的配置文件。

**启用方式。** Predictor 模型配置是唯一启用依据：配置存在即启用，未配置就是没有委托。没有独立开关、环境变量或消融选项；模板自带的非空 Predictor 池同样算作已配置。

**历史数据迁移。** 旧存储不会在新运行时被静默消费：未迁移的旧协议事件在集成规则入口被明确拒绝。迁移是离线一次性操作，在 EventStore 备份副本上进行，先跑 dry-run 核对报告，再加 `--execute`：

```bash
node scripts/build.mjs
git rev-parse --git-common-dir        # 找到 EventStore 所在的 common dir
cp -a "$(git rev-parse --git-common-dir)" /path/to/backup   # 先复制；迁移只在副本上进行

node scripts/migrate-delegation-history.mjs \
  --backup /path/to/backup --input-version pre-delegation \
  --contract-revision 1 --report migration-report.json
# 核对报告后：
node scripts/migrate-delegation-history.mjs \
  --backup /path/to/backup --input-version pre-delegation \
  --contract-revision 1 --report migration-report.json --execute
```

`--input-version` 与 `--contract-revision` 按迁移工具内的登记表校验：输入版本只有 `pre-delegation`（分类器只认定这一种旧协议），历史导入工具登记的契约修订版本保持为 `1`（这是历史事实），而当前运行时采用的委托契约修订版本为 `2`（定义在 `src/Wanxiangshu/Strength/OpenCode/Delegate.fs`）。

迁移只追加 `DelegationHistoryImported` 导入事实，不改写 append-only 历史、Git 对象或 refs，也不触碰用户配置；脚本拒绝在活库上运行。已配置并启用后，主模型经过逐工具判定的 12 个参与工具有效参数中会包含调查估计 `estimated_readonly_rounds`（连续只读轮数，大于 0 时附带条件参数 `self_note` 简述调查展望；为 0 时省略 `self_note`）；未参与工具无此参数；规范详见 [requirements/speculative-investigation/WHAT.md](requirements/speculative-investigation/WHAT.md) 与 。

### 快速开始

安装并在 OpenCode 注册插件后，Orchestrator 发起任务，Manager 分解并管理子会话，子角色按工具面分工：

```bash
npm install ./wanxiangshu-0.9.0.tgz
# 在 OpenCode 注册插件后启动会话
```

```text
Orchestrator
  └── Manager
        ├── Engineer
        └── DevOps
```

Blogger、Bookkeeper、Predictor 等内部角色由编排路径调用，不作为单独“安装角色”配置。

### 智能体角色

核心活跃角色与 `requirements/office-capability`、`requirements/capability-enforcement` 一致。工具面由 `OfficeCapability.permissions` 定义（`src/Wanxiangshu/Foundation/OfficeCapability.fs`；`Roles.fs` 只承载 Role 词汇）：

| 角色 | 典型工具面 | 说明 |
|------|------------|------|
| Orchestrator | `commission`, `join`, `horizon` | 顶层战役战略统筹与独立道路委任 |
| Manager | `fork`, `resume`, `join`, `horizon`, `review`, `suicide` | 独立评估、任务分解与推进未尽账本；通过 fork 派发 Engineer，通过 resume 续做固定 DevOps（无 Fission） |
| Engineer | `read`, `write`, `edit`, `glob`, `grep`, `mv`, `rm`, `fetch`, `js-engineer`, `bash-honeypot`, `fission` | 本地事实调查与源码读写实现（不执行真实命令，不差遣 DevOps）；独占 Fission 权能 |
| DevOps | `read`, `write`, `edit`, `glob`, `grep`, `mv`, `rm`, `js-devops`, `run`, `open-terminal`, `send-terminal`, `read-terminal`, `signal-terminal`, `join`, `horizon` | 真实命令执行、终端与进程管理；具备角色固有的非架构级自修授权（无 Fission） |
| Blogger | `chronicle` | Companion 叶子，记录工作历史与认知上下文 |
| Plan | `js-plan`, `ask`, `resume`, `handoff`, `deliver` | 接力式规划跑者，负责产出保姆级底稿 P；三阶段交接、任期隔离与崩溃恢复 |

Bookkeeper 是内部叶子角色（有独立 Role Law，不进 public Role DU）。每个 managed work session 配套叶子 Companion（Blogger）。精确权限见 `requirements/participant-identity` 与 `requirements/capability-enforcement`。

### 运行时数据

领域事实写入 Git common directory 下插件私有 `wanxiangshu-next/runtimes/` 路径中的 journal（按 runtime 的 NDJSON；取不到 common dir 时回退 XDG state home），不在业务 workspace 强制创建插件私有目录。随包资源：

- `resources/provider/`（Common Law / Role Law / Tool Law / Delegation Law / Office Library，加 Casebook、Attention Regulation、Concern Routing；EN + zh-CN）；`resources/ablation/{fact-map,nodes,profiles,tool-map}.json`（feature-ablation 拓扑）；`resources/enforcer/<TipName>/{enforcer,main}{,.zh-CN}.md`；`resources/git/wanxiang-hook.mjs`；`resources/wanxiangshu.mjs`（model routing 模板）；`resources/degeneration-guard/envelope/LoopDetectorEnvelope.js`（入库的 loop detector envelope；构建复制到 `dist/`）。**无** `resources/prompts/*`；**无** `catalog.json` SSOT。
- journal 与事实名默认冻结；升级前阅读 [CHANGELOG](CHANGELOG.md)。


### 商业许可与支持

使用、复制、修改与分发受 [LICENSE](LICENSE) 约束。`license` 为 `SEE LICENSE IN LICENSE`，`publishConfig.access` 为 `restricted`。

商业授权与支持请联系版权方。本 README 不承诺开源时间表。

---

## 贡献者指南

面向维护者。法律上仍为专有商业软件，内部工程纪律见 [AGENTS.md](AGENTS.md)。

### 仓库结构

```text
src/           生产源码
resources/     随包运行时资源
requirements/  56 包 normative 语义树（另 2 个历史包）：每包必备 WHY.md、WHAT.md 与 tests/
人工审订语义指南的保姆级多人协作实现法/  过程规范：000 手册与 001 指南
proposals/     现行施工计划、未来提案与 archive 历史记录（用户管理）
万象体系/     投资人材料（DOC.html、PPT.html）
scripts/       构建与少量仓库检查
dist/          最终编译输出，不提交
artifacts/     中间产物与本地发布产物，不提交
.github/       CI workflows
```

- 生产 F# 唯一根：`src/Wanxiangshu/`
- 规范导航 [requirements/README.md](requirements/README.md)；历史 Clause 与变更工作流已归档（2026-08-14 cutover；git 历史可回溯）
- 施工与提案导航 [proposals/README.md](proposals/README.md)；旧计划与已结束批次见[归档索引](proposals/archive/README.md)
- 测试全部包自有：`requirements/<package>/tests/`；共享 harness 在 `requirements/verification-system/tests/`（含 `support/`、unit runner、integration orchestrator、Long Stroke e2e）
- 脚本：`scripts/build.mjs`、`scripts/check.mjs`、`scripts/checks/*`、`scripts/lib/walk.mjs`

### 开发环境

Node.js ≥ 20，`npm@11.12.1`；.NET SDK（`global.json`）；本地工具 `.config/dotnet-tools.json`（Fable、Fantomas）。

### 首次设置

```bash
npm ci
dotnet tool restore
npm run format-build-test
```

请用 `npm ci`。`bun-pty` 经 `overrides` 固定（见 `package.json` / [AGENTS.md](AGENTS.md)）。

### 常用命令

验证入口只有两个：

```bash
npm ci
dotnet tool restore
npm run format-build-test   # 日常验证
npm run verify:release      # 发布验证
```

| 命令 | 作用 |
|------|------|
| `npm run format-build-test` | 日常验证（入口 `node scripts/verify.mjs`）：Fantomas 检查（`format:check`）→ `check` → 编译 → unit → integration（warmup 与 distribution package 子步骤随 integration 调度） |
| `npm run verify:release` | 发布验证：在日常阶梯基础上追加 clean build（`--clean`）、Long Stroke e2e（`tests/014.test.mjs`）与真实 package 校验 |

辅助命令（不是验证入口）：

| 命令 | 作用 |
|------|------|
| `npm run format` | Fantomas 写盘（与 `format:check` 的相对面：一个改文件，一个只判失败） |
| `node scripts/build.mjs --plan` | 只读计划报告：`mode`/`reason`/`changedInputs`/`selectedShards`/`compileItems`/`fableCompileInvocations`，不写 `dist/` |
| `node scripts/derive-envelope.mjs` | 手动派生 loop detector envelope，更新入库产物 `resources/degeneration-guard/envelope/LoopDetectorEnvelope.js`。构建不自动派生，把入库产物复制到 `dist/Execution/Session/LoopDetectorEnvelope.js`；入库产物缺失时构建会提示运行本命令 |

### 测试分层

语义命题按 Pure laws → Temporal → Adapter → Long Stroke 四层逐级证明（WHAT[verification-system-001]、[003]）。日常入口 format-build-test 调度编译与 unit/integration；Long Stroke 与真实 package 校验由发布入口 verify:release 额外调度。

| 层 | 入口 | 范围 |
|----|------|------|
| unit | `requirements/verification-system/tests/run.mjs` | 对 `dist/` 的契约；经 `requirements/verification-system/tests/support/` |
| integration | `requirements/verification-system/tests/integration/run.mjs` | resources、plugin、persist、strength、package、harness（用例经 tier-gate 门控并入各包顶级 `tests/NNN.test.mjs`） |
| e2e（Long Stroke） | `requirements/verification-system/tests/014.test.mjs` | `scenarios/long-stroke.toml` + `support/` oracles；单次连续生命周期 |

`dist/` 陈旧时 unit 拒绝运行。资源路径由包内 `dist/` 相对定位到 `resources/`，不依赖 `process.cwd()`。

### 规范与文档体系

规范是万象术的语义根：每条行为命题有稳定 ID、测试落点和 owner 包。规范不跟踪实现进度，只定义正确性。

- **规范**：`requirements/<package>/`（56 包 normative 树，另 2 个历史包；必备 WHY.md、WHAT.md 与 tests/；WHAT 命题 ID 稳定寻址，条款与测试文件一一映射，覆盖缺口见 [requirements/GAP.md](requirements/GAP.md)；含 `planning`（planning-001~019）：Plan 角色的接力式规划——三阶段交接、任期隔离、崩溃恢复）。
- **历史 Clause 与变更记录**：2026-08-14 cutover 已归档（含 Kolmogorov 工程纪律与 completed change 考古；git 历史可回溯）。
- 测试全部包自有（`requirements/<package>/tests/`），直接引用 WHAT 命题 ID。规范不跟踪实现进度。

导航：[requirements/README.md](requirements/README.md)。治理见 [AGENTS.md](AGENTS.md) 与归档文档。

### 运行时资源

```text
resources/provider/
  world/common-law/{en,zh-CN}.md
  role/<role>/{en,zh-CN}.md
  tool/<tool>/{en,zh-CN}.md
  delegation/<scenario>/{en,zh-CN}.md
  host/<guideline>/{en,zh-CN}.md
  lifecycle/<phase>/{en,zh-CN}.md
  runtime/<scenario>/{en,zh-CN}.md
  library/<office>/{en,zh-CN}.md
  casebook/<step>/{en,zh-CN}.md
  attention-regulation/<entry>/{en,zh-CN}.md
  concern-routing/<entry>/{en,zh-CN}.md
  README.md
resources/ablation/{fact-map,nodes,profiles,tool-map}.json
resources/enforcer/<TipName>/{enforcer,main}{,.zh-CN}.md
resources/git/wanxiang-hook.mjs
resources/wanxiangshu.mjs
```

加载：`Resources/`（`PackageResources`、`ProviderResources`、`PromptResources`、`EnforcerCatalogResource`、`RuntimeResources`）；插件初始化 load/install 一次。
旧 `resources/prompts/*-system.md` 已删除；生产 system 仅由 Common Law → Role Law → Office Library 组成。

### 构建与打包

- **构建**：`scripts/build.mjs`（增量：按输入摘要判定 no-op / focused / full / clean 四模式，plan 与 run 共用判定；非源码输入或工具链变化进入 full 编译但不清空 `dist/`；仅源图删除、重命名或显式 `--clean` 时清空输出目录后重建；其余情况按受影响分片增量聚焦编译；随后把入库的 loop detector envelope 复制到 `dist/` 并校验入口与资源）。除这一处 runtime import 产物外，不把 `resources/` 复制进 `dist/`。
- **打包**：仓库根 `npm pack`（或 `--pack-destination artifacts/package`）。tarball = `dist/` + `resources/` + metadata（`package.json`、`README.md`、`LICENSE`）。不得含 `src/`、`requirements/`、`scripts/`、`artifacts/`。

发布预检：`npm run verify:release`（干净工作树；验证日志默认写 `.fable-build/verify-logs/`，已被 .gitignore 忽略；CI 工作流 `.github/workflows/ci.yml` 运行同一命令，但无 artifact 上传，runner 结束后只剩余作业控制台输出）。

### 提交要求

1. 源码变更后运行 `npm run format-build-test`。
2. 优先 stage 具体路径；保留 hooks；不用 `--no-verify`。
3. 用户可见变化写入 [CHANGELOG.md](CHANGELOG.md)。
4. 不推送对 `main`/`master` 的破坏性历史改写；force push 等需显式许可。

### 发布

```bash
npm ci
dotnet tool restore
npm run verify:release
npm pack --pack-destination artifacts/package
```

Git 工作树须干净。验证输出留存本地 `.fable-build/verify-logs/` 或作为发布附件，不提交进仓库。

### 安全与保密

源码与内部脚本默认不进 tarball。勿提交密钥与私有 registry 凭证。漏洞与授权走版权方私有渠道。

### 许可证

专有商业软件。见 [LICENSE](LICENSE)。`private: true`；分发受 LICENSE 与商业合同约束。

更多：[requirements/README.md](requirements/README.md) · [计划与提案](proposals/README.md) · [历史归档](proposals/archive/README.md) · [CHANGELOG.md](CHANGELOG.md) · [LICENSE](LICENSE) · [AGENTS.md](AGENTS.md)
