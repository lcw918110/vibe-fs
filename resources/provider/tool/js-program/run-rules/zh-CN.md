await js.call(class extends js.Callable { ... }) —— 工程成员。

在全局 `js` 对象上调用它，不在基类上：`js.call(...)` 是唯一用法，`this.call(...)`
不存在。你的程序入口是 `async run()`——两个不同的名字，两件不同的事。

控制流归 Host，你只写语义回调。每轮 Host 调用 `next(ctx)`，执行你返回的东西，
每个 interval 时间片用 `watch(ctx)` 守望一次，进程退出交给 `onExit(result)` 处置，
然后再次调用 `next(ctx)`——直到它返回 `null` 或 `maxDuration` 到期。你的代码不会在
时间片之间运行，切片时钟、子进程收割与结局都归 Host 所有：Callable 无法跳过、
拉长或重排一个时间片。

`next` 是循环回调，不是一次性声明。每轮命令退出后 Host 都会再次调用它；工作做完时
必须返回 `null`——固定剧本写 `ctx.step === 0 ? "command" : null`，随机应变的场景
则读状态决定。

必选成员；缺失或形态不合法即 `CALLABLE_SPEC_INCOMPLETE`：
- `maxDuration` —— 毫秒，正数。整个工程的执行上界。
- `next(ctx)` —— 下一步，每轮循环调用一次。
- `watch(ctx)` —— watchdog。必选，每个时间片运行一次。
- `sanity()` —— 控制流自证（见下）。
可选：
- `interval` —— 每个时间片的毫秒数，默认 10000，下限钳制为 10。
- `onExit(result)` —— 刚退出进程的处置。`result` 是
  `{ root, exitCode, stdout, stderr, elapsedMs }`，其中 `stdout`/`stderr` 携带该子进程的
  完整输出（有界头尾），不是时间片增量。
- `onTimeout` —— `maxDuration` 到期时的处理；默认即 abort。

`sanity()`：证明你没有糊弄。Host 在一切开始之前先跑它一次。它返回两个 thunk——
thunk 只把实例拨到目标状态，并返回参数数组；宿主用这个数组调用 `next` 或 `watch`，
再机械地检查结果。程序绝不自己调用这两者，断言代码不用你写：
```
sanity() {
  return {
    nextDone: () => { this.index = this.files.length; return [{ root: "/", step: 99 }]; },
    watchVeto: () => { this.stalled = 6; this.current = this.files[0]; return [{ ...ctx }]; },
  };
}
```
`nextDone` 把实例拨到「全部完成」状态，返回 `[ctx]`——宿主调用 `next`，并要求返回 `null`。
`watchVeto` 把实例拨到「出了问题」状态，返回 `[ctx]`——宿主调用 `watch`，并要求一次否决
（`false` 或 `{ abort }`）。返回值不是数组，或调出的方法没展示出出口，整个 `js.call` 就在调用点 park，携带
诊断载荷——在新调用里修好类，或 unpark 注入一个由你的程序当作 js.call 结局消费的值。
写 thunk 就是在设计终止条件：如果你 mock 不出一个让 next 返回 null 的状态，出口还不存在。

驱动 `next` 的两种形态：轮次驱动（`ctx.step === 0 ? cmd : null`）适合固定剧本——
spawn、waitFor、测试、收割；事件驱动适合一切需要随机应变的场景——`onExit` 把退出码
记进实例字段、`watch` 把观察记进实例字段，`next` 读这些字段再决定：重试、跳过、
hold 一片、推进，或 `null`。完整示例两种都有：`run-recipes` 是固定剧本，`ultra-devops`
是纯事件驱动。

输出在 JS 里处理，不要用 shell 管道。outcome 携带命令的完整 stdout（有界头尾，
`truncated` 如实相告）；接 `| head`、`| tail` 或 `grep` 扔掉的恰恰是你可能需要的
字节——关键错误常在前头，尾部不保证有任何东西。返回裸命令，在 JS 里切原文：
`outcome.stdout.split("\n").filter((l) => l.includes("error"))`——逻辑随 JS 一样丰富，
原文留在手上，第一次切漏了还能 fallback。

`next(ctx)` 只返回以下之一：
- `"command"` —— shell 命令，前台跑到退出。
- `null` —— 没有更多工作；run 结束。
- `{ spawn, name }` —— 启动后台进程并立即继续；`spawn` 是 shell 命令字符串，与前台
  同一形态。
- `{ signal, name }` —— 向该具名后台进程发送指定信号（默认 `SIGTERM`）；2 秒后仍存活
  则 SIGKILL。
- `{ send: { name, input } }` —— 把 `input` 写进该后台进程的 stdin（缺行尾换行时补
  一个）；它的增量输出在下一个 `watch` 片到达。嵌套是 canonical 形态；平铺
  `{ send: input, name }` 拼写被接受为同一件事。这就是交互形态：起一个 REPL/server，
  发一行、读增量、再发下一行。
- `{ waitFor: { port, timeoutMs } }` —— 等该 TCP 端口可接受连接；等待期间 `watch` 以
  探测节奏（约 100ms）继续被调，崩溃仍可被观察并 abort。

`watch(ctx)` 返回：
- 什么都不返回，或 `true` —— 继续。
- `false`，或 `{ abort: reason }` —— 立即否决。`false` 是真实的否决，不是「继续」
  的同义词。
- `{ park: payload }` —— 通过与 `js.park` 相同的通道挂起。恢复时注入字符串 'abort'
  即中止；注入其他任何值都继续。

先观察，再裁决。`watch` 应当读 ctx 增量、把要紧的状态记在实例字段
（`this.lastSize`、`this.stalled`）上，然后再决定。一个什么都不看就 `return true` 的
watch 是没有在监督的 watchdog——Host 不会拦你，但也没有任何东西在被监督。
`next` 或 `watch` 抛异常会让整个程序失败。

`next(ctx)` 的 `ctx` 是 `{ root, step }`（`step` 从 0 起，计循环轮数）。
`watch(ctx)` 的 `ctx` 是
`{ root, step, stdout, stderr, exited, exitCode, backgrounds, elapsedMs, remainingMs }`，
其中 `backgrounds` 是 `{ name, exited, exitCode }` 数组；`stdout` 与 `stderr`
是本片增量——前台命令与全部后台子进程合并进同一个观察窗——不是全程记录。

await 交回的结局是
`{ ok: true, status: 'completed' | 'aborted' | 'timeout', reason, exitCode, stdout, stderr, truncated, bytes, durationMs, steps }`。
`reason` 在正常完成时为 null，其余情况带中止/超时的原因；`steps` 计 Host 执行的
循环轮数。

失败模式，一次说清：
- `sanity()` 携带 `sanity.failure` 载荷 park：thunk 没返回参数数组，或宿主调出的方法没展示出口——
  `next` 没返回 `null`，或 `watch` 没否决。载荷携带失败原因与当时的返回值；
  在新调用里修好类。
- `status: 'timeout'` 且命令输出每轮重复一份，意味着 `next` 从未返回 `null`——Host
  每轮忠实重跑它返回的东西直到 `maxDuration`。看 `steps`：轮数很多而命令很短，
  就是这个病的签名。
- 平铺的 `{ send, name, input }` 对象点不出子进程：嵌套 `{ send: { name, input } }`
  才是 canonical（平铺 `{ send: input, name }` 拼写被接受；`send` 载荷解析不出目标名
  时以 `CALL_CHILD_UNKNOWN` 失败）。
- `async run()` 是你的程序入口。工程循环只有 `js.call` 一个入口。

后果，一次说清：
- 中止用 SIGKILL 杀前台进程；`maxDuration` 到期时同样处理，并回报 status 'timeout'。
- run 结束时，每个后台进程都会被杀掉。
- 磁盘状态从不回滚：下到一半的文件就停在一半。把每一步写成「再跑一次是接着做，
  而不是重头做」。
- 每条流最多保留 256 KiB——开头 128 KiB 加结尾 128 KiB——并置 `truncated: true`，
  仍以 `bytes` 报真实总量。

稳定失败码：`CALLABLE_SPEC_INCOMPLETE`、`CALL_COMMAND_UNSUPPORTED`、
`CALL_WAIT_TIMEOUT`、`CALL_CHILD_UNKNOWN`、`CALL_PARK_UNSUPPORTED`。

机械规则写进回调；语义分叉——包括被否决的 sanity thunk——交给 parked 载荷。

更多完整示例：`run-recipes`（分钟级长编译；npm 服务 + 前端测试）。
