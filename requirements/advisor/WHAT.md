# advisor — WHAT

顾问（advisor）是调用者按需请来的第二意见：一个**同一 session 专用**的评审参与者，读调用者交出的 view 与 intent，只回散文，不持有任何工具。仿照 `@juicesharp/rpiv-advisor` 的 advisor 策略，但顾问不是一次性 side-call，而是**按调用 session 归属的常驻专席**。

## [001] 专席归属与「孙子变儿子」

同一个调用 session 的 advisor 请求**永远路由到同一个 advisor**：该专席按调用 session 按需生成（首次调用时建立），此后逐次复用，历史随调用累积。顾问由 `Sessions.createChildSession` 建立，因此物理父级是**家族根**（delegation-007：儿子生的儿子仍是儿子）——subagent 请顾问不会产生 sub-subagent。

## [002] 请求由三样独立材料组成，不合并

一次调用交给顾问的完整请求是**若干条彼此独立的 prompt**，绝不拼成一段：

1. 顾问自己的 system prompt（`resources/provider/role/advisor/**`，由 Host agent 配置与 system transform 投影）；
2. `view`——调用者交出的上下文视图，按仓库统一的 TOML 视图格式渲染（`# ` 注释表头 + `[[new_work_to_record]]` 数据表，`SyntheticToml` 词汇）；
3. `intent`——本次要顾问裁决的问题，**自成一条 user prompt**，与 view 不合并。

## [003] follow-up 追加 view 增量 + 新 intent

后续调用**追加**而不是重发：只把 view 的**增量**（相对上次已交付给该顾问的部分）渲染成新的 view prompt，其后追加新的 intent prompt。已交付游标按 (调用 session, 顾问 session) 记录；顾问看到的是它已经被交底的部分加上新增部分。

## [004] 无工具、散文返回

顾问 office 不携带任何 `ToolPermission`（`OfficeCapability.permissions Role.Advisor = Set.empty`）。它的回答就是散文：调用者拿到的是被建议的散文本身，而不是渲染后的工具回执；空回答按失败处理，不伪造建议。

## [005] 无授权副作用

顾问读不到仓库、跑不了命令、改不了任何东西，也不介入调用者的授权：请求顾问不改变调用者的 capability、不创建 Interaction Authority Root、不写调用者的会话历史。

## [006] `js.advisor` 是编程面成员

调用面是 `js.advisor(intent, view)`：与其他 `js.*` 成员一致，**返回数据而非渲染后的 prose**（`{ ok, advice }` 一类信封），渲染只在工具结果边界发生。`js.horizon` 同样以成员身份返回名册数据（含每个子智能体的四数校准），不再只靠宿主工具呈现。

## 施工分期（继任者按此续作）

1. **已完成**：`Role.Advisor` agent type——内部角色、无工具、persona Auditor、role law 双语资源、Host agent 条目（hidden）、system prompt 投影、交互修复分区排除。
2. **专席归属**：进程内 seat 表按调用 session 归属 advisor child（由 `Sessions.createChildSession` 建立，物理父级为家族根），不写 durable link。
3. **请求装配与增量**：view 选择（复用 `Anchor/AnchorSurface` 与本轮 transform 的 view 应用）→ `SyntheticToml.document` 渲染；intent 独立成条；已交付游标按会话记录；交付走与该专席的托管派发等价路径，读回其散文。
4. **`js.advisor` / `js.horizon` 成员**：`ToolPermission.Advisor`（六处机械面：`ToolRegistry.staticAdmissions`、`baseSpecs`、`OfficeCapability` permissions/label、`StaticTools` toolNames/allPermissions/knownToolNames/permissionFor、`resources/ablation/tool-map.json`、`Strength/InvestigationEstimateContract.classifyTool`）+ `JsCapability.Advisor`/`Horizon` 与 capability fragment（member/signature/description/example/binding key 四层一致）+ `ToolsBindings` 成员 + `JsDelegationHost` 闭包与两处 install（`ToolRegistry.fs` 的生成面宿主、`Repository/Programming/Js/RuntimeSurface.fs`）+ 基类 stub 与描述。`js.horizon` 复用既有 `ToolPermission.Horizon`，无需新权限。
5. **测试与教学**：本包测试按条款编号 `NNN.test.mjs` 锚定 `WHAT[advisor-NNN]`；`repository-programming` 的四层同构与 `participant-horizon` 的名册测试同步扩展。
