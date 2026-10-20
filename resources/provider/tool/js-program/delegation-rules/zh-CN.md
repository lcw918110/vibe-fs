所有委托成员都返回**数据**，不返回渲染后的文字；渲染由宿主替你完成，返回值保持 JSON。

```
{ ok: true, ... }          成功
{ ok: false, code: "..." }  拒绝（稳定的 kebab 码）
```

## fork

```js
await js.fork("为解析器模块建索引");                          // 只给任务
await js.fork({ charge: "为解析器建索引", name: "parser-index", calling: "engineer" });
```

- 载荷：字符串（任务）或 `{ charge, name?, calling?, keywords? }`；缺 `name` 时由任务文本派生。
- 结果：`{ ok: true, name, agentId, outcome }`，`outcome` 为 `created` | `nudged` | `commissioned`。
- 拒绝码：`name-required`、`charge-required`、`unknown-calling`、`calling-conflict`、`name-already-belongs`、`warm-start-unavailable`、`context-unavailable`、`not-a-delegation-role`、`placement-failed`、`dispatch-uncertain`。

## join

```js
const { records, byName } = await js.join();
```

- 结果：`{ ok: true, records: [{ name, agentId, role, status, view }], byName: { [name]: record } }`。
- `status` 为 `completed` | `undelivered` | `failed` | `abandoned`；`view` 是子工作经 `js.deliver` 交回的 TOML 视图（`undelivered`、`failed` 或 `abandoned` 时为空）。
- 按成员归属收拢：收的是本会话创建的子工作。拒绝码：`context-unavailable`、`not-ready`、`nothing-to-join`、`join-in-progress`、`interrupted`、`abandoned`。

## fission

```js
const group = await js.fission({ charge: "探索三种设计", lanes: 3 });
const { records } = await js.join();          // 收拢本组的 lane
```

- 同身份多 lane：每条 lane 是同一参与者对任务某一分片的执行，宿主负责收拢。
- 结果：`{ ok: true, groupId, lanes: [{ index, sessionId }] }`。
- 拒绝码：`fission-unavailable`、`lane-start-failed`、`not-ready`、`context-unavailable`、`invalid-prompt`。

## dag

```js
const outcome = await js.dag({ nodes: [
  { id: "spec",  charge: "写出接口规格" },
  { id: "impl",  charge: "实现它", deps: ["spec"] },
  { id: "probe", charge: "并行探索两种设计", deps: ["spec"], lanes: 2 },
] });
```

- 节点按依赖波次推进：本波就绪节点各自 fork，随后一次 `join` 收拢；下游节点的 prompt 会带上依赖节点的记录。
- 结果：`{ ok: true, results: { [id]: record }, skipped: [id...] }`。
- **节点失败会让程序 park**——与 `where` 同一条恢复通道。parked 载荷为 `{ dag, node, code, deps, done, pending }`；注入值决定：
  - `"retry"`——重跑该节点；
  - `"skip"`——标记跳过（下游看到 `null`）；
  - 其他任意值——直接当作该节点的结果继续；
  - `{ abort: reason }`——以该原因让程序失败。

## horizon

```js
const { agents, ptys } = await js.horizon();
```

- 谁还在你的视野里，以数据形式给出：`{ ok: true, agents: [{ label, status, target, workState, record, turnsUsed, turnsLeft, timeUsedMs, timeLeftMs }], ptys: [{ terminal }] }`。
- `status` 为 `returned` | `still-away` | `did-not-return`；`workState` 为 `latest` | `none` | `unavailable`，为 `latest` 时 `record` 带上最新工作的正文。
- 四个校准数说的是这个参与者已经花了多少、大概还需要多少：按委任者估算已用与剩余的回合数、已用的 wall-clock、以及按它已经显示出的节奏估计的剩余时间。任一来源缺失时，四个数都是 `0`。
- 拒绝：`{ ok: false, code: "horizon-unavailable" }`。

## advisor

```js
const advice = await js.advisor("Is the caching layer's eviction policy sound?", { prefixUntil: 12 });
```

- 请教这个 session 的**专属顾问**——一位不持有任何工具、只以散文作答的评审者——用在敲定路线之前，或宣布收工之前。同一个 session 永远落到同一个顾问，之后的调用只追加 view 的增量与新 intent，所以这条线是延续的。
- `intent` 是要问的问题，自成一条 prompt。`view` 声明顾问可以读什么，形状与本轮的视图声明相同（`{ prefixUntil, thenRemove? }`）；宿主把这些记录渲染成一条 TOML prompt，再把 intent 作为另一条发出。二者永不合并。
- 结果：`{ ok: true, advice }`（散文即数据）或 `{ ok: false, code: "advisor-unavailable", reason }`；视图声明非法时以 `VIEW_DECLARATION_INVALID` 拒绝。
- 把回答当作指引：一个 plan、一个 correction，或一个 stop signal。这次请教本身不会往你的会话里写入任何东西。
