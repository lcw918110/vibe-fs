# todowrite：命名程序与 program/runtime 公开面

2026-10-24 定稿。**本文是设计记录**；现行合同以 `requirements/repository-programming/WHAT.md`、`requirements/process-execution/WHAT.md`、`wanxiangli/src/program/AGENTS.md` 与各包 `.d.ts` 为准。正文已按当前代码核对：字段名是 `intent` 而不是 `planB`，期限按 `UTF8Bytes(intent) × 50ms` 计算，旧八入口已撤销。

## 1. 公开入口

`todowrite-for-ROLE` 恰好接收三个必填字符串：

```ts
{
  name: string;
  program: string;
  intent: string;
}
```

`name` 与 `program` 不得为空白，`intent` 不得为空。模型为每个新运行取名；宿主不按物理调用身份偷偷生成名字。程序保持 `class Js extends JsProgram` 与 `async run()`。旧顶层 unpark、abandon、handle、prefixUntil、carry 不构成兼容入口。

| 成员 | 合同 |
| --- | --- |
| `this.call(command)` | 排空此前 SDK 操作，再启动一条 shell 命令，阻塞所属程序。 |
| `this.park(payload)` | 显式暂停，恢复值成为该表达式的结果。 |
| `this.unpark(name, value?)` | 恢复同会话内的原程序，或返回其保留终态。 |
| `this.abandon(name)` | 取消并确认资源收尾，不是删除登记。 |
| `this.signal(name, signal)` | 控制目标当前唯一命令，不恢复程序。 |
| `this.send(name, input)` | 向目标当前唯一命令提交一行输入，不恢复程序。 |

`call` 只收一个参数；`unpark` 至多两个，第二个只是显式 park 的恢复值。成员不能接收或创造预算。`intent` 是请求文本，不作代码执行，也不进程序变量。

## 2. 一次准入，一条绝对期限

工具完成参数与权限准入时一次计算：

```text
budgetMs = UTF8Bytes(intent) × 50
deadline = admissionTime + budgetMs
```

每 20 字节一秒。wanxiangli 不截短合法预算，不加隐藏上限。本次所有操作、SDK 排空、连续命令与被唤醒目标共享同一个 RequestWindow；任何 call、命令退出或嵌套恢复都不能刷新、叠加或延长期限。

| 计量 | 等待期间 |
| --- | --- |
| 程序计算预算 | 等待时停表，恢复后用剩余量。 |
| 请求期限 | 持续推进；到期真实暂停活动程序并返回暂停回执。 |
| 命令运行时长 | 可观察；不是由 intent 暗中设定的命令寿命。 |

底层 ProcessSession 的物理有界执行合同独立存在，运行硬顶与请求期限不同，不能因迁移取消。

## 3. call：先等真实结清，再启动唯一命令

进入 call 先检查形状与权限，再等本程序此前发起的 SDK 操作取得真实终态。在途不是错误，不报 PENDING_OPERATIONS，不要求补 await 后重发，也不取消原操作。真实 SDK 异常沿原错误合同呈现，已落盘效果保留；业务成员返回的 `{ ok: false }` 仍是该成员的业务数据。

每个命名程序拥有独立执行进程。同步桥接在 call 表达式求值处停住执行单元，漏写 await 或写 Promise.all 都不能越过首个 call 启动第二条命令。宿主、其他程序与既有 SDK 继续响应。并行命令通过不同名字的程序表达。

## 4. 暂停与恢复

- 显式 park 与 `where` 定位歧义走同一通道；恢复值只喂给显式 park，`call` 取得原命令真实结果。
- 请求期限耗尽也真实暂停续体，不把 running 对象当 call 结果。
- 恢复入口只有新命名控制程序里的 `this.unpark(name, value?)`；它不重编译、不重建沙箱、不扫描工作区、不重发操作。
- 控制者与本次唤醒的目标共享控制请求期限；到期时两者都暂停。恢复控制者不隐式再次唤醒目标。
- 重启不恢复丢失的 JS 栈；只恢复实际持久化归属与已确认终态，丢失现场报中断或未知，不自动重跑源码。

## 5. 名称、身份与重放

登记键是会话 + `name`，一个名字同一时刻只对应一次运行。同一物理工具调用重试重放已有回执，不再启动程序；新调用占用仍在活动（运行中或 parked）的名字报 `NAME_TAKEN`，已结束（completed、failed、abandoned）的名字可登记为新运行。重复或迟到的 unpark 不消费后来暂停，迟到的 signal/send 不误击后续命令；观察到旧运行的控制不作用于同名新运行。

## 6. 控制、清理、输出与结算

每个 Run 终态的 effects 经 settle 一次结算并保留；重复控制不重结。目标 effects 留在目标回执，不冒充控制者新产生的文件效果。持久错误保留身份与原因；生命周期审计按序等待，失败以 lifecycleError 进入回执，不抹去真实暂停或已发生效果。

`parked.delta` 是新增观察，不消费最终 CallResult。退出码、信号、完整输出文件、字节数与截断分别保留；信号退出的 null 退出码不得补零。原始输出文件在程序与会话关闭后保留。

## 7. 独立 join 与 DAG

独立 `join()` 无参数、无 name/program/intent，不建立临时 JS 程序。它沿既有工作归属、结果批次、准入与中断规则收拢，不证明全部子工作成功。用户输入或宿主中断只结束这次等待，不取消子工作，不把超时呈现为完成。编程内 join/dag 的能力投影与专用隐藏执行路径已退出；依赖由模型显式安排：程序派工 → 独立 join 收拢 → 下一程序。

## 8. 两包的所有权

| 入口 | 所有者与职责 |
| --- | --- |
| `wanxiangli/program` | Program() 登记；ProgramSession 会话内名称、重放与协作；私有 Run 的单次运行、窗口、恢复代际与结算；ProgramCatalog 成员投影；Wait 控制。 |
| `wanxiangli/runtime` | Runtime 独立进程、隔离与通信；RuntimeTools 原语与字面锚点；FileSession 读快照与版本；Filesystem、ProcessSession、Terminal、OutputSpool、Time 各自物理资源。 |

`program → runtime` 是永久单向基础依赖，只消费公开产物。runtime 不解释 intent，不认识角色、评审或业务 deliver。

旧八入口（programming / sandbox / fs / process / pty / spool / time / wait）已撤销：能力投影归 program，沙箱与文件归 runtime，预算语义归 program。Work、Journal、权限等其他领域保持各自归属。宿主只经 npm 公开入口消费；宿主编码与业务准入不进程序设施。

## 9. localspace-js backport

相同思想已回移到 localspace-js：`js({ workspaceId?, program, planB })`、`call(command)`、`unpark(handle, value?)`；planB 只在准入时计算，同一请求的命令、恢复与嵌套目标共享期限。该仓公开上限为 `min(UTF8Bytes(planB) × 50ms, 30000ms)`，是该仓已有的并发改动，不回灌成 wanxiangli 的隐藏上限。断连保持 detach，不续期、不杀原命令；只有显式新请求可恢复。worker 内直接执行的计算与文件 API 不因等待到期整体冻结。

## 10. 当时的交付边界

2026-10-24 合入时：程序与运行时 64 项测试在 Node 26.12.0 与 Bun 1.4.2 通过；宿主核心三包 641 项（622 通过、12 跳过、7 TODO）；干净安装下新包可被 Node/Bun 消费且旧入口不可导入；库全量 947 项中 16 项失败与隔离基线逐项一致。跳过与 TODO 不计作通过。

这些数字只对当时那棵树成立。现行状态以 `git log`、当前 `npm test` 结果与 `requirements/GAP.md` 为准，不把本文当验收证书。
