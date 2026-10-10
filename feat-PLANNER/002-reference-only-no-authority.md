# 万象术 Planner：保姆级完整设计

版本：第一稿  
日期：2026-09-29  
范围：系统 `plan` role 的独立规划功能  
状态：设计草案；未实施，未复核实时仓库，未运行构建或测试

## 0. 先把这次要做的事说清楚

用户选择系统的 `plan` role，进入一段独立规划。Planner 根据已有材料理解目标，需要本地事实和运行验证时，持续找绑定的 DevOps；需要用户的场景、取舍或授权时，问用户。信息足够后，Planner 自行形成最终计划，系统将本次规划的 LWR 落盘，确认成功，再结束。

**本期到这里为止。** 不把计划交给 Manager，不启动实施，不设计“拿到 LWR 后继续做”的入口、队列、道路接续或转换协议。

调查并非只读。DevOps 可以在现有职责范围内就地修复非架构级缺陷，补测试并重新验证。已经发生的修复必须成为计划的依据，不能继续冒充待办。允许修复不等于允许实施整份方案，也不等于允许提交、部署、扩大权限或替用户承担新的产品与架构决定。

本稿选择一条完整主线，不把所有工程问题重新抛给用户。不过，选择与事实必须分开：

- **已确认**：用户在资料或本轮讨论中明确要求的产品边界。
- **本稿选择**：为了形成可实施方案，本稿提出的设计裁决；不是用户已经逐条批准的要求。
- **待代码核实**：资料没有证明的宿主能力、实际接口或存储行为。对应章节给出必须满足的合同和验收方法，不假装现成可用。

文中的新增类型、函数、事件语义与文件路径，除明确注明“资料已登记”的内容外，都是拟议接口或落位。伪代码不是当前源码。施工时可以调整内部命名，但不能借调整命名改变责任和后果。

### 0.1 已确认的产品边界

| 编号 | 已确认内容 | 依据 |
| --- | --- | --- |
| U-01 | 用户可见的是 Planner；使用系统已有的 `plan` 入口，而不是另造一个并列 Planner 入口 | 本轮：“Plan 是单独的，就是用系统的 plan role” |
| U-02 | 规划独立运行，不插入 Manager 正在执行的任务 | 本轮明确否定同任务中途插入规划 |
| U-03 | Planner 拿到 ASK 的方法；可以问用户，也可以找 DevOps 调查 | 原始改造目标 |
| U-04 | DevOps 调查时可以就地修复并验证 | 本轮：“允许它当场修复并验证” |
| U-05 | Planner 自行判断何时收尾，不等用户说“出最终计划” | 本轮：“自行收尾” |
| U-06 | 最终交付是落盘的 LWR | 原始改造目标及本轮确认 |
| U-07 | 计划结束即停止；从 LWR 到实际实施的功能不在本期 | 本轮：“得到 LWR 并想办法实际做的功能，现在先不做” |
| U-08 | 不要求另建 QA、SKILL 一类持久化；Casebook 收纳调查成果不是必须项 | 原始改造目标 |

原始目标见《设计资料》§1；本轮修订优先于其中尚未裁决的解释。（资料：`设计资料.md`，L50–L54。）

### 0.2 本稿采用的主要选择

**一个入口，一个规划者。** 对外唯一标识是 `plan`，Planner 是它的职责称呼。内部采用一个 canonical Plan 身份，不同时出现 `Plan` 和 `Planner` 两个角色。不恢复 Student、Teacher 或 fast/deep 组合名。

**一个持续的调查对象。** 本次 Plan work 最多绑定一个活跃 DevOps，使用标准 DevOps 职责和工作会话，通过受限的 `resume` 路径持续交付调查目标。不把它改造成 Teacher Satellite，也不借 Manager 的名字创建假道路。

**三个主要动作。** Planner 可以 `resume` DevOps、`ask` 用户、`finish` 最终计划。可以使用受限的本次记录回读和既有认知画板，但没有直接源码调查、任意文件写入、终端、浏览、fork 或 Fission 权限。

**不把 idle 当完成。** 用户等待、工具等待与收尾分别有明确的运行时事实。Planner 的 `finish` 表达“我决定交付”；框架只检查身份、在途操作、记录完整性与落盘后果，不判断知识是否足够。

**一份正式产物。** 最终计划是本次 LWR 中最后一条正式助手正文。系统导出完整 bounded LWR，不另写第二份独立 `plan.md`，不新增 Closing report 段。

**不另造知识存储。** 用户输入、问答、调用与完成沿用既有事件、trace、工作记录和认知画板。可以补充等待、关联、导出收据等控制事实，但不建立 Planner 专属知识数据库、QA 字节流或决策事件表。

**Casebook 本期不扩展。** 保持现行来源限制。没有案例归档不影响计划完成。

这些是本稿的设计选择，尤其是“不直接调查”“固定 DevOps 工作会话”“显式 finish”和“完整计划作为最终正文”，不能写进后续文档的“用户原话”一栏。

### 0.3 阅读导航

产品与分工见 §1–5；交互、工具和正常流程见 §6–9；记录、落盘与恢复见 §10–14；提示词与资源见 §15；工程落位和施工顺序见 §16–18；验收与测试见 §19–21；开放问题的裁决对照与最终施工清单见 §22–23。

---

## 1. 依据、基线与不能越过的事实边界

本稿以七份上传文件为依据：`设计资料.md`、`01-历史.md`、`02-现状.md`、`03-映射与问题.md`、`04-证据与术语.md`、`DOC.html`、`ASK.md`。不使用未读取的实时源码补齐结论，也不把产品说明中的示意类型当作当前源码全集。

资料收集起点是 `ab62b7d2f`，窗口中推进到 `cc4bfec18`，同时存在 PROMPT-006 的并行未提交修改。这不是一份已经证明与今天工作树逐字相符的代码快照。（资料：`04-证据与术语.md`，L8–L17。）

必须保留四处区别。

第一，用户明确说使用系统 `plan` role，但现状分册登记的万象术 `Role` 枚举没有 Plan。**入口存在与已被万象术正式托管不是一回事。** 本稿要做的是把该入口纳入明确的身份、权限和生命周期，不另造入口，也不假设只改一段提示词就够。（资料：`02-现状.md`，L8–L13。）

第二，历史 Teacher 是内部叶子；现行 DevOps 是固定的真实执行者。用户已经确认保留后者的修复能力。因此不能把 Teacher 改个名字就接回来。（资料：`03-映射与问题.md`，L84–L113。）

第三，LWR 已有渲染和读取实现，但资料没有证明它已经具备“写成计划文件并在成功后完成”的接口。本稿为此补的是导出后果，不是第二套工作记录。（资料：`02-现状.md`，L94–L104。）

第四，资料同时保留了 Planning Table/T1 资源和“BlindPlan/T1 machinery 已退役”的说明。这里不替它们消除矛盾。本期与 Manager/T1 无运行依赖；施工也不顺便复活、删除或重写该机制。（资料：`02-现状.md`，L49–L75。）

《DOC.html》提供的是职责拆分的产品理由：减少注意力浪费，而不是让组织变得复杂。本稿据此把 Planner 的注意力放在理解、取舍和计划上，把实际调查交给 DevOps；不据此新建一套通用多代理平台。（资料：`DOC.html`，L424–L425。）

## 2. 目标、非目标与成功的含义

### 2.1 目标

一次规划结束后，未参加对话的人应当能从 LWR 理解：这次到底要解决什么；哪些是用户已确认的约束，哪些是查明的事实；选了什么办法及其理由；调查中已经做过什么；后续工作怎样开展和验收；哪些地方仍不成立、需要什么证据才可继续。

规划不要求每次都调用 DevOps，也不要求每次都问用户。材料足够时可以直接成稿；任务不值得做、应缩小范围、应先验证，或暂时无法承诺，都可以是有效规划结论。ASK 已明确允许这些结果。（资料：`ASK.md`，L168–L181。）

### 2.2 非目标

本期不做自动从普通任务切入 Plan，不做 Manager 与 Planner 的中途切换，不做计划批准与实施授权转换，不做计划执行器，不做计划版本管理产品，不做计划任务调度，不做自动向 Casebook 扩大归档来源。

不恢复 QA.md、SKILL 编译器、Teacher 角色或 Teacher Satellite。不新建认知知识图谱，不用机器给“已经理解”“意见一致”“计划可行”打分，不把 ASK 的草案栏目变成工具参数或数据库必填字段。

本期也不借“查资料”扩张 DevOps 的外部浏览职责。需要外部事实而当前能力不支持时，应使用用户已提供的材料，或标明待核实并给出验证办法。

### 2.3 三种结果必须分开

**规划完成**：最终 LWR 已真实落盘，记录可读，成功收据与本次 work 的终态一致。计划可能写明“有条件开始”或“暂不可承诺”。

**运行失败**：身份、权限、交互关联、持久化、物理执行收敛等出错，系统不能可靠完成本次规划。不能把这种错误改写成“规划充分收敛”。

**用户取消**：停止本次继续工作，不宣称计划完成。已发生的修改与证据照实保留，不因为取消就自动撤销仓库变化。

“方案当前不可实施”与“系统没能保存计划”是两回事。前者可以有一份成功交付的 LWR；后者不能显示规划完成。

## 3. 继承历史什么，不继承什么

历史 Student/Teacher 的核心，不是两个名字，而是让一个参与者持续形成理解，让另一个参与者用实际调查纠正它；知识用自由文本表达，框架只管控制流。历史同时有严格的单飞、持久化、恢复和真实宿主测试要求。（资料：`01-历史.md`，L51–L82。）

| 历史机制 | 本稿处理 | 理由 |
| --- | --- | --- |
| Student 主动暴露当前理解，Teacher 可以推翻前提 | 保留为 Planner–DevOps 的工作方式 | 避免每次只取零散事实，无法纠正整体误解 |
| 同一任务持续使用同一个调查上下文 | 保留正常路径的同一 DevOps 会话 | 减少重查和上下文割裂 |
| 每轮一个中心问题，自由文本回答 | 保留 | 不预设知识分类，不把多项取舍塞进一轮 |
| Teacher 的标准执行能力 | 改为现行标准 DevOps 能力 | 用户已确认允许就地修复和验证 |
| Teacher Satellite，无 Companion | 不继承 | 本稿选标准 DevOps WorkSession，不另造查询职分 |
| QA 作为唯一权威知识状态 | 不继承 | 沿用现行 trace、LWR 和认知画板，不增加专属知识真相源 |
| 学习 idle 自动进入编译 | 不继承 | Planner 会等待用户，idle 无法区分等待与交付 |
| Learn/Compile 两套请求工具面 | 不继承 | 本期不写 SKILL；单一工具面足够，避免不可逆误入编译 |
| 最终苏格拉底反证 | 保留目的，改变强制性 | 有实质调查时应做针对性反证；不为零调查、简单任务强制多一次模型调用 |
| 先删 QA 再完成 | 改为先导出 LWR 再完成 | 新功能需要证明的是产物存在，不是临时文件消失 |
| 历史 Replacement 自动补教师 | 本期不采用 | 现行固定 DevOps 不得由 resume 制造替代者 |
| 纯逻辑→契约→重放→真宿主测试 | 保留 | 只测提示词或私有函数不足以证明可交付 |

本稿不承诺复现历史 QA 的逐字、不可压缩知识保存保证。现行工作记录中有 Chronicle 与压缩；这份变化必须明说。关键原始输入、调用和结果仍应能从现行权威记录回读，但最终 LWR 不是原始问答的逐字转储。历史的语义无损知识编译也不直接变成计划的制品合同。（资料：`01-历史.md`，L187–L217。） （资料：`02-现状.md`，L79–L91。）

## 4. 身份、入口与授权

### 4.1 外部名称

用户选择的名称固定为 `plan`。界面说明可以写“Planner：澄清、调查并交付计划”，但不增加另一个可选 `planner` agent。

若当前万象术 canonical role 尚无 Plan，拟新增 `Role.Plan` 并映射到这个入口；若施工基线已经有相同语义的 Plan，直接接入该身份，不建立第二个角色。一个 Role 对应一个 Persona，不引入 fast/deep 档位。

这项修改必须进入角色目录、可见性、托管配置、权限投影和身份校验，不能只加一份 `role/plan` 文本。

### 4.2 `plan` 是新的独立规划工作，不是 Manager 的阶段

初始用户请求创建一段 Plan work。它的因果身份复用系统已有的 invocation/work 身份；本文简称 `PlanWorkId`，**不是要求再造一套 ID、Road 或 LogicalRun**。

一个 Plan work 可以跨越多个 provider 请求和用户澄清轮次；不能按“每收到一条用户物理消息”重新创建一段规划。活跃规划内的回答和修订必须通过现行交互权威续入。

用户在这段规划结束后再提出新规划，按新的显式用户请求创建新 work；不修改已经完成的 LWR，不把旧计划默认为新工作的执行授权。

### 4.3 初始与续接权威

用户选择 `plan` 的原始请求是 HumanRoot。后续澄清回答使用当前交互的合法续接，不从正文前缀、工具名称或当前界面标签猜测身份。

Planner→DevOps 的首次绑定由受控运行时创建；其后是同一受控委托关系的 resume。每次真实调用的 authority、目标会话与返回对象均须可证明。

所有发送都经 PromptDispatcher，沿用 claim、submit、physical acceptance 与恢复合同。Plan 不为方便而直接调用 Host 发消息接口。Owner 证明的是“有权委托”，不是“子代理必须继承父代理的角色”：Plan→DevOps 必须走标准委托身份签发，得到 DevOps 的 canonical 身份；不能直接把 Plan 的 InheritedFromOwner seed 当作 DevOps profile。Plan 自身的 continuation 则继续保持 Plan 身份。角色、Persona、工具能力和请求身份在 attempt 创建时一并冻结；不通过可选 model 参数绕过当前模型路由规则。（资料：`02-现状.md`，L173–L194。）

### 4.4 角色权限不跟随父会话的表面标签漂移

Planner 没有直接写代码或运行命令的权限。DevOps 拥有的是自身角色的现行能力，而不是从父 `plan` 继承一个笼统“只读模式”。

反过来，允许 DevOps 修复也不能让 Planner 获得终端、源码修改或通用代理创建能力。必须同时检查 provider 可见工具面和实际执行后果；父子 profile 分别构造、分别校验。

宿主是否会额外给 `plan` 注入内建只读提示、通用工具、切换到 build 的入口，需要真实 canary 核实。冲突必须在托管配置和执行门解决，不让模型靠“忽略宿主提示词”绕开。

## 5. Planner、DevOps、框架各负责什么

### 5.1 Planner

Planner 负责理解目标，区分已确认、已核实、暂定、待验证和未决；决定该查证、默认、试验、提问还是延后；维护少量真正影响行动的认识；形成计划并决定何时交付。

这些标签是模型组织材料的方法，不是机器知识协议。框架不解析它们来决定权限、恢复步骤或任务是否完成。ASK 明确要求只在有助审阅时显示标签，不给每句话贴标签。（资料：`ASK.md`，L27–L57。）

Planner 可以提出架构与产品方案，也可以在用户已给定的边界内作出草案取舍。但不能把自己的草案写成用户已确认的要求，更不能通过调查委托把草案自动实施。

### 5.2 DevOps

DevOps 负责获得真实本地事实、读取源码与配置、执行允许的命令、运行测试、检查进程和环境，并在固有授权范围内修复非架构级缺陷。保持现行“不发明产品含义、兼容性或安全政策，不削弱断言、不绕过门禁”的边界。（资料：`02-现状.md`，L19–L36。）

调查委托中可以包含 Planner 当前理解和猜测。DevOps 应主动指出错误前提，而不是顺着 Planner 的分类填表。它可以说“你正在问的不是关键问题”，但需要说明证据和影响。

回答必须区分已观察、已修改、已验证、未核实。没有跑过的测试不能写成通过，已运行但失败的测试不能变成“稍后验证”。这不要求固定返回字段，只要求自然语言说清楚。

### 5.3 就地修复边界

本稿采用“保持已成立的意图，不引入新意图”作为修复判断原则。

已确定行为下的局部缺陷、错误调用、遗漏检查或可重现回归，通常可以在职责内修复并验证。修复中发现需要选择新的产品语义、改变兼容合同、决定数据迁移损失或扩大安全权限，应把事实与选择交回 Planner，不擅自完成这些决定。

不能按改动行数判断是否属于非架构级修复：一行默认值也可能改变产品承诺；多个文件的修复也可能只是在恢复已有不变量。机器门负责已知能力和危险操作的授权，不能声称自动识别任意补丁的架构含义。

所有仓库修改都要保留来源和可归属证据。没有用户明确授权，不自动 commit、push、部署、发布、购买服务或清除数据。不为让测试通过而改弱测试目标。

### 5.4 框架

框架负责角色与工具权限、调用归属、单飞、输入与结果的 durable acceptance、等待与取消、物理完成检查、记录边界、LWR 导出和终态提交。

框架不负责判定用户真正想要什么、不负责证明计划正确、不从“没有问题了”等字符串推断收敛，也不根据 ASK 标签计算完成度。

### 5.5 可见性

主对话中的澄清、进展和最终计划由 Planner 对用户表达。DevOps 的委托回答首先返回 Planner，不抢占主对话的最终发言。

不承诺恢复历史的“Teacher 所有工具流永不可见”。如果现有界面展示子会话、命令和审批卡，保留这些正常审计能力；涉及修改时尤其不应隐瞒。只保证它们不被错误投影成 Planner 的正式最终陈述。

DevOps 不能另起一条产品访谈。需要用户决定时把问题交回 Planner；宿主为危险操作提供的必要审批不在此限，但批准某次操作不能被扩写成批准整份计划。

## 6. 交互：该问谁，什么时候停止问

### 6.1 先决定动作，再决定措辞

Planner 吸收已有材料和最新回答后，只处理会影响当前交付边界的未知。可以从以下动作中选择，不要求按顺序走一遍。

| 动作 | 本功能中的实际去向 | 不得误用 |
| --- | --- | --- |
| 查证 | 已有对话直接使用；本次记录通过受限回读；仓库与运行事实交给 DevOps | 不让用户替模型查文件，也不冒充已经核实 |
| 默认 | 在低风险、可撤回且有依据的地方提出草案选择 | 不把用户沉默当同意，不代替必要授权 |
| 试验 | DevOps 在已有权限内运行最小验证；无权限时只给出验证步骤 | 不以“试验”为由实施完整方案或执行破坏性操作 |
| 提问 | `ask` 发出一个会改变当前行动的实质问题 | 不批量填问卷，不反复询问已经回答的问题 |
| 延后 | 在计划中写明什么时候必须重开 | 不把当前阻塞伪装成下一阶段细节 |

这些动作沿用 ASK 的判断方式，不建立五态业务状态机。（资料：`ASK.md`，L65–L79。）

### 6.2 问用户

只问用户能够回答、且回答会改变目标、范围、关键取舍、验收或授权的问题。一轮最多一个实质问题；一个问题是一次取舍，不是把多个问题写进一个问号。

问题要落在具体场景里。例如：“失败时，第一版必须保住已输入内容，还是允许重新填写？”比“选哪一种持久化架构？”更适合用户作答。

问题通过 `ask` 发出后，Planner 停止继续推演用户的选择；没有回答，不编造答案，也不继续执行依赖该答案的调查。安全且不依赖该答案的并发调查虽然理论上可行，**本期也不做**，以保持清楚的单飞和等待边界。

用户一次回答解决了多个分歧，要一起吸收，不按原来的问题清单重复询问。明确的修订替代旧决定；未解释的冲突保留，不擅自调和。

### 6.3 问 DevOps

一次 `resume` 交付一个中心调查目标，可以附当前理解、已有证据和最担心的反例。不要求模型每次填“猜测／依据／问题”模板。

好的委托例子：

> 我现在认为阻塞来自完成记录没有可靠落盘，而不是 Planner 缺少写文件权限。请沿真实完成链检查这个理解：哪个已接受事实能够证明记录已经存在？若前提错了，请直接推翻。调查中可以按现行职责修复局部缺陷并验证，但不要实施新的规划功能。

不好的委托是一次列十个独立问题，或命令 DevOps 证明 Planner 已选中的方案必然正确。

### 6.4 停止条件

Planner 每轮判断当前信息是否足以形成有用的计划。范围和验收足够明确，重要分歧已解决或被安全验证步骤承接，剩余假设与阻塞可见，就应交付。

ASK 中“第三个实质问题之后检查能否先交草案”是提醒，不是硬性三问上限。框架不得因达到三轮而自动结束，也不得要求至少三轮才准交付。（资料：`ASK.md`，L166–L179。）

对依赖实质仓库调查的方案，Planner 在交付前应将当前理解交给同一 DevOps，做一次针对关键前提的反证检查。若最后一次调查已经完成这件事，不再补一轮形式审查。简单且无需调查的任务不强制创建 DevOps；用户要求停止访谈时，也不以“反证未完成”为由强行延长。

反证是模型工作纪律，不是 DevOps 必须返回 `approved` 的协议。发现新问题可以继续调查或修改计划；没有高价值问题就自行 `finish`。

### 6.5 等待期间的体验

提问被系统接受后，界面显示问题和“等待回答”，而不是让用户对着一直转动的推理状态。此时不保持一个无限占用的 provider 请求，不轮询模型、不周期催问、不消耗自动恢复预算。

用户可以正常回答、补充材料、修订前提，或取消。没有回答时，默认保持暂停；不因为普通推理超时就替用户作决定。宿主确有会话保留上限时，到期显示交互已失效，并保留已接受记录，不显示完成。

## 7. 会话拓扑与固定 DevOps

### 7.1 选择标准工作会话，不选择 Teacher Satellite

本稿采用如下拓扑。图中 `PlanWork` 是因果工作范围，不是新增一层持久会话。

```text
用户显式选择 plan
    │ HumanRoot
    ▼
Planner WorkSession ── 既有 Companion / Blogger
    │
    │ 当前 PlanWork 的唯一固定绑定
    │ resume（同步取得本次 invocation 的完成结果）
    ▼
DevOps WorkSession ─── 既有 Companion / Blogger
    │
    └─ 本地调查、真实执行、职责内修复与验证
```

DevOps 使用标准 WorkSession，就按现行规则拥有其记录能力。本稿不新增 `SatelliteKind.Teacher`、`SatelliteKind.PlanDevOps` 或叶子查询代理；Planner 的记录也不借用历史 QA。

这是对历史拓扑的明确更换。现状资料区分了 WorkSession、SatelliteSession 与普通 attached work；它们不能靠当前工具面推导，也不能混用。（资料：`02-现状.md`，L142–L169。）

### 7.2 绑定范围的最小扩展

当前固定 DevOps 绑定以 Manager 的道路为前提。独立 Plan 无 Manager 道路，因此**需要明确扩展固定 DevOps 的 owner 范围**，不能声称当前 resume 已自然支持 Planner。

拟议语义：

```fsharp
// 示意，不是已存在的定义。
// 各个 Id 必须映射到现有 canonical owner / invocation 身份。
type FixedDevOpsOwner =
    | MissionRoad of ExistingRoadId
    | PlanWork of ExistingWorkId
```

公共不变量不变：一个 owner 同时只有一个有效的 DevOps 绑定；持续 resume 不改变该绑定的角色、配置或控制权；不能因失败创建多个候选再挑一个。合法的现行 provider fallback 仍由原有规则处理；Planner 不能指定子代理 model、重置其恢复预算，或把“复用上下文”误写成永远禁止正常 fallback。

授权的新增范围只限于：**Plan owner 可以给自己绑定的 DevOps 交付调查目标并收取结果。** 不因此获得 Manager 的任意委托、道路接责、Engineer 差遣、fork 或 resume 任意角色的权利。

绑定应由现有持久关联／委托投影持有。若现行 owner 类型过窄，在该归属处扩展；不要在 Plan runtime 再建一张 `planDevOpsOwners` 持久表，与原投影互相证明。

### 7.3 创建与续做

首次真正需要调查时，由固定绑定的受控 bootstrap 创建 DevOps。只创建必要会话，不为每个 Plan 预付一次闲置调查调用。

创建必须 single-flight。创建动作取得固定 owner 见证后才能发第一次 assignment；创建或关联结果不明时，不继续另建会话。

所有调查任务，包括第一次绑定后的实际任务提交，均走现行 DevOps `resume` 后果。Planner 可见的是受限的 `resume(message)`；目标始终由运行时解析为本 Plan 的固定 DevOps，不能由模型传入另一个 session id、name、role、agent 或 model。

底层 resume 若是异步投递，适配层可以等待该 invocation 的既有完成凭证；这不要求改造所有调用者的 resume 语义，也不让 Planner 获得任意 join/list 能力。

### 7.4 持续性与恢复边界

正常的多轮调查复用同一个物理 DevOps Session 及其已有上下文；一个问题完成只是当前 invocation 完成，不是销毁整个 DevOps 会话。

重启后能够证明该绑定和会话仍存在，就重绑继续；关联冲突或存在性不明，停止新投递并报告错误；能够证明会话永久丢失，本期也不自动创建替代 DevOps。可保留已有结果形成受限计划，但必须先确认没有未收敛的执行责任。

不把历史 Teacher 的 Replacement 分支直接搬来。需要自动替换固定 DevOps 时，应由系统已有的正式恢复规则明确支持；本期不偷偷创造这个例外。

### 7.5 不接管别人的执行现场

不同 Plan work 不共享调查上下文，但这不代表可以在同一物理执行现场同时造多个运维主线。

创建和运行 DevOps 前，必须经过现有工作区／执行现场的所有权检查。如果该现场正由另一条道路或 Plan 的 DevOps 控制，不能夺取绑定、冒名 resume 或另开终端并行修改。

本期不新增全局调度器。沿用现有排他约束；若无法取得合法现场，就向 Planner 返回冲突。Planner 可以基于已有材料交付带限制的计划。隔离到其他 worktree 只有在系统已经支持且授权明确时才可采用，不把自动新建工作区当默认绕路。

### 7.6 结束时

计划结束后，该 Plan 不再向 DevOps 提交任务。先收敛本次已启动的调用和自有进程，再释放运行时作用域与控制关联；持久记录保留。

不删除 DevOps 已完成的源码修改，不清除工作区，不自动停止原本不属于本次任务的进程，不向 Manager 转让任务或执行现场。

## 8. 工具合同与执行门

### 8.1 Provider 可见工具面

拟议角色 JS 工具名称为 `js-plan`，不是 `js-planner`。具体如何接入现有 JS 执行封装，需要施工时读取当前合同；下面定义的是它允许产生的后果。

| 能力 | 入参 | 结果 | 边界 |
| --- | --- | --- | --- |
| `resume` | 一个非空自由文本 `message` | 固定 DevOps 本次 invocation 的已接受完成记录／完整正式答复 | 不接目标 id、role、model，不创建替身 |
| `ask` | 一个非空自由文本 `message` | 用户已接受的原始回答，或明确的交互失败／取消结果 | 一次一个未决问题，问出后暂停 |
| `finish` | 一个非空自由文本 `message` | 进入最终交付；成功后本次 work 终止 | message 是完整计划正文，不是标题或路径 |
| `review` | 可选的、由本次读取签发的分页游标 | 本次规划可访问的记录及下一页游标 | 无任意路径、任意会话或全局搜索参数 |
| 既有认知画板能力 | 沿用现有接口 | 本次工作的小量认知整理 | 不建立新的 `plan-state.md` 或私有账本 |

控制层可以有调用 id、分页、错误类别、来源 cursor 等结构；禁止的是对知识正文强制 `decision/evidence/confidence/remaining_unknowns` 等 schema。控制结构与知识结构不是一回事。

Planner 不获得普通 read/glob/grep/write/edit、终端、浏览、fetch、fork、任意 resume、Fission、Manager mission ledger 或切换 build 的能力。它可以读取的是已交付给本次规划的材料，不是借回读工具遍历整个仓库。

如果 JS 入口承载程序执行，必须继续使用现有受限宿主：不能经 import、require、进程接口、网络对象或任意文件 API 逃逸。不能只在提示词里列五个方法，却在实际 JS 对象中暴露更大的能力集。

### 8.2 `resume(message)`

处理顺序：

```text
核对 Plan 身份、当前 work、单飞与调用参数
→ 原始请求及调用归属 durable accepted
→ 解析或首次建立唯一固定 DevOps 绑定
→ 经 resume / PromptDispatcher 投递
→ 等待本次 invocation 的正式完成事实
→ 确认答复已被权威记录接受，且本次执行已收敛
→ 将完整、未被模型二次摘要替代的返回结果交给 Planner
```

结果可以是现行 bounded LWR 容器，但不能只返回一个无法读取的记录 id，也不能把部分工具流或普通 idle 当正式答复。

DevOps 的标准 completion 合同是唯一完成依据；不新设一套 Teacher `return` 标记。若现行 DevOps 结束路径不能被同步等待，就在委托 owner 处补这个明确后果，并以契约测试证明；不能靠截取“看起来像答案”的最后一段文字实现。

同一时刻最多一个未结算 `resume`。失败后保留已接受的请求、执行记录和修改。若完成情况未知，不自动重发可能产生外部效果的任务；先按投递和完成凭证对账。

### 8.3 `ask(message)`

处理顺序：

```text
核对 Plan 身份、无在途 DevOps 调用、无其他未决 ask
→ 接受提问文本及其调用身份
→ 以同一身份建立待用户输入的控制事实
→ 幂等显示问题
→ 停驻本次模型运行，等待合法用户输入
→ 用户原文 durable accepted，并关联到该待回答调用
→ 只选择一个合法回送路径
→ Planner 继续
```

本功能不依赖未经证实的 Host 内建 `question` 工具。需要一个受控的主对话等待适配器；现成工具若符合上述合同可以复用，否则通过现行用户消息入口实现，不新增另一套聊天系统。

资料明确将“是否有可用 question 工具”列为未核实项。（资料：`02-现状.md`，L203–L207。）

用户回答如何关联，依据当前 work、被接受的待回答操作和物理消息身份，不依据自然语言是否含“是”“选 A”或问号。用户答非所问、提出新要求或纠正前提，原文也应交给 Planner 判断，不能由接入层替模型筛掉。

`ask` 是逻辑等待，不要求一个 HTTP 请求或内存 TCS 永远存活。原工具调用仍可合法续接时，以工具结果返回；该物理请求已结束时，通过具备原调用来源的 HumanMessage／合法 continuation 恢复。两条路径互斥，不能把同一回答既作工具结果又再注入一次用户消息。

### 8.4 `finish(message)`

`message` 必须是完整最终计划正文。它表达的是 Planner 决定交付，不是“计划已经保存”的假收据。

调用前机器只检查：调用者与当前 work 合法；没有未回答 ask、未完成 DevOps 调用或其他未结算外部效果；当前输入版本没有落后；没有已经获胜的取消或成功完成。

随后进入 §11 的封口、记录导出与完成事务。成功时不再回到模型继续工作；失败时返回或呈现明确的运行错误，不把失败写成成功。保存重试使用同一已接受正文和同一边界，不重新调用模型凭记忆写一版。

不能只在工具参数里放计划：该正文必须经受控 completion 边界成为权威 trace 中的正式助手文本，才能进入 LWR 的 Recent work。否则 raw tool filtering 会把所谓“最终计划”过滤掉。

### 8.5 `review` 与认知画板

回读范围限定为当前 Plan work 的用户已交付材料、已接受的对话与调用结果、当前画板，以及由本次 DevOps 返回记录明确关联的来源。跨 session 的访问需要同一 owner 的来源见证，不靠猜 id 扩大范围。回读不包含 provider 的隐藏推理或私有草稿，只使用系统按既有合同接受的消息、工具证据和记录。

分页必须显式返回是否还有内容和下一页游标，不得把截断伪装为全文。读取可以按既有容量限制分批进行；超出可处理容量就说明限制，不声称完成了无损阅读。

认知画板若现行接口允许 Plan 使用，则复用它的唯一写入口。若尚未授权，需要在该 owner 的能力表加 Plan，而不是建一个私有 `self_note` 文件绕过去。画板是认知摘要，不是权限来源、完成证书或新事实源。（资料：`02-现状.md`，L303–L312。）

### 8.6 双层门禁

Provider schema 必须只展示本次 role 允许的能力；执行门必须重新检查相同的 role、work、owner、目标和操作状态。未知宿主工具默认拒绝，不能因为没有进入静态工具清单就放过。

伪造 `finish`、resume 任意 session、传入任意磁盘路径、调用旧 Student/Teacher 工具、借 Host 内建 plan-exit 进入实施，都必须在实际执行边界失败。

权限投影应沿用现行单一 owner，不能手写另一份 role→JS 工具矩阵。新增 `js-plan` 要同时更新相关 required/denied 检查和真实工具枚举测试。（资料：`02-现状.md`，L245–L258。）

## 9. 正常流程、调度与停止信号

### 9.1 主流程示意

下面的分支来自已接受的工具操作，不是让框架读取自然语言后分类。

```text
接受显式 plan 用户根
    ↓
建立本次 Plan work 与不可变身份／工具配置
    ↓
运行 Planner
    ├─ resume → 固定 DevOps 调查并正式返回 → 继续 Planner
    ├─ ask    → durable 等待用户 → 接受原文回答 → 继续 Planner
    ├─ review／认知整理 → 继续 Planner
    └─ finish → 接受最终正文 → 收敛物理运行 → 导出 LWR → 完成
```

正常流程可以零次提问、零次调查。不能用“复用历史”要求先创建 DevOps 再判断是否必要。

### 9.2 不增加 Learn/Compile 切换

Planner 从开始到决定交付使用同一职责与工具面。起草中发现事实不足，可以继续 resume 或 ask；不出现“已经切入编译，所以再也不能调查”的人工障碍。

`finish` 被正式接受后，本次普通工具面关闭，只允许框架完成导出、取消、错误对账或必要的保存重试。这里是交付事务的边界，不是第二种可任意切回的知识阶段。

### 9.3 idle 的处理

| 已对账的事实 | idle 的含义 | 动作 |
| --- | --- | --- |
| 有未决 ask | 在等用户 | 不 nudge，不完成 |
| 有未结算 resume 或工具 | 在等外部结果 | 等待／对账，不完成 |
| 已接受 finish，尚在物理收敛或保存 | 正在交付 | 继续框架交付路径，不再让模型调查 |
| 当前调用已失败或已取消 | 异常结束 | 走错误或取消路径 |
| 没有上述事实，Planner 普通 text-out | 尚未明确交付或等待 | 发送有界 continuation，要求选择实际下一动作 |
| 已有本次成功终态与导出收据 | 完成后的重复信号 | 幂等忽略，不启动下一任务 |

idle 只能在读到完整消息与工具状态、通过现行 reconcile 之后使用；原始 idle payload 本身不构成完成证据。由 idle 派生的 nudge 消耗 SessionQuiescenceGate 的一次性 permit，不能重复利用同一信号发多次请求。（资料：`02-现状.md`，L209–L219。）

### 9.4 nudge 与预算

本期仅需要“没有明确动作时继续”和“当前调用未按合同完成时修复”的运行提示，不复制历史四种 Student/Teacher continuation。

优先复用现行 `BusyAgentNudge`、`InteractionRepair` 等语义匹配的类别；只有无法准确表达后果时，才在 dispatch owner 增加最小的新类别。普通用户回答使用合法 HumanMessage 续接，不伪装成新任务。

所有自动修复共享系统已有的有限预算，不能因切换发送路径或重建内存作用域而重置。若施工基线没有可复用的有限预算，本稿的保守默认是：同一未完成操作最多两次自动 nudge；之后明确失败。这是本稿默认，不是资料中的现行数值。

知识调查轮数不设这种硬上限。资源上限触发的是运行限制或带缺口交付，不能伪装成“没有更多有价值的问题”。用户等待不消耗 nudge 预算。

### 9.5 普通文本不承担协议

Planner 可以正常给进展说明，但“我想问一下……”这类普通正文不建立待用户状态，“方案如下……”也不自动完成本次 work。

提示词应要求真正需要回答时使用 ask，交付时使用 finish。若模型仅输出正文便停下，框架用上表中的有界修复处理，不扫描问号、标题或关键词判断意图。

### 9.6 不设业务 Stage 状态机

运行程序用现有宿主语言的 continuation、作用域和 await 表达。必要的持久事实只回答“哪次调用被接受、等待谁、结果是否被接受、哪个文件导出已经提交”，不回答“现在处于理解、反证、收敛还是编译阶段”。

内存中可以有一个活动操作的取消句柄和 waiter，但不能再建若干相互印证的 `isPlanning`、`isCompiling`、`hasAnswer`、`canFinish` 注册表。恢复只读权威事实，不用注册表是否有某个 key 来推断业务真相。历史 CE collapse 正是在修正这一问题。（资料：`03-映射与问题.md`，L213–L223。）

## 10. 记录、证据与“无需其他持久化”

### 10.1 哪一层保存什么

“无需其他持久化”在本稿中的含义是：不新增 Planner 专属知识存储，保留系统正常运行本来需要的记录；LWR 是本功能唯一正式交付文件。

| 内容 | 唯一负责的现行层／拟补后果 | 明确不做 |
| --- | --- | --- |
| 用户原始请求、后续回答与修订 | 现行交互接受记录及其 trace | 再复制进 QA.md 作为第二份权威 |
| Planner→DevOps 的问题与正式返回 | 现行委托调用、完成事实与来源 trace | 新建 QuestionAsked／TeacherAnswered 知识事件流 |
| DevOps 实际命令、修改和测试证据 | DevOps 自身的执行与工作记录 | 只凭 Planner 转述冒充原始证据 |
| Planner 当前理解、暂定和阻塞 | 既有 cognitive-workspace | 私有 plan-state.json、问题树数据库 |
| Blogger 已沉淀经历 | 既有 Chronicle | Planner 自己重写一份回忆录 |
| 最终计划 | 当前 Plan work 的正式助手文本 | 独立的计划正文数据库或 SKILL |
| 完整导出 | 同一 bounded LWR 的 `LWR.md` | 另产 plan.md 与 LWR 两份可分别编辑的正式计划 |
| 等待、绑定、导出收据、完成 | 相应的现行控制记录 owner，必要时补最小合同 | 新建 Planner Journal，与既有事件流互相 reconcile |

导出文件是 canonical record 的有意物化，不成为可以反向修改系统事实的第二个真相源。文件被用户编辑，不能自动改写 trace 或已完成的计划；系统只报告它与已接受导出不符。

### 10.2 输入先被接受，再产生后果

用户请求未 durable accepted，不启动 Planner。调查问题未被接受，不向 DevOps 投递。DevOps 答复未被权威记录接受，不作为成功结果交给 Planner。用户回答未被接受，不恢复 Planner。

这里复用现行事件与 trace 的接受边界，不要求照搬历史 QA 原子追加实现。但必须证明等价的“接收方不会基于未被接受的内容继续工作”。若现行接入只留下可丢失的 provider transcript，不能宣称满足此条。

接受与物理发送不是同一件事。发送已被接受但回执丢失时，先对账，不重新执行相同外部效果。不能用文本尾部相同来判断两个业务输入是不是同一次调用；以调用和消息的因果身份去重。

### 10.3 Planner 的成稿输入

Planner 的正常上下文包含用户已交付信息、已返回调查结论和当前理解。需要重读时，通过受限 review 获取本次工作记录和可访问来源；恢复时也从这些记录重建，不要求 DevOps 再调查一遍只为重现背景。

对决定方案成立与否的事实，必须保留能够定位的来源：用户明确选择、资料位置、调查 invocation、实际测试观察，或原始 trace 游标。引用记录本身不把旧结论升级为当前已验证事实。

不要求 Planner 读完全部终端日志。它需要读完与当前决定有关、已经向它交付的内容；决定仍有疑点时，再让 DevOps针对性核实。原始日志过长时，不能静默截断还称“完整证据”。

### 10.4 父记录与子记录不伪造拼接

DevOps 的工作记录属于 DevOps 那次 invocation；Planner 的记录属于本次 Plan work。不能把子会话 trace 复制成父会话发生过的助手或用户消息，不能拼接 Opening，不能把记录消费者误当记录 owner。

Planner 会在自己的正文中表达吸收后的事实与理由，并引用已交付的子记录来源。子记录可以通过合法来源关系回读，但不用为每次调查再导出一份文件作为用户交付。

如果当前返回容器被 raw tool filtering 排除，关键调查结论必须在 Planner 的正式计划中自然表达；不能依赖“工具结果反正出现在历史里”来保证 LWR 内容完整。

### 10.5 LWR 的固定形状

沿用：

```text
Opening? + Chronicle + Recent work
```

记录边界沿用本次逻辑 invocation 的因果范围，不以整个物理 Session 的起止代替。Planning Table/T1 不参与这次 Opening；澄清发生在原始 Opening 之后，就按实际因果位置保存，不延长 Opening 来模仿 BlindPlan。

最终计划处在 Recent work 的最后一条正式助手文本中。不增加“Closing report”第四段，不把计划藏在被过滤的工具参数里，也不在它后面追加一句助手“已保存”抢走正式陈述的位置。（资料：`02-现状.md`，L79–L91。）

导出默认使用包含 Opening 的完整视图。展示界面若有其他省略 Opening 的投影，仍然是同一个 canonical record 的不同视图，不因此改变交付文件。

### 10.6 压缩与容量

本稿接受现行 Chronicle／Recent 的记录语义，不恢复历史 QA 的“永不 compaction”。但是最终交付时，最终计划正文必须完整保留在 Recent work，不能刚被 Blogger 覆盖，就只剩一段摘要。

为此，导出必须冻结一致的 trace 范围和 Chronicle 覆盖水位；Blogger 的覆盖终点不得越过该正式最终消息。可以复用已有 terminal-text 保留能力；资料中的 `captureTerminalText` 是定位线索，不是已经证明这条约束完整成立。

回读或渲染遇到容量上限，要分批读取、使用现有记录机制，或明确失败。不静默丢失尾部，不用另一模型重新写一份“差不多”的计划作为恢复办法。

### 10.7 Casebook

本期不新增 Plan 或 DevOps 的 CaseFinalize 来源。资料中的现行规则将来源限定为 Engineer 的一次逻辑工作轨迹，Bookkeeper 也不能自行调查仓库。用户的“不反对收纳”不是已经存在的通道，也不是必须扩展的需求。（资料：`02-现状.md`，L108–L140。）

原有、已经合法发生的缓存读取或维护不因 Plan 被破坏；Plan 本身不增加案例归档调用，也不等待案例写入才完成。

## 11. LWR 落盘与最终完成事务

### 11.1 本节的硬约束

**没有成功落盘凭证，不提交规划成功终态；没有相同 work 的成功终态，不展示“计划已完成”。**

文件保存、运行终态和界面展示是三个不同后果。它们不能依靠同一句模型文本相互证明。

LWR 渲染属于现有 WorkRecord owner；文件落盘属于受限的 artifact export 适配器；完成提交仍由 managed execution owner 负责。Plan runtime 只编排，不自己拼出另一种 LWR。

### 11.2 文件位置

本稿固定相对布局：

```text
<项目私有数据根>/work-record/plan/<canonical-work-key>/LWR.md
```

`canonical-work-key` 从现有 work／invocation 身份作安全、确定性编码得到，不由模型起名，不使用用户标题作为路径，也不另分配第二套规划身份。若单个现有 id 不具备所需唯一性，就编码现有完整身份元组，不凭时间戳猜唯一。

项目私有数据根优先复用宿主已经验证的持久根。资料没有给出该统一根的可调用接口，因此施工必须定位；不能把下面的后备选择写成现状事实。

如果当前没有可复用的私有根接口，本期对 Git 工作区的后备实现是：以 `git rev-parse --absolute-git-dir` 得到的真实 Git 私有目录为基础，使用其下 `wanxiangshu` 目录。不能字符串拼出 `<workspace>/.git`，因为它可能是文件或 worktree 引用。历史 QA 使用真实 Git 私有目录的做法只是可参考的实现证据。（资料：`01-历史.md`，L460–L464。）

非 Git 工作区只有在宿主提供稳定、私有、可持久化的项目数据根时支持。没有这样的根，初始化就报能力不足；不静默写到临时目录、用户仓库根或另一个项目中。

目录只能由运行时推导，模型不传入绝对路径。拒绝路径穿越、符号链接逃逸、不安全 id 和根目录漂移。采用现有平台的最小访问权限；支持 POSIX 的后备实现使用私有目录和仅当前用户可读写的文件权限。

### 11.3 文件内容与对外定位

文件编码为完整 UTF-8，内容来自同一 canonical bounded LWR renderer，默认包含 Opening。不得为了好看删掉 Chronicle、改写已接受的最终计划或另加一个与系统定义不同的 JSON/YAML 报告头。

文件系统适配器不改变正文换行或 Unicode 内容；需要规范化时由统一 renderer 决定并绑定版本，不能让写盘与读回使用两套规则。

成功后，界面通过系统产物卡或现有工具收据展示可定位路径。文件定位信息不作为一条新的助手正文追加到当前 record 尾部。若宿主只能把路径写成助手消息，必须先修正这个边界，不能牺牲“最终正文就是计划”。

不自动打开执行入口，不附带“按此计划开始”的按钮或后续任务。已完成文件只有普通查看／取得能力。

### 11.4 接受最终正文

`finish(message)` 首先要把完整计划接受为**正式助手正文**。这是一个拟议的 completion 接入后果，不是向工具结果里塞一段 Markdown。

拟议内核端口可写成：

```fsharp
// 合同示意；实际类型复用当前 owner 的身份与游标。
AcceptFinalStatement :
    CurrentPlanWitness * FinishOperationId * string
    -> Async<Result<AcceptedFinalStatement, CompletionError>>
```

`AcceptedFinalStatement` 只能由受控接受边界签发，至少能定位当前 work、finish 调用与正式消息；调用方不能自行构造一个字符串冒充已接受证据。

普通 `finish` 工具的参数记录仍然只是工具记录；它与正式助手文本可共享被接受的内容来源，但不能通过把原始工具 call/result 塞入 LWR 来冒充正规陈述。

### 11.5 物理停止与逻辑完成分开

系统要能够先停止当前 provider 继续产生普通后果，再完成落盘，最后提交逻辑成功。provider turn 结束，不等于 Plan work 成功。

接受 finish 后，工具执行门关闭本次普通能力。当前 finish 自身作为收尾操作不算阻塞它自己的未结算工具，否则会形成自等死锁。其他真实在途操作必须已完成或依法取消收敛。

如果宿主需要一个 completion acknowledgement 才能物理结束当前 turn，该确认必须是协议层处理，不能成为新的正式助手文本、不能向用户提前宣布成功，也不能替换计划正文。

不能未经验证就复用历史固定完成字符串或 TextComplete 改写。宿主是否支持“受控最终文本接受／停止 provider／稍后提交 work 成功”，是上线前的硬门槛。

### 11.6 一致快照

导出取得一个不再漂移的记录快照，至少绑定：

```text
canonical work 身份
本次记录的 start / end 因果边界
正式最终助手消息的来源位置
Chronicle 覆盖水位与 Recent 后缀边界
记录渲染版本或等价稳定配置
本次 finish 操作身份
```

这些是记录与控制元数据，不包含计划知识分类。

快照应由现有 WorkRecord owner 构造。`lifecycleWorkRecordBounded`、`lifecycleWorkRecordBoundedForRun`、`captureProjection`、`captureTerminalText` 是已登记的定位入口；实际选哪个，要看它是否能满足上述同一次快照合同，不能把几个各自读取最新状态的 API 顺次调用就称为一致快照。（资料：`02-现状.md`，L94–L102。）

最终的 invocation end 边界必须由 WorkRecord／execution owner 签发，不能把当前 Session 长度当作结束游标。若现行类型只有在成功 terminal 后才能提供 end 边界，需要在对应 owner 增加合法的封口接受／预备完成合同，先确定此次工作的正文范围，再执行导出；私有 terminal 标记不成为额外正文。不能先完成，再为了满足类型而倒补“先落盘”的说法。

不强制新增一次 Blogger 调用来“补全”记录。已有 Chronicle 加上其尚未覆盖的 Recent suffix 应能形成记录。冻结期间不得让新的覆盖水位悄悄吞掉最后计划。

### 11.7 写盘协议

在既有 artifact／文件原子操作能力满足时复用；否则新增最薄的受限适配器。协议为：

```text
由被冻结的记录快照渲染完整字节
→ 校验记录形状、正式最终消息归属、UTF-8 与非空性
→ 在目标目录创建排他临时文件
→ 写入全部字节并完成平台要求的持久化 flush
→ 原子发布到唯一目标，禁止覆盖不同的已存在内容
→ 持久化目录项（平台要求时）
→ 读回，确认目标字节与本次预期完整一致
→ 得到导出收据
```

“原子发布”不等于随手使用可能覆盖旧文件的 rename。适配器必须提供并验证 no-replace 发布语义，或采用经过证明的同等原语。目标已存在时，只能在完整字节、身份与冻结来源一致的条件下视为同一次成功重试；不同内容必须报冲突。

收据至少包含：现有 work 身份、finish 操作、冻结范围／消息引用、受限目标位置、字节长度、内容摘要和 renderer 版本。摘要是完整性校验，不是计划正确性证明。

临时文件只是原子导出的技术中间物，不是第二份正式计划、恢复用 QA 或认知状态。任务取消或安全恢复后可清理未发布临时文件；不自动删除已成功交付的 LWR。

### 11.8 提交终态

导出成功后，由 managed execution owner 将**导出收据与成功终态作为同一次可验证接受**提交。

优先使用现有可原子提交的完成机制。如果现有事件接口不能原子写两个事实，应把导出收据作为同一个完成事实的受限载荷，或扩展相应 owner 的原子合同；不能顺次写两条独立事件后声称没有崩溃窗口。

任何方案都必须有一个唯一的成功线性化点。取消与完成谁先被接受，由同一个权威提交顺序决定，不由两个内存布尔值决定。

终态提交成功后，显示最终计划和系统文件卡。界面回执丢失时重放同一个完成结果，不重新生成、重新执行调查或再提交一个成功终态。

### 11.9 新用户输入与封口的竞争

最终正文不能建立在已经过时的用户决定上。finish 的准入与用户输入接受必须在同一 work 的交互序列上比较。

如果新用户消息先被接受，当前 finish 的输入见证已经落后：拒绝该次封口，将新输入交给 Planner，再决定是否重新交付。不能先保存旧计划，再补一句“用户刚才又说了什么”。

如果 finish 封口先被接受，它取得当前 work 的收尾权。普通后续消息不能悄悄插进已经冻结的范围；系统明确记录它尚未归属当前 work，等本次结算后按新的用户请求处理。不能丢失消息、自动实施旧计划，或把物理到达时间冒充权威接受顺序。

封口不等于成功。用户取消仍可在成功提交前竞争取得取消终态；取消胜出后，即使已经存在未提交的文件，也不得显示规划完成。若成功已提交，后到取消不能抹去已发生完成事实。

该排序要由交互／执行 owner 提供，不在 Plan 中持有一个跨磁盘 I/O 的粗暴全局锁，也不无限阻塞其他会话。

### 11.10 落盘失败

写盘、flush、发布、读回或完成提交任何一处失败，都不能显示成功。保留已接受的计划正文、冻结来源和错误，以便针对同一导出操作重试。

只要来源和环境未改变，保存重试不需要新的模型调用。目标不同内容、来源快照缺失或 renderer 版本不兼容时，停止重试并明确报告，不能用新文本覆盖旧目标。

保存失败提示应来自系统错误面，不作为新的正式助手正文加到 LWR 尾部。界面可以显示“计划已形成，记录保存失败，本次尚未完成”，但不能生成假路径。

## 12. 恢复、取消与幂等

### 12.1 恢复原则

恢复先看既有 durable facts，再看物理会话和文件证据；内存状态只用于当前进程的协调，不能成为唯一恢复依据。

不按问答文本内容猜阶段，不从最后一个问号推断是否正在等用户，不凭目标文件存在就推断计划完成，也不凭同名 session 推断它就是绑定 DevOps。

若事实不足以证明可安全继续，就失败关闭，并保留证据。恢复能力不能建立在“再发一次大概没事”。

### 12.2 重启恢复矩阵

| 已接受事实／物理证据 | 恢复动作 | 禁止行为 |
| --- | --- | --- |
| 用户根已接受，Planner 尚未开始 | 复用原身份按 dispatch 合同继续 | 再建一个用户根 |
| ask 已接受、未显示成功 | 按同一操作幂等显示问题，进入等待 | 另问一个问题或当完成 |
| ask 正在等回答 | 恢复等待，不发模型 nudge | 用超时或重启补一个默认答案 |
| 用户回答已接受，尚未交给 Planner | 只回送一次原文，按来源续接 | 同时回填工具结果和新用户消息 |
| resume 已接受，物理发送未知 | 按现有发送接受记录查询／对账 | 直接重发可能修改仓库的任务 |
| DevOps 调用仍在合法运行 | 重绑同一调用等待结果 | 再造一个 DevOps 抢先完成 |
| DevOps 答复和调用完成已接受，Planner 未收到 | 重放同一个已接受结果 | 重新调查或丢失已完成修复 |
| DevOps 物理 session 永久丢失 | 报错并确认在途责任是否已收敛 | 静默新建替身并说“仍是同一个” |
| 最终正文已接受，尚未导出 | 恢复相同正文、范围和导出操作 | 让模型重新写一版计划 |
| 文件已发布，成功终态未提交 | 按原冻结来源验证字节，重试原完成提交 | 仅凭存在性宣布成功或覆盖异内容 |
| 成功终态与导出收据已接受 | 幂等呈现已完成产物，释放残余作用域 | 再次调用模型或重新调查 |
| 取消已接受，后到子结果或 idle | 保留合法诊断，拒绝重新唤醒本次工作 | 迟到结果把取消任务复活 |
| 权威来源缺失或冲突 | 报告无法可靠恢复，保留现有文件与记录 | 拼接材料制造一份看似连续的历史 |

### 12.3 取消

取消沿当前已有 managed work 取消路径扩展：撤销本次继续投递的能力，通知在途调用，释放 waiters，停止能证明属于本次调用的进程，最终提交唯一取消结果。

用户取消期间不要求 Planner 再生成一份总结；已接受材料仍可由系统正常查看。取消不触发最终 LWR 成功交付。若取消胜出时文件已发布、成功终态尚未接受，该文件属于未结算导出，不是已交付产物。仅当现有记录和完整字节能证明它由本次未结算操作创建，才可按原导出合同清理；无法证明或内容已变，就保留并显示未结算状态，不能误删已完成或用户修改的文件。

已完成的修复留在工作区，系统显示其存在和验证情况；不自动 git reset、checkout、clean、删目录或卸载依赖。回滚是一项另外需要明确目标和授权的工作。

无法证明进程已停止时，不能显示“所有运行已结束”。显示取消处理中或明确失败，停止新的委托；安全停机和证据保留优先于界面看起来干净。

### 12.4 并发不变量

同一 Plan work 同时最多有一个活动 Planner provider 请求、一个未完成 DevOps invocation、一个未决用户问题、一个最终导出操作。`ask`、`resume`、`finish` 在会产生外部后果的范围上互斥。

这些上限不要求维护四张彼此推断阶段的表。由一次活动操作的作用域和现行 invocation／等待／完成事实落实；只保留必要的投递与取消句柄。

并发调用应在产生外部效果前明确拒绝。重复投递同一已接受操作要幂等，两个内容相同但身份不同的真实用户输入不能被文本去重抹掉。

### 12.5 进程与副作用的恢复

DevOps 可能已经修改文件、启动测试或留下输出，然后丢失回执。恢复必须从它的实际工具记录和当前物理证据对账，不靠 Planner 的记忆决定“应该没执行”。

失去完成回执不等于没有效果。只有已证明可重复的操作，才可在现行安全重试合同下自动重试；其他情况先调查或报告不确定性。

计划中可以写明“该验证未完成”“该效果尚不能确认”，但这不免除系统收敛当前活跃执行的责任。未知结论可以交付；仍可能继续运行的未受控执行不能被一份 LWR 遮过去。

## 13. 失败处理总表

| 情形 | 对模型／用户的结果 | 是否可交付一份受限计划 | 系统动作 |
| --- | --- | --- | --- |
| Plan 身份或能力构造失败 | 启动失败 | 否 | 不发 provider 请求，不降级为普通万能 agent |
| 私有输出根无法确定或没有基本写权限 | 初始化能力错误 | 本次不能宣称 LWR 交付 | 尽早失败；不隐式改写其他目录 |
| 初始材料无法读取 | 明确指出缺少哪些材料 | 其余证据足够时可以 | 不声称全部读过 |
| DevOps 首次创建／绑定失败 | resume 错误 | 可以，写明调查未发生 | 保留已接受请求，不创建第二候选 |
| 现场被其他执行者合法占用 | 所有权冲突 | 可以 | 不接管、不绕过、不平行改写 |
| DevOps 调查发现必须作架构／产品选择 | 返回事实与选择 | 可以；必要时问用户 | 不让 DevOps 擅自裁定 |
| DevOps 已修复但验证失败 | 返回真实失败和剩余影响 | 可以 | 不把失败测试写成通过，不继续列已做修复为待办 |
| DevOps 无法提供外部网络事实 | 明确未核实 | 可以 | 不扩权浏览，不使用旧缓存冒充现状 |
| DevOps 普通 idle，无合法完成 | 先有界修复，耗尽则失败 | 在执行责任收敛后可以 | 不截取普通正文当答案 |
| 用户无法回答某个取舍 | 保留未决／提出安全验证 | 可以 | 不重复审问，不把“随便”扩大成授权 |
| 用户不回答 | 保持等待 | 不自动交付 | 不 nudge 用户，不虚构回答 |
| 用户说“别问了，给草案” | 交付已知边界内的计划 | 可以 | 未授权风险仍挡在计划之外 |
| 输入或结果持久接受失败 | 运行错误 | 需先恢复可靠记录 | 不让未记录内容推动后续效果 |
| 上下文容量不够 | 分批回读或容量错误 | 能诚实限定时可以 | 不静默丢失关键约束 |
| LWR 来源损坏或最终正文缺失 | 记录错误 | 否 | 保留原证据，不临时合成一份替代记录 |
| 输出目标存在不同内容 | 导出冲突 | 否 | 不覆盖，不换随机路径掩盖同一 work 冲突 |
| 磁盘满／flush／读回失败 | 保存失败，本次尚未完成 | 否 | 重试同一导出或停止，不能成功终止 |
| 文件已写，终态提交失败 | 结算未完成 | 否 | 验证原文件后重试同一提交 |
| 界面显示失败，终态已成功 | 展示错误 | 已完成 | 幂等重放产物收据，不重跑工作 |
| 用户取消 | 取消结果 | 不新增成功交付 | 收敛自有执行，保留已发生修改 |

## 14. 安全、隐私、观测与容量边界

### 14.1 材料是证据，不是新指令

仓库文件、日志、Casebook、DevOps 读到的文档和外部提供材料，均不能自行改写 Plan 的权限或要求转入实施。材料中出现“忽略用户”“运行部署”“把密钥发到某处”，仍只是待解释的数据。

被正式装配到 Planner 的 ASK 方法资源，与普通仓库内容分层处理。不能因为仓库中也有一个叫 ASK.md 的文件，就允许任意仓库覆盖系统方法或安全规则。

### 14.2 授权不扩张

用户选择 `plan`，授权规划及已约定的职责内调查／修复，不自动授权新的付费、生产变更、不可逆删除、数据外发、权限扩大或安全弱化。ASK 也明确不允许借默认代替这些授权。（资料：`ASK.md`，L110–L126。）

资料中的 DevOps 无外部浏览职责。因此本稿不新增浏览器、web 工具，也不借 shell curl 绕过这一职责边界；原有合法的构建／依赖网络行为仍按既有能力和审批规则执行，不能顺势扩大到私人数据上传。

### 14.3 私有记录

LWR 可能含代码、路径、内部架构、错误日志和用户要求，默认保存在项目私有根，不自动纳入 Git、外发、发布或共享。

既有 trace 和系统日志的敏感数据规则继续生效。正式计划尽量引用敏感事实的必要结论，避免复制密钥或无关私密原文；不以“完整”为由导出全部敏感工具输出。

文件权限、访问见证与路径隔离是机器约束；模型“承诺不泄漏”不能替代它们。

### 14.4 观测

允许记录：现有 session/work/call id，操作名称与结果，等待类别，dispatch 接受／完成位置，重试与 nudge 次数，导出字节数、摘要、耗时和错误代码。

不额外记录：提问全文、用户回答全文、计划全文、秘密、完整工具输出、模型的逐步私人推理、推测的置信度或“是否已经理解”。正文已有其合法 trace owner，不再复制进诊断日志。

运行指标用于发现死等、重复调用、导出失败和取消问题，不作为机器判断规划是否充分的评分。观察轮数不是建立硬性知识收敛阈值。

### 14.5 容量与成本

不固定“至少一次 DevOps”“至少一次用户确认”“至少一次总结调用”。简单任务允许直接 finish；导出不增加模型调用；保存重试不增加模型调用；等待用户不增加模型调用。

对记录分页、单次工具消息大小、调用时间和执行资源使用既有上限。触发上限应给出具体限制与剩余影响，而不是截断后继续表示全部完成。

暂不为本期新增一组预算配置、并发配置、计划深度档位或自动总结服务。只有真实验收证明现有上限不适用，才在对应 owner 层作最小调整。

## 15. 提示词、ASK 与最终计划的内容合同

### 15.1 资源装配

Planner 的有效方法来自：现行公共规则、Plan 职责文本、正式打包的 ASK 方法，以及必要的运行提示。公共安全和权限规则不因 ASK 而降低。

ASK 不能只在仓库根放着，期待没有源码读取权限的 Planner 自己找。应把用户提供并经项目维护者纳入本功能的文本正式打包到 provider 资源中，在创建 Plan 的有效 profile 时实际装配进去。

中文方法正文以本次 `ASK.md` 的正文为初始来源；去掉 skill frontmatter 等不参与提示的包装时，不删除正文纪律。生产资源是唯一运行时来源；根目录文件可以作为用户原始资料保留，但不同时作为第二个动态覆盖源。

英文资源作等义版本，遵守现行 `en.md` / `zh-CN.md` 成对、占位符集合一致、协议标识符不翻译的规则。不能以英文空文件通过 parity，也不能在一个 life 中随意切换自然语言世界。（资料：`02-现状.md`，L255–L258。） （资料：`02-现状.md`，L303–L308。）

方法在有效 system 资源中装配，不每轮当新用户指令重复追加。资源版本和身份按现行 profile 合同稳定；同一 life 中不会因为工作区有人改了 ASK.md 就悄悄变更行为。

### 15.2 Planner 职责提示词：中文候选正文

以下是拟议的 `role/plan/zh-CN.md` 正文；它与完整 ASK 方法一起使用，不替代 ASK。

```markdown
你是 Planner，用户通过系统的 plan 入口选择你。你的工作是把用户的目标、约束、事实和取舍整理成值得执行的计划，而不是开始实施。

先利用用户已经提供的材料和已有回答。遵循“只问关键”的方法：先决定查证、默认、试验、提问还是延后，不把能查明的事实推给用户。已确认、已核实、暂定、待验证和未决不能混写；你提出的默认不是用户的决定。

需要源码、环境或运行事实时，用 resume 持续找本次绑定的 DevOps。你不直接调查仓库，不运行命令，不修改源码，不差遣其他代理。提问时说明对当前决定有用的理解和疑点，允许 DevOps 直接推翻你的前提，不要求它按固定知识栏目回答。

DevOps 可以在现有职责内就地修复非架构级缺陷、补测试并重新验证。吸收它已经做过的事情：已完成修复是新的事实，不是未来待办。没有证据的验证不能写成通过。不要把调查委托变成整份方案的实施，也不要替用户授权提交、部署或新的高风险后果。

需要用户的场景、优先级、取舍或必要授权时，用 ask 问一个实质问题，然后等待回答。不要把多个问题藏在一个问号中；不要再次询问已经回答的内容；不要在等待时虚构用户选择。普通进展正文不代替 ask。

维护能影响下一步的小量认识，使用本次已有记录和认知能力，不建立独立 QA、问题树或计划状态文件。需要回读时只读取已交付给本次工作的材料；不要声称拥有未实际保存的记忆。

每轮判断是否已足够交付。足够就结束，不要求用户批准你的收尾，也不为了完整而延长访谈。有实质调查的方案，交付前检查会推翻计划的关键前提；最后一次调查已经完成这件事时，不为形式再问一遍。

合理结论也可以是不做、缩小范围、先验证或暂不可承诺。说明依据、阻塞、验证办法和重开条件。用户要求停止提问时，交付当前边界内的草案，不用默认抹去未授权风险。

最终计划应让未参加对话的人知道：要做什么、为何这样做、已经查明或修复什么、后续怎样开展和验收、哪些仍不能当真。长度服从任务，不机械填模板，不为压缩而隐去关键风险。

决定交付时，用 finish 提交完整最终计划正文，不只提交标题、路径或“计划已生成”。提交正文时不要声称已经保存，保存和完成由系统确认。finish 成功后本次规划结束，不接 Manager，不启动实施，不追加“是否开始执行”的询问。
```

### 15.3 Planner 职责提示词：英文等义候选

这是同一角色资源的语言配对，不是另一个角色或另一套权限。

```markdown
You are Planner, selected through the system's plan entry. Turn the user's goals, constraints, evidence, and choices into a useful plan. Do not begin implementing that plan.

Use the materials and answers already provided. Follow “ask what matters”: choose whether to verify, adopt a reversible default, test, ask, or defer before deciding what to ask. Do not ask the user to retrieve facts you can establish through the available investigation path. Keep confirmed decisions, verified facts, provisional choices, unverified claims, and unresolved decisions distinct. Your defaults are not the user's decisions.

Use resume to consult the DevOps bound to this planning work whenever you need repository, environment, or execution evidence. Do not investigate the repository, run commands, edit source, or commission other agents yourself. Give the understanding and uncertainty that matter to the current decision. Let DevOps reject your premise rather than filling a fixed knowledge schema.

DevOps may repair non-architectural defects, add regression tests, and verify the result within its existing authority. Incorporate work already performed as evidence, not as future tasks. Never describe an unperformed verification as passing. Do not turn investigation into implementation of the whole proposal or authorize commits, deployment, or new high-risk consequences on the user's behalf.

Use ask for one substantive question when the user's situation, priorities, trade-off, or required authorization would change the next action. Then wait. Do not hide several decisions in one question, repeat answered questions, or invent the user's answer. Ordinary progress text is not a substitute for ask.

Keep only the understanding that affects the next action. Use this work's existing records and cognitive facilities; do not create a separate QA log, question tree, or plan-state file. Review only material delivered to this work. Do not claim memory that was never retained.

Check after each step whether there is enough to deliver. Finish when there is, without requiring the user to approve the act of finishing or extending the interview for completeness. For plans grounded in substantial investigation, challenge premises that could invalidate the plan before delivery. Do not add a ceremonial extra round when the latest investigation already did that.

A valid outcome may be not proceeding, reducing scope, verifying first, or being unable to commit yet. Explain evidence, blockers, verification steps, and reopening conditions. When the user asks you to stop questioning, deliver the current bounded draft without defaulting away unauthorized risks.

The final plan must let someone outside the conversation understand what to do, why, what has already been established or repaired, how subsequent work can proceed and be checked, and what remains uncertain. Match the length to the task. Do not mechanically fill a template or hide important risks to shorten the document.

When ready, use finish with the complete final plan body, not merely a title, path, or statement that a plan was generated. Do not claim it is already saved; the system confirms persistence and completion. After finish succeeds, this planning work ends. Do not hand it to Manager, begin implementation, or ask whether execution should start.
```

### 15.4 DevOps 调查委托的补充文本

不复制一套新的 DevOps 系统角色。保持标准角色文本，只在由 typed provenance 证明的 Plan owner assignment 中加入有限的委托上下文。

```markdown
这次目标来自独立的 Plan 工作。请调查会改变规划判断的事实，必要时直接纠正问题前提；不要为迎合提问而证明一个尚无证据的结论。

你的现行职责与权限不变。可以就地修复非架构级缺陷、补回归测试并重新验证，但不要实施尚未获授权的完整方案，不代定新的产品、架构、兼容或安全政策。

回答应让 Planner 分清事实依据、已经发生的修改、实际验证结果和仍未知的事项。用适合问题的自然语言说明，不需要固定知识字段。需要用户取舍时，把相关事实与问题交回 Planner。

通过本次标准 invocation 的完成合同交还结果，不抢占主对话，不把普通 idle 当作完成，不创建其他代理或替代 DevOps。
```

这段文本本身不授予能力；权限来自受控调用者、绑定和 DevOps 现有 profile。

### 15.5 非完成 idle 的提示

```markdown
本次规划尚未交付，也没有登记正在等待用户或调查结果。请继续完成实际下一步：需要事实就 resume，需要用户回答就 ask，已经可以交付就 finish。不要仅输出结束语后停止，也不要为了继续而制造问题。
```

只在 §9 的准确条件和有限预算内发送。等用户时不使用这段话。

### 15.6 最终计划的内容合同

最终计划是自然语言，不是固定字段报告。以下是模型自检要点，不是解析器或数据库必填项。

应说清本次交付边界、目标与明确不做的事；有证据的现状与关键约束；主要选择及其理由；后续步骤的依赖和验收；已发生的修复及验证；仍然未决或待验证的事项；何种变化会推翻当前方案。

用户要的是方向选择，就不要强制补完整实现清单；用户要的是保姆级设计，则应给出组件、接口、异常、测试与施工顺序。ASK 的“一页左右”是默认偏好，不是压缩掉必要细节的硬上限。（资料：`ASK.md`，L183–L220。）

可参考但不强制的正文结构：

```markdown
# 计划：{任务名称}

本次只到：{明确的阶段}
结论：{可开展什么；还不能开展什么}

## 目标与范围
{目标、成功表现、不做什么}

## 已确定的依据
{用户决定、已核实事实、关键来源；不要把二者混成一种权威}

## 方案与理由
{主要选择、必要的替代方案及为何未选、代价与适用条件}

## 调查中已经完成的工作
{仅在实际发生时填写：修改、验证结果、仍存在的影响}

## 后续步骤与验收
{顺序、依赖、预期产物、如何判断是否完成；这些不是已经执行的事实}

## 未决、验证与重开条件
{未知→需要什么证据或决定→影响哪个动作；责任人未知就写待指定}
```

不得把示例路径、预期测试结果、假设的 API 或尚未核实的运行能力写成已存在。信息不足时明确限定，不用模板填满制造确定性。

### 15.7 提示词验收

快照与语言 parity 测试验证实际装配结果；行为 canary 用明确目标、错误前提、用户说“别问了”、需要重大授权、调查发现修复等情景检查表现。

模型行为的语义好坏由人工看具体输出，不能建一个“计划充分性评分器”反过来控制运行终态。机器测试应检查真正可验证的后果：有没有错误发工具、有没有缺失来源、有没有提前完成、有没有越权或重复投递同一个调用。是否在语义上重复询问已回答的问题，仍由具体行为场景审阅，不接入通用语义判定器。

## 16. 工程落位与逐文件改造地图

### 16.1 分层原则

Plan orchestration 只连接现有 owner，不吞并它们。角色规则归角色／能力层；固定 DevOps 归委托和关联层；用户等待归交互层；工作记录归 WorkRecord；原子文件 I/O 归 Host 适配层；终态归 managed execution。

`Foundation/` 保持纯核，不为 Plan 引入文件、Host 或上层运行时依赖。所有新增 F# 文件要进入唯一 owner 的 fsproj，并按真实编译依赖排序。资料已明确 owner-locality 与合法源根要求。（资料：`02-现状.md`，L255–L258。）

### 16.2 已登记位置：施工必须检查

下表中路径在资料中出现过，但具体签名与行号尚未在实时工作区复核。

| 位置 | 需要检查或修改的内容 | 不得顺便做的事 |
| --- | --- | --- |
| `src/Wanxiangshu/Foundation/Roles.fs` | 唯一 Plan 身份、公开目录与角色规则 | 复活 Student/Teacher 或 fast/deep |
| `src/Wanxiangshu/OpenCode/Tools/ManagedAgent.fs` | `plan` 进入合法身份表、名称与可见性一致 | 新建并列 `planner` 名称 |
| ManagedAgentConfig 的当前归属文件 | 把系统 `plan` 纳入正确托管配置，冻结能力与 prompt | 静默退回宿主普通万能／build agent |
| `Interaction/Authority/` | Plan 根与续接、owner 见证、不可变 profile | 再建一份 model 或 authority 绑定 |
| `Interaction/Dispatch/` | ask 续接、固定 DevOps assignment、nudge 与完成对账 | 绕过 PromptDispatcher |
| `OpenCode/SessionNudge.fs` 及当前 reconcile 接入 | idle 分类与 quiescence permit | 从原始 idle 或文本推断完成 |
| `Execution/Delegation/` | Plan owner 对固定 DevOps 的受限 resume 与同步结果收取 | 授予任意委托能力 |
| `Execution/Session/Association.fs` 及关联投影 | 标准 WorkSession 的 owner 关联与恢复证明 | 新增 Teacher Satellite |
| `Mission/Relay/Fold.fs` 与实际固定 DevOps owner | 找出道路专属绑定假设，支持 PlanWork owner 的最小扩展 | 为 Plan 创建假 Manager／假 Road |
| `Mission/WorkRecord/Materialize.fs`、`Model.fs`、`Surface.fs` | 同一 work 的一致 bounded render、最后助手正文保留 | 新建 Closing 段或拼接 Opening |
| `OpenCode/Plugin/PluginSessionWiring.fs`、`PluginHooks.fs` | 启动、输入、完成、取消与导出装配 | 在多处维护重复 lifecycle 状态 |
| `Context/Companion/Blogger/` | 沿用记录、冻结覆盖水位、不吞掉最终计划 | 强制追加一次付费总结 |
| `requirements/*` 对应 owner | 把新增后果纳入 WHAT/WHY/证明与测试 | 只改 prompt 不改权限合同 |
| `scripts/checks/js-surface-gate.mjs` 等 gate | `js-plan`、双语、工具边界和架构覆盖 | 放宽 gate 来容纳错误实现 |

### 16.3 拟议新增文件

以下是建议落位，不是当前已存在的文件；若现有 owner 已有合适模块，优先在原模块增加小的正式能力，避免为命名整齐新造目录层次。

```text
src/Wanxiangshu/Execution/Planning/
    Program.fs              Plan 的作用域、三个动作编排与取消
    Policy.fs               纯的角色／动作准入规则；不判知识语义
    Prompt.fs               资源组装与运行提示，不写业务知识状态

src/Wanxiangshu/OpenCode/Tools/
    PlanTools.fs            js-plan 的受限能力适配

src/Wanxiangshu/Mission/WorkRecord/
    ExportContract.fs       冻结记录引用与导出收据的合同（需要新增时）

src/Wanxiangshu/OpenCode/Host/
    WorkRecordFileExport.fs  受限目录、原子发布、读回证明（无现成实现时）

resources/provider/role/plan/{en.md,zh-CN.md}
resources/provider/planning/ask-what-matters/{en.md,zh-CN.md}
resources/provider/planning/devops-assignment/{en.md,zh-CN.md}
resources/provider/planning/idle-repair/{en.md,zh-CN.md}
resources/provider/tool/js-plan/{en.md,zh-CN.md}

requirements/plan-workflow/
    WHAT.md
    WHY.md
    tests/...
```

不要照着目录清单机械新建所有文件。`ExportContract` 属于 WorkRecord 语义，物理写盘不进入该纯合同；交互等待的 durable 支持放在交互 owner，不移到 `Planning/Program.fs` 私有实现。

`ProjectionPlanner`、`AttemptPlanner` 等现有模块保持原义。新功能用 `Execution.Planning` 和 canonical `Plan` 说明归属，不为同词不同义开展无关大重命名。

### 16.4 拟议接口边界

以下端口说明责任和证据流，不要求照抄接口名称，更不是现成可调用代码。

```text
FixedDevOpsPort
    ensureBinding(currentPlanOwner) -> 已证明的唯一绑定
    resume(bindingWitness, acceptedAssignment) -> invocation handle
    awaitSettled(invocation) -> 已接受的完成记录或明确失败
    cancelOwned(invocation) -> 取消／物理收敛结果

HumanInteractionPort
    acceptQuestion(currentWork, callId, text) -> 待回答操作见证
    park(questionWitness) -> 可恢复的等待
    acceptAnswer(questionWitness, physicalMessage) -> 原文接受凭证
    resumeExactlyOnce(answerWitness) -> 合法续接结果

WorkRecordCompletionPort
    acceptFinal(currentWork, finishCall, text) -> 正式消息见证
    quiesceForFinal(currentWork, finishCall) -> 物理收敛见证
    freezeRecord(finalWitness, quiescenceWitness) -> 稳定 bounded snapshot

WorkRecordExportPort
    resolvePrivateRoot(workspaceWitness) -> 经验证的持久根
    publish(snapshot) -> 导出收据
    verify(receipt or accepted export intent) -> 完整字节与来源校验

ManagedCompletionPort
    acceptSuccess(currentWork, exportReceipt) -> 唯一成功终态
    acceptCancel(currentWork) -> 唯一取消终态
```

端口中的 witness／receipt 由相应 owner 构造，不能让模型通过输入 JSON 冒充。实现可以复用现有具体类型，不为每个英文词造一个空包装类型。

### 16.5 编排伪代码

```text
onResume(context, message):
    验证文本非空，但保留原始正文，不 trim 后再当原文
    取得本 work 的排他外部操作作用域
    durable 接受调用及来源
    取得固定 DevOps 绑定见证
    经标准 resume 投递，并等待已接受的 invocation 完成
    确认返回结果和执行责任已经结算
    交付结果
    作用域退出时释放运行时 waiter；不删除持久证据

onAsk(context, message):
    取得排他外部操作作用域
    durable 接受提问及待回答关联
    幂等显示，停驻 provider
    通过交互 owner 等待并接受用户原文
    根据物理请求证据选择唯一合法续接路径
    交付回答，关闭该待回答操作

onFinish(context, message):
    通过交互／执行 owner 竞争取得合法收尾权
    拒绝落后的输入版本和其他未结算外部操作
    接受完整正式助手正文
    关闭普通工具能力，收敛当前物理 provider 与自有执行
    冻结同一次 WorkRecord 快照
    原子导出，flush 并读回验证
    将导出收据与成功终态一次接受
    由系统展示原正式正文与产物定位；不再调模型
```

异常处理应以作用域和现有取消传播实现。不要为了恢复伪代码中的每一行而建立一个自有业务 Stage 枚举；恢复从原 owner 已接受的输入、等待、调用、导出和终态事实继续。

### 16.6 新增规范条款建议

可在新 `plan-workflow` 包登记产品独有的不变量，其余仍引用原 owner 的合同。

| 拟议锚 | 内容 |
| --- | --- |
| `WHAT[plan-workflow-001]` | 显式系统 `plan` 入口；无隐式启动、无并列 Planner 入口 |
| `WHAT[plan-workflow-002]` | 独立规划，成功交付后结束；无实施转换 |
| `WHAT[plan-workflow-003]` | Planner 的职责与受限工具面 |
| `WHAT[plan-workflow-004]` | ASK 实际装配；模型判断取舍与收敛 |
| `WHAT[plan-workflow-005]` | 固定 DevOps、标准职责、合法 owner 和 resume |
| `WHAT[plan-workflow-006]` | 用户等待与普通 idle、工具等待、完成分别表达 |
| `WHAT[plan-workflow-007]` | 不建 QA／知识 schema；输入与结果先接受后使用 |
| `WHAT[plan-workflow-008]` | 最终正文为完整计划，canonical LWR 形状不变 |
| `WHAT[plan-workflow-009]` | 导出验证先于成功终态；唯一完成点 |
| `WHAT[plan-workflow-010]` | 单飞、幂等、恢复、取消与有界 nudge |
| `WHAT[plan-workflow-011]` | Casebook 来源不扩展；非 Plan 零额外调用 |
| `WHAT[plan-workflow-012]` | 真实宿主验证是开放能力的前提 |

不能只新增这个包而不修订原约束。`office-capability`／`delegation` 要明确 Plan owner 的有限新增权利；`interaction-authority`／`managed-chat-execution` 要明确等待和最终接受；`work-record` 要明确导出后果及最后正文保留。实际锚号以施工基线为准，不覆盖已有编号。

## 17. 开工前必须核实的工程门槛

这些不是等待用户选择的需求问题。它们是实施者必须查清并交出证据的宿主与代码事实。

| 门槛 | 要证明什么 | 验证办法 | 不成立时怎样处理 |
| --- | --- | --- | --- |
| G-01 入口与身份 | 系统 `plan` 可被唯一托管，实际 provider 收到正确 role、方法与工具面 | 读取注册链，加真宿主 profile/schema canary | 在配置与身份 owner 补接入；不另造 Planner 入口 |
| G-02 固定绑定 | PlanWork 可以合法拥有一个标准 DevOps，所有调查经 resume | 追踪当前道路绑定与 owner 校验，做首次创建／三次续做／冲突测试 | 明确扩展绑定 owner；不能假造 Manager |
| G-03 问用户 | 提问、显示、停驻、回答关联、重启恢复能成立 | 真 Host 问答 canary，加回答接受前后崩溃注入 | 补交互适配器；不靠普通 text-out 猜等待 |
| G-04 最终正文 | 完整 finish 正文能成为正式助手文本，provider 能停止而 work 尚未成功 | 实测工具 call→正式 trace→物理停止→终态的顺序 | 补 completion 边界；不把正文留在被过滤的工具参数 |
| G-05 稳定 LWR | 可得到一致的因果范围、水位和最后正文，包含全部已接受澄清 | 长对话、跨轮 resume、Blogger 并发与 final 保留测试 | 在 WorkRecord owner 补冻结／保留能力 |
| G-06 原子导出 | 稳定私有根、no-replace 原子发布、flush、读回与恢复可证明 | 真实文件系统故障注入与 worktree 路径测试 | 补薄文件适配器；不以 temp 文件冒充持久产物 |
| G-07 唯一结算 | 导出收据、成功／取消竞争和重复消息由同一接受顺序裁定 | 事务重放与双调用竞争测试 | 扩展现行完成合同；不建并列 Planner 状态库 |
| G-08 变更协调 | 当前 PROMPT-006 与发送链实际状态已查明 | 读取当前 HEAD、AGENTS 和相关差异 | 按真实已落地合同实现，不恢复已删除的重复绑定 |

资料中 PROMPT-006 和相邻的只读调查协议都处于特定收集窗口的状态；本稿不据此声称它们现在未完成或已经完成。（资料：`02-现状.md`，L196–L207。）

### 17.1 只读核实命令

下面是定位命令，不是“已经执行过”的证明。仓库目录以开发者当前工作区为准；资料中的 `~/Desktop/vibe/wanxiangshu` 只是历史收集位置。

```bash
# 先在实际仓库根运行。不要在本阶段改动或重置已有工作区。
git rev-parse --show-toplevel
git rev-parse HEAD
git status --short
git diff --stat

# 角色、注册与 plan 的真实入口。
rg -n 'type Role|Roles\.all|isInternal|ManagedAgentCatalog|requiredNames' src/Wanxiangshu
rg -n '\bplan\b|Role\.Plan|Role\.Planner|ManagedAgentConfig' src/Wanxiangshu resources requirements

# 固定 DevOps 与调用完成；找定义，也要找全部使用处。
rg -n 'BoundDevOps|FixedDevOps|\bresume\b|ManagedDelegationAssignment' src/Wanxiangshu requirements

# 交互等待、续接、真实接受与终态。
rg -n 'HumanMessage|HumanRoot|AgentOwnerRoot|QuiescencePermit|BusyAgentNudge' src/Wanxiangshu
rg -n 'question|pending.*[Hh]uman|[Aa]wait.*[Hh]uman|captureTerminalText' src/Wanxiangshu

# LWR 的一致性边界、最终消息、导出与存储根。
rg -n 'lifecycleWorkRecordBounded|captureProjection|captureOpening|WorkRecordStart' src/Wanxiangshu
rg -n 'fsync|rename|atomic|private.*[Rr]oot|git-dir' src/Wanxiangshu requirements

# 读取实际构建与 gate 入口，不能凭经验发明项目脚本。
find . -maxdepth 3 \( -name package.json -o -name AGENTS.md -o -name '*.sln' -o -name '*.fsproj' \) -print
```

这些命令中的 `rg` 无命中也是待记录结果，不证明整个能力不存在。要沿当前 API 和真实 Host 行为继续核实，不能把搜到某个词当实现证明。

## 18. 施工顺序与每一步的退出条件

### 18.1 第零步：记录基线，先证最难的三处

记录当前 HEAD、工作区差异、相邻改造状态和项目真实测试入口。读取本稿涉及的现行 WHAT、源码、资源及 owner 项目。

先做最薄的 Host canary，证明 ask 能停驻并续接、固定 DevOps 能被 Plan 合法调用、最终正文能先接受而后导出再完成。这里不需要完整优美的 Planner 提示词，也不要先写几百行 runtime 后才发现宿主不支持完成顺序。

退出条件：G-01 至 G-08 都有事实结论；不具备的能力有明确的 owner 级最小修改，不能用“后面再看”掩盖主路径缺口。

### 18.2 第一步：补规范与纯规则

登记 `plan` 职责、工具后果、固定 DevOps 新 owner、问用户与 finish 合同；补纯的身份映射、动作互斥、路径推导和收据约束测试。

此时不开放半成品入口。失败时必须拒绝启动托管 Plan，不回落到可直接改仓库的普通角色。

退出条件：产品边界有规范锚；纯规则测试能拒绝第二入口、越权目标、同 work 并发动作及不安全路径。

### 18.3 第二步：固定 DevOps 调查链

实现 PlanWork owner 绑定和受限 resume，复用标准 DevOps 会话、完成记录及取消链。证明连续三次调用同一物理 session，且三次 invocation 边界各自正确。

退出条件：首次创建 single-flight；并发冲突在外部效果前拒绝；返回结果来自已接受完成；现场已有 owner 时不接管；修复和验证能按标准能力完成。

### 18.4 第三步：用户等待链

实现 ask 的接受、显示、停驻和恢复。回答通过已有用户输入入口续入，不新增外部问卷系统。

退出条件：等待不被 idle 终止；回答不重复注入；问答任一接受窗口重启可恢复；用户取消不触发最终成功。

### 18.5 第四步：记录与导出闭环

实现最终助手正文接受、一致记录快照、受限路径导出、读回、收据与唯一终态。优先使用现行 capability；缺口只在对应 owner 补齐。

退出条件：最终计划不被过滤或摘要覆盖；文件未证明保存时不会完成；每个写盘／结算崩溃窗口都有重放测试；成功后没有新的模型调用或执行任务。

### 18.6 第五步：资源与 ASK 正式装配

纳入中文方法来源和等义英文资源，装配角色与委托补充文本，检查实际 provider system 内容、语言稳定性和工具 schema。

退出条件：不是仅有资源文件，而是实际请求收到正确方法；模板不要求固定知识 schema；宿主额外工具不能穿透执行门。

### 18.7 第六步：故障、恢复与回归

把取消、输入竞争、文件冲突、未知投递、DevOps 丢失、Blogger 并发、预算耗尽纳入同一端到端验证。

退出条件：下节必测项全部通过；正常 Manager、Engineer、DevOps、Orchestrator 和 Blogger 路径没有新增模型调用或权限漂移。

### 18.8 第七步：开放能力

在实际宿主 canary 和项目既有 gate 通过后，才把托管 Plan 作为完整功能开放。保持唯一 `plan` 入口；不加一个绕过门禁的测试入口作为长期产品面。

发布说明明确三件事：Plan 只交付 LWR；调查中 DevOps 可以职责内修复并验证；不自动实施计划。不能宣传成“全程只读”，也不能宣传成“批准后自动执行”。

### 18.9 迁移与回退

已有完成 LWR 不因升级或回退被删除。新增 owner 标签、等待事实或导出收据若进入持久协议，要按现行事件版本与 projection 迁移规则处理；旧版本不能理解时应明确拒绝相关未完成 work，不能忽略未知事件。

回退代码时先停止新 Plan 工作，结算或明确暂停已有在途 work，保留产物与证据。不得把一个已持有 DevOps 执行责任的 Plan 改名成 Manager 以便继续。

不为旧 Student/Teacher 名称增加兼容别名，不恢复退休的 QA／SKILL 数据迁移。不要改动与本功能无关的用户未提交文件。

## 19. 测试设计：从纯规则到真实宿主

### 19.1 四层证明

纯规则测试证明身份映射、目标限制、动作互斥和路径／收据规则。契约测试通过真实公开工具和输入边界证明接受、调用、回送和完成。重放测试逐个打断崩溃窗口。最后用真实 Host 和实际 provider 验证提示词、工具面、会话连续性与最终输出。

这沿用历史的四层测试阶梯，但断言对象换成 Plan 的真实后果，不以历史测试曾通过为当前背书。（资料：`01-历史.md`，L409–L419。）

以下 `P-*` 是本稿用例编号，不代表当前仓库已经存在这些测试。

### 19.2 身份与工具面

| 编号 | 输入／故障 | 必须观察到的结果 | 主要层级 |
| --- | --- | --- | --- |
| P-ID-01 | 用户选择系统 `plan` | 只有一个入口，实际请求为正确 Plan 身份和 Persona | 契约＋真宿主 |
| P-ID-02 | 用户使用普通 Manager／Engineer | 不创建 Plan work、DevOps 查询或 ASK 注入 | 回归 |
| P-ID-03 | 尝试用 `planner`、Student/Teacher 或档位组合绕路 | 不产生新增兼容身份，不扩大工具能力 | 纯规则＋契约 |
| P-ID-04 | Provider 输出直接 read/write/terminal 调用 | 执行门拒绝，仓库和进程无副作用 | 契约 |
| P-ID-05 | 经 JS 入口访问任意文件／网络／进程 | 受限宿主拒绝，不仅是 prompt 劝阻 | 契约 |
| P-ID-06 | 宿主意外提供 plan-exit／build 切换工具 | schema 不暴露，伪造执行也拒绝 | 真宿主 |
| P-ID-07 | Plan 创建子调查 | 子身份是标准 DevOps，不误继承 Plan 权限；父仍无执行能力 | 契约 |
| P-ID-08 | profile／工具装配失败 | provider 未启动，不出现空工具或万能后备角色 | 契约 |

### 19.3 固定 DevOps 与执行

| 编号 | 输入／故障 | 必须观察到的结果 | 主要层级 |
| --- | --- | --- | --- |
| P-DV-01 | 同一 Plan 连续三次 resume | 三次同一物理 SessionId，三个准确 invocation 范围 | 真宿主 |
| P-DV-02 | 并发首次 resume | 只建立一个固定绑定，另一请求在效果前被拒 | 契约＋重放 |
| P-DV-03 | 模型试图指定另一个目标、角色或 model | 无对应入参，底层伪造也不通过 owner 校验 | 契约 |
| P-DV-04 | 本地存在与目标有关的非架构级回归缺陷 | DevOps 可修复、补测试、实际验证并返回证据 | 真宿主 |
| P-DV-05 | 调查要求新的产品语义或安全政策 | 不擅自实施该选择，返回事实和未决边界 | 行为 canary |
| P-DV-06 | DevOps 仅普通 text-out 后 idle | 不作为已完成结果；按标准合同有界修复或失败 | 契约 |
| P-DV-07 | 调用结果已接受但回送丢失 | 原结果只交付一次，不再跑命令 | 重放 |
| P-DV-08 | 固定 DevOps session 永久丢失 | 明确错误；不静默创建替代者 | 重放 |
| P-DV-09 | 存在性查询失败／关联冲突 | 不创建、不投递、不认领同名会话 | 重放 |
| P-DV-10 | 同一现场被其他道路占用 | 不接管 owner，不并行改写现场 | 契约 |
| P-DV-11 | 计划完成后到达重复 DevOps 结果 | 不唤醒 Planner，不创建下一次工作 | 重放 |
| P-DV-12 | 要求外部浏览或从命令绕过职责 | 不增加职责外工具或外发权限 | 契约＋行为 canary |

### 19.4 用户等待与交互顺序

| 编号 | 输入／故障 | 必须观察到的结果 | 主要层级 |
| --- | --- | --- | --- |
| P-HU-01 | Planner ask 后正常 idle | 显示等待，不导出、不完成、不 nudge | 契约＋真宿主 |
| P-HU-02 | 用户迟迟不答 | 模型调用数不增长，恢复预算不消耗 | 假时钟＋真宿主 |
| P-HU-03 | 用户正常回答 | 原文先接受再续入当前规划，非新 Plan work | 契约 |
| P-HU-04 | 用户回答同时补充／纠正多个事项 | 完整原文交付，不由 UI 解析成单选结果而丢内容 | 契约＋行为 canary |
| P-HU-05 | ask 已接受，问题尚未成功显示时重启 | 按同一操作幂等显示，不多开问题 | 重放 |
| P-HU-06 | 问题显示成功，进程重启 | 恢复原待回答状态，不靠内存 TCS | 重放 |
| P-HU-07 | 回答已接受，尚未回送时重启 | 原回答只回送一次 | 重放 |
| P-HU-08 | 原物理工具请求已经结束 | 使用有来源的续接，不同时再回填一次工具结果 | 真宿主＋重放 |
| P-HU-09 | 另一会话或另一 work 的回答 | 不能被消费为当前 ask 的回答 | 契约 |
| P-HU-10 | 未答 ask 期间发 resume／finish | 在外部效果之前拒绝 | 纯规则＋契约 |
| P-HU-11 | 用户在 finish 前先修订要求 | 旧 finish 见证过期，先吸收已接受修订 | 并发测试 |
| P-HU-12 | finish 封口获胜后普通新消息到达 | 不混入冻结记录、不丢失，按明确新请求归属处理 | 并发测试 |
| P-HU-13 | 只有普通提问文字，没有 ask 操作 | 不猜测等待；有界修复，不宣称完成 | 契约 |
| P-HU-14 | 用户取消提问界面但未取消整项工作 | 按真实宿主取消语义区分；不能把关闭界面当成回答或成功 | 真宿主 |

最后一项必须先核实宿主具体行为。若没有单独“关闭提问界面”的动作，不新增这个产品动作，只验证现有取消输入不会被错误解释。

### 19.5 记录与 LWR 导出

| 编号 | 输入／故障 | 必须观察到的结果 | 主要层级 |
| --- | --- | --- | --- |
| P-RC-01 | 多轮澄清后 finish | LWR 范围覆盖本次全部已接受输入，不只最后一轮 | 契约 |
| P-RC-02 | 同一物理 session 已有较早任务 | 导出不混入不属于本次的 work | 契约 |
| P-RC-03 | finish 参数带完整计划 | 最终正式助手文本存在，计划不随 raw tool filtering 消失 | 真宿主 |
| P-RC-04 | 输出文件与主界面完成回执 | LWR 最后正式正文仍是计划，不是“已保存” | 真宿主 |
| P-RC-05 | Blogger 在最终输出附近写入 | 冻结一致水位，计划保持完整 Recent suffix | 并发测试 |
| P-RC-06 | renderer 需要 terminal 才能取边界 | 新的封口／完成合同能先得合法范围，再导出，后提交成功 | 契约 |
| P-RC-07 | read/review 分页 | 返回明确继续游标；未读完不显示全部完成 | 纯规则＋契约 |
| P-RC-08 | 标题含 `../`、分隔符或超长字符 | 标题不参与路径，目标仍由安全 work key 推导 | 纯规则 |
| P-RC-09 | Git worktree 的 `.git` 是引用文件 | 使用真实私有根，不误写工作区或其他 worktree | 文件系统集成 |
| P-RC-10 | 目标目录符号链接逃逸／跨 owner 路径 | 发布失败，无越界写入 | 文件系统集成 |
| P-RC-11 | UTF-8 长正文、代码块、混合换行 | 导出与预期完整字节一致，没有半个字符或截断 | 纯规则＋文件集成 |
| P-RC-12 | 临时写入一半崩溃 | 没有成功终态；正式目标不出现撕裂版本 | 重放 |
| P-RC-13 | 发布完成、收据尚未提交时崩溃 | 从冻结来源验证原文件，提交同一完成，不覆盖 | 重放 |
| P-RC-14 | 目标已存在同 work 同内容 | 幂等接受同一次导出，不产生第二份计划 | 文件集成＋重放 |
| P-RC-15 | 目标已存在不同内容 | 报冲突，不覆盖，不改随机文件名掩盖问题 | 文件集成 |
| P-RC-16 | flush、目录项持久化或读回失败 | 不提交成功，不提供假成功文件卡 | 故障注入 |
| P-RC-17 | 导出成功，完成提交失败 | 显示未结算，重试不再调用模型 | 重放 |
| P-RC-18 | 完成提交成功，界面回执丢失 | 幂等重放同一结果；只存在一个成功终态 | 重放 |
| P-RC-19 | 已保存文件被人工修改 | 检测不一致，不反向改写源记录，也不静默覆盖 | 文件集成 |
| P-RC-20 | 根目录或 renderer 配置在恢复时变化 | 按原接受配置恢复或明确失败，不伪造同一导出 | 重放 |
| P-RC-21 | 同一 finish 重投，或两个 finish 并发 | 同一操作幂等、竞争者被拒，只有一条正式完成和一个交付文件 | 并发＋重放 |
| P-RC-22 | 非 Git 工作区且无宿主私有持久根 | 初始化明确失败，不先做调查再临时找地方保存 | 文件集成 |

### 19.6 取消、预算与非 Plan 回归

| 编号 | 输入／故障 | 必须观察到的结果 | 主要层级 |
| --- | --- | --- | --- |
| P-SF-01 | 调查中取消 | 撤销继续工作，收敛自有进程，保留已发生修改 | 真宿主 |
| P-SF-02 | 保存中取消与成功提交竞争 | 唯一终态；未提交导出不能被当成已完成计划 | 并发＋重放 |
| P-SF-03 | 取消后迟到用户答案／idle／工具结果 | 不复活原 work | 重放 |
| P-SF-04 | 无明确动作反复 idle | 有限 nudge 后明确失败，预算不被新请求重置 | 契约 |
| P-SF-05 | 输入或结果 durable acceptance 失败 | 接收方没有依据未接受文本继续工作 | 故障注入 |
| P-SF-06 | 标准 Manager→DevOps resume | 原角色、目标、权限与返回语义不变 | 回归 |
| P-SF-07 | 普通 Engineer、Fission、Sphinx | 没有因新增 Plan 获取／失去不相关能力 | 回归 |
| P-SF-08 | 未启用 Casebook 的仓库 | 无额外缓存事件、索引或模型调用 | 回归 |
| P-SF-09 | Plan 正常完成 | 不启动 Manager、实施、Git 提交、部署或新模型回合 | 契约＋真宿主 |
| P-SF-10 | 缺少支持新持久事实的版本回退 | 明确拒绝无法理解的未结算工作，保留证据 | 迁移测试 |

### 19.7 机器测试与语义审阅的边界

权限、调用次数、id、投递接受顺序、游标、文件字节和终态可以作精确断言。对“问题是否值得问”“默认是否合理”“修复是否改变产品意图”“计划能否让人理解”等，使用具体场景人工审阅，不将自然语言评分器接入生产完成路径。

测试代理可以按脚本故意调用越权工具或重复 finish，以验证机器边界；这不等于真实模型每次都会遵循正确方法，因此仍需要真实行为 canary。

### 19.8 必须留存的验收证据

每个真宿主 canary 留存最小必要的身份／工具面、调用关联、物理会话连续性、工作记录边界、导出收据和成功／取消结果。敏感正文按既有规则处理，不为了测试把私有仓库全文复制进日志。

“进程退出码为零”不能单独证明验收通过；还必须看到预期后果。尤其要实际打开最终 LWR，确认完整计划仍是最后正式助手正文。

## 20. 端到端示例

以下是行为示例，不是现有系统运行实录。

### 20.1 材料充分，直接交付

用户选择 `plan`，给出完整目标、范围、约束和验收要求。Planner 发现不存在必须调查的事实或必须由用户决定的分歧，直接形成正文并调用 finish。

系统接受正式正文、冻结记录、导出并提交完成。过程中没有 DevOps session、没有 ask、没有补一次“确认结束”的模型调用。

验收重点不是“流程走得短”，而是零调查路径也能得到合法、完整、可定位的 LWR。

### 20.2 先调查，再问一个会改变方案的问题

用户要改造导出功能，已经给出使用场景，但未说明失败后是否必须保住尚未导出的输入。

Planner 先让 DevOps 查明当前保存机制。DevOps 返回：某段输入只在内存里，相关行为有实际测试证据。Planner 再问用户：“失败恢复时，未导出的输入也必须保住，还是可以重新输入？”

此时 ask 已登记，Planner 暂停；Host 的 idle 不触发交付。用户确认必须保住后，原文被接受并续入。Planner 更新方案，需要时再查当前持久化入口，然后自行 finish。

不能在用户回答前按“应该愿意重新输入”继续实现，也不能要求用户知道应使用哪种数据库。

### 20.3 调查中完成一次局部修复

DevOps 调查某个失败路径，发现一个违反已有合同的边界检查缺陷。它修复、补回归测试，并记录哪些测试实际通过、哪些未运行。

Planner 的最终计划应写明“调查中已完成该修复”及证据；后续步骤从修复后的真实状态出发。不再列“修复同一个缺陷”为未来任务，也不顺势让 DevOps 把剩余新功能全部实现。

LWR 同时体现规划结论和规划过程中真实发生的工作，而不是假装这是一段完全只读的讨论。

### 20.4 结果是暂不可承诺

调查发现关键行为依赖尚无授权的生产数据，现有材料无法确认真实上限。Planner 不请求越权访问，也不估出一个看似精确的数值。

它交付计划：已有证据支持哪些判断；当前不能承诺什么；最小验证需要什么样的脱敏样本和权限；什么观察结果会改变方案。LWR 成功落盘后，规划完成。

这是合法规划结果，不是运行失败，也不是上线许可。

### 20.5 磁盘故障

Planner 已提交完整计划，但写盘失败。界面显示系统错误：计划正文已形成，记录未保存成功，本次尚未完成。不能出现“完成”标记或假文件链接。

故障排除后，系统使用原正文和原冻结范围重试，不向 DevOps 重新调查，也不让模型生成另一版。保存与终态提交成功后才展示完成。

### 20.6 用户说“就按这个做”

这句话不能触发本期未实现的执行衔接。Plan 不变成 Manager，不把 LWR 自动转成 mission debt，不继续编码。

若它是在最终交付后的新消息，按明确的新请求边界处理，并说明当前 Plan 功能只到计划交付。用户另行使用系统已有其他功能属于另一项工作，不由本功能偷偷代办。

## 21. 上线验收与完成定义

### 21.1 功能完成的最小条件

系统 `plan` 能以正确身份启动；ASK 在实际请求中生效；本地调查使用固定 DevOps 且保留职责内修复能力；ask 能可靠停驻并恢复；Planner 自行 finish；最终 LWR 真正落盘；唯一成功终态在此之后提交；成功后没有实施或新模型工作。

任何一条不成立，都不能把功能称为已完成。

### 21.2 必须通过的项目门禁

按照施工时真实 AGENTS／package scripts／owner 项目执行构建和测试，不凭本稿猜命令。至少包含角色和能力测试、派发与交互权威测试、委托及固定 DevOps 测试、工作记录与文件导出测试、取消恢复测试，以及以下已登记 gate 对新代码的检查：

`provider-leak-gate`、`js-surface-gate`、`language-parity-gate`、`architecture` 和 owner-locality 编译覆盖检查。

当前有哪些命令聚合这些 gate，要在 §17 读取真实入口后确定。本稿不声称已经运行。

### 21.3 不能用来替代验收的东西

一份漂亮的计划截图、一段能跑通的 mock、几条私有函数单测、模型说“已经落盘”、历史版本曾经通过测试，均不能替代当前 Host 的真实证据。

尤其不能只验证正常结束，漏掉等待用户、回执丢失和文件已写但终态未提交。它们不是附加优化，而是本功能“计划结束”的含义本身。

## 22. 原始开放问题逐项裁决

《03-映射与问题》只登记问题，没有给出方案。下表是本稿给出的回答，不能反过来说资料已经这样规定。（资料：`03-映射与问题.md`，L115–L179。）

| 原问题 | 本稿裁决 | 性质／落位 |
| --- | --- | --- |
| Q1 Planner 正式定义 | 系统唯一 `plan` 入口，对应一个 canonical Plan 身份；不新增并列 Planner | 用户确定入口；内部映射为本稿选择，§4 |
| Q2 如何触发 | 用户显式选择；独立运行，无 Manager 中途插入 | 用户已确认，§4 |
| Q3 问答如何持久化 | 使用既有交互、委托、trace 和认知画板；不建 QA | 本稿选择，§10 |
| Q4 计划制品合同 | 完整最终正文进入 LWR；私有根下确定性 `LWR.md`；先验证导出再完成 | 用户确定 LWR；路径及事务为本稿选择，§11 |
| Q5 计划内容语义 | ASK 导向的可行动草案，区分事实／决定／暂定，保留验收、阻塞与重开条件 | 本稿选择，§15 |
| Q6 怎么询问用户 | 显式 ask，durable 等待，原文续接；不依赖未经核实的 Host question | 本稿选择及工程门槛，§8、§17 |
| Q7 DevOps 调查和可见性 | 标准 DevOps WorkSession，固定绑定、resume；主对话由 Planner 表达，保留正常审计 | 用户确认修复；拓扑与可见性为本稿选择，§5、§7 |
| Q8 何时完成、是否接 T1 | Planner 自行 finish；idle 不完成；不接 T1 | 用户确定自行结束及独立范围；信号为本稿选择，§9 |
| Q9 LWR 怎样落盘 | 复用 renderer，补受控导出与完成收据；不另建 plan.md | 本稿选择，§11 |
| Q10 名称与迁移 | 使用 plan／Execution.Planning；不改 Manager Planning Table，不复活旧名 | 本稿选择，§4、§16 |
| Q11 PROMPT-006 关系 | 先核实当前已落地合同，复用统一身份和派发，不增加重复绑定 | 工程门槛，§17 |
| Q12 gate 适配 | 在现行工具／角色单一投影中接入，更新双语及编译归属 | 本稿选择，§16、§21 |
| Q13 何时查、问、默认 | ASK 由模型执行；框架不做语义分类器 | 本稿选择，§6 |
| Q14 与委托、Sphinx 的关系 | 复用受限 resume 与正式结果收取；不用 Sphinx／Teacher Satellite，不额外 fork | 本稿选择，§7、§8 |

仍待核实的是实际代码和宿主能否履行这些合同，不是产品边界还需要重新问一轮。若查出的事实迫使某项方案改变，应指出改变的具体位置和代价，不能无声替换成本稿原意。

## 23. 交给实施者的最终清单

按下面顺序工作，每一步留下实际证据。

1. 在真实工作区登记 HEAD、未提交改动与现行构建入口；读取相关 requirements 和调用链。
2. 证实 `plan` 身份、ask 停驻续接、固定 DevOps、最终助手文本接受及 LWR 冻结／导出能力。
3. 明确扩展 Plan owner 的有限委托权，不造 Manager，不造 Teacher，不另起一套持久状态库。
4. 先补规范和失败测试，再实现 resume、ask、finish 的完整后果与取消传播。
5. 打包 ASK 和成对资源，检查实际 provider prompt、schema 及执行门，而不只检查文件存在。
6. 跑纯规则、公开边界契约、崩溃重放、真实 Host canary 和非 Plan 回归。
7. 实际打开生成的 LWR，核对来源、范围、完整计划、最后正文及系统收据；确认没有后续实施。

实现说明最后应报告：实际修改的 owner 和文件、与本稿不同的裁决及原因、真实执行的测试及结果、仍未通过的门槛。不得把拟议接口写成已存在，不把未跑的测试写成通过。

---

## 附录 A. 本稿引用资料的角色

`设计资料.md` 是原始任务与资料入口，不是方案。

`01-历史.md` 提供历史 Student/Teacher 的原则、实际实现和测试经验；历史成功或删除均不自动证明本次该照搬。

`02-现状.md` 提供收集快照中的角色、DevOps、LWR、Casebook、会话和门禁约束；它不是实时源码证明。

`03-映射与问题.md` 是差异与开放问题清单；本稿在 §22 作出裁决，不把清单本身当建议。

`04-证据与术语.md` 提供可回到 git 历史与源码的索引、基线及只读核实方向；原资料明确未运行构建和测试。

`DOC.html` 提供减少注意力浪费、职责分离和真实执行连续性的产品理由；其中示意代码、Sphinx 描述和其他产品方向，不被扩写成本期 Planner 的技术要求。

`ASK.md` 是 Planner 要实际拿到的方法来源，不是问答存储，也不是执行授权。它关于默认、未知、提问负担和停止条件的纪律，进入本功能的提示词与行为验收。

## 附录 B. 本稿刻意没有留下的隐含功能

没有“批准即执行”；没有“计划完成后顺手发给 Manager”；没有“先造 Teacher，以后再换 DevOps”；没有“所有 Plan 都必须先问三轮”；没有“另存一份 QA 只是为了保险”；没有“最后一句已保存就算 LWR 正式计划”；没有“工具调用失败就换个 agent 再试”。

本期的闭环只有一个：**独立规划，必要时调查和澄清，形成完整计划，可靠交付 LWR，然后停止。**
