# host-boundary 测试说明

[WHAT](../WHAT.md) 是规则来源。当前测试中的模拟端口、参数与辅助模块是证明设施，不是另一份产品合同。

## 三类证据

1. 信号解码、身份唯一性、类型化错误、快照决策、顺序转换和订阅释放的用例驱动生产 Surface，提供具体合同证据。
2. 026/027/029 等用例检查编译依赖与能力分界；部分源码/项目名称锁定仍待进一步整理，不能用它们代替运行行为或真实 Host 能力。
3. 真实 Host canary 需要实际启动受支持 Host，通过公开 Hook/SDK 完成场景。019 的受控端口只证明生产 transform 的条件分支，不能代替该物理证据；全能力覆盖仍是 TODO。

2026-09-26旧基线曾执行023的独立真实Host canary。该历史结果不作为当前上游基线验收；它也不等于完整项目插件的 Engineer 会话、只读工具和取消现场巡检。

## 本轮调整

- 015 增强工具注册到输出的完整断言，验证多字节文本、完整尾行、超大单行 Unicode 后缀、字节上限及重复限界稳定性。固定预算数值是当前兼容对象的设施配置，测试不替代真实 Host 版本能力核验。
- 019 清退已删除 MagicTodo 协议及源码词形证明。快照定位的有效案例归012；实际 transform 分支通过只转换参数、记录端口调用的生产接缝验证，不再从JS拼装Fable内部对象。Replica真实物理路线仍须019/Long Stroke证据，未用手填runtime Map充数。
- 026 新增真实 Fable 编译诊断分片的用例，先暴露缺少 failure decision 依赖的错误，再补齐该引用。全量构建恰好包含类型定义，并不能证明声明的局部依赖完整。
- 029 由真实子进程调用终止/诊断入口。报告器抛错时，测试安装未捕获异常观察器区分“异常退出”和“确实触发终止”，不能把任意非零退出当作硬退出。持久事实由同一个随后被终止的子进程写入，父进程重开核验；不再用父进程预先写好的文件冒充子进程崩溃证据。
- 029 原来的正常拒绝、stale callback、耗尽模式只打印成功后退出，完全没调用业务路径。已删除这些假证明并保留 TODO；相应产品义务没有删除。真实流程的正常退出证据仍需补齐。
- 032 的 C15 精确检查评审字段首/中/尾的原始键序；C44 经注册 hook 检查只读估计字段与评审字段交错时的完整值、对象身份、原键序、重复 after 及跨 session 同 callID 隔离。C45 检查无法恢复键序的参数在隐藏前原子拒绝，并允许位于协议字段之前的不可配置业务键；C46 触发实际 before 隐藏失败，要求已经隐藏的评审字段同源恢复。C47 在已删除估计字段后让短记字段的首次删除抛出异常，要求两个协议族全部恢复且重抛同一 Error；这是可恢复的一次删除故障，不宣称任意 Proxy 都能回滚。C11 只证明携带已报告错误的 after 回调，C12 只证明正常 after 返回；不再用测试自抛再手动 after 冒充真实执行异常传播。安装版 Host 执行器抛错后自动恢复仍单列 TODO，不以私有 Symbol 名或固定 schema 装饰格式代替合同。
- 032 的 C48–C52 检查 configured 状态下重复 before 的边界：仅相同参数对象及完整 session/call/tool 身份可沿用已隐藏参数；不同身份、不同对象、缺失身份及协议字段重现都不豁免校验，合法的数值 0 重现也须拒绝。非评审工具自有的 contract 保持原对象；评审工具重加 contract 不能冒充重复回调。私有暂存所有权仅服务参数恢复，不授予业务准入。
- 032 的 C53 同时启动两个共用参数对象的不同调用，要求仅一个取得暂存所有权；败方不得恢复胜方仍在使用的参数。获胜调用的重复 before 与最终 after 继续验证同源恢复，不靠定时等待或重跑安排交错。
- 032 的真实 canary 按物理目标隔离正常、业务错误返回和取消三条路径，仍在同一个真实 Host 生命周期内执行。正常工具的自然 provider followup 检查 schema 稳定及原参数历史；各路径的 before 观察同时核对 session 与 SDK tool part 的 assistant.parentID，不能用 retry/Guard 的同名 call 冒领。完成屏障读取本轮物理消息的 assistant 完成与 Host 当前状态，不把 collector 到达序号当因果顺序。provider 按当前 prompt 和 call 历史选择响应，不以请求总数推进；正式反例覆盖旧完成、busy/retry、错误终态及额外请求串线。取消仍须先观察真实 running，再外部 abort 并核对后续 wire 原值与键序，25 秒预算保持不变。隔离过程中另外发现的自动 Manager 接续与新用户输入交错失败登记为 GAP-223，后由033独立修复和证明；参数 canary 本身不能关闭它。
- 032 的 C54/C55（评审工具在 Predictor 未配置与已配置两种状态下的参数所有权，以及协议暂存不得读取工具自有字段）随 Predictor 槽位一并退役；这些用例当时取得的修复前失败证据见下条记录。
- C52 已在候选编译产物中复现评审字段重现后恢复到错误键位（`/tmp/vibe-fs-argument-owner-batch3.log`，4 通过、1 失败）；C53 已复现两个不同调用同时取得同一参数对象（`/tmp/vibe-fs-argument-owner-race-red.log`，两个 before 均成功）。这些日志保留为修复前的失败证据。
- C54/C55 的六个独立用例已取得修复前失败证据（`/tmp/vibe-fs-argument-after-business-red.log`，0 通过、6 失败）：三个 after 所有权反例与三个非协议业务 getter 读取反例分别失败。最终候选已完成 Fable 822 模块编译链接，新增 Host 回归 16 通过、0 失败（`/tmp/vibe-fs-host-final-regressions.log`），覆盖共同调用身份、参数恢复和按字段所有权读取；该结果不替代安装版 Host 执行异常后自动调用 after 的待证义务。
- 032真实canary不再把缺失SDK观察默认为成功，不伪造版本、完成状态和取消结论。观察器保存原始参数副本及键序，实际after验证恢复；公开事件唤醒完整SDK读取后确认终态和持久输入。目标版本fixture只声明预期环境，不是通过记录。归档证明分列两轮：103 轮五文件验证共 103 通过、0 失败、5 TODO（`/tmp/vibe-fs-resume-focused-integration.log`），其中 host-boundary-032 的 integrationTest 为 skip（integration tier not enabled）；032 真实 canary 的通过发生在 364 轮完整 integration。这证明已执行的正常恢复场景，安装版 Host 执行器抛错后自动调用 after 仍单列待证。
- 003 调用实际注册 hook：coarse abort 保留当前 execution，当前 exact cancelled assistant 才结算自己的 Cancelled；033 另证迟到旧 receipt 不动新 Human，以及后来的输入可替代旧 pending demand 而不发出 Host abort。
- 033 的 HumanMessage/BusyAgentNudge 回归先接纳两份追加材料，验证旧请求的只读 params 仍合法；实际选择新材料的 transform 才交接单枚 token，较早材料精确终结，迟到旧事件不释放新 token。delegation 027 的 GUIDANCE/GUIDANCE_STOP canary 启动真实 Manager/Engineer，用户输入在 Host 保存后释放 Join，由 Manager 调用 resume；child 原输出以 tool-calls 或 stop 自然完成，实际下一次请求包含指导，冷读 canonical journal 仍只有原 Root 的一份 work，最终 Join 消费为 Retired。辅助会话自身的正常收束不混同为 Manager/child 中断。
- 033 的安装版 OpenCode 1.18.29 canary 改为用户输入的非中断合同：分别挂住实际 SSE 输出与生产 `todowrite-for-manager` 工具体，在新 Human 抵达后确认没有 SDK abort、没有提前发起新 provider 请求；释放后旧输出完整、工具 completed，下一次实际 provider wire 包含新输入及旧输出/工具结果，回答 parent 匹配新 Human。默认容量以准入完成为屏障，Manager 单槽容量以物理输入抵达为屏障，覆盖 Host 保存窗口；已接受材料保留原 credit，不要求第二枚 token。各执行两种场景，独立 Host、工作区和回环服务，最终检查物理清理。原“新输入必须排空 Guard”的 canary 已删除；历史记录不再定义当前产品语义。Join 的独立唤醒和 child 保留由 delegation 015/027 验证。

03 对内部测试入口的限制与本包 029 的物理终止证明方式仍需统一裁决。现有物理 fixture 保留，不新增扫描豁免。

## 运行与限制

先执行 `node scripts/build.mjs`，再运行本包正式用例：

```sh
TESTS_MJS_FILES="$(rg --files requirements/host-boundary/tests | rg '/[0-9]{3}\.test\.mjs$' | sort | paste -sd, -)" node requirements/verification-system/tests/run.mjs
```

019/029/032 的 TODO 阻断完整验收。013旧基线曾出现断言后不退出，未取得本基线闭合证据。023、032与033需要真实Host及本机端口权限；026真实编译用例需开启integration。2026-09-28 的历史构建为 822 模块、harness 为 278 通过，范围见[当时记录](../../../proposals/archive/2026-10-03/20模块上游适配记录-2026-09-28.md)；本批新增证据与正式结果以[Host就绪与Guard替代修复记录](../../../proposals/archive/2026-10-03/Host就绪与Guard替代修复-2026-10-03.md)为准。各轮数字不能相加，也不消除上述 TODO；残余跨包案例和物理证明缺口见[GAP](../../GAP.md)。
