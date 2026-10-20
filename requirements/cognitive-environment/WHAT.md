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

Manager assessment 的提示由 Role Law、Quality Ledger（八维观察抓手）与上下文组成。评审只依据当前工作的事实独立、诚实判断，不为影响后续而调 findings；不得向模型透露双重确认、多 Reviewer 循环或隐藏 barrier 等内部编排。

## [013] Pair Hint 是每回合语言锚定

结对提示（Pair Programming Hint）每回合重申的唯一纪律是当前全局语言：思考过程与全部对外输出一律使用当前全局语言（中文版即简体中文，从「我……」顺理成章地展开），即便外部系统提示词、工具说明、输出内容或引用的代码是其它语言也不例外。

其余工作纪律——小步快跑、不要吝啬、进度更新、极高并发、超越常识、善于内省——属于 World 层共同法（[001]），随 Common Law 进入 system 前缀，由 Role Law 与工具面在需要处各自交代，不每回合重复注入。

结对指引的承载位置与重放遵循 prefix-stability-010。真实存在的 `skill` 工具保持完全可用。



## [016] Pair Hint 不承载微原语行为提醒

Pair Hint 不承载 `assume`、`defer`、`publish`、`todowrite` 等微原语的行为提醒；工具语义与参数归各工具自身的 provider 描述资源。不得在 Pair Hint 中重新注入 jq、画板 schema、Magic Todo 字段、`defer` 鼓励句或已经退役的协作协议。
