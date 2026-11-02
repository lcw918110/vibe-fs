# 响应文本检查：撤回过细的库入口

2026-11-01。`wanxiangli/response-text` 的独立入口已从本次库候选撤销，词法检查回到主体现有 `TerminalValidity` 内部。它只服务当前响应判断，没有必要为一段正则另建工程、公开分类和类型转换。原抽取记录保留在 Git 历史；本次运行结果统一见[边界调整方案](已拆模块边界调整方案-2026-11-01.md)。

这不代表主体是它的永久归宿。以后整理完整回复解释职责时，再判断中立能力与宿主适配的归属；不并入负责输出表示的 llm-text，也不并入终态消息分发的 terminal。

## 现有行为

[TerminalValidity](../src/Wanxiangshu/Participant/Provider/Attempt/TerminalValidity.fs) 保留 `check/isValid/describe`：

| 检查结果 | 主体结果 | 诊断 |
| --- | --- | --- |
| null、空串或现有 JavaScript 空白 | `Error Rejection.Empty` | `empty terminal` |
| 包含固定工具标记 | `Error Rejection.XmlOnly` | `XML-only terminal` |
| 其它文本 | `Ok` | 无拒绝 |

先检查空白，再运行原正则。FEFF、2028、2029 属于当前空白集合；0085、180E、200B 不属于。工具名仍为 `tool_call`、`use_tool`、`call`、`function_call`、`invoke`。

这是包含匹配，破损的 `<invoke` 和正文混有工具标签仍命中。大小写、空格、斜线和词边界保持：`<CALL>`、`<call-x>` 命中，`<call_x>`、`List<string>` 和 `<em>` 不命中。重复调用不保留正则游标。

保留 `XmlOnly` 名称和诊断，不把它理解成完整 XML 检查。此检查不代表回答正确或工具真的执行过；正文包含标签时应怎样处理的既有产品问题，本次没有改变。

## 与回合判断的关系

`CompletedTurn` 继续结合真实工具活动、正式正文、reasoning、provider 错误与用户中止决定后续；顺序和结果不改。CompressionSurface 调用同一份主体检查，没有第二份词法实现。

旧 TextShape 字符串桥、库源码、工程、类型声明和专用安装夹具均退出。原来有意义的词法用例收进[主体测试 016](../requirements/context-compression/tests/016.test.mjs)，与既有 CompletedTurn 组合用例一起保持。实际主体包消费直接使用打包后的 TerminalValidity，不再导入已撤销的库子路径。
