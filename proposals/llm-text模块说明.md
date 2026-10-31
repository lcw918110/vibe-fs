# llm-text：面向 LLM 的文本表示

2026-10-31。已实现完整库能力并在主体隔离树接回、退出旧算法，相关验证见文末。本文是该模块的具体说明；施工与候选交付顺序见 [拆分计划](新边缘模块拆分计划-2026-10-31.md)。

原 `synthetic-toml` 只迁底层生成器，主体仍拥有文档组合和结构化树编码。新边界把这三部分作为一个完整职责迁入 `wanxiangli/llm-text`；`SyntheticToml` 是其内部实现，不再另设公开子路径。仍然只有一个 `wanxiangli` npm 包，不增加独立发行包。

## 解决什么问题

调用方已经知道要给模型哪些指令、哪些参考数据。它应能直接表达这些材料，并得到格式一致的文本，不必自己处理引号、表嵌套、字段归属或指令与数据的排版。

例如 JS 工具取得一个对象结果，调用方给出“ok”以及返回值；模块将前者呈现为指令注释，将后者编码为数据。Join 把几个结果组合，模块统一安排指令在前、数据在后，不要求调用方先生成几段文本再拆开拼装。

这项能力服务于万象术，不要求适用于任意软件。边界按职责划分：库负责表示，调用方负责材料的含义与使用决定。不会因文字内容像命令就把参考数据升级为指令，也不会判断模型回答的对错。

## 迁入与保留

| 部分 | 归属 | 原因 |
| --- | --- | --- |
| Document 的指令／数据分区、追加、合并和生成 | llm-text | 所有消费者共同需要的表示行为 |
| Field、DataBlock 的构造与封装 | llm-text | 外部不应把任意已拼好的文本冒充结构化数据 |
| Value 树、原生 JS 值转换、两种根部编码 | llm-text | 与最终文本的结构和数值表示直接相关 |
| 字符串、键、数值、表与表数组编码；布局与字节计量 | llm-text 内部 | 与文档和值编码一起维护，不分成独立同级模块 |
| 哪些材料是指令、哪些仅供参考；任务、角色和业务字段的选择 | 真实调用方 | 属于使用意图和业务决策，不由表示器猜测 |
| `[fs]` 中 rewritten／created／removed／moved 的字段选择 | `JsToolsResult` 就近的小型组装函数 | 调用方掌握文件效果的事实；通过库的结构化接口呈现，不保留 TOML 拼装算法 |
| ReturnedMaterial 的 canonical JSON 编解码、校验和交还协议 | 原职责 | 与存储及交还格式有关；其显示部分调用 llm-text，不把存储协议一并吞入表示器 |
| 原始材料采集、持久保存、压缩与上下文取舍 | 各自现有职责 | 文本表示不等于原件保管，也不决定删留 |
| Provider 请求、资源读取、宿主消息与工具执行 | 调用方／万象通 | 模块不做 I/O，不认识宿主 SDK |

原主迁移源是 `Foundation/LlmFacing.fs` 与 `Foundation/SyntheticToml.fs`。库实现位于 `wanxiangli/src/llm-text/`；主体 [LlmFacing.fs](../src/Wanxiangshu/Foundation/LlmFacing.fs) 只保留 F# 类型适配。[ReturnedMaterial.fs](../src/Wanxiangshu/Foundation/ReturnedMaterial.fs) 是消费者，不是本次吞并对象。

`OriginalMaterial` 当前只是 `LlmFacing.fs/.fsi` 中没有生产使用的空类型。它不证明原件保存能力，本次不沿旧注释建设新的存储系统。

## 一次调用怎样完成

```text
调用方：选材料，明确指令／参考用途，给出结构化值
    ↓
llm-text：构造字段与数据块 → 组合文档 → 生成文本
    ↓
调用方：发送文本，或用库返回的字节数安排已有预算
```

模块不保存跨请求的业务状态、缓存或后台工作。各实例的值索引用弱键封装，令牌不暴露可修改的表示内容。Document 是不透明的值；追加、筛选和合并返回新的文档，不暗改已有文档。内部编码器直接调用，不需要再放一个组合者或通用调度框架。

公开入口导出 `LlmText` 类，以下能力由其实例提供：

| 能力 | 操作与结果 |
| --- | --- |
| 文档 | `empty`、`instructions`／`instruction`、`withInstructions`／`withInstruction`、`withData`、`instructionsOnly`、`combine`；得到 Document |
| 数据 | 字符串、整数、布尔等字段／表成员构造，`table`、`tableArray`、`textTables`，`structuredValue` 与 `rootStructuredValue`；得到 Field 或 DataBlock |
| 值转换 | `ofJs` 接收现有支持范围内的 JS 值，得到表示模块拥有的 Value；显式 Value 支持 int64／bigint 和有序对象字段 |
| 生成与计量 | `render`、`renderInstruction`／`renderInstructions`、`normalizeNewlines`、`byteCount`；得到字符串或 UTF-8 字节数 |

参数和导出形状在库的 `src/llm-text/index.d.ts` 中固定。调用按原生 JS 数组、字符串和 bigint 传值，不暴露 Fable list、DU 的编译布局。`integerValue`、`arrayValue`、`objectValue` 构造显式 Value；对象字段保持给定顺序。Document／Field／DataBlock／Value 使用所属实例持有的不透明令牌，只能交回创建它们的实例；不能混用不同实例的值。

主体可以保留 F# 名称与类型的薄适配，避免让大量调用方了解 JS 边界。适配只做类型、数组和参数转换，不再遍历 Value 生成 TOML，不拼文档，不私藏另一份布局实现。若需要跨边界传递原生数组／对象，库必须在接收时建立自己的值，不能保留调用方可修改的内部别名。

底层 `field(name, renderedValue)`、`tableEntry`、`renderKey` 等不作为另一套公共拼装 API。原 `renderStringByteCountPrefix` 没有生产调用，随旧测试入口退出；实际公开生成与 `byteCount` 的 Unicode／代理项边界继续测试。

## 现有行为怎样保留

本次改变职责位置，先保持现有支持范围内的输出字节。以下都来自当前实现，不是新增 TOML 方言：

- 指令文本规范 CRLF／CR 为 LF，并逐行加注释；数据字符串保留原来的换行内容。显示派生不等于修改原件。
- 指令在数据之前；两者都有内容时相隔一个空行；非空文档保留末尾 LF，空文档为 `""`。
- 根部裸字段稳定排在表／表数组之前，避免后追加的根字段被前一张表吸收。各组内部维持现有顺序。
- `structuredValue` 用 `data` 包装根值；`rootStructuredValue` 的对象字段直接放在根部。两者对空数组的处理不同，不能在迁移时合并成一种行为。
- 对象字段中的 Null 目前省略。整数保留 int64 精度，不经过可能舍入的 JS number；`ofJs` 仅把安全整数转为 int64，其余 number 走现有浮点路径。
- UTF-8 字节数不是字符数。调用方按 UTF-16 索引截取并拼接出的文本，也必须按最终字符串计量，覆盖代理项切分与重新接合。

当前实现不保证把任意 JSON 无损编码成 TOML。例如混合数组中 Null／Object 的现有内联路径会产生 `false`，非有限数和异常 JS 对象也没有完整支持承诺。迁移时先记录并保持这些边界，不把“库化”写成它们已经修复；如实际消费者必须改变此行为，应把具体输入、输出差异作为单独行为变更说明。

调用方提供的字段／表名沿用现有约定，不能把任意外部字符串当作已经验证的 TOML 路径。本模块保住值的转义与布局，不声称能够阻止所有提示注入，也不审查内容意图。

## 与已有模块相比，是否仍然太小

不属于异常小的模块。原方案过细的依据是“表示职责被切开、跨仓接口增加”，不是源码行数不足。

下表取库提交 `eb6425ef16e47cc451952d8c1e0763fbef801818` 的工程 Compile 清单，统计 `.fs` 的物理行数，包含注释与空行，不含测试、类型声明、构建文件或资源。表中不据此推断各模块接回主体的完成程度。

| 模块／范围 | F# 源码行数 | 职责特征 |
| --- | ---: | --- |
| warm-start | 182 | 关键词、命中选择、去重和预算规则 |
| ordinal | 297 | 序数观测的数值运算 |
| prefix | 352 | 前缀比较及世代规则 |
| 原 synthetic-toml 试作 | 421 | 底层生成器加导出适配；尚未交付 |
| events | 450 | 事实编码、身份和闭包检查 |
| resources | 508 | 资源访问与语言处理 |
| delivery | 592 | 交还材料及相关规则 |
| llm-text 的现有主迁移源 | 817 | LlmFacing 408 行 + SyntheticToml 409 行；不是最终实现行数 |
| lifecycle（work 入口） | 1,537 | 工作生命周期及当前工程内的相关对象 |
| runtime | 4,381 | 文件、进程、PTY、时间等多项运行能力 |

该提交中 49 个带工程文件的模块，以上口径的中位数是 435 行。原试作已经接近中位数；新范围的主迁移源约 800 行，后续会剔除空类型、业务字段组装与多余注释，同时增加必要的 JS 边界，最终行数要等实现后再说。

因此，新模块比 Work／Runtime 小，和库中较小的完整职责相比并不偏小。它没有持久化、并发或资源生命周期，所需代码自然少一些；没有必要再吞入 ReturnedMaterial、压缩、资源管理或 Provider 协议来增加体量。

## 行为与验证范围

库侧在实际公开入口上验证三组行为：

1. 文档组合：指令／数据分离，合并、仅保留指令、空文档，稳定根字段排序；原生输入修改不影响已构造值。
2. 结构化编码：两种根部模式，嵌套对象、对象数组、空值与现有混合数组行为，字段顺序、int64 边界；支持的表示用真实 TOML 解析器往返。
3. 字符串与计量：末尾换行、CRLF／CR、引号与控制字符、非 BMP 字符、前缀与 suffix 的代理项接合，重复生成相同字节。

主体的验证仍走真实消费者：

| 入口 | 本次关心的结果 | 现有相关测试 |
| --- | --- | --- |
| 进程输出与格式检查 | 输出里的伪表头仍只是数据；删旧入口不会静默跳过 | process-execution/014、provider-projection/013 |
| Join 与 WarmStart | 指令／数据组合后统一生成，晚来的指令仍在数据前 | provider-projection/014 |
| JS 工具结果 | 成功值、文件效果和失败字段保留原表示 | repository-programming/016 |
| ReturnedMaterial 呈现 | body／carry 的显示通过新模块，原 canonical JSON 不变 | delegation/005；在这一相关用例中补存储字节不变断言 |
| Bookkeeper 的结构化内容 | 实际策略输出使用同一值编码器 | provider-projection/015 直接调用实际 BookkeeperPolicy；同文件核对 ReturnedMaterial 存储字节 |

迁移范围扩大后，主体的起始定向命令为：

```sh
TESTS_MJS_FILES='requirements/process-execution/tests/014.test.mjs,requirements/provider-projection/tests/013.test.mjs,requirements/provider-projection/tests/014.test.mjs,requirements/provider-projection/tests/015.test.mjs,requirements/repository-programming/tests/016.test.mjs,requirements/delegation/tests/005.test.mjs' node requirements/verification-system/tests/run.mjs
```

只更新真正受文件／调用名变化影响的源码检查，不把源码匹配通过当作文本行为通过。库的隔离构建、包安装和主体候选一致性继续按拆分计划执行。

## 本次接回结果

库候选为 `8a4d037de28b8800f832802261dd22232c27a66b`（0.68.0），主体隔离树 generation `714`。主体 gitlink、锁文件、实际安装和打包输入均固定到该候选。

- 库侧：11 个表示行为、3 个入口检查、只含本模块的隔离构建，以及真实 tarball 的 TypeScript／Node 26.6.0／Bun 1.3.14 消费者通过。
- 主体：上列六份定向测试累计 30 项通过；原有 2 个 GAP-082 TODO 仍保留。新增 ReturnedMaterial 用例最初误传 JS 数组，改成以实际接口返回的 F# 列表回传后，仅重跑该文件，2 项通过。没有更改生产行为来适配测试。
- 依赖：目标 SHA、gitlink、检出 HEAD、lock 与实际安装的正例通过，7 个候选错误反例通过。改过的 arch010 呈现 helper 共 14 项通过。
- 发行闭包：`WANXIANGLI_EXPECTED_SHA=8a4d037de28b8800f832802261dd22232c27a66b node scripts/verify-package.mjs` 通过。实际主体归档含 1,457 个成员，隔离消费者经主体适配调用 bundled 库；SHA-256 为 `16048fa41430748861a4d6a7ce18081988d75dacf807a6d2a0eddedbead242e5`。

主体已删除 SyntheticToml 及旧 Surface，LlmFacing 只保留 F# 类型适配；文件效果的业务字段由 JsToolsResult 组织，ReturnedMaterial 的存储协议未改。本次只验证这项表示迁移，不把两个未完成的全调用路径 TODO 或未执行的全量回归计作通过。提交保存在本地，尚未推送远端；跨机器取得该候选仍须先交付库提交。
