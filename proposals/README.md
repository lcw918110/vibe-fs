# 计划与提案目录

`proposals/` 放未来材料、设计记录与延期提案。它**不是活规则**：规则在根 `AGENTS.md`、`wanxiangli/AGENTS.md` 与各包规范文件里。除使用者明确要求，不把这里的文字当施工依据（`requirement-system` [011]）。

历史施工截面、验收日志、基线 JSON 与旧原型已删除，需要时翻 git。`wanxiangli` 内若有 `CUT-NN`、`evidence/CUT-*` 之类的历史指称，同样只作考古坐标，不是活文档。

## 一、现行约束与已实施的设计

| 文档 | 性质 | 为什么还在 |
| --- | --- | --- |
| [todowrite：命名程序与 program/runtime 公开面](todowrite-program-runtime-clean-break-2026-10-24.md) | 设计记录，已按现行代码校正 | `name/program/intent`、一条请求期限、call/park/unpark、program 与 runtime 的包边界。现行合同以 `requirements/repository-programming/WHAT.md` 与 `wanxiangli/src/program/AGENTS.md` 为准。 |
| [N04：真实只读输入的执行契约](N04真实只读Fable执行契约-2026-10-05.md) | 设计记录 + 未完成清单 | `scripts/lib/verification-readonly-inputs.mjs` 的所有权转换、失败回收与保护边界；接入 `verify.mjs` 与 Linux/Windows 仍未完成。 |
| [长程任务不停机与显式用户询问](long-work-user-question-static-handoff.md) | 已实施的设计记录 | 自然停机的业务门、`ask(question)` 与等待的 durable 事实。规则见 `requirements/work/WHAT.md` [049]–[051]。 |

## 二、决策依据（不施工，防止重交学费）

| 文档 | 性质 | 保留理由 |
| --- | --- | --- |
| [js- 编程面：暂停-恢复与 as-is 写语义](旧的.md) | 2026-10-16 定稿的取舍记录 | 保活现场为何胜过决定性重放、as-is 为何全局翻转、finalizer 为何用原生栈展开。 |
| [Scope、专席 Advisor 与上下文压缩](Scope进度自评-专席Advisor-双重压缩-2026-10-25.md) | 已按 `scope-retention` 改写 | 现行 Advisor 触发源、四段咨询、临时只读会话与唯一 transform view 的约束。 |
| [跨 Scope 回问](跨Scope回问-2026-10-24.md) | 已按现行实现改写 | `askPreviousScope` 的分界、fail-close 与未完成的 OpenCode 侧边界。 |
| [Work / DurableWork / Projection：2026-10-26 设计记录](验收驱动.md) | 历史设计记录（已实施） | 为何是三个包、用户看得见的时序、Ask 跨账本与「先提交再发送」等仍成立的协调约束；现行合同指向 `requirements/work/WHAT.md` 与 `wanxiangli/src/lifecycle/AGENTS.md`。 |

## 三、构想

| 文档 | 性质 |
| --- | --- |
| [Steward 角色构想](Steward.md) | 未实施的第 11 个角色与 Standing Sphinx 骨架；不作施工依据。 |

## 写作约定

- 不写验收栏、证明栏、oracle 或批次红绿数字；需要差分时写「改了什么、在哪个世代跑了哪几条」。
- 与程序对不上就标明「程序待改」，或直接按程序改写；不用旧证明腔把目标拖回教会。
- 新文件先问：它挡住了哪条总流程？没有答案就不写。
