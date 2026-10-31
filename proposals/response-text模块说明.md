# response-text：响应文本形状

2026-10-31。第三项已在主体接回：库只观察文本的空白和工具标记形状，主体保留现有结果类型、诊断和回合处置。运行结果及既有 TODO 在文末记录。

## 公开能力

```ts
export function classify(text: string | null): 'Empty' | 'ToolMarkup' | 'Text';
```

`Empty` 表示按现有字符串规则为空；`ToolMarkup` 表示原正则在文本中找到了指定工具标记；`Text` 表示上述两者均未命中。没有 I/O、持久状态或宿主依赖；不接收 provider 事件，也不决定任务是否结束。

这是 `wanxiangli` 单一 npm 包中的一个子路径。已有 `wanxiangli/terminal` 管理会话终态通知等生命周期协议，本项不扩展或替代它。

## 保持哪些具体行为

词法实现取自原 `Participant/Provider/Attempt/TerminalValidity.fs`：先做 `String.IsNullOrWhiteSpace`，再按原正则的大小写与词边界规则包含匹配。

- null、空串与现有 JavaScript 空白集合返回 `Empty`。例如 FEFF、2028、2029 属于空白；0085、180E、200B 不属于当前空白集合。这里保留实际运行时行为，不借用另一套 Unicode 或 .NET 定义替换它。
- 工具名固定为 `tool_call`、`use_tool`、`call`、`function_call`、`invoke`。保持原有空格、斜线、词边界与可缺失闭合字符的规则。
- 是包含匹配：`说明 <tool_call> 内容`、破损的 `<invoke` 仍返回 `ToolMarkup`。并非要求整段文本是完整 XML。
- 大小写、普通标签和近似单词继续按原规则区分：`<CALL>` 命中，`<call_x>` 不命中，`<call-x>` 命中；`List<string>`、`<em>` 和 `a < b` 作为普通文本。
- 同一输入重复读取的结果相同；正则内部 lastIndex 不能让结果交替变化。Unicode 大小写和词边界以现有生成的 JavaScript 正则行为为准。

`Text` 不是“回答正确”，`ToolMarkup` 也不是“实际执行过工具”。本模块不根据内容猜测原因、动机或用户意图。现有 GAP-105（正文包含工具标记该如何处置）没有在本次解决；固定现状用于防止移植变更，不为该产品策略作新的判断。

## 主体接回边界

主体 `TerminalValidity.fs/.fsi` 保留很小的适配：

| 库观察 | 主体原结果 | 原诊断 |
| --- | --- | --- |
| Empty | Error Rejection.Empty | `empty terminal` |
| ToolMarkup | Error Rejection.XmlOnly | `XML-only terminal` |
| Text | Ok | 无拒绝 |

保留旧 `XmlOnly` 名称是为了保持主体协议和诊断，不把名称误当成完整 XML 检查。`isValid` 继续与 `check` 一致。

`CompletedTurn` 的判断与次序不改：真实工具调用、正式正文、reasoning、provider 错误和用户中止仍按已有协议处理。CompressionSurface 继续调用主体适配，不建立第二份词法实现。

源实现退出是删除主体中的空白算法与正则；保留这个类型适配不等于保留旧实现。没有新增源代码跨仓编译，也不从旁边开发仓读取私有 dist。

## 验证范围

库侧覆盖 null、空串、各类空白、普通中英文、五种标签及大小写、闭合与破损标签、前后混合正文、词边界和重复调用；对比实际 Fable 字符串和 Regex helper。独立构建只带本模块源码，实际 tarball 用 TypeScript、Node、Bun 消费公开入口。

主体保留原五份相关测试，并新增聚焦现有行为的独立用例，覆盖适配的结果／诊断，以及实际 CompletedTurn 的分支：无工具活动的 provider 错误为失败，有工具活动的错误保持进行中，无错误的真实工具调用可完成，reasoning 不是正式正文。不能只通过直调库证明主体接回。

原计划的组合范围：

```sh
TESTS_MJS_FILES='requirements/context-compression/tests/004.test.mjs,requirements/context-compression/tests/005.test.mjs,requirements/provider-attempt-recovery/tests/008.test.mjs,requirements/interaction-authority/tests/019.test.mjs,requirements/interaction-authority/tests/024.test.mjs' node requirements/verification-system/tests/run.mjs
```

其中有原有 GAP-104／105／139 TODO，保留并如实报告；不删 TODO，不设置接受 TODO 的覆盖变量。库候选与主体实际加载内容的一致性，以及最终实际包消费，继续使用既有分发检查。

新增聚焦用例独立运行：

```sh
TESTS_MJS_FILES='requirements/context-compression/tests/016.test.mjs' node requirements/verification-system/tests/run.mjs
```

## 本次结果

库候选为 `09f0534a6bf5d02aeaf14c7a9e456d3ce7a5e478`（0.70.0），主体隔离树 generation `716`。主体 gitlink、锁文件和实际安装已固定到同一候选，真实候选一致性用例通过。

- 库侧：新模块编译无警告；6 个形状行为与 3 个 exports 检查通过。仅含自身源码的隔离构建通过；实际 tarball 的 TypeScript 正反类型检查、Node 26.6.0／Bun 1.3.14 消费者通过。两边实际 helper 与 `/gui` 标志一致，覆盖 Unicode 大小写、词边界和重复匹配；无新增依赖。
- 主体原五文件：24 项通过、0 项执行失败、3 个既有 TODO，runner 返回 1，不能称整组验收全绿。原 GAP-104／105／139 继续保留。
- 主体新增 016：4/4 通过。初轮一条测试把“错误加工具活动”误写为 TurnFailed；对照原主体构建与未修改的 CompletedTurn 源码后改回原有 TurnInProgress，仅重跑新增文件。没有为测试修改生产判断。
- 实际包：`WANXIANGLI_EXPECTED_SHA=09f0534a6bf5d02aeaf14c7a9e456d3ce7a5e478 node scripts/verify-package.mjs` 通过。归档含 1,460 个成员，隔离消费者从实际 bundled 包裸导入三种形状，并保留此前两个入口的消费检查；SHA-256 为 `cbd7a2eed23be45da51ab11b29a04f3f34dd374163662b5c3363c04c53634612`。

主体已退出空白判断与正则实现，只保留返回类型和原诊断映射；CompletedTurn／CompressionSurface 的生产代码未修改。三项实现均已接回，各自现有 TODO 不因库化而关闭。提交保存在本地，尚未推送远端。
