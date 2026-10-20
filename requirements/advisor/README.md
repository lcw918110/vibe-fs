# advisor — README

本包规定「调用者按需请来的第二意见」这一席位：条款正文在 `WHAT.md`，用例覆盖与局限在 `tests/README.md`。这里补说机制与落点，说法与 `WHAT.md` 保持同一套，不新增规则。

## 席位与归属

同一个调用 session 的每次咨询都落到同一个顾问。`AdvisorSeat.consult` 首次调用为该 session 建立 advisor child——走 `ISessionHostPort.CreateChildSession`，因此物理父级是家族根而不是调用者本身——之后复用同一个 child，并订阅它的终态读回散文。进程内 seat 表按调用 session 记 child，重启即遗忘，下一次咨询重新建立：seat 不持有 durable link，也不写调用者的会话历史。子会话请顾问与顶级会话请顾问得到同样的顾问，不会产生 sub-subagent。

## 三材料装配

`AdvisorView.plan(language, delivered, request)` 是纯函数（无 Host、无 journal、无时钟，同输入必得同字节）：把调用者交出的 messages（anchor + 文本）按声明裁到 `prefixUntil` 之上、排除已交付的 anchor，`thenRemove` 命中的 anchor 用摘要替换其文本，然后渲染成 TOML 视图形状的一条 view prompt——作者写的 `# ` 注释表头、一个空行、之后每记录一张 `[[new_work_to_record]]` 表。表头文本取自 `resources/provider/lifecycle/advisor/view`（双语同一语义路径，按 `language` 选 locale）。intent 不进入这条 prompt：`consult` 时 view prompt 与 intent 作为两条独立 prompt 追加，顾问自己的 law 由 Host 的 agent 配置与 system transform 投影，三条材料永不拼接。

## 增量与游标

`Cursor` 是已交付游标，按调用 session 记 anchor 集合，`record` 只增不减，`clear`/`clearAll` 只影响指定 session。下一次 `plan` 只渲染游标之外的 anchor，于是 follow-up 只携带 view 增量加新的 intent。无新材料的请求得到一条只含表头的 prompt 且 `HasNewMaterial = false`；`HasNewMaterial` 不是发问的门禁——intent 本身就是新材料，所以宿主按 intent 是否为空继续咨询。

## 成员与信封

`js.advisor(intent, view)` 由 `JsCapability.Advisor` 投影：capability fragment（成员名、签名、描述、示例、binding key）与生成面的描述、基类 stub、运行时绑定同源，`ToolsBindings.makeAdvisorMember` 把宿主闭包（`JsDelegationHost.Advisor`）注入成员，两处 install 分别在 `ToolRegistry` 的生成面宿主与 `RuntimeSurface`。宿主闭包本身实现 view 声明解析（非法声明拒绝为 `VIEW_DECLARATION_INVALID`）、调用 `AdvisorView.plan` + `AdvisorSeat.consult`、成功后推进游标，返回值是数据信封：`{ ok: true, advice }`，或 `{ ok: false, code, reason }`——席位无法作答为 `advisor-unavailable`，宿主缺席为 `DELEGATION_UNAVAILABLE`。拒绝是数据而非抛出，程序不需要为它写 catch；渲染只在工具结果边界发生。

## 无工具与无副作用

`OfficeCapability.permissions Role.Advisor = Set.empty`，`StaticTools` 不为 `ToolPermission.Advisor` 准入任何工具，agent 条目 hidden——顾问只能读被交给它的材料，回答只能是散文。因此一次咨询不改变调用者的 capability、不创建 Interaction Authority Root、不写调用者的历史；空回答按失败返回，不伪造建议。

## 局部运行

`node --test requirements/advisor/tests/*.test.mjs`（构建须 `node scripts/build.mjs`；红绿由 DevOps 执行）。
