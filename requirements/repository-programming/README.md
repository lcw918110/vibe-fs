# repository-programming — README

本包的条款正文在 WHAT.md，用例覆盖与局限在 tests/README.md。这里补说 028-041 涉及的 JS 编程面与持久化机制，说法与 WHAT.md 保持同一套。

## 沙箱句柄与 deadline（WHAT[repository-programming-028]）

沙箱启动与结果等待解耦：`runSurface` 返回持有 `ProgramPromise` 与可变 `DeadlineRef` 的句柄，调用方可跨调用持有 promise 而不在启动处 await 到底。`resetDeadline` 在不重包、不重跑程序的前提下重置后续 api 调用的超时窗口，也能让在跑的程序过期。

`deadlineEpochMs = 0` 表示不设总时限。所有 `todowrite-for-*` 调用（含 unpark）使用此模式，`join` 可长期等待；每个同步执行片段仍受 10 秒保护，具体操作保留各自的限制。显式 abandon 的清理窗口仍有界。

## park/unpark/abandon 生命周期（WHAT[repository-programming-030]、[032]-[036]、[038]、[039]）

旧成员 `file`/`glob`/`edit`/`rewrite` 退役后，文件系统主原语的 canonical member 序列为 `write, rm, mv, cat, ls, rg, stat, mkdir, whereAll, where`

`unpark` 以 `{ handle, value }` 恢复挂起程序，挂起点的 await 求值为注入值；恢复后的程序可再次 park 并得到新 handle。handle 恰好消费一次：第二次 unpark、abandon 之后的 unpark、对未知 handle 的 unpark 或 abandon 都报 `HANDLE_UNKNOWN`。

`abandon` 以拒绝 parked await 的方式终结程序：原生 try/finally 作为 finalizer 照常运行，finalizer 内的 rm/write 按 as-is 落盘；清理窗口内 api 照常可用，但再 park 被拒为 `CANNOT_PARK_WHILE_ABANDONING`。捕获放弃继续执行的程序，其返回值被丢弃，结果仍为 Abandoned。

park 现场表是独立的进程级静态表模块 JsParkedSites（ParkedSites.fs，并发上限 8），由工具编排层 ToolWorkflow 与 session 回收层 PluginSessionScope 共享引用，进程内存活：同进程后续调用仍见其挂起。超出上限的 park 以 `CONCURRENCY_LIMIT` 拒绝，无排队；abandon 释放现场后新 park 可入。现场携带 session owner，会话终结据此 reap。

## 调用面三态互斥（WHAT[repository-programming-064]）

一次编程调用只携带 `program` / `unpark` / `abandon` 之一，三个参数在调用面上都是可选的，模型只发其中之一。同时给出两个及以上（例如同时给 `program` 与 `abandon`）以 `INVALID_ARGUMENT` 拒绝，reason 点名冲突的字段名，且是零效果：程序不跑、不恢复、不放弃、不产生文件效果，也不留待下一请求消费的视图声明，更不消费被点名的 handle——现场原样留在现场表里，等模型用单状态调用处置。一个状态都不给是缺状态，报缺少 program 的错误（`missing 'program' argument`），与“两个状态”的拒绝是两件事。

## 泄漏兜底三件套（WHAT[repository-programming-031]）

交付硬门禁接在 deliverSpec 执行链，plan/manager 分派之前：存在未处理 parked handle 时拒绝交付并列出全部 handle（资源 `tool/js-program/parked-deliver-blocked`）。每回合提醒经由 PairHint 的 composeMarkerDocument 并入（资源 `tool/js-program/parked-hint`），无载体回合不注入。会话终结在 ClearSession 清理链强制 reap 该 session 名下全部现场，durable 记 Reaped。两个资源均为 zh-CN.md/en.md 成对双语、占位符一致且含 `{{handles}}`；三个调用点只调用 JsParkedSites，不定义现场表。

## rm/mv 编程面（WHAT[repository-programming-040]）

独立宿主 `mv`/`rm` `ToolSpec`（及 `FileTools`/`FileMutationTools`）已退役，`rm`/`mv` 仅由 Remove/Move capability 在 js 编程面上投影，与 `write` 同属 as-is 逐笔落盘路径：rm 删文件或空目录，mv 按 POSIX 语义移动或重命名并支持覆盖。支持跨越工作区访问合法路径；成功渲染报 `removed = [...]` 与 `moved = ["from -> to"]`。每次 rm/mv 各记一笔 durable 条目，Prepared/Committed 成对排空 pending 投影。

## js.call（WHAT[repository-programming-050]、[058]）

Exec capability 投影 `this.call(command, intent)`、`this.signal(signal)`、`this.send(input)`。`intent` 是 `call` 的第二个参数，不是工具参数。谁让这次程序 park，`signal` 与 `send` 就作用在谁身上；那条命令已经结束时是 `CALL_CHILD_UNKNOWN`。

工具参数 `name` 的描述写明每次调用都填。park 的 handle 就是这个 `name`。自动 yield park 的载荷带 `{ name, intent, delta: { stdout, stderr } }`。`where` 是字面 `IndexOf`。

## 委托原语：fork / join / fission / dag（WHAT[repository-programming-056]）

**所有 js.* 成员返回数据，绝不返回渲染后的 prose**（宿主在工具结果与 parked 边界统一渲染）。委托成员统一信封：`{ ok: true, ... }` / `{ ok: false, code }`。

- `fork(payload)`：字符串或 `{ charge, name?, calling?, keywords? }`（缺名由 charge 派生），返回 `{ ok, name, agentId, outcome }`。
- `join()`：按成员归属收拢，返回 `{ ok, records: [{ name, agentId, role, status, view }], byName }`。
- `fission({ charge, lanes })`：同身份多 lane，返回 `{ ok, groupId, lanes: [{ index, sessionId }] }`；owner 的 `join()` 收拢本组。
- `dag({ nodes: [{ id, charge, deps? }] })`：基类编译成波次 `fork` + 一次 `join`，下游 prompt 带依赖记录；返回 `{ ok, results, skipped }`；**节点失败即 park**（`where` 同通道），注入 `"retry"`/`"skip"`/任意值/`{ abort }` 决定。

实现边界：delegation shard 新增结构化入口 `ForkTool.executeReceipt` / `JoinTool.executeReceipt` / `FissionTool.executeReceipt`（返回数据与稳定 kebab 码），宿主 `fork`/`join`/`fission` 工具的 prose 输出保持不变；ToolRegistry 在每次编程调用期间安装进程内委托宿主驱动这些入口，调用结束即清除；未安装宿主时成员返回 `{ ok:false, code:"DELEGATION_UNAVAILABLE" }` 而不抛出。Orchestrator 无文件权限但持有 Fork/Join，因此获得仅委托的编程面；`hasFsCapability`/`admissionFor` 把 Fork/Join/Fission 计入授权。双语教学在 `tool/js-program/delegation-rules`。

## ls 的 ignore 语义与 rg 搜索（WHAT[repository-programming-057]、[009]）

`ls` 列一层时应用 gitignore 语义。递归枚举与内容搜索用 `rg(argv)`：一个 argv。内容搜索等价于 `rg --json` 加上该 argv，不追加 `--no-ignore`，返回 `{ exitCode, messages }`。`--files`、`--files-with-matches`、`-l`、`--count` 等与 `--json` 互斥，原样执行并返回 `{ exitCode, paths }`。二者都是 JSON 对象，不是文本。写法见双语资源 `tool/js-program/recipes`。

## 持久化机制（WHAT[repository-programming-012]、[013]、[015]、[019]、[029]）

as-is 逐笔落盘：每笔 `write`（及 `rm`/`mv`）调用即经 MutationFs 单笔原子落盘，跨笔不回滚；程序以任何方式终止时，已落盘改动保持为事实分界线，失败结果如实附已落盘清单（`# failed` 加 `[fs]`），无落盘改动时不附 fs 节。

durable 记账只进统一 EventStore：Prepare/Committed 成对追加。记账追加失败以稳定 `TRANSACTION_PREPARE_*`/`TRANSACTION_COMMIT_*` 码拒绝成功、如实上报已落盘效果，不重试追加、不换事务身份，失败携带原事件 id、事务 id 与原始 cause。物理写失败同样以 `TRANSACTION_COMMIT_FAILED` 失败且失败笔之前的落盘改动保留；程序失败先于记账完成时不留 durable pending。Prepare 无 Committed 是中断审计证据，EventStore 重开只观察证据，不重放、不回滚、不补交。EventStore 端口的坏输入先结算字节事实，再经唯一的语义熔断终止。

## Lifecycle 审计接线（WHAT[repository-programming-041]）

Parked/Unparked/Abandoned 的审计沿生产组装路径接线：PluginHostInterop 从 workspace 的统一 WorkspaceEventStore 同源构造 `IJsLifecyclePersistence`，经 ToolRegistry.create 贯通到每个生成的 JsToolSpec，执行调用点传入该 persistence、不留硬编码 None；执行链记录 Parked、Unparked、Abandoned。会话关闭链经 PluginBoot → PluginRuntimeScope → PluginSessionScope 贯通；ClearSession 先枚举现场再 reap，逐 handle 记录 Reaped。四种 lifecycle 事件同住 TransactionStore 单一审计流；ParkedSites 现场表保持进程内存活，Reaped 审计归调用方，现场表自身不触碰 durable。