# 长程任务不停机与显式用户询问

2026-10-15 定稿，2026-10-31 按当前实现改写。本文只讲两件仍生效的事：目标未完成时不许静默停机；缺用户信息时用显式询问等待。规则以 `resources/provider/work/` 与 `requirements/work/WHAT.md` 为准。

## 1. 自然停机的业务门

目标义务独立于 provider 回合。Manager/Plan 未完成目标时，自然 stop、空回复、进度汇报都不能充当完成；必须继续当前任务，或用有效 `deliver` 交接/完成。provider 错误、length 截断和用户中止仍走各自路径，不把所有终态都改成继续。

实现位置：

- `Composition/Turn/OrdinaryTurnWorkflow.fs` 在发布自然完成前检查长程工作：已有显式人类等待、旧权威/旧提问者回执或已证明的退休/交付时不触发修复；仍有义务的自然停止走既有 idle gate 提醒。
- Manager 有独立 idle 分派，`Mission/Manager/Workflow.fs` 同步处理等待与退休；缺工作投影时不当作完成，回落到明确的任务核对提醒。
- 提醒复用已有的精确 provider-run 去重，不加「提醒一次后永久放行」的预算。

## 2. 两个名字，两种能力

- `devops(intent)`：固定的 DevOps 同步调查，Plan 独占。
- `ask(question)`：Manager/Plan 询问用户。独立权限 `DevOpsConsultation`；新 ask 不创建 DevOps，也不保留 `ask(intent)` 别名。

## 3. 等待不等于结束

PromptAuthority 记录 `UserQuestionAsked`／`UserQuestionAnswered`，问题绑定逻辑运行、权威根、源物理用户消息、provider run、tool call 与原问题。`UserQuestionJournal` 只经既有 AgentJournal 追加/读取，不新建数据库，也不改 wanxiangli 的 Work 词表。

真实呈现询问并成功登记后返回「询问已发出」一类占位回执；占位回执不是用户原文，也不是完成报告。等待期间的新调用受既有等待门禁拒绝，只有真实用户输入解锁。

中断放在实际 `message.part.updated` 的 completed 工具部件上，必须匹配 session、provider message identity 与 callId；未完成部件、别的调用、别的 provider run、用户回复后的旧事件都不能中断新执行。不用 sleep/setTimeout 猜保存顺序。

根会话的 interrupt 原先被整体拒绝；现在只对已登记的 durable 等待开放 attempt interrupt。普通 root `AbortSession` 仍拒绝，不取消已排队的用户输入、不递归清理 children、不关闭 logical run。

## 4. 入口

现行合同见 `requirements/work/WHAT.md` [049]–[051]（ask、js.ask 与首任空账问询）与 `resources/provider/work/manager-continue*` 双语提示。旧稿里「Plan 可以空停」的描述已被本决议取代，不再作为依据。
