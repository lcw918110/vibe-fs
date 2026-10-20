[Apply ASD-STE100 to source code, Mise-en-place to code design. Adapt Gall’s Law, Chesterton’s Fence, and Poka-yoke. Invoke Goodhart’s Law and Via Negativa when needed. Treat these principles as Wittgenstein’s Ladder. Build the real thing, not its imitation. Try to falsify it.] [For natural-language discussion, follow Fei Xiaotong’s plain precision and Kunihiko Kodaira’s quiet clarity. Avoid officialese, performative rhetoric, translationese, and textbook cadence. Let the subject shape the prose.]

## 工具调用纪律

- 只要动作彼此独立，就尽量并行。
- 精准修改局部代码，拒绝无谓的全量重写。

## 架构与编码风格

### 源码与运行数据分离

- `wanxiangli/` 是正式 Git submodule，跟踪 `main`。依赖升级以远端最新 `main` 为目标，只 fast-forward、不覆盖本地工作；提交宿主时记录确切 gitlink。开发更新与运行程序分开，运行时不得自行拉取或切换源码。
- npm 通过 `file:wanxiangli` 消费公开入口，构建使用该源码版本。任何 `.tgz` 二进制包不得加入 Git，不因体积小、方便本地安装或发行验收而破例。临时产包只允许写到忽略或临时目录。
- 全部项目运行数据统一放在主工作树的 `.Herdchestra/`，linked worktree 共用这份本机状态。Git 仅用于源码管理及只读拓扑查询；应用不得把日志、SQLite、快照、诊断、锁或 SSH 包装写进 `.git`，也不得安装数据同步 hooks/refspec。
- 旧 `.git/wanxiang*` 数据与运行状态 refs 已退役，不读取、不迁移、不作为恢复候选。当前不需要跨机持久化。清理只针对已确认的应用专属目录、配置和 refs，不清空 Git 的 objects、代码分支、标签或其他人的 hooks；历史改写和物理 GC 不是默认清理动作。

对应合同：`requirements/durable-events/WHAT.md` 的本机状态切换条款。禁止以删验收或伪造构建回执掩盖源码依赖尚未准备好。

### 长程委托的连续性

- 公理：使用者委托的是目标的完成，不是一条回复的结束。目标尚未完成时，长程工作不能静默停止；有下一步就继续，遇到阻塞就明确呈现，不能靠用户再次催促维持推进。
- 推论：provider 的自然 stop、空输出和进度小结都是物理回合的边界，不是业务完成证据。Manager 与 Plan 必须通过既有 `deliver` 形成有效交接或完成；否则自然停机应触发当前任务的 nudge，并说明尚未解除的义务。
- 显式等待：Plan 真正缺少用户决定或信息时使用 `ask(question)`（Manager 禁用 `ask`）。工具只返回询问已发出的占位回执，系统中断本次 attempt、保留未完成委托；新的真实用户输入到来后接续。`devops(intent)` 负责原 Plan 的固定 DevOps 调查，`join` 负责 Manager 临时等待后台工作，二者都不证明任务结束。
- 边界：禁止模型自行放弃，不等于禁止用户取消、强迫重试基础设施故障或复活已退休者。用户明确取消、已证明的交接/完成、显式提问等待和可见的运行故障分别处理；不能把失败或未知伪造成成功，也不能把 `ask` 的暂停当作逻辑运行关闭。

对应合同：`requirements/work/WHAT.md` [027]、[028]、[049]。测试必须覆盖自然停机、提问与真实回复、重复及迟到回执；仅改提示词不能替代终态准入规则。

- 崇尚朴素、简洁、直白的架构，去除不必要的过度设计，拒绝复杂的错误包装、日志堆砌和配置层级。
- 除非绝对必要，不写多余注释。不搞糊弄过关的临时补丁，想清楚再动手。
- 避免冗余的中间赋值，边界清晰即可，不引起阅读负担。
- 严禁通过一行写多件事或滥用分号来伪造行数减少。
- 变量命名务求明白晓畅，绝不用晦涩名字和让人费解的缩写。
- 重构时痛快甩掉历史包袱，不滥用外观模式（facade）逃避清理；不保留已废止的代码，不合理之处随时与下游协同修改。
- 精准实现，不搞看似保险其实谁也说不清原理的兜底实现。

## 具体工作与流程

- 严禁使用 `dotnet build`。本仓只有 Fable 编译目标，构建必须使用 `node scripts/build.mjs` 或 `npm run format-build-test`，严禁引入或依赖 .NET 编译构建。
- 宁慢且稳，严禁使用自动化程序批量增删改查程序代码。手工编辑，步步为营。慢即是快。

1. 工作流程
  - 普通小型修复、重构与测试补充不要求创建 Change；在单次提交内原子闭环。
  - 基本闭环：弄清为什么做（why）→ 明确要做成什么样（what）→ 阅读相关规范理解依据 → 调整与补充测试 → 落实实现（how）→ 检查所有相关测试全绿 → 闭环结束。
  - 按施工计划认领的工作，每个工作包完成后同步更新计划状态、验收证据与剩余边界；不能只在聊天中报告完成，也不能把局部证明写成整个 TODO 或 GAP 已闭合。
2. 两种典型失败
  - 写完代码才想起看文档：代码已经按旧想法写死，要么彻底返工，要么硬把旧语义塞进系统，导致代码与规范越走越远。
  - 陷在局部细节里丢了大局：虽然修好了表面报错，却违背了整体规则（例如只给旧类型打补丁加字段、加适配器凑合，合起来就是在维护混乱）。
3. 交付门禁
  - 涉及行为、持久化、Host/provider、Git 或跨包边界时，跑对应 requirement 测试套件，尽量不全量漫跑。
  - 测试文件按 WHAT 条款编号统一命名为 `NNN.test.mjs`；需求与测试的映射由测试用例标题中的 `WHAT[PREFIX-NNN]` 锚点定义。
  - 提交前确认没有残留的临时文件、调试打印、旧路径残骸或未跟踪的生成物。
4. 修改纪律
  - 工作区可能包含用户的未提交改动。动手前先看 `git status` 与对应 diff，妥善保留无关改动。
  - 自动 git commit 并推送到 `master`；严禁对 `master` 执行 force push。提交成功、验证通过、已部署运行分别报告，不相互代替。

**未闭合**：release 层 e2e（Long Stroke）仍红——编排器第三轮后停住，manager / engineer 的 lane 期望未发出；回退本轮清理后停点不变，属既有欠账（pre-cutover 基线的 shard 覆盖本身不闭合），Long Stroke 场景尚未与新的接力/工作记录语义对齐。
