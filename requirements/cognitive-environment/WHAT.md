# cognitive-environment — WHAT

## [001] 五层认知材料

每份自然语言材料只归属一个主权威层：
- **World**：普适世界观与通用公理（Common Law）。
- **Role**：参与者职责与自我模型（Role Law，fast/deep 共享）。
- **Library**：继承的技术知识与职位经验（Office Library）。
- **Runtime**：本次调用时刻成立的生命周期与事件注入。
- **Mission**：本次委托的具体目标与当前任务。

## [002] 语义所有权

各层可以告知事实，不得冒充其他层的权威。内容冲突按事实的领域语义所有权裁决，不以固定的层级优先序覆盖。

## [003] canonical 组合顺序

标准组合顺序为：
```text
SYSTEM:  Common Law → Role Law → Office Library
TOOLS:   当前生成的工具描述集合（独立于 Role 章节）
RUNTIME: 生命周期与事件注入
USER:    当前分配的 Mission 任务
```

## [004] Tools 不是 Role Prompt 章节

System Prompt 表达职位职责、认识论原则、权能边界与易犯错误，不枚举当前环境的全部工具。工具面决定可用工具，工具可用不等于职位获权。

## [005] Role Law 是长期 self-model 层

Role Law 定义参与者的长期自我模型。同一 Office 的 fast/deep 档共享相同的 Role Law 与自我模型；提示词不得暴露 `fast-*`、`deep-*` 等机器路由名称。

## [006] Office Library 是继承的技术书籍，不是 Common Law

Office Library 提供技术经验与操作指南，服从具体任务需求，不定义系统公理或职位权能。

## [007] 知识可跨 authority 边界流动，authority 不随知识流动

知识可以跨职位传授，阅读知识不增加职位权能；修改代码、执行环境等权能仍由 office-capability 定义。

## [008] Library 三轴：Class × Delivery × Audience

Office Library 遵循三轴分类：
- **Class**：Rulebook、Handbook、Ledger、Atlas、Field Notes。
- **Delivery**：Inherited Volume、Triggered Folio、Request-Bound Volume。
- **Audience**：按职位角色或请求契约绑定，不按模型推理深度分叉。

## [009] Library 禁令

书籍不得扩大角色权能，不得成为所有角色共用的全能手册，不得按同角色的 fast/deep 档分版本，不得向评审者透露隐藏评审编排。

## [010] 生命周期文本只 orient，不 educate

生命周期文本（Activation、Reawakening、Continuation、Handoff、Fission、Departure）只说明当前处境（orient），不重复教授知识（educate），不触发 System Prompt 替换。

## [011] 瞬时 runtime/mission 不重写长期 self-model

瞬时任务与运行时事件通过会话消息传递，不得借提示词伪造激活或改写长期 Role 自我模型。

## [012] 独立评审指引不灌输隐藏流程机制

独立评审指引不灌输隐藏流程机制。Manager assessment 的提示由 Role Law、Quality Ledger（八维观察抓手）与上下文组成。评审只依据当前工作的事实独立、诚实判断，不为影响后续而调 findings；不得向模型透露双重确认、多 Reviewer 循环或隐藏 barrier 等内部编排。

## [013] Pair Hint 的长期口诀与状态性提醒分开

结对指引每回合重申当前会话绑定的语言、`resetScope` 上下文闭环与共同法一句口诀；不指示模型伪造 `<think>` 等内部推理格式，也不在常驻指引里复述带条件的初始化命令。思考与对外输出遵守所绑定的语言，不因外来的英文系统提示、引用代码或参数名自行换语种。

代理归属的答复 steer（包括前一 Scope 的答复）不是 Pair Hint 载体，不追加新提示，也不以它为锚点新增 occurrence。归属取自宿主消息或 info 的 attribution，不匹配答复标题；重复请求保持答复原文，后续真人消息仍可正常承载提示。

计费基线与 ResetDue 由 `wanxiangli/scope-progress` 拥有，不再靠 Pair Hint 的固定 turnBudget 倒计时决定初始化。正常 Provider 请求缺少可信估计或已经锁存 ResetDue 时，宿主只向请求副本追加 `agent` 归属的状态提醒；SQLite 结果 Unknown 不能冒充缺估计。首次委派 `negotiate` 尚未真实落地答复时，Pair Hint 仍强提醒协商；第二次及以后重置仍要求主动 `askPreviousScope`。原始用户／工具结果不变，不再生成或回写预算倒计时尾巴，普通真人输入与代理回答不重置计费基线。

小步、不要吝啬、并发、笃定、内省等完整纪律仍归 World 共同法（[001]）；Pair Hint 不展开，只保留简短口诀。承载位置与重放遵循 prefix-stability-010，真实 `skill` 工具始终可用。


## [016] Pair Hint 不承载微原语行为提醒

Pair Hint 不承载 `assume`、`defer`、`publish`、`todowrite` 等微原语的行为提醒；工具语义与参数归各工具自身的 provider 描述资源。不得在 Pair Hint 中重新注入 jq、画板 schema、Magic Todo 字段、`defer` 鼓励句或已经退役的协作协议。


## [017] 宿主事实决定提示是否出现，不推给模型猜条件

宿主/内核已知的条件（会话是否需要首次协商、是否已有 `resetScope`、预算和请求 epoch、Plan 是否处于 `S3`/`post-S3`、资源与义务台账是否阻塞）**必须先由其状态所有者判定**。条件成立才在真正的请求副本、工具结果或阶段专属提示里向模型交代当下要做的动作；条件不成立不得靠常驻角色/Host 提示的「如果/当……时请……」要求模型自行裁决适用性。初始化提醒、预算尾巴、协商先问和 post-S3 交付均应有各自真实的发生点。

常驻角色/Host 指引只保留当前始终成立的职责与协议事实；已知状态分支、已作废的双阶段确认、人工 `<think>` 格式以及嵌入中文资源的英文 `[System: ...]` 不得保留。清理静态文字时同时删掉实际调用链中依赖英文短语剔除某行的过滤器。已注入过的旧 Host-owned Scope 段落在 OpenCode/OMP 重入时应按中英文身份识别并由新版替换，不能跳过更新、重复追加，也不能改动宿主其他 system 内容。

**例外边界：** 是否值得等待、哪项事实不足、工作是否达标、哪个假设应继续调查等实质问题由模型按真实资料判断，不能因为句子含「如果/当」就移到宿主。模型决定内容，系统决定已知状态与权限。

证明：tests/013.test.mjs、tests/017.test.mjs；host-boundary/tests/omp-scope-seam.test.mjs、scope-hni-physical.test.mjs 的真实 Fable/Host 正反物理断言；前缀冻结与委派回复门禁由原有 prefix-stability-010、delegation-negotiation WHAT 继续验证。


## [018] 动态 Scope 提醒以宿主实见语言呈现

`wanxiangli/pair-hint` ScopeSession 拥有协商与 Scope 重置协议状态，`wanxiangli/scope-progress` 拥有真实计费估计与 ResetDue；宿主只按已确认状态选择并渲染动态提醒，**不把任何一个已知状态条件写回永久 Provider 角色或系统提示**。模型可见动态文字必须取自宿主当前真实的 ProviderLanguage（`en` 或 `zh-CN`），不因 Fable 默认值或先前英文会话而在简体中文请求中泄漏 `[System: ...]`、英文协商前缀或英文条件句。

`planRequest` 和 `prepareNegotiationReply` 从 HNI 绑定获取本次语言；缺估计与 ResetDue 提醒从宿主双语资源取得。每份领域判断仍只有一个 owner。语言偏好改变只影响之后新生成的提示；已有用户消息与工具结果必须逐字重放，禁止二次本地化或改写持久工具结果。不再把 `[Remaining turnBudget: N]` 当作现行输出协议。未知语言值必须明确拒绝，不能静默按英文投射给中文会话。

中文估计提醒、委派首次问答与协商回执 steer 分别验收；首次协商尚未答复时，非 reset 工具结果不得推送重置初始化提醒。模型负责判断任务状态闭环何时真正完成，系统只报告已确认的进度状态。host-boundary 的真实 OMP / OpenCode 请求副本不得修改历史或继承错误语言。

证明：`tests/018.test.mjs` 经真实注册的 OpenCode Provider hook 验证中文／英文切换只改变新 steer，不改用户或工具原文，并验证中文首次协商的精确物理回执门；`host-boundary/tests/omp-scope-seam.test.mjs` 与 `scope-hni-physical.test.mjs` 验证两个宿主的请求副本。


## [019] 模型只看到当前有效的 Work 生命周期入口

Plan 与 Manager 的任期、义务账、接力、晋级及结案只通过 `review` 进入领域状态机。工具描述、调用提示、角色表和项目导览必须按这一条直接告知模型，不得暗示 review 后还需要第二个确认调用；已存在的有效 `review` 不因某份历史资源文案而失去效力。独立的 `wxs_deliver` 仅把材料交还接收方，不推进任期，也不得因清理生命周期说明而丢失。

证明：`tests/019.test.mjs` 覆盖现行中英双语工具资源、仓库 README／导览／office 权限说明及 `wxs_deliver` 的独立合法性。
