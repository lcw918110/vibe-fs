本文件只规定 Agent 如何查找规范、修改仓库和验证交付。产品语义只由 `requirements/<package>/` 定义。本文件引用条款，不复述条款。

## Kolmogorov 原则与工程手艺

写代码如同造器物。器物好用，是因为结构清楚、分工明白、没有多余的摆设。好代码每一行都在解决实际问题：名字说的是实在的事物，分支对应实际的业务情况，类型则把不合规的差错挡在门外。

- **追求简单明了**：如果一个函数或文件写得太长、太绕，通常不是业务本身真有那么深奥，而是塞进了太多多余的样板、框架礼仪和绕圈子的抽象。好设计把不可减少的业务说清楚，不多费读者一句口舌。
- **划清边界，不乱套用**：两处代码长得像，不等于就是同一种事物。生命周期不同的概念，即使眼下字段一样，也要各自分开。同一个用户在登录、账单、权限和展示里关注的信息完全不同，在各自的语境里只传真正需要的数据，不要试图造一个无所不包的“大万能对象”。
- **用类型系统把关**：字符、数字、布尔值最容易混淆。订单号、用户号如果都写成普通的字符串，编译器就分不清。给独立的领域概念各自起好类型名字，运行时代价极低，却能让编译器在门前把关。有限的状态就用明确的联合类型列出来，不靠一堆布尔开关在运行时互相猜。
- **纯计算与外部操作分开**：纯函数不读时钟、不掷骰子、不查网络、不写磁盘，同样的输入一定给同样的输出。这部分最容易测试，最让人踏实。把读写文件、网络通信和时序操作留在薄薄的外壳层，由外壳负责调度和持久化。
- **分清意图与事实**：用户命令是“想要做什么”，系统检查规则，通不过就拒绝；事件则是“已经做成的事实”，发生了就不能赖掉。银行靠流水算出余额，系统靠不可篡改的事件还原历史。不要直接在原字段上覆盖涂改旧数据，历史需要诚实追加。
- **围绕所有权处理并发**：并发最怕的是多方同时改同一块状态。让每个处理单元拥有自己的状态，外界只发消息，内部按序处理，就不需要到处上锁。
- **持久化诚实可靠**：收到请求先落盘，写入成功后再更新内存，不能让内存看见没有证据的未来。文本日志一行一个自包含记录，追加只在末尾，恢复时按序重放。
- **追查根因，不做糊弄修补**：工具报错和测试失败是线索，不是噪音。动手改代码前，先顺着调用链摸清原委，找到真正管事的模块和破坏的不变量。消除错误靠因果解释和正式回归测试，不靠猜谜式试错。
- **文档与测试就是手艺的记忆**：踩过的坑若不记下来，以后还会掉进去。改动涉及协议边界时，必须补上正式的自动化回归测试。随手在控制台打印、跑完即删的临时脚本不算验证。
- **名字是给人读的**：名字要反映代码所依赖的真实概念。不用让人猜谜的生僻缩写，不用故弄玄虚的数学黑话，注释只留给真正隐晦难明之处，不写重复代码字面意思的废话。
- **测试要有说服力**：先写能证明问题的失败测试，再把代码改对。测试断言的是对外的公开行为，不是内部具体先调哪个辅助函数。不为图省事而削弱断言，不靠碰运气重跑，也不靠放大超时来掩盖时序漏洞。
- **范围克制，不留尾巴**：把托付的事改得清清爽爽。不以顺手为由重构无关模块，但也不在改动范围里留下废弃代码、调试打印和未清理的死分支。

## 思考与输出

说话与写作要直截了当，不端官架子，不搞仪式感表演。凡事拿不准时，怎么简单明白就怎么来。
不要搞生硬的翻译腔和居高临下的教材腔，文字多向费孝通先生的直白精准、从小处着眼，以及小平邦彦先生的自然顺畅靠拢。

## 工具调用纪律

- 只要动作彼此独立，就尽量并行读取、调查与验证。并发是为了提高效率、理清因果，不是为了盲目追求工具数量。
- 在同一文件上有重叠的编辑、存在先后依赖的修改，或者依赖上一条命令结果的执行，必须串行完成。
- 跨文件修改先判断依赖关系：修改公共类型或接口时，先定好契约边界，再调整调用方，最后清理旧路径。
- 精准修改局部代码，拒绝无谓的全量重写。
- 把大诉求拆成细小、明确的独立步骤，避免长时停滞。

## 架构与编码风格

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
  - 自动提交 git commit 并推送到 `master` 分支；严禁对 `master` 执行 force push。
# 000 — 实现开发者意见的全流程操作手册

- 对象：仓库根 `用于人工审订的当前语义指南.md` 中全部 `> {开发者…}` 意见。共 42 条。行号：45、56、84、109、153、175、207、264、381、388、402、416、458、465、469、487、494、499、503、509、600、601、602、627、639、662、668、715、724、731、732、733、740、741、742、748、808、825、840、846、853、863。
- 目的：给多人协作施工提供完整流程。每条意见一张工作包卡。无遗漏。
- 边界：本文不是产品规范。产品语义只由 `requirements/<包>/WHAT.md` 定义。冲突时，以规范与源码为准，并修正本文。
- 基线：`6f4a1a99a`。写作时工作树干净。文中行号与锚点按此基线。动工前必须重读当前源码。
- 状态：施工完成（2026-10-10）。逐条落实状态见 §4「状态」列；指南意见块已同步标注。本文只写「怎么做」，不写「做了什么」。

---

## §0 记号与写作规则

### 0.1 Knuth 风格伪代码

本文按 TAOCP 惯例书写。

- 算法头：`Algorithm <字母>（<题目>）。输入…。输出…。`
- 步骤标号：字母加数字，如 `M1.`。子步：`M1.1.`。
- 步骤名放在方括号内。例：`M3. [写红测试.]`
- 一句一个动作。句尾用句号。
- 赋值用 `←`。比较用 `＝`、`≠`。
- 条件句：`若…，则…。`
- 跳转：`转 M5.`
- 循环：`循环…，直到…。`
- 结束：`算法终止。` 或 `返回…。`
- 圆括号内是说明。`#` 开头是断言。
- `▷` 是裁决点。裁决必须留下书面结论与证据。
- 步骤标号在算法内有效。§3 的算法是全局算法。§6 每张卡自带算法，标号只在该卡内有效。

### 0.2 ASD-STE100 风格中文

- 短句。一句一事。主动语态。
- 同一概念用同一个词。不换同义词。
- 名词固定：工作包、波次、裁决、红测试、绿、套件、门禁、锚点、集成人、施工人、评审人、台账、卡。
- 动词固定：读、写、跑、改、删、提交、推送、记录。
- 不写比喻。不写官腔。不写无信息的过渡句。

### 0.3 术语表

| 词 | 含义 |
|---|---|
| 工作包 | 一条意见对应的一次施工。编号 `WP-NNN`。 |
| 卡 | 工作包卡。见 §6。 |
| 波次 | 一批工作包。编号 `W0`…`W6`。 |
| 裁决 | 先定「保留 / 修改 / 删除」的调查。 |
| 红测试 | 按新合同先失败的测试。 |
| 套件 | `requirements/<包>/tests/` 下的全部 `NNN.test.mjs`。 |
| 门禁 | `scripts/check.mjs` 的静态检查。 |
| 锚点 | 源码或文档位置。格式 `<相对路径> · <符号>`。 |
| 台账 | §4 的 42 行登记表。 |
| 集成人 | 管 `master`、管共享文件的人。 |
| 施工人 | 工作包负责人。 |
| 评审人 | 独立复核人。 |

---

## §1 全局不变量

I1. 权威单一。产品语义只由 `requirements/<包>/WHAT.md` 定义。计划、提案、指南、本文都不增加义务。

I2. 规范先行。改代码前先读规范。合同变化时，先改 `WHAT.md`，再改代码与测试。

I3. 红先绿后。行为变化先有一条按新合同失败的测试。禁止先改代码再补测试。

I4. 手工编辑。禁止用自动化程序批量增删改源码。

I5. 原子闭环。一个工作包一次提交。提交内规范、代码、测试三者一致。

I6. 证据分级。实跑过的写「通过」。未跑过的写「未验证」。禁止把「预计红 / 绿」写成证据。

I7. 保护用户改动。动工前读 `git status` 与相关 diff。无关改动一律保留。

I8. 推送纪律。自动提交并推送到 `master`。严禁 force push。推送前先 `pull --rebase`。

I9. 不用 fail-closed 掩盖测试不足。新增门禁前，先写能红的事实。

I10. 先读后改字节合同。`prefix-stability`、`host-boundary`、`provider-projection` 有字节级合同。改动前先读条款与测试。

I11. 不削弱断言。不放大超时。不重跑撞绿。

I12. 构建只用 `node scripts/build.mjs` 或 `npm run format-build-test`。严禁 `dotnet build`。

---

## §2 主流程

```
Algorithm M（实现 42 条意见）。输入：本仓库与指南文档。输出：全部工作包闭环。

M1. [立项.] 执行算法 L。
M2. [裁决.] 对每一张「裁决」型卡执行算法 J。
M3. [逐波施工.] 令 w ← 0。循环：执行算法 W(w)；令 w ← w＋1；直到 w ＝ 7。
M4. [收尾.] 执行算法 F。
M5. [终止.] 算法终止。
```

---

## §3 子算法

### 3.1 立项

```
Algorithm L（立项）。输入：《用于人工审订的当前语义指南.md》。输出：台账。

L1. [取意见.] 取全部以 `> {开发者` 开头的行。按行号升序。
L2. [建卡.] 每条意见建一张卡。编号从 WP-001 起，按行号升序。
L3. [记要素.] 记行号、原文、锚点、规范包、验收。
L4. [定类型.] 类型 ∈ {实施, 裁决, 核对, 删除}。
L5. [定波次.] 按 §5 波次表填波次。
L6. [校验.] 计数。若 ≠ 42，则停并报错。
L7. [返回.] 返回台账。算法终止。
```

### 3.2 裁决

```
Algorithm J（裁决）。输入：一张「裁决」型卡。输出：裁决记录。卡可改型。

J1. [采证.] 读源码、规范、测试、git 历史。记录证据行号。
J2. [三问.] 问三句。现状防住哪类真实错误？去除后哪条 WHAT 条款失守？有无更简单实现？
J3. [定论.] 结论 ∈ {保留, 修改, 删除}。写理由。理由必须引证据。
J4. [记录.] 按附录 A 写讨论记录。落盘 `proposals/`。
J5. [分流.] 若「保留」，则关闭卡，转 J7。若「修改」或「删除」，则改卡类型，转 J6。
J6. [转实现.] 转算法 R。
J7. [终止.] 算法终止。
```

### 3.3 实现循环

```
Algorithm R（实现循环）。输入：一张卡。输出：新合同下的绿测试与提交。

R1. [读.] 读该卡与相关 WHY/WHAT。列出要改的条款。
R2. [改条款.] 改写条款。写明触发、允许后果、禁止后果、失败后果。
R3. [同步导航.] 改 `requirements/INDEX.md`。若包增删，改 `requirements/GAP.md`。
R4. [写红测试.] 写或改用例。文件名 `NNN.test.mjs`。标题含 `WHAT[<包>-NNN]` 锚点。
R5. [跑红.] 运行该用例。确认失败。确认失败原因与本卡相关。
R6. [改实现.] 改代码。只改卡内文件。复用既有模式。
R7. [删旧.] 删旧分支、旧常量、旧资源、旧注册。不留兼容层。
R8. [验证.] 执行算法 V。
R9. [关闭.] 执行算法 C。
R10. [终止.] 算法终止。
```

### 3.4 验证

```
Algorithm V（验证）。输入：卡与变更。输出：证据记录。

V1. [跑包套件.] 运行该包套件。
V2. [跑共享套件.] 运行 §7 验收矩阵中该波的相关套件。
V3. [跑门禁.] 运行 `node scripts/check.mjs`。
V4. [跑编译.] 运行 `node scripts/build.mjs`。
V5. [记录.] 记命令、通过数、失败数、跳过数、TODO 数。
V6. [分支.] 若全绿，则返回。若有失败，则查根因、修复、转 V1。
```

### 3.5 关闭

```
Algorithm C（关闭）。输入：卡与证据。输出：提交与推送。

C1. [清残留.] 删临时文件与调试打印。查未跟踪生成物。
C2. [提交.] 提交信息含卡号与行号。格式 `<type>(<范围>): <一句话> (WP-NNN, Lnn)`。
C3. [记账.] 在 `proposals/` 的记录里写：完成范围、证据、剩余边界。
C4. [同步远端.] 运行 `git pull --rebase`。运行 `git push`。
C5. [分支.] 若推送失败，则保留提交、记原因、通知集成人。返回。
```

### 3.6 波次

```
Algorithm W（波次）。输入：波次号 w。输出：该波完成，或被阻塞卡的清单。

W1. [开波会.] 集成人宣读目标、卡清单、冲突面、顺序约束。
W2. [认领.] 每人认领一卡。同一文件只有一个负责人。
W3. [施工.] 对每张已认领卡执行算法 R。循环，直到全部完成或阻塞。
W4. [全波验收.] 集成人运行 §7 矩阵第 w 行的全部套件。
W5. [分支.] 若失败，则退回相应卡，转 W3。若通过，则返回。
```

### 3.7 冲突处置

```
Algorithm X（处理冲突）。输入：一条冲突。输出：处置。

X1. [文件冲突.] 按 §5 冲突面排序。后动者先运行 `git pull --rebase`。
X2. [条款冲突.] 先合并裁决。再施工。禁止各自改测试。
X3. [语义冲突.] 以 `WHAT.md` 为准。
X4. [用户改动.] 保留。不动。
X5. [终止.] 返回。
```

### 3.8 收尾

```
Algorithm F（收尾）。输入：全部已关闭的卡。输出：文档与仓库同步。

F1. [同步指南.] 改《用于人工审订的当前语义指南.md》。已落实的意见块标注 WP 编号。
F2. [同步 README.] 改仓库结构、命令、环境变量表。
F3. [同步 CHANGELOG.] 写 `Unreleased` 条目。逐波一句。
F4. [同步索引.] 改 `requirements/INDEX.md` 与 `requirements/GAP.md`。
F5. [同步消融.] 改 `resources/ablation/nodes.json` 与 `tool-map.json`。
F6. [全量验证.] 运行 `npm run format-build-test`。运行 `npm run verify:release`。
F7. [清残留.] 查临时文件、旧路径、未跟踪生成物。
F8. [终止.] 算法终止。
```

---

## §4 意见台账（42 条全覆盖）

| WP | 行 | 主题 | 类型 | 波次 | 规范包 | 状态 |
|---|---|---|---|---|---|---|
| 001 | 45 | 路径统一 `.git/wanxiangshu/…` | 裁决 → 实施 | W1 | durable-events | 已落实（`d5f0c9366`） |
| 002 | 56 | 会话准入简化评估 | 裁决 | W0 | managed-chat-execution | 裁决保留 |
| 003 | 84 | 漂移校验退役，转 fast-check | 实施 | W2 | execution-model-routing | 已落实（`5fcb495b3`） |
| 004 | 109 | `messages.transform` 洋葱式 | 重构 | W5 | provider-projection | 已落实（`2575c5baa`） |
| 005 | 153 | `tool.definition` 洋葱式 + DRY | 重构 | W5 | host-boundary | 已落实（`1d38aa294`） |
| 006 | 175 | `tool.execute` 洋葱式 + DRY | 重构 | W5 | host-boundary | 已落实（`d2bf95fcf`） |
| 007 | 207 | 工具面洋葱式 + DRY | 重构 | W5 | capability-enforcement | 已落实（`b2d0ff36b`） |
| 008 | 264 | 事件与调和 KISS 评估 | 裁决 | W0 | host-boundary | 裁决保留 |
| 009 | 381 | fork 的 calling 可选 | 实施 | W3 | delegation | 已落实（`e20725307`） |
| 010 | 388 | keyword / attach 必要性 | 裁决 | W0 | delegation | 裁决保留 |
| 011 | 402 | commission 的 calling 可选 | 实施 | W3 | delegation | 已落实（`e20725307`） |
| 012 | 416 | 底层 id 概念去除 | 裁决 → 实施 | W3 | participant-horizon | 已落实（`9e087469d`；可见面核对零改动） |
| 013 | 458 | Road 概念评估 | 裁决 | W0 | relay-incumbency | 裁决保留 |
| 014 | 465 | 评审对象重定义 | 实施 | W4 | relay-assessment | 已落实（`30f53445e`） |
| 015 | 469 | review 返回 pairs，空集终止 | 实施 | W4 | relay-assessment | 已落实（`30f53445e`） |
| 016 | 487 | 最后一任收尾 | 实施 | W4 | relay-assessment | 已落实（`7ebe9e749`） |
| 017 | 494 | 快照机制去除 | 裁决 → 实施 | W4 | relay-assessment | 已落实（`694750f00`） |
| 018 | 499 | 两次 suicide | 实施 | W4 | relay-retirement | 已落实（`694750f00`） |
| 019 | 503 | 证书去形式主义 | 裁决 → 实施 | W4 | relay-assessment | 已落实（`694750f00`） |
| 020 | 509 | 重启状态指导 prepend | 实施 | W2 | crash-reconciliation | 已落实（`6e5cbd442`） |
| 021 | 600 | ndjson 内嵌 payload | 实施 | W1 | durable-events | 已落实（`734a6bce0`） |
| 022 | 601 | UTC 日期分组 GC | 实施 | W1 | durable-events | 已落实（`fdfab580d`） |
| 023 | 602 | 性能问题排查 | 裁决 → 实施 | W1 | durable-events | 已落实（`a0d3344c5`；数字对比未验证） |
| 024 | 627 | git hook 修复后重开 | 实施 | W1 | durable-convergence | 已落实（`cda78e0d2`） |
| 025 | 639 | horizon 泄露重置 | 裁决 → 实施 | W2 | crash-reconciliation | 已落实（`c9be6e3c7`） |
| 026 | 662 | 「证据」措辞清理 | 实施 | W2 | provider 文本面（多包） | 已落实（`d36d41b82`） |
| 027 | 668 | 失败恢复优雅化 | 实施 | W2 | execution-failure-policy | 已落实（`5faaf81ea`） |
| 028 | 715 | assume 不被 LWR 替换（核对） | 核对 | W2 | context-compression | 已落实（`ecdd82d7a`） |
| 029 | 724 | enough / abandon 合入 assume | 实施 | W3 | attention-regulation | 已落实（`b15a3d01a`） |
| 030 | 731 | defer 新语义 | 实施 | W3 | attention-regulation | 已落实（`b15a3d01a`） |
| 031 | 732 | orchestrator 的 defer | 实施 | W3 | attention-regulation | 已落实（`b15a3d01a`） |
| 032 | 733 | Pair Hint 鼓励 defer | 实施 | W3 | cognitive-environment | 已落实（`b15a3d01a`） |
| 033 | 740 | 自动 subscribe 自身名字 | 实施 | W3 | concern-routing | 已落实（`877b8b8ad`） |
| 034 | 741 | 只有 publish；user 弹窗 | 实施 | W3 | concern-routing | 已落实（`877b8b8ad`） |
| 035 | 742 | 向 LLM 解释邮箱语义 | 实施 | W3 | concern-routing | 已落实（`877b8b8ad`） |
| 036 | 748 | 删除 celebrate / regret | 实施 | W3 | institutional-learning | 已落实（`a66f204dc`） |
| 037 | 808 | 环境变量 KISS | 实施 | W6 | 多包（见卡） | 已落实（`b35ad48ea`） |
| 038 | 825 | 验证入口 KISS | 实施 | W6 | verification-system | 已落实（`b35ad48ea`） |
| 039 | 840 | Sphinx 恢复 | 裁决 → 实施 | W5 | sphinx-v2 | 已落实（`b97845a49`） |
| 040 | 846 | query-shell 丢弃 | 删除 | W5 | process-execution | 已落实（`c5109e6ae`） |
| 041 | 853 | CHANGELOG 遗骸丢弃 | 删除 | W5 | action-affordance | 已落实（`b35ad48ea`） |
| 042 | 863 | envelope 手动更新 | 实施 | W1 | degeneration-guard | 已落实（`c557749c5`） |

# 断言：本表 42 行。每行对应指南一条意见。

---

## §5 波次与顺序

### 5.1 波次表

| 波次 | 目标 | 卡 |
|---|---|---|
| W0 | 调查与裁决。不产出产品变更 | 002、008、010、012、013、017、019、023、039、042；加 001、014、015、033、034、036 的设计半段 |
| W1 | 持久化与构建工具链 | 001 → 021 → 022 → 023 → 024；042 独立 |
| W2 | Provider 投影与崩溃恢复 | 003、025 → 020、026、027、028 |
| W3 | 工具面与通信 | 036 → 029、030 → 031 → 032；033 → 034 → 035；009、011、012 |
| W4 | 任期、评审与退休 | 014 → 015 → {016、018} → {017、019} |
| W5 | 结构重构与遗骸 | 004 → 005 → 006 → 007；040、041、039 |
| W6 | 文档与全局收尾 | 037、038；算法 F |

### 5.2 顺序约束

S1. WP-001 → WP-021 → WP-022。先定路径。后做载体与 GC。

S2. WP-036 → WP-030 / WP-031 / WP-032。celebrate 删除改变 defer 的弹出载体。

S3. WP-014 → WP-015 → WP-016。WP-015 → WP-018。

S4. WP-017 与 WP-019 同一人。WP-016 之后动。

S5. WP-004 到 WP-007 在全部行为卡之后。重构不夹带行为变化。

S6. WP-039 在 W0 考古之后。

S7. WP-037、WP-038 最后做。

### 5.3 冲突面（同文件串行）

| 文件 | 相关卡 | 规则 |
|---|---|---|
| `src/Wanxiangshu/Persistence/EventStore/ProcessEventLog.fs` | 001、021、022、023 | 单人串行 |
| `src/Wanxiangshu/OpenCode/Plugin/PluginTransforms.fs` | 020、032、004 | 020、032 先；004 最后 |
| `src/Wanxiangshu/OpenCode/Tools/ToolRegistry.fs` | 029、036、040、007 | 行为先；重构最后 |
| `src/Wanxiangshu/Mission/Relay/**` | 014—019 | 全波单人 |
| `CHANGELOG.md`、`README.md` | 041、037、038、F1—F3 | 集成人统一改 |
| `requirements/INDEX.md`、`requirements/GAP.md` | 全部 | 集成人统一改 |

---

## §6 工作包卡

每张卡给：类型与波次、目标、现状锚点、算法、验收、依赖与风险。卡内算法标号只在本卡内有效。

### WP-001 · L45 · 路径统一

- 类型：裁决 → 实施。波次：W1。
- 目标：全部插件路径收敛到一个根名。
- 现状锚点：
  - 事件存储 `<git-common-dir>/wanxiang/{events,payloads}`：`src/Wanxiangshu/Persistence/EventStore/ProcessEventLog.fs · wanxiangDirectory / eventsDirectory / payloadsDirectory`（约 206-212）。
  - 运行时目录 `<git-common-dir>/wanxiangshu-next/runtimes`：`src/Wanxiangshu/Persistence/Journal/RuntimePath.fs · runtimeDirectory`（约 30）。回退 `XDG_STATE_HOME`，再回退 `~/.local/state/<sha256(workspace)>`。
  - 诊断目录 `<workspace>/.wanxiangshu/diagnostics`：`src/Wanxiangshu/Execution/Session/Wait/Bridge.fs`（约 169）。
  - hook 侧 `<common-dir>/wanxiang/ssh-*`、`ssh-command`：`src/Wanxiangshu/Git/Hook/Dispatcher.fs`（约 232-244）。
  - 同步缓存 `<common-dir>/wanxiang/sync-materialization-cache`：`src/Wanxiangshu/Persistence/EventStore/WriterStreamSync.fs`（约 93）。
  - 旧路径已被测试锁定为「不得读取」：`requirements/durable-events/tests/009.test.mjs`、`010.test.mjs`。

```
Algorithm P（路径统一）。输入：全仓路径字面量与消费者。输出：统一根名与新合同。

P1. [盘点.] 列出全部路径字面量。标出读点、写点、测试断言、文档引用。
P2. [裁决根名.] ▷ 定统一根名。建议 `<git-common-dir>/wanxiangshu/`。单独裁决诊断目录去留。
P3. [裁决迁移.] ▷ 定旧数据处理。建议：不读、不写、不自动迁移。历史材料走离线迁移
    （对齐 `durable-events-026`）。禁止双读。
P4. [改条款.] 改写 `durable-events-009/010`。必要时新增一条路径条款。
P5. [改实现.] 建单一路径常量入口。替换全部字面量。删旧字面量。
P6. [改测试.] 更新 `durable-events/009`、`010`；更新
    `requirement-grounding/tests/007.test.mjs` 的 `.git/wanxiang/payloads/…` 引用。
P7. [验证.] 跑 durable-events、durable-convergence、requirement-grounding、host-boundary
    套件。跑门禁。
P8. [终止.] 算法终止。
```

- 验收：全仓 grep 无旧字面量。套件绿。记录写明旧数据处置。
- 风险：真实用户历史数据在旧路径。迁移边界必须写清。

### WP-002 · L56 · 会话准入简化评估

- 类型：裁决。波次：W0。输出：结论记录。不得直接改产品行为。
- 现状锚点：`src/Wanxiangshu/OpenCode/Host/HostSignalBootstrap.fs · chatMessageHook`；`src/Wanxiangshu/Execution/Session/ChatExecution/Admission.fs`；`requirements/managed-chat-execution/WHAT.md`（准入顺序与「追加材料」条款）。

```
Algorithm A（准入评估）。输入：指南 §2 与准入源码。输出：裁决记录。

A1. [抄录.] 抄出指南 §2 的七步准入。与源码逐条对应。
A2. [三问.] 对每步执行算法 J2。
A3. [查合并.] 检查「追加材料」路径与租约交接能否合并。
A4. [定论.] ▷ 结论 ∈ {保留, 简化}。简化时给出新准入序列。
A5. [记录.] 写讨论记录。若简化，另开实施卡，转算法 R。
A6. [终止.] 算法终止。
```

- 验收：讨论记录含逐条结论、证据行号、风险。不只写结论。

### WP-003 · L84 · 漂移校验退役

- 类型：实施。波次：W2。
- 目标：运行时不再校验 agent 与 model/reasoning 漂移。不变量交给测试期 fast-check。
- 现状锚点：`src/Wanxiangshu/OpenCode/Host/ChatParamsHook.fs · validateObservedProvider`（约 104-114，抛 PROMPT-006 漂移错）；`requirements/execution-model-routing/WHAT.md` [009]；测试 `execution-model-routing/tests/009`、`interaction-authority/tests/011`、`host-boundary/tests/003`、`033`。

```
Algorithm D（漂移退役）。输入：漂移校验实现与相关条款。输出：性质测试与简化后的运行时。

D1. [写性质测试.] 用 fast-check 写性质：对任意 `route(role, running, previous, purpose)` 序列，
    provider 观测 target 等于已提交租约 target。放进 execution-model-routing 或 host-boundary 套件。
D2. [改条款.] 改写 [009]。写明：运行时观测只读；漂移由测试期性质保证。
D3. [改实现.] 删漂移拒绝分支。保留两条：durable 已接受且无租约 → fail closed；
    managed 请求投影 `temperature = 1.0`。
D4. [改写用例.] 漂移拒绝用例按新合同改写为「观测只读、不阻断」。
D5. [验证.] 跑 execution-model-routing、interaction-authority、host-boundary 套件。
D6. [终止.] 算法终止。
```

- 验收：性质测试绿。套件绿。条款写明取舍：漂移若真实发生将静默通过。

### WP-004 · L109 · `messages.transform` 洋葱式

- 类型：重构。行为不变。波次：W5。
- 现状锚点：`src/Wanxiangshu/OpenCode/Plugin/PluginTransforms.fs · normalTransform`（约 840-935）。副本分支另有五步。已知：代码注释编号与指南 §4 错位（第 5 步起偏移一位；指南 13 在代码中拆成三段）。

```
Algorithm T（投影管道）。输入：`normalTransform` 与指南 §4 的 16 步。输出：有序 stage 管线。

T1. [定义管道.] 管道 = 有序 stage 列表。`stage = {name, before, after}`。
T2. [修编号.] 以指南 §4 的 16 步为唯一编号。先修指南，再改注释。发现实质错序时先裁决。
T3. [抽函数.] 逐 stage 抽函数。次序与副作用点不动。
    副作用点：durable 证据门、Relay 切分、XTrace 捕获、attempt plan 冻结。
T4. [补断言.] 每 stage 补一条顺序断言测试。
T5. [验证.] 跑 prefix-stability、provider-projection、speculative-investigation、
    relay-context-projection、context-compression 套件。
T6. [终止.] 算法终止。
```

- 验收：套件绿。管线次序与指南逐条一致。无行为差异。
- 风险：次序是合同（`host-boundary-019`）。不得重排。

### WP-005 · L153 · `tool.definition` 洋葱式 + DRY

- 类型：重构。波次：W5。
- 现状锚点：`src/Wanxiangshu/OpenCode/Host/ManagerReviewContract.fs · decorateDefinition`；`src/Wanxiangshu/OpenCode/Host/ReadonlyDelegationContract.fs · decorateDefinition`；`src/Wanxiangshu/OpenCode/Host/ToolSchemaJson.fs · providerSchema`。两处重复「属性描述符 hide / restore + 私有 Symbol」。

```
Algorithm F（定义装饰）。输入：两级定义装饰实现。输出：共享原语与两个薄策略。

F1. [抽原语.] 抽公共参数保险库装饰器一份：hide、restore、classify。
F2. [变薄策略.] 两个装饰器只声明字段名、取值、出现条件。
F3. [保幂等.] 保持 `isSameBudgetProperty`、`appendBudgetIfMissing`、
    `stripInvestigationOutlookBlocks` 语义不变。
F4. [保解码.] `parameters` 保持原样供宿主解码。只装饰 `jsonSchema` 视图。
F5. [验证.] 跑 host-boundary/032、speculative-investigation/013 与 016、schema canary。
F6. [终止.] 算法终止。
```

- 验收：装饰结果逐字节不变。幂等测试绿。键序与对象身份不变（`host-boundary-032`）。

### WP-006 · L175 · `tool.execute` 洋葱式 + DRY

- 类型：重构。波次：W5。
- 现状锚点：`src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs · toolBefore`（grounding → 评审权限 → 估计参数校验 → vault 记录 → 剥除）；`toolAfter`（还原 → grounding → casebook）。

```
Algorithm X（执行管道）。输入：toolBefore/toolAfter。输出：decorator 门链。

X1. [定管道.] 门链 = decorator 列表。同一执行器。两端插入 stage。
X2. [共享.] 与 WP-005 共用 vault 原语。删重复实现。
X3. [保异常路径.] 异常路径同样还原。
X4. [验证.] 跑 requirement-grounding/007、008、009；capability-enforcement/025；
    host-boundary/032；effect-accounting/008。
X5. [终止.] 算法终止。
```

- 验收：还原与剥除语义不变。套件绿。

### WP-007 · L207 · 工具面洋葱式 + DRY

- 类型：重构。波次：W5。
- 现状锚点：`src/Wanxiangshu/OpenCode/Tools/ToolRegistry.fs · executeAfterBoundary`（消融、只读副本）→ `executeEstablished`（office、private attachment）→ `executeManager`（Manager facts）。

```
Algorithm S（工具面）。输入：ToolRegistry 门链。输出：decorator 列表。

S1. [定管道.] 门链 = decorator 列表。每门独立可测。
S2. [保单源.] schema 与门禁读同一份 capability 真源（`capability-enforcement-002`）。
S3. [对表.] 核对指南 §8 工具表与注册全集一致。
S4. [验证.] 跑 capability-enforcement/005、006、010、025；feature-ablation/002。
S5. [终止.] 算法终止。
```

- 验收：门禁后果逐项不变。套件绿。

### WP-008 · L264 · 事件与调和 KISS 评估

- 类型：裁决。波次：W0。
- 现状锚点：`src/Wanxiangshu/Composition/Turn/ReconcilePass.fs`、`ReconcileSurface.fs`、`TurnReconcile.fs`；`src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs · observeEvent`；`src/Wanxiangshu/OpenCode/Host/HostSignalBootstrap.fs · ObserveEvent`。

```
Algorithm C（调和评估）。输入：全部 reconcile 路径。输出：保留与删除清单。

C1. [罗列.] 列出全部 reconcile 路径与信号：`SessionIdle`、`ProviderRetry`、
    `ProviderFailure`、`SessionDeleted`、`AttemptAborted`、todo 终态。
C2. [标后果.] 每条标用户可见后果：完成结算、重试授权、消息可见性、checkpoint。
C3. [筛选.] 无用户可见后果的路径列为删除候选。
C4. [保栅栏.] 保留发送栅栏（`provider-attempt-recovery-022`）与效果结算路径。
C5. [定论.] 裁决并执行。跑 host-boundary、effect-accounting、obligation-ledger、
    dispatch-protocol。
C6. [终止.] 算法终止。
```

- 验收：删除项有清单与理由。保留项有条款依据。

### WP-009 · L381 · fork 的 calling 可选

- 类型：实施。波次：W3。
- 现状锚点：`src/Wanxiangshu/Execution/Delegation/Fork/OpenCode/Tool.fs · managerSpec`（calling 必填）；`requirements/delegation/WHAT.md` [003]；测试 `delegation/tests/003.test.mjs`。

```
Algorithm K（fork calling）。输入：fork schema 与条款 [003]。输出：可选 calling 与推导。

K1. [改条款.] 改写 [003]。calling 可选。缺省推导：`name ≠ devops` → `calling ＝ engineer`；
    否则 `calling ＝ devops`。
K2. [裁决冲突.] ▷ 显式 calling 与推导不一致时 → typed 拒绝（对齐 resume 拒绝 calling 的风格）。
K3. [改 schema.] 改 decode 与 schema。改 `resources/provider/tool/fork/` 中英描述。
K4. [改测试.] 改写必填用例。补两条：缺省推导、冲突拒绝。
K5. [验证.] 跑 delegation 套件。跑语言对等门。
K6. [终止.] 算法终止。
```

- 验收：缺省推导有测试。返回值不携带物理拓扑（[005] 保持）。

### WP-010 · L388 · keyword / attach 必要性

- 类型：裁决。波次：W0。
- 现状锚点：`Fork/OpenCode/Tool.fs` 参数解析（约 248-271）；`repository-investigation-007/009`（关键词上限）；`delegation-021`（attach）；测试 `delegation/tests/021`、`022`。

```
Algorithm Q（关键词评估）。输入：keywords 与 attach 实现。输出：裁决与执行。

Q1. [采证.] 收集使用频率、低信任提示收益、维护成本、误导风险。
Q2. [定论.] ▷ 保留或删除。保留时写边界与理由。
Q3. [执行删除.] 删除时改 schema、条款、资源、测试、描述。记录 GAP 变化。
Q4. [终止.] 算法终止。
```

- 验收：结论有使用频率或等价证据。删除时调用方全部更新。

### WP-011 · L402 · commission 的 calling 可选

- 类型：实施。波次：W3。
- 现状锚点：`Fork/OpenCode/Tool.fs · orchestratorSpec`；`delegation-004`；`office-capability-012`。

```
Algorithm O（commission calling）。输入：orchestratorSpec 与条款 [004]。输出：可选 calling。

O1. [改条款.] 同算法 K1。范围限 orchestrator 的 commission。
O2. [裁决冲突.] 同 K2。
O3. [改 schema.] 同 K3。资源目录为 `resources/provider/tool/commission/`。
O4. [改测试.] 同 K4。
O5. [验证.] 跑 delegation 与 office-capability 套件。
O6. [终止.] 算法终止。
```

- 验收：同 WP-009。

### WP-012 · L416 · 底层 id 概念去除

- 类型：裁决 → 实施。波次：W3。
- 现状锚点：`src/Wanxiangshu/Execution/Session/OpenCode/HorizonTool.fs · labelForHandle`；`scripts/checks/provider-leak-gate.mjs · FORBIDDEN_TOKENS`（SessionId、AgentId、PtyId 等）；`src/Wanxiangshu/Execution/Delegation/Fork/Model.fs · AgentRecord.AgentId`。

```
Algorithm I（id 清理）。输入：全部 id 种类与可见面。输出：脚手架 id 的去除。

I1. [盘点.] 列 id 种类与可见面：SessionId、AgentId、HandleId、PtyId、LaneIndex、
    TerminalName、Byname。
I2. [核对.] 确认现状：provider 面已禁物理 token。horizon 只显示 Byname 与 TerminalName。
I3. [裁决.] ▷ 区分脚手架 id 与职责 id。ownership 与 attachment 需要物理身份。
I4. [实施.] 只改判定为脚手架的部分。
I5. [验证.] 跑 participant-horizon/010、011；delegation/026；provider-leak-gate。
I6. [终止.] 算法终止。
```

- 验收：结论引用禁词门与输出面。改动有测试。

### WP-013 · L458 · Road 概念评估

- 类型：裁决。波次：W0。
- 现状锚点：`src/Wanxiangshu/Mission/Relay/Contract.fs · RoadId`；`Facts.fs · RoadOpened / RoadDevOpsBound`；`Fold.fs · RoadState / RoadView`。引用面约 45 个文件（近似）。

```
Algorithm R（Road 评估）。输入：Road 全部职责。输出：保留或合并结论。

R1. [列职责.] 列出 Road 承担：任期容器、证书绑定、上下文切分、变更集成。
R2. [评估替代.] 评估以既有逻辑执行或会话身份替代的后果。
R3. [定论.] ▷ 保留或合并。合并时跨包修订 `relay-incumbency`、`relay-assessment`、
    `relay-retirement`、`change-integration`。
R4. [终止.] 算法终止。
```

- 验收：结论覆盖全部职责。合并时跨包条款与测试同步。

### WP-014 · L465 · 评审对象重定义

- 类型：实施。波次：W4。与 WP-015 同一人。
- 现状锚点：`src/Wanxiangshu/Mission/Relay/Contract.fs · ScoreDimension`（约 53-83）；`Mission/Relay/Assessment/Model.fs · schemaJson`；`Mission/Relay/OpenCode/ReviewTool.fs · fields`；资源 `resources/provider/tool/review/`、`resources/provider/runtime/manager-assess/`。

```
Algorithm V（评审对象）。输入：评审条款与提示词。输出：三问合同。

V1. [改条款.] 改写 `relay-assessment`：评审必答三问——目标状态形式化、当前 GAP、
    GAP 是否非空。八维降为观察抓手。
V2. [改提示词.] 改双语提示：`tool/review/description`、`runtime/manager-assess`、
    `manager-work`、`manager-finish`。
V3. [裁决.] ▷ 定三问是否进 schema。进 schema 时改 fold 与投影。
V4. [改测试.] 更新 `relay-assessment/tests/001`、`008`、`support/plugin.mjs`。
V5. [终止.] 算法终止。
```

- 验收：条款与提示词一致。测试绿。

### WP-015 · L469 · review 返回 pairs

- 类型：实施。波次：W4。
- 目标：`review()` 返回 `[(验收标准, 工作计划), …]`。空集 = 通过终止。首个 Manager 原则上非空（提示词建议）。

```
Algorithm Y（评审对）。输入：review 返回形态。输出：pairs 语义与实现。

Y1. [裁决八维.] 与 WP-014 合并定八维去留：抓手保留或向量删除。
Y2. [定类型.] 定返回类型与持久化载荷。
Y3. [定语义.] 空集 → 生成证书并退场。非空 → 每对一项修复义务。
Y4. [改实现.] 改 `ReviewTool` schema、fold、投影、证书摘要。
Y5. [改测试.] 补四条：空集终止、非空修复、重放幂等、异载荷拒绝（对齐 [002]）。
Y6. [终止.] 算法终止。
```

- 验收：条款改写先行。四条测试绿。
- 风险：与 [001]（八维必填）冲突。先改条款。

### WP-016 · L487 · 最后一任收尾

- 类型：实施。波次：W4。
- 现状锚点：`src/Wanxiangshu/Foundation/OfficeCapability.fs · permissionsForManagerFacts`；`src/Wanxiangshu/OpenCode/Tools/ToolRuntimeScope.fs`；`ReviewTool.acceptedResult` 文案；`relay-assessment-005`。

```
Algorithm L（最后一任）。输入：通过评审后的权限。输出：收尾例外与通知。

L1. [定范围.] ▷ 定收尾权限：读、清理、收口类动作。禁止开新工作。逐项列明。
L2. [改条款.] 改写 [005]：失去修改能力的时点与例外。
L3. [改实现.] 改权限收口。
L4. [改文案.] 双语加「最后一任」通知。
L5. [改测试.] 补三条：收尾可用、新工作拒绝、通知文本存在。
L6. [终止.] 算法终止。
```

- 验收：三条测试绿。条款写明例外清单。
- 依赖：WP-014、WP-015 先定语义。

### WP-017 · L494 · 快照机制去除

- 类型：裁决 → 实施。波次：W4。与 WP-019 同一人。
- 现状锚点：`src/Wanxiangshu/Mission/Relay/OpenCode/WorkspaceSnapshot.fs · capture`；消费者 `ReviewTool`、`SuicideTool`、`Change/Program.fs`（publish 门，约 191-197、628-633）；`relay-assessment-010`。

```
Algorithm H（快照去除）。输入：快照全部消费者。输出：门禁去留结论与执行。

H1. [列消费者.] 列出快照全部消费者与门禁。
H2. [定论.] ▷ 保留或去除各门。去除时保留必要记录（若有）。
H3. [改条款.] 改写 [010] 与 `change-integration` 相关条款。
H4. [实施.] 改代码。改写测试。
H5. [记录.] 写明取舍：一致性由流程与 fast-check 承担。
H6. [终止.] 算法终止。
```

- 验收：条款、代码、测试一致。取舍有记录。
- 风险：并发改动下评审与发布的一致性。不得静默降级。

### WP-018 · L499 · 两次 suicide

- 类型：实施。波次：W4。
- 现状锚点：`src/Wanxiangshu/Mission/Relay/OpenCode/SuicideTool.fs · runFrozen`（约 273-284）；`resources/provider/tool/suicide/`。

```
Algorithm U（两次 suicide）。输入：suicide 流程与 review 承诺。输出：两段式确认。

U1. [定状态.] 第一次调用 → 返回 review 承诺列表（取自 WP-015 的 pairs）＋附言
    「无工作可做时再调第二次」。第二次 → 正常退休流程。
U2. [记账.] durable 记录第一次确认。重放幂等。异载荷拒绝。
U3. [改实现.] 改 `SuicideTool`。保留既有 blocker 检查。
U4. [改文案.] 双语文案。
U5. [改测试.] 补四条：第一次拦截、第二次通过、重放幂等、与 blocker 的顺序。
U6. [终止.] 算法终止。
```

- 验收：四条测试绿。
- 依赖：WP-015。

### WP-019 · L503 · 证书去形式主义

- 类型：裁决 → 实施。波次：W4。
- 现状锚点：`Mission/Relay/Fold.fs · newCertificate`（约 73-86）；`Change/Program.fs` 证书门；`relay-assessment-005`；`relay-incumbency-005/006`。

```
Algorithm Z（证书清理）。输入：证书全部运行时门。输出：逐门裁定与执行。

Z1. [枚举.] 枚举证书全部运行时门：finish、publish、新任限制。
Z2. [定论.] ▷ 逐门裁定去留。
Z3. [改条款.] 改写相关条款。不变量移交流程 + fast-check。
Z4. [实施.] 改代码。改写测试。
Z5. [记录.] 与 WP-017 合并记录。
Z6. [终止.] 算法终止。
```

- 验收：逐门有结论。测试绿。

### WP-020 · L509 · 重启状态指导 prepend

- 类型：实施。波次：W2。
- 冲突：`requirements/crash-reconciliation/WHAT.md` [018] 现文本写「没有独立的续传材料通道，也没有 disclosure-only 的 provider 轮次」。与意见冲突。必须先改写。

```
Algorithm B（重启指导）。输入：重启归位事实与下一次新指令。输出：prepend 的状态指导。

B1. [裁决载体.] ▷ 定注入点。建议与 `guidance-delivery` 同规则：附在最新一条真实消息上。
B2. [改条款.] 改写 [018]：重启后首个新指令前 prepend 一次状态指导。
    写明：只读、不改写用户原文、不创建 authority。
B3. [实现.] 检测「本进程加载时发生过归位」→ 下一次真实用户指令渲染指导段。
    材料取自 durable 投影。
B4. [定边界.] 只出现一次。重放幂等。字节冻结（对齐 `guidance-delivery-011`）。
B5. [改测试.] 补四条：首次含指导、第二次不含、无新指令不生成、重放稳定。
    扩展 `crash-reconciliation/tests/018.test.mjs`。
B6. [终止.] 算法终止。
```

- 验收：四条测试绿。
- 风险：与 `prefix-stability` 交互。指导字节必须冻结。

### WP-021 · L600 · ndjson 内嵌 payload

- 类型：实施。波次：W1。
- 现状锚点：`src/Wanxiangshu/Persistence/EventStore/Model.fs · PayloadRef / PayloadRefs`；`Store.fs`（closure 校验，约 177-182）；`src/Wanxiangshu/Persistence/Journal/EventStoreJournalWriter.fs · writePayload`（约 25-36）；`durable-events-012`。

```
Algorithm N（内嵌载荷）。输入：事件行与旁挂 payloads。输出：自包含事件行。

N1. [改条款.] 改写 [012]：取消引用层。事件行自包含。
N2. [改实现.] codec、append、journal blob 写入改为内嵌。
N3. [删旧.] 删 payloads 目录与全部写入点。
N4. [改测试.] 更新 `durable-events/012`、`023`；更新 `requirement-grounding/007` 路径引用。
N5. [验证.] 跑 durable-events、durable-convergence 套件。
N6. [终止.] 算法终止。
```

- 验收：套件绿。全仓无 payloads 目录引用。
- 风险：单行体积上限（如 512 KiB 文本）。与 WP-023 联合验证。

### WP-022 · L601 · UTC 日期分组 GC

- 类型：实施。波次：W1。
- 目标：按 UTC 日期分组。今天、昨天保留。前天及更早、无使用者删除。日期只用于 GC。允许跨日期移动。日期 = 最近修改语义。

```
Algorithm G（日期 GC）。输入：事件载体与保留规则。输出：日期分组与回收。

G1. [定表示.] ▷ 定分组表示：按日期目录，或等价方案。
G2. [定「无使用」.] ▷ 定判定：自该日期后无 append，或未被引用。
G3. [定与 retention 关系.] ▷ 与 24 小时 writer retention（`durable-convergence-011`）
    合并或替换。
G4. [改实现.] 跨日期移动要原子。定 GC 执行点：装载时或同步时。远端快照一致。
G5. [改测试.] 补四条：三态日期、跨日期移动、今天与昨天不删、确定性与重放。
G6. [终止.] 算法终止。
```

- 验收：四条测试绿。
- 依赖：WP-001、WP-021。

### WP-023 · L602 · 性能问题排查

- 类型：裁决 → 实施。波次：W1。
- 观察数据：`.git/wanxiang/events/` 共 44 个 writer 文件，约 566 MB。最大单文件约 102.5 MB。`.git/wanxiang/payloads/` 约 45,538 个文件。

```
Algorithm E（性能排查）。输入：存储体量与读路径。输出：根因与修复数字。

E1. [测基线.] 测装载耗时、读路径耗时（`readStreamsAt`）、integrator 合并耗时、内存峰值。
E2. [定位.] 定位热点。参考先例：`RuntimePath.fs` 的 common-dir 缓存注释
    （同步 git spawn 曾阻塞热路径）。
E3. [修复.] 流式读、增量装载、文件级缓存、按需解析。
E4. [验收.] 给出修复前后同输入的数字对比。
E5. [终止.] 算法终止。
```

- 验收：数字对比存在。未测的写「未验证」。
- 禁止：放大超时掩盖问题。

### WP-024 · L627 · git hook 修复后重开

- 类型：实施。波次：W1。
- 现状锚点：
  - kill switch：`src/Wanxiangshu/OpenCode/Host/HostSignalBootstrap.fs`（约 550-561）。`WANXIANG_GIT_SYNC` 未设 → 不安装 hook。注释写「investigated」。
  - 事故记录：`CHANGELOG.md`（约 353）。pre-push 钩子进程成孤儿并卡住，仍持 `.git/wanxiang.lock`。之后 durable 激活永久阻塞。处置：杀进程、禁用集成。待办：锁有界重试、强类型诊断、不跨网络 I/O 持锁。
  - 锁实现：`Persistence/EventStore/ProcessEventLog.fs · acquireStoreLock / acquireAvailableLock`（约 228-266）。proper-lockfile，stale 5000，retries 0，循环等待无上界。
  - 现场：`.git/hooks/` 为空目录。

```
Algorithm W（hook 重开）。输入：孤儿事故与锁实现。输出：修复后的默认开启。

W1. [根因.] 读事故记录。复现孤儿与锁问题。
W2. [修锁.] 加有界重试与强类型诊断。修完与 WP-023 联动。
W3. [修 hook.] hook 内 converge 不跨网络 I/O 持锁。加超时。
W4. [烟雾.] 在隔离练习仓库安装 hook、运行 push、观察无阻塞。
W5. [改开关.] ▷ 修复充分后，默认开启。
W6. [改条款与测试.] 更新 `durable-convergence-008` 与测试（现为 `test.todo` 的真实激活时机）。
W7. [终止.] 算法终止。
```

- 验收：复现记录、修复记录、烟雾结果三者齐备。

### WP-025 · L639 · horizon 泄露重置

- 类型：裁决 → 实施。波次：W2。与 WP-020 同波，本卡先。
- 意见推论：horizon 应为空。join 应立刻返回无。意见写明「不是公理，是推论」。
- 现状锚点：`src/Wanxiangshu/OpenCode/Plugin/PluginRecoveryWiring.fs · settleOrphanedChildRuns`（约 81）；`src/Wanxiangshu/OpenCode/Host/ChildWorkRecovery.fs`；`src/Wanxiangshu/Execution/Session/OpenCode/HorizonTool.fs · shouldIncludeHandleInRoster`（现为 `currentProcessHandle || isAbandonedHandle`，约 156）；`src/Wanxiangshu/Execution/Delegation/LinkageProjection.fs · horizonVisible`（约 610-621）；`Execution/Delegation/Fork/Host/Join.fs · currentProcessHandle`（约 64-67）；测试 `crash-reconciliation/020`、`participant-horizon/011`、`delegation/026`。

```
Algorithm J（泄露重置）。输入：崩溃场景与名册过滤。输出：空的 horizon 与即返回的 join。

J1. [复现.] 构造崩溃场景。检查上一进程遗留工作是否出现在 horizon。
J2. [裁决.] ▷ 定 Abandoned handle 的可见性（现在恒上榜）。
J3. [实现.] 崩溃前的未决工作不泄露。horizon 空、join 立即返回无。
J4. [改测试.] 扩展三处测试。
J5. [终止.] 算法终止。
```

- 验收：复现红例与修复绿例成对存在。

### WP-026 · L662 · 「证据」措辞清理

- 类型：实施。波次：W2。
- 目标：生产环境的 provider 可见文本不出现「证据」一类词汇。不用 fail-closed 掩盖测试不足。

```
Algorithm M（措辞清理）。输入：provider 可见文本与 fail-closed 门。输出：替换清单与执行。

M1. [盘点.] 扫 `resources/provider/**` 与工具描述。列出含「证据 / evidence」的条目。
M2. [定论.] ▷ 逐条定替换或保留。替换时同步中英双语。
M3. [列门.] 与 WP-027 合并列「为掩盖测试不足」的 fail-closed 门。逐项裁决。
M4. [验证.] 跑语言对等门与相关断言。
M5. [终止.] 算法终止。
```

- 验收：替换清单完成。双语一致。

### WP-027 · L668 · 失败恢复优雅化

- 类型：实施。波次：W2。
- 目标：保持 user-facing 行为。删除「刻板实现」。
- 现状锚点：`src/Wanxiangshu/Execution/Failure/Decision.fs · ExecutionFailureResolution`（约 15-19）；`Execution/Failure/Policy.fs`；`execution-failure-policy-001` 的十一类失败。

```
Algorithm F（失败恢复）。输入：十一类失败与五种决议。输出：保行为的简化。

F1. [列行为.] 列十一类失败与五种决议的用户可见行为：重试预算、终态呈现、中止语义。
F2. [筛形式.] 识别形式分支：只为自洽的归类、重复归类、不可达决议。
F3. [保行为.] 保用户可见行为。删或合并形式分支。
F4. [验证.] 跑 execution-failure-policy、provider-attempt-recovery、
    host-provider-failure-ownership、degeneration-guard。
F5. [记录.] 写取舍。
F6. [终止.] 算法终止。
```

- 验收：套件绿。取舍有记录。
- 风险：发送栅栏与容量结算必须保留。它们影响物理行为。

### WP-028 · L715 · assume 核对

- 类型：核对。波次：W2。
- 目标：确认 assume 从不被替换成 LWR 压缩版。
- 现状锚点：`src/Wanxiangshu/OpenCode/Codec/ProjectionMessageEdit.fs · isAssumeCallPart / assumeCallIds / survivesReplacement`（约 22-90）；`src/Wanxiangshu/Context/Prefix/Wire.fs · replacePrefixByHostIds`（约 151-163）；测试 `requirements/context-compression/tests/030.test.mjs`；`GAP-026` 已记 CLOSED。

```
Algorithm A（assume 核对）。输入：替换豁免实现与测试覆盖。输出：核对结论。

A1. [核对.] 核对实现与覆盖：assume 的 call 与 result 在替换后逐字节保留。
A2. [分支.] 覆盖充分 → 记录证据，转 A4。不足 → 补一条回归测试。
A3. [验证.] 跑 context-compression 套件。
A4. [终止.] 算法终止。
```

- 注意：LWR 文本渲染本身剔除工具（`Context/Trace/Model.fs`）。保留来自替换豁免。两者勿混。

### WP-029 · L724 · enough / abandon 合入 assume

- 类型：实施。波次：W3。
- 目标：删除 `enough` 与 `abandon` 工具。教育 LLM 用 `assume` 表达。
- 现状锚点：`src/Wanxiangshu/OpenCode/Tools/AttentionTools.fs`（enough、abandon、defer）；`OpenCode/Tools/AssumeTool.fs`；`attention-regulation-001/002`；`resources/provider/attention-regulation/`。

```
Algorithm M（合并认知工具）。输入：enough 与 abandon。输出：assume 单口。

M1. [改条款.] 退休 [001]、[002]，或并入 assume 语义。
M2. [改文案.] 提示词教 LLM 把 assume 当「足够」「放下」使用。
M3. [实施.] 删注册、删资源、删消融映射（`resources/ablation/tool-map.json`）。
M4. [改测试.] 改写 attention-regulation/001 到 006。改 capability 枚举测试。
M5. [验证.] 跑 attention-regulation、capability-enforcement、cognitive-workspace。
M6. [终止.] 算法终止。
```

- 验收：套件绿。历史投影中的旧调用仍可读，断言这一边界。

### WP-030 · L731 · defer 新语义

- 类型：实施。波次：W3。
- 目标：Manager 的 defer 项进入第一次 suicide 返回的待办。DevOps / Engineer 自然终止时若存在 defer 项，以 user prompt 触发一次回合，同时消灭 defer。
- 现状锚点：`OpenCode/Tools/AttentionTools.fs · defer` → `DeferredWorkRecorded`；`src/Wanxiangshu/Interaction/Attention/Projection.fs · pending / resurface / closeLife`；现弹出点：celebrate 尾部（`InstitutionalLearningTools.fs`，约 134-136、249）。

```
Algorithm F（defer 语义）。输入：DeferredWork 与角色终点。输出：三条消费路径。

F1. [改条款.] 改写 [003]、[004]、[005]。celebrate 载体退休后改写 resurface 条款。
F2. [实现 Manager 路径.] 待办进入第一次 suicide 返回（WP-018）。
F3. [实现 Engineer / DevOps 路径.] 自然终止时注入 user prompt。注入后清项。
F4. [改测试.] 补三条路径测试：幂等、消灭语义。
F5. [终止.] 算法终止。
```

- 验收：三条路径测试绿。
- 依赖：WP-036 先执行。

### WP-031 · L732 · orchestrator 的 defer

- 类型：实施。波次：W3。

```
Algorithm O（orchestrator defer）。输入：WP-030 的精神。输出：orchestrator 定义。

O1. [推导.] 按 WP-030 精神定 orchestrator 的 defer 处理（建议：交接或收尾时处理）。
O2. [改条款.] 写入条款。
O3. [改测试.] 补对应用例。
O4. [终止.] 算法终止。
```

- 验收：条款与测试一致。
- 依赖：WP-030。

### WP-032 · L733 · Pair Hint 鼓励 defer

- 类型：实施。波次：W3。
- 现状锚点：`src/Wanxiangshu/OpenCode/Host/PairProgrammingThoughtTransform.fs`；资源 `resources/provider/host/pair-programming-guideline/{zh-CN,en}.md`。

```
Algorithm P（Pair Hint）。输入：双语 Hint 文本。输出：鼓励 defer 的措辞。

P1. [改文本.] 双语 Pair Hint 积极鼓励 defer。
P2. [对冲突.] `cognitive-environment/tests/013`、`016` 断言「hint 不含工具行为提醒」。
    改写断言或裁决措辞边界。
P3. [验证.] 跑语言对等门与相关套件。
P4. [终止.] 算法终止。
```

- 验收：双语一致。套件绿。
- 依赖：WP-030 定稿。

### WP-033 · L740 · 自动 subscribe 自身名字

- 类型：实施。波次：W3。
- 目标：任意 session 启动时自动 subscribe 自身名字（上级对它的命名）。user-facing 无名字者叫 `root`。另有 `user` 地址。
- 现状锚点：`OpenCode/Tools/ConcernTools.fs`（subscribe、publish）；`Interaction/Concern/{Facts,Projection,Surface}.fs`；`concern-routing-001/003`。

```
Algorithm S（自动订阅）。输入：会话建立与地址语义。输出：自动订阅事实。

S1. [改条款.] 改写 [001]、[003]：地址语义与自动订阅。
S2. [实现.] 会话建立时追加订阅事实（`MailboxSubscribed`）。
S3. [删工具.] 删 `subscribe` 工具面（与 WP-034 一起）。
S4. [改测试.] 改写 concern-routing/001 到 007。
S5. [终止.] 算法终止。
```

- 验收：套件绿。
- 依赖：与 WP-034、WP-035 同一人。

### WP-034 · L741 · 只有 publish；user 弹窗

- 类型：实施。波次：W3。
- 目标：publish 给 agent = fire-and-forget。publish 给 `user` = 用户可见弹窗，同时复制给 `root`。

```
Algorithm P（发布路由）。输入：publish 与宿主通知能力。输出：路由实现。

P1. [查宿主.] 调查 OpenCode 插件面的弹窗或通知能力。不可用时裁决替代
    （TUI 输出或诊断通道）。不得发明接口。
P2. [实现路由.] `user` → 弹窗 ＋ `root` 复制。其他 → fire-and-forget。
P3. [改条款与文案.] 更新条款与资源文案。
P4. [改测试.] 补三条：三条路由、未知地址拒绝或诊断。
P5. [终止.] 算法终止。
```

- 验收：路由测试绿。
- 依赖：WP-033。

### WP-035 · L742 · 向 LLM 解释邮箱语义

- 类型：实施。波次：W3。

```
Algorithm E（邮箱解释）。输入：concern-routing 描述。输出：双语解释文案。

E1. [改描述.] 改写 `concern-routing` 工具描述与 Pair Hint 说明。中英双语。
E2. [改测试.] 补语言对等与描述断言。
E3. [终止.] 算法终止。
```

- 验收：双语一致。测试绿。
- 依赖：WP-033、WP-034。

### WP-036 · L748 · 删除 celebrate / regret

- 类型：实施。波次：W3。先盘点，后删除。
- 盘点面：`src/Wanxiangshu/Enforcer/InstitutionalLearning/**`（Facts、Fold、Surface、Enhancer，共约 474 行）；`OpenCode/Tools/InstitutionalLearningTools.fs`；`ToolRegistry.fs` 注册；`resources/ablation/tool-map.json`（celebrate、regret、institutional-learning）；`resources/provider/institutional-learning/`；引用方：attention-regulation 的 resurface、capability-enforcement 枚举、`requirements/INDEX.md` §10、GAP 行。

```
Algorithm D（删除学习工具）。输入：celebrate 与 regret。输出：无残骸的删除。

D1. [定范围.] ▷ 定整包退休，或只删工具面。建议整包退休，因为包的 WHY 就是这对工具。
D2. [执行删除.] 删源码、资源、注册、消融映射、测试。
D3. [改导航.] 改 `INDEX.md`、`GAP.md`、`README.md` 提及。
D4. [核链路.] 确认 BIRTH → Enforcer 规则书链路不受影响（Blogger 路径保留）。
D5. [验证.] 跑 attention-regulation、capability-enforcement、behavior-diagnosis。
    institutional-learning 套件随包删除。
D6. [终止.] 算法终止。
```

- 验收：无残骸。剩余套件绿。

### WP-037 · L808 · 环境变量 KISS

- 类型：实施。波次：W6。
- 现状清点：生产读取点 8 个（`WANXIANGSHU_PROVIDER_LANGUAGE`、`WANXIANGSHU_SKIP_AUTO_INJECTED`、`WANXIANGSHU_PROCESS_HARD_LIMIT_SECS`、`WANXIANGSHU_ADMISSION_TIMEOUT_MS`、`WANXIANGSHU_DIAG`、`WANXIANGSHU_NO_FATAL_EXIT`、`WANXIANGSHU_ABLATION_PROFILE`、`WANXIANGSHU_ABLATION_<node>`）。另有 `SPHINX_COMMON_DIR`（`Sphinx/V2/ServeEntry.fs` 约 46、`Sphinx/V2/Composition/Bind.fs` 约 36）与 `SPHINX_START_CONFIG`（`Sphinx/V2/Hosts/Mcp/Server.fs` 约 513），属独立 MCP 进程；`SEMBLE_*`、`WANXIANGSHU_TEST` 为内部/测试变量（`Repository/Investigation/Semble/Mcp.fs` 约 54-57）。`WANXIANG_GIT_SYNC` 已由 WP-024 删除。文档：`README.md`（约 79-89）。

```
Algorithm V（环境变量）。输入：全部变量与读取点。输出：最小集合。

V1. [列表.] 列全部变量：读取点、模板名、测试用、文档引用。
V2. [定论.] ▷ 定最小集合。删除失效或重复项。
V3. [实施.] 删读取点、删文档、改测试（feature-ablation/002 与 004、behavior-diagnosis/017、
    host-boundary/001 与 002 等）。
V4. [对齐.] 裁决 `SPHINX_COMMON_DIR` 是否并入统一清单。
V5. [终止.] 算法终止。
```

- 验收：README 表与代码读取点一致。

### WP-038 · L825 · 验证入口 KISS

- 类型：实施。波次：W6。
- 现状锚点：`README.md` 命令章节（约 215-228）；`package.json` scripts；`.github/workflows/`。

```
Algorithm V（验证入口）。输入：README、scripts、CI。输出：最小入口。

V1. [列表.] 列现有入口：README 命令表、scripts、CI 引用。
V2. [定论.] ▷ 定最小入口集合。建议保留 `npm run format-build-test` 与
    `npm run verify:release` 为正式入口。
V3. [实施.] 改 README 与 CI。删别名前查外部消费者。
V4. [验证.] 确认 CI 不引用被删命令。
V5. [终止.] 算法终止。
```

- 验收：入口一致。CI 绿。

### WP-039 · L840 · Sphinx 恢复

- 类型：裁决 → 实施。波次：W5。
- 现状锚点：`ToolPermission.Sphinx`：`Foundation/OfficeCapability.fs`（约 31、56、65、78）。schema 名单有 sphinx：`OpenCode/Tools/StaticTools.fs`（约 29、99）。`ToolRegistry` 不产出 sphinx spec。`host-boundary/WHAT.md`（约 118）声明 `Sphinx.Host.Adapter`，src 无实现。可运行入口：`Sphinx/V2/Hosts/Mcp/`（七件套，`apiVersion="2"`）；`Sphinx/V2/ServeEntry.fs · serveDefault`。资源 `resources/provider/tool/sphinx/` 存在。`GAP-219` PARTIAL（`GAP.md:260`）、`GAP-222` OPEN（`:263`）。

```
Algorithm S（Sphinx 恢复）。输入：遗骸证据与 git 历史。输出：裁决与恢复路径。

S1. [考古.] 运行 `git log --all -- src/Wanxiangshu/Sphinx/**`。找 ToolSpec 注册与 adapter
    的历史实现。
S2. [定论.] ▷ 定目标接口：原生工具面（注册 ＋ adapter ＋ 资源），或保留 MCP-only。
    遵守 clean-break：不复活旧内核。
S3. [恢复.] 按裁决 cherry-pick 或最小重建。
S4. [改导航.] 更新 `host-boundary-026`、`office-capability`、`capability-enforcement` 测试、
    `GAP-219/222`。
S5. [验证.] 跑 sphinx-v2、host-boundary、capability-enforcement。
S6. [终止.] 算法终止。
```

- 验收：恢复路径可运行且有测试。GAP 状态更新。

### WP-040 · L846 · query-shell 丢弃

- 类型：删除。波次：W5。
- 现状锚点：`src/Wanxiangshu/OpenCode/Tools/ExecutorTool.fs · queryShellAdmission`（约 393）、`queryShellSpec`（约 415-418）；`ExecutorTool.fsi`；`ExecutorToolSurface.fs`。资源 `resources/provider/tool/query-shell/` 已不存在。`process-execution/tests/011.test.mjs` 有两条 TODO（GAP-091）。`GAP.md:127` 记 PARTIAL。

```
Algorithm D（丢弃 query-shell）。输入：spec、admission、surface。输出：无残留。

D1. [删.] 删 spec、admission、surface 导出。
D2. [改测试.] 按裁定删除两条 TODO 用例，或改为「不存在」断言。
D3. [改导航.] 更新 GAP-091、`tool-map.json`、静态名单。
D4. [验证.] 跑 process-execution。
D5. [终止.] 算法终止。
```

- 验收：全仓无 query-shell 残留。GAP 更新。

### WP-041 · L853 · CHANGELOG 遗骸丢弃

- 类型：删除。波次：W5。
- 现状锚点：`CHANGELOG.md`（约 196-200）整节「todowrite 列表的 provider 名与 Host 名分离」。该改动已由 `c8a742d23` 撤销（文件删除，`todos` 恢复宿主原状）。

```
Algorithm D（丢弃旧条目）。输入：CHANGELOG 旧节。输出：删除。

D1. [删节.] 删除该节。
D2. [核对.] `action-affordance/tests/015`（断言宿主原样 `todos`）与
    `cognitive-workspace/007`（无 `TodoWriteCompressionContract`）保持绿。
D3. [同步.] 与算法 F1 同步指南附录 A3。
D4. [终止.] 算法终止。
```

- 验收：旧节不存在。两处测试绿。

### WP-042 · L863 · envelope 手动更新

- 类型：实施。波次：W1。
- 目标：可以派生，但不自动派生。不检查是否改变。只允许手动更新。
- 现状锚点：`scripts/build.mjs · verifyArtifacts`（约 131-135，每次构建自动派生）；`scripts/lib/derive-loop-detector-envelope.mjs · writeLoopDetectorEnvelopeArtifact`（约 296-310）；`requirements/degeneration-guard/tests/004.test.mjs`（含「current runtime envelope matches a fresh derivation」集成用例）；`package.json · imports`（`#wanxiangshu-loop-detector-envelope`）。

```
Algorithm M（手动 envelope）。输入：自动派生链。输出：手动更新流程。

M1. [定论.] ▷ 定产物存放与生成时机。选项 A：显式命令生成；构建缺产物时报错并提示命令。
    选项 B：产物入库，构建复制到 `dist/`。
M2. [改构建.] 删构建期自动派生，或改为显式 flag。
M3. [改测试.] 删「检查是否改变」的集成用例，或改为手动审计命令。
    保留选择器、字节过滤、并行分词等用例。
M4. [写文档.] 写手动更新流程。
M5. [终止.] 算法终止。
```

- 验收：构建不再自动派生。测试集与文档一致。
- 风险：本文与手册目录都是新增 tracked `.md`，会改变派生值。本卡完成前，构建产物与派生值保持一致。

---

## §7 验收矩阵

| 波次 | 必跑套件 | 门禁 |
|---|---|---|
| W0 | 无（只出裁决记录） | 无 |
| W1 | durable-events、durable-convergence、requirement-grounding、host-boundary | `node scripts/check.mjs`、`node scripts/build.mjs` |
| W2 | execution-model-routing、interaction-authority、crash-reconciliation、participant-horizon、context-compression、execution-failure-policy、provider-attempt-recovery、host-provider-failure-ownership、degeneration-guard | 同上 ＋ 语言对等门 |
| W3 | delegation、attention-regulation、concern-routing、capability-enforcement、office-capability、cognitive-workspace、behavior-diagnosis | 同上 |
| W4 | relay-assessment、relay-incumbency、relay-retirement、relay-context-projection、change-integration | 同上 |
| W5 | host-boundary、provider-projection、prefix-stability、speculative-investigation、process-execution、sphinx-v2、capability-enforcement | 同上 |
| W6 | 全量：`npm run format-build-test` | `npm run verify:release` |

单文件测试：`node --test requirements/<包>/tests/NNN.test.mjs`。
单元套件：`node requirements/verification-system/tests/run.mjs`。

---

## §8 协作规程

### 8.1 角色

| 角色 | 职责 | 限制 |
|---|---|---|
| 集成人 | 管 `master`。管共享文件：`INDEX.md`、`GAP.md`、`CHANGELOG.md`、`README.md` | 不单独裁决产品语义 |
| 施工人 | 一张卡一个负责人 | 同卡不得兼任评审人 |
| 评审人 | 独立复核：读 diff、跑套件、查证据 | 不代替施工人改码 |

一人可兼多角色。同一卡的施工与评审必须分工。

### 8.2 会议

- 开波会：算法 W1。
- 波末验收：算法 W4。
- 裁决会：W0 卡按算法 J 逐张过会。

### 8.3 提交与记录

- 一次卡一次提交。信息含卡号与行号。
- 记录写进 `proposals/`。含：红例输出、绿例输出、命令、通过数、剩余边界。
- 「预计红 / 绿」不得写成证据。未跑过的写「未验证」。
- 失败先查根因。记录根因与修复。
- 局部绿不得写成整波完成。

---

## §9 附录

### A. 讨论记录模板（W0 用）

```text
# <主题> 裁决记录（<日期>）

- 工作包：WP-NNN（Lnn）
- 现状锚点：<路径 · 符号>（行号）
- 意见原文：<原文>

## 证据
1. <事实>。来源：<路径:行号>。
2. …

## 三问
1. 现状防住哪类真实错误？<答案>
2. 去除后哪条 WHAT 条款失守？<条款号>
3. 更简单的实现？<方案或「无」>

## 结论
<保留 | 修改 | 删除>
理由：<三条以内>
剩余边界：<未决事项>
```

### B. 禁止清单

1. 禁止 `dotnet build`。
2. 禁止自动化批量改码。
3. 禁止削弱断言、放大超时、重跑撞绿。
4. 禁止只改测试让旧实现变绿。
5. 禁止在 `master` 上 force push。
6. 禁止把未验证写为「已通过」。
7. 禁止用 fail-closed 掩盖测试不足。
8. 禁止删除或改写与本次无关的用户改动。
9. 禁止把局部完成写成整波完成。
10. 禁止条款冲突时各自改测试。

### C. 文件编号约定（000-999）

- `000`：本手册。
- `001`—`042`：留给逐条意见的扩展卡（编号与 WP 一致）。
- `100`—`199`：W1 施工记录。依此类推到 `600`—`699`。
- `900`—`999`：收尾与汇总。

### D. 已知耦合

- `degeneration-guard-004` 的经验包络当前从全部 Git-tracked 源文档派生。新增 tracked `.md` 会改变派生值。本文即属此类。WP-042 完成前，新增文档后需重建产物。
- `requirements/GAP.md` 有两种表头（8 列主表与 4 列简表）。新增行用 8 列表头。
- `.git/wanxiang/events/` 当前约 566 MB。大文件读路径的性能由 WP-023 处理。

### E. 覆盖率校验

```
Algorithm Z（覆盖率校验）。输入：指南与本文。输出：无遗漏断言。

Z1. [逐行核对.] 对照指南，逐行勾对 §4 台账。行号清单见文件头。
Z2. [计数.] 台账行数 ＝ 42。工作包数 ＝ 42。
Z3. [断言.] 若缺一条，则停并报错。
Z4. [对偶.] 每条意见恰好一张卡。无合并，无拆分。
Z5. [终止.] 算法终止。
```

# 断言：42 行意见与 WP-001 到 WP-042 一一对应。无遗漏。
# 万象术对 OpenCode 的功能增强（过程规范）

- 范围：本仓 `wanxiangshu` 插件在 OpenCode 之上叠加的全部实质性功能。
- 写法：Knuth 式伪代码。文字用简体中文，短句，一句一事，主动语态。标识符、Hook 名、字段名、文件路径保持原文。
- 依据：`src/Wanxiangshu/**` 源码与 `requirements/<包>/WHAT.md`。写作基线 `0726b76e5`（2026-10-08，工作树干净）。
- 用法：本文是阅读地图，不是权威规范。权威规范在 `requirements/`。二者冲突时以 `requirements/` 与源码为准，并修正本文。

## 记号

- `procedure 名称(输入)`：一个过程。步骤按序执行。
- `← 来源`：该过程绑定的 OpenCode Hook 或事件。
- `#` 后为条件说明。
- `→` 表示结果或后果。
- 括号内给出规范包与条款号，供查证。

---

## 0. 总不变量

`procedure Invariants()`

1. 插件不改宿主机源码。不补丁、不 fork、不 import 私有模块、不做 monkey patch。全部集成只经公开 Hook 与 SDK。（host-boundary-018）
2. 业务不消费流式碎片事件。碎片只在最早边界过滤成粗粒度唤醒信号，业务事实一律读完整快照。（host-boundary-001）
3. durable 事实是唯一真相。内存投影是派生物。先落盘，再更内存。（durable-events-001）
4. 每个外部副作用分「请求意图」与「物理确认」两段记录。未知结局保持未知。（effect-accounting-001/002）
5. 失败先归入封闭类型，再由唯一纯策略一次裁决。（execution-failure-policy-001/002）

---

## 1. 装载期

`procedure Boot(input)`

1. 解析全局语言一次。（provider-language-004）
   - 阶梯：`WANXIANGSHU_PROVIDER_LANGUAGE` → 宿主配置 `language` → 本地化环境探测 → English。
2. 装载 `resources/`。
   - Common Law、Role Law、Office Library、Tool Law、Delegation Law。
   - Enforcer 规则书：`resources/enforcer/<tip>/`，当前 120 条。
   - 消融图：`resources/ablation/{nodes,profiles,fact-map,tool-map}.json`。
   - 缺失或非法 → fail fast。不用代码内置副本兜底。（distribution-006）
3. 读 `~/.config/opencode/wanxiangshu.mjs`。
   - 缺文件 → 原子写推荐模板；create-if-absent，已有文件永不覆盖。
4. 建 journal。路径为 `<git-common-dir>/wanxiangshu-next/runtimes/`。取不到 common dir 时退到 XDG state home。（action-affordance / persistence）

> {开发者认为应该统一使用 .git/wanxiangshu/... }
> 状态：已落实（WP-001，`d5f0c9366`）

5. 注册全部 Hook，并逐行登记 Hook 元数据。（host-boundary-024）
   - 每行声明 criticality、允许的 context/effect、retry 权限、容量 owner、失败处置。
6. 只做资源解析、静态校验、Hook 注册。
   - 不调用宿主业务接口，不恢复崩溃，不写业务事实。（host-boundary-021）

---

## 2. 会话准入 

> {开发者认为有简化空间——如果按照存在即合理的逻辑，是否能简化架构，还是说反而造成了退化？开放讨论}
> 状态：裁决保留（WP-002）

`procedure AdmitChat(input, output)` ← `chat.message`

1. 解码 `SessionId` 与物理消息 id。
   - 二者必须唯一、无歧义。类型非法或两处取值冲突 → 整体 fail closed。（host-boundary-015）
2. 判定来源，取值只限四类。（interaction-authority-005）
   - `AuthorityRoot`（含 `HumanRoot`、`AgentOwnerRoot`）、`Continuation`、`HostInternal`、`UnknownOrigin`。
3. `UnknownOrigin` → 拒绝。不建执行，不升权。（interaction-authority-007）
4. 写 durable `Accepted`。
   - 携带 exact key `(SessionId, PhysicalUserMessageId)`、participant 身份证据、来源证据。
   - 不携带尚未存在的 `ProviderRun`。（managed-chat-execution-002）
5. 取容量租约，再把输入投影进宿主。
   - 顺序固定：`resolve pre-provider identity → durable Accepted → acquire exact capacity → project into Host → commit → provider effect`。（managed-chat-execution-003）
6. 物理准入本身关闭上一终态的 idle 发送窗口。
7. 已有同一逻辑执行的输入是「追加材料」，不是新任务。
   - 保留旧租约；宿主下一次 provider 边界实际选入新材料后，才交接容量。（managed-chat-execution-003）

---

## 3. 参数屏障

`procedure ValidateParams(input, output)` ← `chat.params`

1. 只读查 exact 已提交租约。
   - 不调调度器，不发 fence，不建第二份绑定。（host-boundary-033）
2. durable 已接受执行 + 有租约 → 校验 agent 与 model/reasoning 不漂移。

> {开发者认为 agent 与 model/reasoning 不漂移应该由测试期强 fast-check 保证，运行时作为不变量但不再校验。}
> 状态：已落实（WP-003，`5fcb495b3`）

3. durable 已接受执行 + 无租约 → fail closed。拒绝时宿主输出不被改写。
4. 无 durable 已接受执行 → 完全留给宿主。
   - 宿主压缩、标题生成、辅助子会话都走这条。
5. 校验通过 → 对 managed 请求投影 `temperature = 1.0`。
   - 非 managed 会话保持原样。

---

## 4. 消息投影

`procedure Transform(input, output)` ← `experimental.chat.messages.transform`

1. 判定分支。
   - 会话是只读副本 → 走副本分支（第 2 步）。
   - 否则 → 走普通分支（第 3 步）。
2. 副本分支：
   1. 应用 XWire 前缀选择。
   2. 冻结 provider attempt plan。
   3. 还原被剥掉的协议字段。
   4. 交给副本运行时处理。
   5. 清洗输出消息。
3. 普通分支，按下述固定次序执行。次序是合同，不得重排（host-boundary-019）：

> {开发者认为此处可以重构成洋葱式 decorator 模型，而不是大泥球架构}
> 状态：已落实（WP-004，`2575c5baa`）

   1. 开物理 provider attempt。这是 durable 证据门。
   2. 绑定会话起点时间。失败 → 终止会话。
   3. 结算并替换延迟检查结果；做 Relay 投影切分。
      - 退休旧请求在此拦截：清空消息，释放其 provider 步准入，等旧 attempt 中断完成。（relay-context-projection-002）
   4. 重放 Strength 决策。
   5. 从进程内 vault 还原协议字段，供下一步读取。
   6. 捕获 XTrace。这是唯一的 append-only 语义历史。（semantic-trace-001）
   7. 提交已追踪的重放。
   8. 刷新 Companion 的 XTrace。
   9. 投影 Companion 材料。
   10. 应用 XWire 前缀选择。
       - 选中前缀探针 → 本次请求为 tentative cold horizon。（prefix-stability-003）
   11. 冻结 provider attempt plan；确认宿主真实 assistant 身份与 durable `ProviderStarted`。
   12. 应用 Enforcer 续行决策。
   13. 仅当 horizon 为 Current 时，依次做：
       1. 注入 Pair 指引。
       2. 投影 requirement grounding。
       3. 捕获并启动只读委托。
       - horizon 为 TentativeCold → 跳过本步，避免旧 horizon 材料穿透新前缀。（context-compression-019）
   14. 注入 Blogger 纪事文本。
   15. 重放替换后的检查结果。
   16. 清洗输出消息。
4. 每个物理 payload 只渲染一次。指令在前，数据在后。（provider-projection-014）

---

## 5. 系统提示投影

`procedure SystemTransform(input, output)` ← `experimental.chat.system.transform`

1. 读全局语言。
2. 若会话是 Bookkeeper，或角色提示属于本插件：
   - 原地改写为当前语言的 Role Law 与 Office Library 组合。
3. 只按字节识别自己的段落。
   - 不翻译，不发明散文。（provider-language-009）
4. 若会话是只读副本：
   - 追加该语言的只读调查约束行。
5. 语言变化立即生效于下一次渲染。已在进行中的会话同样切换。（provider-language-013）

---

## 6. 工具定义

`procedure DefineTool(toolInput, toolOutput)` ← `tool.definition`

> {开发者认为此处可以重构成洋葱式 decorator 模型，而不是大泥球架构，并且洋葱式作为 higher-order design pattern 与之前的进行 DRY}
> 状态：已落实（WP-005，`1d38aa294`）

1. 若工具是 `js-manager`：
   - 追加必填 `contract` 字段，取值 `do-not-use-except-for-review`。
   - 该字段只作提示；本地对缺失或错误取值乐观处理。（host-boundary-032）
2. 若 Predictor 已配置：
   - 对参与工具追加必填 `estimated_readonly_rounds`，与可选 `self_note`。
   - 在原描述之后幂等追加估计说明。（speculative-investigation-001/012）
3. 参与工具集由 `InvestigationEstimateContract.classifyTool` 单一判定。
   - 12 个参与工具，29 个显式不参与工具，其余为未判定。
   - 禁止前缀匹配与名字模糊匹配。（speculative-investigation-016）
4. 内建工具的参数是 Effect schema、`jsonSchema` 为空时：
   - 用宿主同版本的渲染器产出 JSON 视图。
   - 只装饰 JSON 视图；`parameters` 保持原样，供宿主解码。
5. 未配置 Predictor → 零增量。不装饰、不追加说明。

---

## 7. 工具执行门

`procedure ExecuteTool(spec, args, ctx)`

> {开发者认为此处可以重构成洋葱式 decorator 模型，而不是大泥球架构，并且洋葱式作为 higher-order design pattern 与之前的进行 DRY}
> 状态：已落实（WP-006，`d2bf95fcf`）

1. `tool.execute.before` 阶段：
   1. requirement grounding 决策。修改触碰受覆盖路径时补入规范。（requirement-grounding-007）
   2. 评审权限校验。按 Manager 当前 facts 收口。
   3. 校验估计参数。非法 → 拒绝，不触发进程级 fuse。
   4. 记录协议字段原件到进程内 vault。
   5. 从参数中剥掉协议字段。
      - 剥除与还原用属性描述符完成。键序与对象身份不变。（host-boundary-032）
2. 运行时门链，按序判定：
   1. 消融图禁止该工具 → 拒绝。
   2. 会话是只读副本 → 只放行 `js-predictor`，其余拒绝。（capability-enforcement-005）
   3. 解析 office 准入。
      - 角色无权 → 拒绝。
      - 角色未定 → 先补认定。仍失败 → 拒绝。（capability-enforcement-010）
   4. Manager 工具按当前 facts 收口：
      - 无 active 任期 → 全拒。
      - 清理阻塞或持有效证书 → 只留 `join` 与 `suicide`。
      - 已接纳评审 → 去掉 `js-manager`。（capability-enforcement-025）
   5. private attachment 工具按附着证据准入，不经公开 office。
      - `chronicle`、`js-bookkeeper`、`js-predictor`。（capability-enforcement-006）
3. `tool.execute.after` 阶段：
   1. 还原 vault 原件。异常路径同样还原。
   2. 原生 `read` 结果触发 grounding 注入。
   3. Casebook 观察。失败只发诊断，不改关键结果。（host-boundary-024）
4. provider 可见 schema 与运行时门禁读同一份 capability 真源。
   - schema 是固定上限投影；门禁是当前事实投影。（capability-enforcement-002）

---

## 8. 工具面

> {开发者认为此处可以重构成洋葱式 decorator 模型，而不是大泥球架构，并且洋葱式作为 higher-order design pattern 与之前的进行 DRY}
> 状态：已落实（WP-007，`b2d0ff36b`）

`procedure Tools()`

按角色注册下列工具。权限矩阵与可见 schema 同源：`OfficeCapability.permissions → ToolPermission → 工具名`。

| 组 | 工具 | 准入 |
|---|---|---|
| 编排 | `commission` | Orchestrator |
| 编排 | `fork`、`resume` | Manager |
| 编排 | `join`、`horizon` | Manager、DevOps |
| 评审 | `review`、`suicide` | Manager（按当前 facts 收口） |
| 分裂 | `fission` | 仅 Engineer |
| 执行 | `run` | 仅 DevOps |
| 执行 | `open-terminal`、`send-terminal`、`read-terminal`、`signal-terminal` | 仅 DevOps |
| 文件 | `mv`、`rm` | Engineer、DevOps |
| 认知 | `assume`、`defer` | 除 Blogger、Distiller 外 |
| 通信 | `publish` | 除 Blogger、Distiller 外 |
| 监督 | `chronicle` | 仅 Blogger（按附着） |
| 记忆 | `fetch` | Engineer（有 Fetch 权能） |
| 记忆 | `js-bookkeeper` | 私有附着 |
| 编程 | `js-engineer`、`js-devops`、`js-manager` | 按角色能力生成 |
| 编程 | `js-orchestrator`、`js-blogger` | deny 投影（保留在权限映射，不生成工具） |
| 编程 | `js-predictor` | 仅只读副本（私有附着） |
| 陷阱 | `bash-honeypot` | 有该权能的角色 |

1. `bash-honeypot` 只返回明确拒绝。永不执行 shell。
2. 原生 `bash` 对所有 managed 角色保持拒绝。
3. `chronicle` 参数：必填 `charge`、`occurrence`、`settlement`、`consequence`、`tip`；可选 `evidence`。（behavior-diagnosis-006）
4. `review` 参数：八个评分维度，取值只限 `PERFECT | REVISE | N/A`；可选 `note`。（relay-assessment-001）

---

## 9. 编程面

`procedure JsSurface(role, capabilities)`

1. 能力集为空或无文件系统能力 → 不生成 `js-*` 工具。（repository-programming-001）
2. 四层同构：能力集 → 基类成员 → 工具描述 → 示例 → 运行时门禁。
   - 缺一项能力，四层都不可见、不可调。（capability-enforcement-008）
3. 生成确定性：同一角色与能力集生成字节相同的工具名、schema、描述、基类与示例。
4. 程序在沙箱内运行。
   - 只拿显式纯数据与受控原语。不直接拿文件系统、网络、进程、环境变量。
   - 有硬超时、内存上限、输出上限。（repository-programming-006）
5. 一次调用一个事务。全部修改先暂存内存。
   - 顺序：全部预检 → 持久化 Prepare → 按规范路径顺序写入 → 持久化 Commit → 返回成功。
   - 任一写入失败 → 全量回滚。（repository-programming-013）
6. 冲突检测用读取快照指纹。
   - 读取过的文件或写入目标被外部改变 → `FILE_CHANGED`，不自动重读、不重试。（repository-programming-014）
7. 返回值限 JSON 兼容子集。
   - 非法值在提交前以 `INVALID_RETURN_VALUE` 拒绝。（repository-programming-011）
8. 事务事实写统一 EventStore：`JsTransactionPrepared`、`JsTransactionCommitted`。

---

## 10. 事件与调和

> {开发者认为此处完全是为了自身自洽，没有用户可见的作用，因此需要做 KISS 评估}
> 状态：裁决保留（WP-008）

`procedure ObserveEvent(raw)` ← `event`

1. 归一信号到闭集。（host-boundary-002）
   - `SessionIdle`、`ProviderRetry`、`ProviderFailure`、`SessionDeleted`、`AttemptAborted`。
2. 调和器单飞。收到信号后读一次完整快照。
   - 无信号或明确投影边缘时不轮询。（host-boundary-005）
3. `TurnUnknown` 只作调和器私有观测。不发布为终态。（host-boundary-004）
4. 每次 turn 按精确物理身份结算，只发布 `Completed`、`Failed`、`Aborted`。
   - completion cell 单次赋值。晚到 terminal 幂等忽略。（delegation-025）
5. `todowrite` 终态：
   - 收到 exact `message.part.updated`，且 `status = completed` → 追加一条 `TodoCheckpointCommitted`，键为 exact call id。
   - 重复终态幂等。`error` 只关闭候选，不推进压缩。（obligation-ledger-005 / effect-accounting-008）
6. 订阅与释放：监听器释放后彻底停止投递。`run-scoped Failed/Aborted` 与 `Completed` 一样保留 Authority Root 因果身份。（host-boundary-016）

---

## 11. 配置投影

`procedure Configure(config)` ← `config`

1. 关快照写入：`config.snapshot ← false`。
2. 同步宿主语言设置到全局语言阶梯。
3. 建缺的托管 agent 条目。
   - `manager`、`orchestrator`、`engineer`、`devops`、`blogger`、`bookkeeper`、`predictor`。
4. 只写本插件拥有的字段：`mode`、`permission`、`hidden`、`prompt`、`temperature`。
   - 不写 `model`。模型路由的权威只有 MJS 调度器。（execution-model-routing）
5. 拒收旧名：无前缀名、`build`、`plan`。
6. 目录校验失败也要先写全量 deny 默认。
   - 覆盖宿主宽松默认，再报错。不得静默丢权限写入。（capability-enforcement-010）
7. 设 `compaction.auto = false`。
8. 设 `experimental.chatMaxRetries = 0`。（host-provider-failure-ownership-001）

---

## 12. 压缩门

`procedure Compaction()`

1. `experimental.session.compacting`：钩子不能否决。记录一次压缩开始事件（诊断）。
2. `experimental.compaction.autocontinue`：恒写 `enabled = false`。
3. 首轮探针：
   - 首个 managed 会话第一回合出现压缩伪 run → 拒绝启动。
   - 理由：首回合必然远低于任何阈值，出现压缩说明宿主另有压缩实现。（host-boundary-007）
4. 观测到压缩事实 → 立即重锚：
   - 追加 `ContextReanchored`：epoch 加一、清空 Snapshot、PrefixCoverage 归零。
   - 同一 run 不重锚两次。（prefix-stability-006）

---

## 13. 模型调度

`procedure Route(role, running, previous, purpose)`

1. 权威只有 `~/.config/opencode/wanxiangshu.mjs`。
2. 要求 `routingProtocol = 2`。三参旧配置明确拒绝，并给出迁移错误。
3. 调用纯函数：

```text
route(role, running, previous, purpose) -> { model, reasoning } | null
```

4. 参数含义：
   - `role`：由 IdentitySeed 确立、logical run 内不可变的 canonical role。
   - `running`：跨 plugin 实例共享的活跃租约 multiset。
   - `previous`：被原子取代的当前活跃执行 target，余者为 null。
   - `purpose`：`normal` 或 `readonly-delegate`。用途不改变角色与 participant。
5. 返回目标 → 取租约。返回 `null` → 背压等待，不是失败。（execution-model-routing）
6. runtime 只维护真实租约与串行仲裁。
   - 候选池、容量上限、失败 provider 标记全在 MJS。
7. 容量 exact identity 严格等于 `SessionId + PhysicalUserMessageId + Role + Participant + target + fence`。
8. 容量 fence 精确消费。
   - 失败、取消、supersede、fatal 都不得用计数减一或 session-wide release 代替。（execution-failure-policy-004）
9. 固定 DevOps 的模型绑定在道路初始化时确立并持久化。
   - resume 与恢复严格沿用既有绑定。严禁借 resume 换模型。（execution-model-routing-019）
10. 只读委托从 Predictor 池取目标。
    - 不换角色，不换 participant，不继承 owner 的 target。（execution-model-routing-002）

---

## 14. 只读委托

`procedure DelegateReadonly(batch)`

1. 启用条件唯一：Predictor 槽位已配置。
   - 未配置 → 零可见增量：不装饰 schema、不追加说明、不产生授权、不建副本。（speculative-investigation-001/014）
2. 批次封口：
   - 批次取自同一个真实 provider 响应内的完整工具调用集。
   - 全部调用都有恰好一个 result 后，批次才封口。
   - 结果乱序到达时按原始调用顺序归一。
3. 取参与子集的最大值：
   - 全 0 → 只记录明确零估计，不建副本。
   - 有正数 → 授权轮数 N 取最大值。0 不否决同批其他调用的正值。
4. 授权绑定：DecisionId、owner logical run、来源 provider run、全部 call id、N、契约版本。
5. 常驻副本：
   - 每个 owner 一个副本，随 owner 存活复用。不随单次决策结束。
   - 新决策只把 owner 自上次位点以来的 delta 追加进副本。已镜像前缀不重发。
6. 副本可见工具只有 `js-predictor`，能力严格限定 `{Read, Glob, Grep}`。
7. 回传材料：
   - 保留真实的 call/result 配对、原始参数、真实结果与内容 digest。
   - 工具名投影为主人的 `js-<role>`；digest 与持久化 payload 保持原名。
8. 消费证明：
   - 只有协调后的轮次证据明确证明目标 provider run 产生真实非空输出，才追加 `Promoted`。
9. 预算按真实外发请求记账。
   - 上限是安全门禁，不是必须做满的配额。（speculative-investigation-003）
10. 关闭路径：`DelegationClosed`。终止只来自显式因果事件：达上限、真实终态、取消、按原因关闭。

---

## 15. 委托原语

`procedure Delegate()`

1. `fork(calling, name, charge, keywords?, attach?, expected_tool_calls?)`
   - 必填 `calling`。Manager 只能 fork `engineer`。（delegation-003）

> {开发者认为 calling 其实可以不写，如果 name != devops 则 calling 必然等于 engineer。}
> 状态：已落实（WP-009，`e20725307`）

   - `name` 解析为角色与身份。
   - `keywords` 只对 Engineer、DevOps 生成低信任定位提示。
     - 上限：8 关键词、每词 4 条、共 24 条、64 KiB。（repository-investigation-007/009）
   - `attach` 只把指定同伴的历史 WorkRecord 作只读背景，不克隆 authority。

> {开发者质疑 keyword, attach 功能的必要性}
> 状态：裁决保留（WP-010）

   - 成功只表示该 Byname 已承接 charge。返回不携带物理拓扑。（delegation-005/006）

2. `resume(name, charge, keywords?, attach?, expected_tool_calls?)`
   - 续做既有道路。复用该 participant 的完整历史与 binding。
   - 忙碌时：把 charge 作为 `BusyAgentNudge` 追加到既有 LogicalRun。
     - 在宿主保存后进入下一次尚未开始准备的 LLM 请求。
     - 不中断当前输出或工具。保留原 Root、身份、完成订阅与 handoff frontier。（delegation-027）
   - 传 `calling` → typed 拒绝。（delegation-003）
   - 固定 DevOps 的 `name` 常量是 `devops`。

3. `commission(calling?, name, charge, expected_tool_calls?)`

> {开发者认为 calling 其实可以不写}
> 状态：已落实（WP-011，`e20725307`）
   - Orchestrator 专用。只委任 Manager。（office-capability-012）

4. `join()`
   - 按上限批量消费当前 owner 可用的完成项。稳定排序，逐项 CAS 消费。
   - 用户输入只打断等待，不取消 child。返回 `Interrupted`，不作为错误。（delegation-015）
   - 无 journal 的纯 PTY 模式才以进程内 active run 计数作为工作信号。
   - 有 journal 时，重启后收养的空闲伴随句柄不阻塞 join。


5. `horizon()`
   - 拉取式名册快照。只在调用时读一次。不轮询、不推送、不订阅。
   - 只显示稳定 Byname 与 TerminalName。不暴露底层 id。（participant-horizon-010/011）

> {开发者认为底层 id 的概念本身都应该去掉，因为本身有系统 id，原有的底层 id 只是脚手架}
> 状态：已落实（WP-012，`9e087469d`；可见面核对零改动）

   - 内部角色（Blogger、Bookkeeper、Predictor 等）永不出现。

6. 工具面不暴露机器拓扑。
   - 参数与结果不含 SessionId、AgentId、worktree、reused 等物理标识。（delegation-005）

---

## 16. 裂变

`procedure Fission(prompts)`

1. 准入需同时成立：（intra-participant-parallelism-017）
   - 已证明 canonical role 为 Engineer。
   - 本次执行授权含 Fission。
   - 来源是有物理 Host parent 的 subsession。
   - 没有 active group。
2. 每个 prompt 对应一条 lane，数量 N ≥ 2。
   - 完整保留每项 prompt 的字节、换行与格式。不二次拆分，不静默丢弃空项。（intra-participant-parallelism-002）
3. 全部 lane 原子准入。
   - 每条 lane 新建独立 Host session，`parentID` 与原 caller 相同。
   - 任一创建、绑定或初始化发送失败 → 回滚全部已建 lane。（intra-participant-parallelism-004）
4. 全部 lane 建立并准入后，才静默退休被替代的旧物理 caller。
   - 不发布业务 Aborted completion，不触发故障恢复。（intra-participant-parallelism-005）
5. completion 归属：
   - 准入前未决的子任务归 logical owner 共同所有。每项 completion 向每条 lane 恰好交付一次。
   - 准入后新子任务只由发起它的 lane 消费。（intra-participant-parallelism-006/007）
6. 归并：
   - 以 lane index 为工作记录唯一 key。同键同内容合并幂等，内容冲突拒绝。
   - 按 canonical index 从 lane 0 到 N−1 环行合并。终点 N−1 接受最终接管。
   - 不同到达顺序必须得到相同 merge order 与 takeover lane。（intra-participant-parallelism-015）
7. 全部结算后，向逻辑父级只交付一次普通 terminal completion。
   - 写回原 participant 的 completion cell。（intra-participant-parallelism-009）
8. 同一 logical participant 同时至多一个 active group。活跃 lane 再次裂变 → 拒绝为 already-fissioned。

---

## 17. 任期与评审

`procedure ManagerRelay()`

> {开发者认为 Road 的概念也许是奥卡姆剃刀的使用对象}
> 状态：裁决保留（WP-013）

1. 每条 open Road 至多一个 active 任期。（relay-incumbency-001）
2. 开启任期用统一的 `IncumbencyOpened(IncumbencyId, WorkspaceSnapshotId)`。
   - 首任同时建立 Road。（relay-incumbency-002）
3. 新任期先独立评审接手的工作。不做规划先行。

> {开发者认为，独立评审的对象不是“上一任的工作”，而是写出 1. 目标状态的形式化 2. 当前与目标的 GAP 3. 是否 GAP 非空集。八维评估只是对 1 & 2 & 3 的抓手，而不是死板标准。}
> 状态：已落实（WP-014，`30f53445e`）

4. `review(八维)` 每维只取三态。

> {开发者认为应该让 review() 返回 [(验收标准, 工作计划)...] 的 array of pairs，而不是八维向量。终止条件是，返回空集。提示词中建议原则上第一个 Manager 不返回空集。}
> 状态：已落实（WP-015，`30f53445e`）

| 维度 | 字段名 |
|---|---|
| 语言与算法 | `language_algorithms` |
| 简单性 | `simplicity` |
| 结构 | `structure` |
| 粒度 | `granularity` |
| 测试与证据 | `tests_evidence` |
| 逻辑可靠性与边界 | `logic_reliability_boundaries` |
| 调用方使用体验 | `caller_ergonomics` |
| 完整性 | `completeness` |

5. 评级后果：
   - 任一 `REVISE` → 不通过。每个 REVISE 维度直接定义一项修复义务。
   - 评估者原位接责。不创建返工链，不建新任期。（relay-incumbency-004）
   - 全 `PERFECT/N/A` → 生成证书。本任立即失去工作区修改能力，只留读、清理与 `suicide`。（relay-assessment-005）

> {开发者认为，即使通过评审，也允许当前 Manager 继续做收尾工作，只是立刻通知他就是最后一任，负责收尾，他将明知自己是最后一任并且后继无人。}
> 状态：已落实（WP-016，`7ebe9e749`）

6. 同一任期至多一次评估。
   - 重放幂等；异载荷拒绝；已接纳评估后本任永久失去 review 能力。（relay-assessment-002/006）
7. 工作区变化使旧证据过期。
   - 快照一变，旧测试结果、评估与证书立即失效。（relay-assessment-010）

> {开发者认为无必要使用快照机制，此处乐观认为不会出问题即可。}
> 状态：已落实（WP-017，`694750f00`）

8. `suicide()`：唯一正常退场。
   - 先冻结本任新工作准入，再读精确递归 ownership。
   - 唯一业务 blocker 是递归 live 资源：child、后台作业、PTY/进程、活跃工具、副作用租约、未见终态的 cancel/join。（relay-retirement-001/003/004）
> {开发者认为第一次 suicide 时固定返回 review() 当时的承诺供核对，并要求 LLM 继续工作，附言：当你无任何工作可做时，调用第二次 suicide 以确认。第二次不再拦截。}
> 状态：已落实（WP-018，`694750f00`）

9. 原子提交退休事实与唯一结果 `Continue | Accepted certificateId`。

> {开发者认为之前的万象术处处要证书是形式主义。不变量应该由万象术流程 + fast-check 保证而不是靠运行时到处 fail-closed。}
> 状态：已落实（WP-019，`694750f00`）
   - 退休不可逆。已退休任期不得复活。（relay-incumbency-005/006）
10. 资源交接：
    - `Continue` 保留 Road 与逻辑执行。固定 DevOps、已接收工作与持久后台进程继续运行，交给后继接管。
    - 不得隐式孤儿化，也不得强制销毁。（relay-retirement-009）

> {开发者认为进程重启后的任何用户输入的新指令之前会自动 prepend 一次状态指导，描述万象术已经被重启，并点出这意味着什么，让 LLM 知情。}
> 状态：已落实（WP-020，`6e5cbd442`）

---

## 18. 监督循环

`procedure BloggerCycle()`

1. Blogger 的有效 system prompt：
   - 基础提示 + `# Enforcer Rulebook` 标题 + live 规则书全部条目全文。
   - 按 LexicalOrder 确定性拼接。同一集合合成完全一致的字节。（behavior-diagnosis-004）
2. 每个 Blogger provider run 必须恰好调用一次 `chronicle`。
   - 0 次或 2 次以上均属协议违约。（behavior-diagnosis-009）
3. `chronicle` 内容：
   - 必填 `charge`、`occurrence`、`settlement`、`consequence`、`tip`。
   - 可选 `evidence`：最小决定性原文摘录，≤ 1024 字符。
   - 渲染后的 UTF-8 文本 ≤ 512 KiB。（behavior-diagnosis-011）
4. 通过基数与 provider run 身份校验后原子提交 `BlogObservationCommitted`。
   - 同一事件承载：frame 追加、coverage 推进、单一 TipRuleId、provider/tool 身份、大文本 blob 引用。（behavior-diagnosis-012）
5. 每个已提交 cycle 恰好派生一个 RecentTip。容量有界（最多 8 项）。
6. 无 live cycle 的迟到调用：
   - 产生封闭的 `NoLiveCycle` 协议结果，终止过时会话。（behavior-diagnosis-006）
7. 无效 cycle 进有界协议修复：
   - 首发 nudge 只由完全静止的 idle terminal 发起，不在 transform 阶段发送。
   - 每个请求至多一次 nudge 机会。
   - 再次无效 terminal → 记录 confirmed failure，执行一次 AABB。（behavior-diagnosis-017）
8. 规则书冻结：
   - Blogger life 创建时绑定确定的 `RulebookRevision`。
   - life 存活期间 system prompt、`tip` 枚举、解码映射表、处置索引保持冻结。（behavior-diagnosis-018）

---

## 19. 指引交付

`procedure DeliverGuidance()`

1. 判定只依赖持久事实的投影。按 Main session 隔离。
   - 重启、恢复、重试保持确定。（guidance-delivery-004）
2. 判定：
   - occurrence 未交付，或规则全文已不在当前 horizon → 呈现全文，并原子记录交付事实。
   - 已交付且正文可恢复 → 只呈现稳定 `tip: <name>`。（guidance-delivery-002/003）
3. 承载位置：
   - guidance 只附在本次请求最新一条真实消息上。
   - 用户消息附于末尾，用无 `name` 属性的 `<system>` 包装；其余角色附于 terminal 工具结果的 `NUL+BOM` 后缀。
   - 最新消息不承载载体 → 本轮不生成新 occurrence。（prefix-stability-010）
4. 交付字节冻结。
   - 每个 occurrence 持久记录序号、CallId、放置点与实际 payload 字节。
   - 重放使用这些字节，不随规则版本改写。（guidance-delivery-011）
5. 交付不创建 authority。
   - 不伪造工具调用或用户消息，不创建 Interaction Authority Root。（guidance-delivery-009）

---

## 20. 上下文压缩与前缀

`procedure Compress()`

1. 不读上下文窗口大小。不预测溢出。不主动选择压缩点。
   - 真实 provider attempt 失败是唯一的恢复触发信号。（context-compression-001/002）
2. Blogger delta 渲染上限 200 KiB。超限确定性切块与截断。（context-compression-003）
3. checkpoint 来源：
   - 成功的原生 `todowrite` 形成 compression checkpoint。
   - `assume` 不形成 checkpoint。（cognitive-workspace-006）
4. 固定窗口 K = 3：

```text
N = 0：不给新 cutoff，沿用当前已提交前缀与原始尾部。
N > 0：j = max(1, N − K + 1)
desired cutoff exclusive = Bj
```

   - `Bi` 是包含第 i 个 checkpoint 的完整 semantic turn 起始边界。
   - 绝不在 call/result 中间切断。（context-compression-028）
5. 候选前缀：
   - 必须严格新于当前已提交 epoch。cutoff 不得回退。
   - 候选只进入当前 attempt 的不可变 profile。失败直接丢弃。（prefix-stability-003）
6. 提交：
   - probe 成功 → 先原子提交 `PrefixRebaseCommitted` 并继承 SealRoot，再允许下一次请求组装。
   - 同一 projection fold 内原子退休旧辅助注入可见性。（context-compression-011/019）
7. 永久 raw 例外：
   - 任何真实 Host `role=user` 消息永不改写、删除或重编码。
   - `assume` 的 call 与 result 原样穿透 LWR 保留。（context-compression-017/030）
8. 前缀稳定：
   - 同一 epoch 内，前一次 provider wire 必须是后一次的精确字节前缀。
   - 比较覆盖 provider、model、variant、完整 tools、system prompt 与 message 序列。
   - `tools` 必须完全一致。追加工具不算前缀稳定。（prefix-stability-001/013）

---

## 21. 持久化

> {开发者认为 ndjson 是唯一载体，不要产生额外的非内嵌的 payloads，否则不利于简单 GC。}
> 状态：已落实（WP-021，`734a6bce0`）
> {开发者认为按照 UTC 日期进行分组，昨天和今天保留，前天或之前无使用的内容即删除。日期只用于 GC，和语义无关。允许在日期之间移动数据，日期是“最近修改”语义。}
> 状态：已落实（WP-022，`fdfab580d`）
> {开发者发现随着时间和内容积累有性能问题，需要排查。}
> 状态：已落实（WP-023，`a0d3344c5`；数字对比未验证）

`procedure Persist(fact)`

1. 唯一 durable 载体：`<git-common-dir>/wanxiang/events/<WriterId>.ndjson`。
   - 一行一个自包含记录。canonical JSON + LF 结尾。
   - 键按 Unicode 码点升序递归排序。（durable-events-003/004/010）
2. 大对象写 `<git-common-dir>/wanxiang/payloads/<PayloadRef>`。
   - 事件提交前，其引用的全部 payload 必须已落盘且哈希匹配。（durable-events-012）
3. 每进程独占一个 writer 文件。文件不按体积、事件数或时间分段。
   - 进程退出后文件封存，不得被新进程接管。新进程新 WriterId。（durable-events-005）
4. 追加语义：
   - 提交成功与否只看本地字节是否存在。
   - 追加只碰字节：不创建 Git blob、tree、ref，不做 Git CAS。
   - 复杂度不随历史事件总数增长。（durable-events-006/017）
5. 查询：
   - 只读唯一 Integrator 维护的 `Current`。不手动全量扫描。
   - 多 writer 先按 retention 过滤整条流，再按 K-Way 因果合并。
   - 同一截止时刻与 writer 集合导出相同 Current。（durable-events-013/014）
6. writer 保留 24 小时。
   - 过期 writer 整条同时退出本地集合和远端快照。（durable-convergence-011）
7. Hook 安装：
   - 插件加载不修改 Git 配置。
   - 首次激活持久化能力才装 `reference-transaction` 与 `pre-push`。（durable-convergence-008）

> {开发者发现了 `reference-transaction` 与 `pre-push` 有严重的性能问题，已禁用。未来将优化性能修复缺陷后重开。}
> 状态：已落实（WP-024，`cda78e0d2`）

8. 同步：
   - 只由用户 Git 操作启动独立 Hook 进程。
   - Hook 读 `resources/git/wanxiang-hook.mjs`，发布 `refs/wanxiang/store`。
   - 远端只提供标准 Git 对象读写、引用推进与 CAS。不解释领域事件。（durable-convergence-009）
   - 无变化时在同步 transport 前返回。（durable-convergence-010）

---

## 22. 崩溃恢复

> {开发者发现 DevOps 在崩溃前的工作内容会泄露到 horizon。如果确实如此，应该重置。结果：horizon 应该为空，join 应该立刻返回无。这不是公理，是推论。}
> 状态：已落实（WP-025，`c9be6e3c7`）
`procedure Recover()`

1. 进程内状态全部丢弃：armed anomaly、`QuiescencePermit`、detector、容量租约。
   - 没有新证据就不产生新副作用。（crash-reconciliation-001）
2. 恢复输入只有两类：
   - EventStore 中已提交的不可变事件及其 fold 投影。
   - 宿主 SDK 快照、Git ref 等可信物理观察。（crash-reconciliation-002）
3. 加载期自行归位。无显式续传命令。（crash-reconciliation-018）
   1. 作废上一 runtime 遗留的活跃子 run：追加 `ChildWorkVoided`（有 scoped 准入身份时）或 `ExecutionFactCases.ChildWorkVoided`。
   2. 结算遗留的 Blogger open request：置 `BloggerRequestAbandoned`，原因 `stale-open-at-load`。
      - 本进程仍有同 RequestId live flight 的不结算。（crash-reconciliation-020）
   3. 按 durable 道路投影重新播种每条道路的固定 DevOps 模型绑定。
4. 工具中断不设隐式恢复 owner。
   - 不重放、不补写完成态。被中断的调用保持失败并原样留在可见历史。（crash-reconciliation-017）
5. 进程本地表只是缓存。
   - 「是否存在、属于谁、由谁执行」一律从 durable 投影回答。未命中则按需解析回填。（crash-reconciliation-021）
6. 固定 DevOps 崩溃恢复：
   - 同一道路至多一个可执行物理权威。
   - 未决物理命令按中断处理，不自动重放。
   - 沿用初始化时持久化的模型与 Persona。（crash-reconciliation-020）
7. 证据不足、冲突或缺失 → 停在 `Waiting`、`Blocked` 或 `RecoveryIncomplete`。不猜。（crash-reconciliation-005）

> {开发者认为："证据" 一词应该不要在生产环境出现，存在即合理，存在即证据，不要用 fail-closed 掩盖 fast-check 的无能。}
> 状态：已落实（WP-026，`d36d41b82`）

---

## 23. 失败恢复

> {开发者认为：不要用 fail-closed 掩盖 fast-check 的无能。本节是形式主义重灾区，要保持 user facing 的行为，但不要用“刻板实现”代替“优雅实现”。}
> 状态：已落实（WP-027，`5faaf81ea`）

`procedure ResolveFailure()`

1. 失败先归入封闭集合。（execution-failure-policy-001）
   - `LocalInvariant`、`ProtocolRejection`、`AuthorizationDenied`、`UserCancelled`、`Superseded`、`CapacityQueueFull`、`ProviderTransient`、`ProviderPermanent`、`AcceptanceUnknown`、`StreamInterruptedAfterFirstToken`、`PersistenceFailure`。
   - 自由文本只作诊断，不决定类别与后果。
2. 唯一纯策略一次给出互斥决议。
   - `PreserveCurrentFact`、`AwaitAcceptanceReconciliation`、`RetryFreshAttempt`、`TerminalizeAcceptedPreProvider`、`TerminalizeProviderStarted`。
   - 同时确定 breaker、容量结算与 fatality。（execution-failure-policy-002）
3. 宿主 `session.error` 一律解码为 `ProviderTransient`，交给本插件恢复。（execution-failure-policy-009）
4. 自动重试有界：连续失败预算默认 12。
   - 达上限 → 停止自动物理请求。后续恢复需要新 Authority Root 或显式用户动作。（provider-attempt-recovery-005）
5. 每次重试：
   - 换新 `PhysicalUserMessageId` 与 `ProviderRunIdentity`。
   - 保留 participant、system prompt、Role 与 Authority。
   - 只有精确的 typed failure licence 才授权发送。（provider-attempt-recovery-003/013/019）
6. 发送栅栏：
   - 宿主停止自动重试的唯一证据是精确 `(SessionId, ProviderRunIdentity)` 的 finalized errored assistant 投影。
   - 粗粒度 `session.error`、`session.idle`、其它 run 均不能授权发送。（provider-attempt-recovery-022）
7. 未启动的已接受执行必须定夺。
   - 有 typed resume capability → 用已接受材料恢复。
   - 否则 → 结为终态并报告该 turn 失败。（provider-attempt-recovery-023）
8. 耗尽只呈现一次。
   - 全部 provider/channel/family capacity 归零或预算耗尽后写 typed exceptional terminal。
   - 中间恢复不产生终态呈现。（host-provider-failure-ownership-006）
9. 退化防护：
   - 只消费宿主 assistant 的 text 与 reasoning delta。
   - 用 `gpt-tokenizer/o200k_base` token 维护指数衰减加权相异计数 D_t。
   - D_t 低于经验下界 → `TooRepetitive`；高于经验上界 → `TooRandom`。
   - 命中 → 中断当前 attempt 一次。
   - reconcile 消费匹配 anomaly 后，恰好发一次换表述的 continuation，返回 typed `DegenerationGuard` cause。（degeneration-guard-001/003/007/009）
   - 每次 provider attempt 用 fresh detector。中断后在同一原子边界把 scratch 恢复为初始状态。（degeneration-guard-006）
10. 中止不是失败：
    - 宿主 abort 解码为 typed `AttemptAborted` 控制面事件。绝不改写为 `ProviderFailure`。（crash-reconciliation-008）

---

## 24. 其他治理工具

`procedure Governance()`

1. `assume(assumption)`
   - 只接受必填 `assumption: string`。表示调用方已经完成抽象、接下来将据此行动。
   - 不求证、不持久化工作记忆、不写待办、不触发压缩、不授予权限。
   - 成功返回固定笃定提示，不回显 `assumption`。（action-affordance-014）

> {开发者认为 assume 从不被替换成 LWR 压缩版，这个规则可能已经实现。}
> 状态：已落实（WP-028，`ecdd82d7a`）

2. `enough(decision)`
   - 声明当前信息足以支持下一步。成功后停止重搜或重开同一判断。
   - 不证明决策正确，不创造权威事实。（attention-regulation-001）
3. `abandon(commitment)`
   - 放弃自行形成的计划、推论或假设。无须审批。
   - 不取消真实任务义务，不撤销用户授权，不删除工作产物。（attention-regulation-002）

> {开发者认为，这两个工具可以合入 assume，只是教育 LLM 可以把 assume 当作这两个用即可。}
> 状态：已落实（WP-029，`b15a3d01a`）

4. `defer(new_work)`
   - 把新发现的非阻塞工作登记为 DeferredWork。继续当前主线。
   - 不是活动义务，不自动委派或执行。（attention-regulation-003/004）
   - 下次成功 `celebrate` 时在结果尾部一次性弹出。

> {开发者认为对 manager 而言 defer 就是把某一项加入第一次 suicide 返回的待办事项里。而对 DevOps/Engineer 则是如果在自然终止时存在 defer 项，将 defer 作为 user prompt 触发回合，同时消灭 defer。}
> 状态：已落实（WP-030，`b15a3d01a`）
> {orchestrator 等其他角色如果有 defer 反推以上精神并照猫画虎定义。}
> 状态：已落实（WP-031，`b15a3d01a`）
> {开发者认为应该非常积极地在 Pair Hint 中鼓励 defer 的使用。}
> 状态：已落实（WP-032，`b15a3d01a`）

5. `subscribe(id, concern)` / `publish(id, message)`
   - 语义地址邮箱。同一 workspace 内 `id → concern` 永久不变。
   - pending 消息只在 owner 的下一次新 Pair Hint 聚合交付。
   - 公告与消息不创建或延续 user interaction authority。（concern-routing-001/004/005）

> {开发者认为任意 session 启动时自动 subscribe 自身名字 (名字就是上级对他的命名)，user-facing 的没有名字就叫 "root"，同时还有一个叫 "user" 的地址。}
> 状态：已落实（WP-033，`877b8b8ad`）
> {不再有 subscribe 工具，只有 publish 工具，publish 给其他 agent 就是 fire forget，publish 给 "user" 就是给用户右上角弹窗，同时视为复制给 "root"。}
> 状态：已落实（WP-034，`877b8b8ad`）
> {以上语义向 LLM 解释清楚。}
> 状态：已落实（WP-035，`877b8b8ad`）

6. `celebrate(experience)` / `regret(experience)`
   - 私有 Enhancer 把经验压成单一结论：`ABSORB | BIRTH | DISCARD`。
   - `BIRTH` 需唯一 TipName 与中英双语 EnforcerText/MainText，并过准入后才持久化。（institutional-learning-002/004）
   - 不设第四种结论，不设评分阈值。（institutional-learning-002）
> {开发者认为此工具删除，太重，且难以维护。}
> 状态：已落实（WP-036，`a66f204dc`）

7. `fetch(shelfmark)`
   - 只接受公开 Shelfmark。不暴露内部会话标识。
   - 读旧案及维护基线，捕获相关文件目标状态，计算真实 diff。
   - 无差异 → 返回旧正文并说明未检测到变化。有差异 → 请求 CaseRefresh，成功时在同一事件边界原子提交正文与维护基线。（knowledge-reuse-005）
   - 仓库无 Casebook marker 目录 → 不注入描述，不建索引，入口拒绝 fetch。（knowledge-reuse-009）
8. `chronicle` 见第 18 节。

---

## 25. 语言、消融、交付

`procedure Language()`

1. 语言是封闭强类型：`English | SimplifiedChinese`。对应 `en.md` 与 `zh-CN.md`。
2. 无会话语言绑定，无持久化记录。任何入口问某会话的语言，得到的都是当前全局设置。（provider-language-002）
3. 内容分三类：（provider-language-005）
   - Class A：面向模型的自然语言。必须完整本地化。
   - Class B：工具名、参数、协议字段、路径、命令。保持原样。
   - Class C：内部诊断。不属于 Provider 多语言体系。
4. 每份 Provider 语义资源成对提供。缺一侧即失败，不静默回退英文。（provider-language-006）

`procedure Ablation()`

1. `resources/ablation/nodes.json` 承载节点清单与序号。
2. 三态：`ablated | borrowed | active`。
   - `ablated`：该切面不启用，不得改变可见工具或行为。
   - `active`：包级机制正常运行。（feature-ablation-002）
3. DAG 校验：`station-order` 边与 `borrow` 边。无环。
4. 环境变量 `WANXIANGSHU_ABLATION_<node>` 覆盖后仍须过 DAG 校验。
5. 未配置时 production 默认全部 `active`。
6. Predictor 配置是只读委托唯一的生产启用依据。消融节点只在研究对照中使用。（feature-ablation-005）

`procedure Distribution()`

1. 安装产物同时含 `dist/**` 与 `resources/**`。不分开发送。
2. 资源路径以模块自身绝对位置（`import.meta.url`）上溯包根。不依赖 CWD。
3. 入口：`package.json` 的 `main` 与 `exports["."]` 均指向 `./dist/OpenCode/Plugin/Plugin.js`。
4. 白名单固定：`files: ["dist/", "resources/"]`。不含源码、测试、内部工具。
5. 资源缺失 → 立即抛出致命错误。不用内置 fallback 清单静默降级。

---

## 26. 环境变量

仅列常用项与内部/测试项；用户可见项见 README。

| 变量 | 作用 |
|---|---|
| `WANXIANGSHU_PROVIDER_LANGUAGE` | 语言偏好。阶梯最高优先级。取值 `en` / `zh-CN` |
| `WANXIANGSHU_SKIP_AUTO_INJECTED=1` | 跳过新的 auto-injected 伪工具注入。已落盘历史 pair 仍 replay |
| `WANXIANGSHU_PROCESS_HARD_LIMIT_SECS` | executor 单进程硬超时上限。默认 1 小时 |
| `WANXIANGSHU_ADMISSION_TIMEOUT_MS` | prompt 物理 acceptance 等待超时。默认 10000 |
| `WANXIANGSHU_DIAG=1` | 内部诊断经 stderr 可见。只观测，不改决策 |
| `WANXIANGSHU_NO_FATAL_EXIT=1` | 抑制 fatal 的物理进程退出（`SIGKILL` / `process.exit`）。测试用 |
| `WANXIANGSHU_ABLATION_PROFILE` | 消融拓扑 profile 选择 |
| `WANXIANGSHU_ABLATION_<node>` | 单节点三态覆盖 |
| `SPHINX_COMMON_DIR` | Sphinx MCP 的 durable workspace。缺失即启动失败 |
| `SPHINX_START_CONFIG` | Sphinx MCP start 配置。缺失只允许读取类工具 |
| `SEMBLE_*` | 内部/测试：Semble MCP 集成（`SEMBLE_MCP_DISABLED`、`SEMBLE_MCP_FIXTURE`、`SEMBLE_MCP_REF`） |
| `WANXIANGSHU_TEST` | 内部/测试 |

> {开发者认为要降低此部分的复杂度，保持 KISS}
> 状态：已落实（WP-037，`b35ad48ea`）
---

## 27. 验证入口

```bash
node scripts/build.mjs                    # 编译
node scripts/check.mjs                    # 全 static gates
node requirements/verification-system/tests/run.mjs    # 单元套件
node --test requirements/<pkg>/tests/NNN.test.mjs      # 单包单文件
npm run format-build-test                 # 日常验证
npm run verify:release                    # 发布验证
```

- 测试分层：Pure laws → Temporal → Adapter → Long Stroke。（verification-system-001）
- 条款与测试文件的映射由用例标题中的 `WHAT[<包>-NNN]` 锚点定义。

> {开发者认为要降低此部分的复杂度，保持 KISS}
> 状态：已落实（WP-038，`b35ad48ea`）

---

## 附录 A — 已知缺口与不确定

以下是写作时观察到的、与上文叙述不一致或未闭合之处。它们是线索，不是结论。

1. **`sphinx` 原生工具未注册。**
   - 能力词表（`ToolPermission.Sphinx`）、权限矩阵、`resources/provider/tool/sphinx/` 资源都存在。
   - `ToolRegistry` 不产出名为 `sphinx` 的 `ToolSpec`。
   - `host-boundary-026` 声明的 `Sphinx.Host.Adapter`（`sphinx-host-adapter`）在 `src/` 中查无实现文件。
   - 当前可运行的 Sphinx 入口是独立的 MCP stdio 服务：`Sphinx/V2/Hosts/Mcp/Server.fs`、`ServeEntry.fs`，七件套工具，`apiVersion = "2"`。
   - [INFERENCE] 未注册不等于被删。可能属未施工切片。台账见 `requirements/GAP.md` 的 GAP-219 / GAP-222。

> {开发者认为 Sphinx 被某次大重构改坏了，需要在 git 中 cherry-pick 历史各实现来恢复。}
> 状态：已落实（WP-039，`b97845a49`）

2. **`query-shell` 未注册。**
   - `ExecutorTool.queryShellSpec` 存在，Inspector 准入也在。
   - `ToolRegistry` 不产出它。当前活动角色集合 `Roles.all` 也不含 Inspector。

> {开发者认为这是遗骸，应该丢弃。}
> 状态：已落实（WP-040，`c5109e6ae`）

3. **CHANGELOG 陈旧条目。**
   - `CHANGELOG.md` 的「todowrite 列表的 provider 名与 Host 名分离」一节描述 provider 面字段改名为 `obligations`。
   - 该改动已由 `c8a742d23` 撤销：文件删除，`todos` 恢复宿主原状。
   - HEAD 源码与 `requirements/action-affordance/tests/015.test.mjs` 断言的都是宿主原样 `todos`。上文第 10 节按 HEAD 事实写。

> {开发者认为这是遗骸，应该丢弃。}
> 状态：已落实（WP-041，`b35ad48ea`）

4. **验证边界。**
   - 本文未运行测试，未接真实宿主。
   - 过程描述取自源码阅读与 `requirements/<包>/WHAT.md`。
   - 跨 Hook 的时序结论（第 4 节的 16 步次序、第 2 节的准入顺序、第 13 节的容量交接）已由源码确认。运行期行为未复现。
5. **语料耦合提示。**
   - `degeneration-guard-004` 的经验包络从仓库全部 Git-tracked 源文档派生。
   - 新增一个 tracked `.md` 文件会改变派生值。
   - 该文件即属此类。
> {开发者认为可以派生，但不要自动派生，也不要检查是否改变，应该只允许手动更新。}
> 状态：已落实（WP-042，`c557749c5`）

# 施工进度（checkpoint，2026-10-10）

本节记录已落地的产品变更与验证证据。产品语义只由 `requirements/<包>/WHAT.md` 定义；本节只记进度，不复述条款。

**收尾更新（2026-10-10，算法 F）**：42 条开发者意见已全部施工完结，各条落实状态见指南块尾 `> 状态：` 标注与 `000.md` §4「状态」列；下方「已完成并已提交 / 本轮完成，待提交 / 尚未完成」为当时截面，保留为历史记录。遗留边界：WP-023 修复前后同输入的数字对比未验证；WP-017/019 的 fast-check 性质证明未成文；prefix-stability-012 既有欠账记 GAP-226 另案 OPEN；工作树另有 WP-037/038 的未提交修订（README、verify.mjs、指南 §26）。

## 最终交付状态（2026-10-10 收束）

本节记录 mission 收束时的最终交付状态；已写下的历史更新保留不动。

**收尾段提交链（自 `bcb40ea7b` 起）**：`bcb40ea7b`（EMR-010 修复）→ `8a7ddaca3`（WP-037/038）→ `e106426c8`（算法 F）→ `8af52eb71`（fantomas 全量格式化）→ `aedcea492`（WP-024 时钟注入/锚点）→ `4ead6bfc0`（WP-042 envelope 入库）→ `307e3eb74`（格式化）→ `ff110e8fa`（WP-023 数字对比）→ `ca742b408`（prefix-stability-012 收口）→ `06b492aa6`（crash-020 / ol005 注入修正）→ `e0b86b509`（defer 资源对）→ `699330f6a`（WP-032 注释）→ `7bf37b01a`（fa002）→ `ae8516392`（Long Stroke e2e 声明矩阵）→ `7bb1942f1`（e2e preflow 分层断言）→ `32b4270db`（e2e 失败现场自证）。

**工作包状态**：42 卡全部闭环：38 卡已落实，4 卡裁决保留（WP-002、WP-008、WP-010、WP-013）。
- WP-023 数字对比已测（真实存储复制，357,806,571 字节）：热读 16409 ms → 4 ms（约 4000×）；RSS 峰值 1884.1 MB → 1226.5 MB（约 −658 MB）。
- prefix-stability-012 已收口：连续三次 35/35/0。
- fa002、crash-020、obligation-ledger-005、TC004、RS017、EMR-010、envelope 全部修复。
- defer 中英资源对与 WP-032 注释落地。
- language-parity 门禁通过，323 个语义资源全绿。

**验证基线**：
- `node scripts/build.mjs`：generation 185。
- `node scripts/check.mjs`：18 门禁全过，pyramid 计数 0。
- 全量 unit 在本机 Node 26 下剩 12 条 `verification-system-016` 工具链环境红与 2 条 021 负控 fixture（设计内）；CI 的 Node 22 为权威环境。
- `npm run verify:release`：format、check、build 通过；unit 同上；integration 的 crash-020、ol005 已修；package 通过。

**Long Stroke e2e 现状**：
- 声明矩阵六轮对齐（manager lane、seal、Blogger bootstrap、owner 捕获载体、oracle 计数、preflow 分层断言）全部实证有效，并已提交。
- 场景现停在产品侧疑点：preflow 读到 `DelegationRequested = 0`。现场自证：events 目录、文件与计数均正确；journal 无该 fact；stderr 有 `no-completed-source-batch`（canary owner 侧）与两次 `owner provider run is not uniquely bound`（另一会话）。
- 一处未解矛盾：`strength-canary-replica.0` 曾匹配 ≥1 与 `DelegationBound = 0` 不能同真。需要下一层现场取证：完整 stderr 的 `strength-delegation-requested` 行与 journal 完整内容。

**遗留边界**：
1. 上述 e2e 产品侧疑点（独立议题；入手点：三条 skip 的条件链与 canary 会话映射）。
2. `VS016` 12 条本机 Node 26 环境红（CI 的 Node 22 权威；修法建议已报告：环境对齐或测试矩阵参数化）。
3. 021 两条负控 fixture 为设计内。
4. `/tmp/wxs-perf-old` 等测量现场未入库（复核后可清）。

## 已完成并已提交

| WP | 内容 | 提交 |
|---|---|---|
| WP-001 | 插件路径统一到 `<git-common-dir>/wanxiangshu/` | `d5f0c9366` |
| WP-042 | envelope 改为显式命令手动更新，构建不再自动派生 | `c557749c5` |

## 本轮完成，待提交

### WP-021 · ndjson 内嵌 payload

- `EventEnvelope` 新增内嵌 `Payloads` 索引；`CanonicalEventCodec` 在事件行内写出 `payloads`（仅当事件确实引用载荷时才出现该键，避免无载荷事件字节被改写）。
- `EventStore.WritePayload` 改为进程内暂存，`Append` 把暂存字节内嵌进事件行；`ReadPayload` 先查暂存，再查 Integrator 的已提交索引。
- 删除 `.git/wanxiangshu/payloads/` 目录的全部读写点：`ProcessEventLog` 的 payload 文件 API、`WriterStreamSync` 的 payload 树与缓存、`RetentionSurface` 的远端 payload 树。
- `ICanonicalIntegrator` 新增 `TryPayload`：已提交内嵌载荷的唯一读口。Store 不再自行 `readStreams` 重建缓存（该路径本来也被统一 store 门禁判为第二权威）。
- 条款：`durable-events` [002] [003] [010] [012]、`durable-convergence` [010]、`speculative-investigation` [006] 已同步。
- 证据：`node --test requirements/durable-events/tests/012.test.mjs` 11/11；`knowledge-reuse` 016、`work-record` 001、`host-boundary` 029 全绿；`node scripts/check.mjs` 与 `node scripts/build.mjs` 绿。

### WP-026 · provider 可见文本不再出现「证据」

- `resources/provider/**` 的模型可见文案与 `resources/provider/tool/js-program/**` 的付费失败教训统一改称「事实 / 事实链 / facts」，`证据 / evidence` 只保留在 `evidence` 字段名与「举证责任」这一法律用语上。
- 双语同步；`node scripts/checks/language-parity-gate.mjs` 绿（346 个语义资源）。
- 断言随合同更新：`cognitive-environment/tests/015`、`repository-programming/tests/022`。

### WP-003 / WP-023 的验证缺口

- WP-003（漂移校验退役）与 WP-023（性能排查）的条款与实现已在批次内，但修复前后同输入的数字对比、以及 `execution-model-routing` 性质测试的口径说明尚未成文。见下节。

## 尚未完成

1. **WP-003 收尾**：`execution-model-routing` [009] 的 fast-check 性质已入 `tests/009` 并通过，但条款里尚未写明「漂移若真实发生将静默通过」这一取舍，也未在 `proposals/` 留下裁决与性质测试的对应记录。
2. **WP-023 收尾**：性能修复（流式读、去掉双重全量解码、去掉无限锁等待）已成，但缺「修复前后同输入」的数字对比记录；未测的部分必须写「未验证」。
3. **W0 剩余裁决卡**：WP-002、008、010、012、013、017、019、039 的讨论记录已落 `proposals/`，但其中裁定为「实施」的条目尚未逐条施工。
4. **W1—W6 其余工作包**：见本手册 §4 台账。

## 环境提示

- CI pin 在 Node 22（`.github/workflows/ci.yml`）。`verification-system/tests/016` 校验的是本机 Node/npm 工具链，在本机 Node 26 + npm 12 下必然失败，在 Node 25 下通过；这不是产品缺陷。本机完整套件请在 Node 25 或 CI 的 Node 22 下运行。
- `node_modules/opencode-ai` 的 postinstall 必须执行；缺失会让 `host-boundary` 023 失败。
