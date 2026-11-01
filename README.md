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

## 架构与演进方向

万象术当前以 **OpenCode 插件**形式落地。它不替换 Host 的对话模型，而是在其上叠加结构化编排能力：通过多角色分工（Orchestrator、Manager、Engineer、DevOps 等）、任务派发与收敛（Fission）、沙箱化编程面（todowrite）以及基于事件日志的因果持久化，使多智能体可靠协作。

产品遵循唯一实在的**总流程**：**用户打开软件，提出需求，系统完成调查、修改与测试，帮用户把事情做成并结束。**

系统正在依照《纠偏令》向清晰的三层分工演进：
1. **本仓产品面（wanxiangshu）**：承载提示词、工具资源与顶层组装，逐步减负，最终不保留程序状态。
2. **万象通（wanxiangtong）**：负责抹平宿主（OpenCode 等）怪癖与翻译适配（先作为外层子目录规划，当前部分适配代码仍在宿主仓源码中，标为【程序待改】）。
3. **万象理（wanxiangli）**：中立内核（Git submodule），遵循 Smalltalk / Actor 模型与 A-B-C 组合原则，状态自治，不认识具体宿主。

---

## 用户指南

使用入门见 [功能导览](docs/功能导览.md)；运行与人工核验见 [人工巡检方法](docs/人工巡检.md)；技术待办见 [程序待改清单](requirements/GAP.md)。

### 产品形式与入口

万象术作为 OpenCode 插件加载后，为 Host 会话提供多角色协作、任务分叉与汇合、审阅与恢复。公开入口：

```text
import "wanxiangshu"
→ dist/OpenCode/Plugin/Plugin.js
```

`package.json` 的 `main` / `exports["."]` 指向同一路径。构建产物中，本仓自身将 `dist/` 与 `resources/` 列入 files；依赖的独立 `wanxiangli` 以 bundled npm dependency 进入最终 tarball，不从宿主机器环境解析未定义源码。

### 系统要求

- **Node.js**：`>= 22.13.0`（`engines.node`；使用内建 `node:sqlite`）
- **OpenCode Host**：peer 依赖 `@opencode-ai/plugin`（`>= 1.17.4`）
- **源码构建**：.NET SDK（见 `global.json`）与 `dotnet tool restore`（Fable、Fantomas）

`packageManager` = `npm@11.12.1`。安装依赖使用 `npm ci`（已提交 `package-lock.json`）。

`wanxiangli/` 是正式 Git submodule，跟踪其 `main`；npm 依赖为 `file:wanxiangli`。开发初始化需执行：
```bash
git submodule update --init -- wanxiangli
npm ci
```

更新子模块依赖执行 `npm run deps:update`（只允许 fast-forward 到 `origin/main`）。任何 `.tgz` 都只能是临时构建产物，永远不得提交进入 Git。

### 本机状态目录

运行数据统一位于主工作树的 `.Herdchestra/`；同一仓库的 linked worktree 通过 Git 的只读拓扑查询共用该状态。非 Git 工作区直接使用工作区根下的 `.Herdchestra/`。

- 业务状态按职责组织于 `.Herdchestra/wanxiangshu/`；
- 诊断信息位于 `.Herdchestra/diagnostics/`；
- 目录内自动创建 `.gitignore` 排除全部运行数据。

旧 `.git/wanxiang`、`.git/wanxiangshu`、`refs/wanxiang/*` 为历史退役路径，不读取、不迁入、不作为新历史的恢复来源。

### 获取与安装

专有软件，从本地 tarball 或私有 registry 安装：

```bash
npm install ./wanxiangshu-0.9.0.tgz
# 或
npm install wanxiangshu --registry=<私有源URL>
```

### 常用命令

| 命令 | 作用 |
|------|------|
| `node scripts/build.mjs` | 官方构建脚本：自动增量编译 wanxiangli 及宿主插件 |
| `npm run format-build-test` | 提交前检查：代码格式、增量构建与日常自保测试 |
| `npm run verify:release` | 发布验证：clean build、端到端与安装包完整性校验 |
| `npm run format` | Fantomas 代码格式化写盘 |

编写、构建或优化测试时，阅读[测试规则](requirements/verification-system/AXIOMS.md)；测试入口与设施见[测试说明](requirements/verification-system/tests/README.md)。

### 文档与规范索引

- **功能导览**：[docs/功能导览.md](docs/功能导览.md)
- **人工巡检方法**：[docs/人工巡检.md](docs/人工巡检.md)
- **功能说明书目录**：[requirements/README.md](requirements/README.md)（包清单与写法；分组索引见 [INDEX.md](requirements/INDEX.md)）
- **程序待改清单**：[requirements/GAP.md](requirements/GAP.md)
- **计划与提案**：[proposals/README.md](proposals/README.md)
- **根目录纠偏令**：[AGENTS.md](AGENTS.md)
- **万象理总宪法**：[wanxiangli/AGENTS.md](wanxiangli/AGENTS.md)

### 许可证与保密

专有商业软件。使用、复制与分发受 [LICENSE](LICENSE) 约束。
