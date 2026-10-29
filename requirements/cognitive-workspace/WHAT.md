# cognitive-workspace — WHAT

本包现为退休边界：系统不再提供持久认知画板。保留这些条款，是为了防止已经删掉的 jq/canvas 协议以“兼容”名义重新进入生产路径。

## [001] 不存在 session 级持久认知画板

生产代码不得维护、恢复或投影 session 级 JSON 认知画板；Life、重启、deliver、退休等生命周期都不再携带此类状态。

## [002] assume 只有 assumption

`assume` 只接受 `assumption: string`。不得出现 `update`、`query`、`todos`、selector、vars 或任何 jq 参数。

## [003] 旧 Cognition journal 只读兼容

历史 journal 中已经存在的 `AgentFact.Cognition/AssumePhaseCommitted` 可以被兼容 decoder 读取，但 fold 必须为 no-op，不恢复画板，不产生新 projection，也没有任何新写入口。

## [004] durable projection 不含 Cognition

当前 `AgentProjectionSet` 不得保存 `Cognition`、canvas snapshot、owner ordinal 或同义字段。

## [005] 不存在 canvas runtime、codec、owner 与 jq 依赖

生产编译图不得包含 `Participant/Cognition` runtime、workspace、codec、admission、fold、TodoSink；包依赖中不得包含 `jq-wasm`。

## [006] assume 不产生 compression 边界

`assume` 成功不产生 compression 边界。原视图声明与 compression checkpoint 准入已彻底退役，上下文压缩边界唯一由显式 `scope.kind=resetScope` 注解调用划定（见 context-compression [033] 与 obligation-ledger [001]）。

## [007] 待办写入不通过 canvas 兼容桥

宿主待办写入只经编程面 `todowrite-for-<role>`，插件不维护第二份待办真相。不得恢复 TodoSink、`assume(update,todos)` 或 Magic Todo 作为 UI 写入桥。

## [008] 不执行 jq

没有任何 provider 输入会在本系统内作为 jq 程序执行；不得为了 assume 再引入 jq runtime、jq 编译错误分支或 jq 输出数量协议。

## [009] assume 是无状态动作

`assume` 的结果只取决于合法调用与 provider 语言；它不读取前一次 assume、session 私有记忆或跨实例缓存。

## [010] 笃定不等于事实、权限或完成

`assume` 只表示“当前判断已经完成抽象并准备据此行动”。它不证明判断为真、不授予角色权限、不证明测试通过，也不构成完成或退休证据。

