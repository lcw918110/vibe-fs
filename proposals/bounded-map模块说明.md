# bounded-map：有界异步映射

2026-10-31。拆分计划第二项已接回：库负责每次映射的并发许可、等待队列和结果关联，主体通过公开入口调用。运行结果及组合验收中的既有 TODO 见文末。

## 职责与调用

`wanxiangli/bounded-map` 接收一组条目及工作函数，在指定上限内并发处理，并按原输入顺序返回结果。每次调用拥有自己的许可和等待者；没有跨调用共享的队列、预算或全局并发限额。

```ts
export function mapBounded<T, U>(
  maxConcurrency: number,
  action: (item: T, signal: AbortSignal) => Promise<U>,
  items: readonly T[],
  signal?: AbortSignal,
): Promise<U[]>;
```

限额是正的安全整数。输入数组的成员在调用时取快照；对外部数组增删、替换成员不会改变已提交的映射。条目对象本身保持身份，不宣称深复制或接管条目的业务状态。返回值沿输入排序，不沿完成时间排序。

提供 signal 时，各 action 收到同一个对象；省略时本次调用使用一个未取消的信号。原生 JS 边界使用 AbortSignal，不暴露 Fable CancellationToken、TaskCompletionSource 或内部 semaphore。

## 失败与取消意味着什么

首个 action 失败，整体 Promise 即可拒绝，保留该错误；其他已开始和排队的任务仍可能继续。action 成功、同步抛错或异步拒绝都要归还许可，不能因一个失败使后面的排队项永远取不到许可。

取消在入队前、取得许可后执行 action 前检查。已运行的 action 是否停止由它自行响应 signal；排队期间取消，仍等到取得许可才观察到，不增加即时唤醒等待者的承诺。空数组不调用 action，仍先检查限额是否合法。

因此，整体拒绝或 signal 取消都不能证明外部效果已经结束。调用方如需确认收尾，必须等待自己拥有的工作协议；本模块不做重试、不强杀工作、不判断 provider 或进程是否结束。

## 主体如何使用

真实接回点是 `Repository/Investigation/WarmStart/Runtime.fs`。主体继续选择角色、关键词、工作目录、搜索能力及 `MaxKeywords`，并保留单条搜索失败视为空结果的已有策略。映射器只负责调度这些已选定的工作，回调返回该关键词对应的搜索结果。

旧生产调用使用 `CancellationToken.None`，新调用省略 signal；F# 边界把输入 list 转为数组、返回数组转回 list。不会在主体再保留一份队列或许可算法。

`Foundation/Parallel.fs` 原先还放着 `Defer.defer`。它负责 Promise 微任务延迟，仍由 ChatExecutionJournal 与 Acceptance 使用，因此留在主体的 `Foundation/Defer.fs/.fsi`。保留原编译归属，删除旧 Parallel 实现与仅供测试的 Token Surface，不为微任务延迟另开一个库。

## 验证范围

库行为覆盖并发上限、串行退化、输入／结果顺序、各调用独立、输入成员快照、空输入、非法限额、signal 身份、取消边界、同步与异步错误以及拒绝后兄弟继续。用可控制的 Promise 门闩安排次序，不靠长时间 sleep 猜测调度。

独立构建只携带本模块源码；实际包消费从公开入口导入，分别检查 TypeScript、Node 与 Bun。主体保留 Reconcile scheduler 用例，并验证 WarmStart 的反序搜索完成后仍按关键词顺序呈现，以及一个搜索失败不阻止其他结果。

主体起始定向命令：

```sh
TESTS_MJS_FILES='requirements/structured-workflow/tests/010.test.mjs,requirements/structured-workflow/tests/011.test.mjs,requirements/repository-investigation/tests/007.test.mjs,requirements/repository-investigation/tests/008.test.mjs' node requirements/verification-system/tests/run.mjs
```

该范围含原有 GAP-083／084 TODO，组合入口仍返回非零；本次新增映射用例单列到 `repository-investigation/tests/010.test.mjs`，可通过同一 runner 单独运行。不删除原有 TODO、不设置接受 TODO 的覆盖变量，也不把它们计为通过。

库候选、主体 gitlink、实际安装与包内容继续使用已有候选一致性检查。主体实际包的隔离消费者还会从解包后的包内裸导入 `wanxiangli/bounded-map`，避免开发目录掩盖遗漏。

## 本次结果

库候选为 `f92dfe0ad9b42c11a81574315eef921f811b4b8a`（0.69.0），主体隔离树 generation `715`。候选 SHA、gitlink、lock、实际安装一致性正例通过；主体已删除旧 Parallel／ParallelSurface，WarmStart 使用新入口，Defer 的原调用保持。

- 库：新模块编译无警告；12 个行为与 3 个 exports 检查通过。只带本模块源码的隔离构建通过；实际 tarball 的严格 TypeScript 正反用例及 Node 26.6.0／Bun 1.3.14 消费者通过，没有新增依赖。
- 主体：最初四文件运行（当时新增映射用例仍在 007）21 项通过、0 项执行失败、4 个既有 TODO，runner 返回 1。TODO 是三项原文逐字节呈现断言（GAP-084）和工作目录来源（GAP-083），本次未改这些行为。新增两项映射用例移至独立 010 后，runner 单独运行该文件 2/2 通过；其余已通过用例未重复运行。整组验收仍不能称为全绿。
- 实际主体包：`WANXIANGLI_EXPECTED_SHA=f92dfe0ad9b42c11a81574315eef921f811b4b8a node scripts/verify-package.mjs` 通过。归档含 1,458 个成员，隔离消费者从实际包内裸导入 bundled `wanxiangli/bounded-map`；SHA-256 为 `a077b2df30eeff104cd5a0be40b266d06c7e4809b303b8159b35ff18df2429d0`。

提交保存在本地，尚未推送。后续第三项的结果见 [response-text 模块说明](response-text模块说明.md)；上述既有 TODO 不因本次拆分或包检查通过而关闭。
