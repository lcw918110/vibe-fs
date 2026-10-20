# advisor 测试与证明范围

本目录不定义额外规则；条款正文在 `../WHAT.md`，用例标题以 `WHAT[advisor-NNN]` 锚定条款。当前只有 002、003、006 三个条款有可执行用例，其余条款的证明缺口见文末。

- `002.test.mjs`（`WHAT[advisor-002]`）：对编译产物 `dist/Execution/Delegation/Advisor/Surface.js` 断言 view prompt 的顾问 TOML 形状——`# ` 注释表头、空行之后每记录一张 `[[new_work_to_record]]` 表，记录保持调用者 anchor 序，`prefixUntil` 上的 anchor 一律不进 view；`thenRemove` 命中的 anchor 以摘要（而非原文本）到达；空增量报 `hasNewMaterial = false` 且不产出任何数据表，只留表头。JS 面由该 Surface 自己承担 Fable list/map 的展开，不另写 host 投影。
- `003.test.mjs`（`WHAT[advisor-003]`）：同一 `Surface.js` 里的 plan 走真实 follow-up——带上次 `deliveredAnchors` 的第二次规划只发出新增 anchor 的表，已交付 anchor 不重发，`thenRemove` 也不会重新打开已交付的 anchor；再走同一 Surface 的游标成员——游标按调用 session 累积、只增不减、session 之间不共享，`clear` 只重置一个 session。
- `006.test.mjs`（`WHAT[advisor-006]`）：`generateRole('manager','en')` 的生成面在成员表、描述文本、基类 stub 与运行时绑定四处都携带 advisor，且去掉 `ToolPermission.Advisor` 后四处同时消失；`installDelegationHost` 注入的宿主闭包值被 `js.advisor` 原样按数据返回（不重渲染、不落到 fork/join/fission/horizon 的宿主），未注入宿主时以 `code = "DELEGATION_UNAVAILABLE"` 拒绝，且拒绝不使程序失败。

## 证明缺口

- `001`（专席归属与家族根父级）：需要真实 advisor child 的建立与父子关系观察口，尚无本包用例；`Seat.consult` 的复用路径也没有断言。
- `004`（无工具、散文返回、空回答按失败）：仅由 `006` 间接覆盖成员路径；"顾问没有工具"与"空回答即失败"尚无直接用例。
- `005`（无授权副作用）：运行时 capability 与 journal/authority 的观察口不在本包用例内。

`plan`/游标的用例只证明纯规划与进程内游标：它们不证明 view 与 intent 真的以两条独立 prompt 到达顾问，也不证明散文真的从真实 terminal 读回——这两件事归 001/004 的缺口。002/003 只固定 `en` locale，双语表头的成对性由 language parity 门禁与 `provider-language` 承担，不由本目录断言。
