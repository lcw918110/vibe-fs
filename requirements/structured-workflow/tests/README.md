# structured-workflow 测试说明

[WHAT](../WHAT.md) 定义控制流、知识边界及编译要求。这里说明当前证明设施和缺口，不把源码模式、文件名或教程长度补成规范。

## 证据范围

- 003—009 保留了部分领域样本和源码扫描；这些并非任意流程都没有程序计数器的证明。003/006 对多轴状态的范围仍有文字冲突，005 的注释分类和部分词形检查也还需要语义审阅，见 GAP。
- 原 001、007、008 的入口名称/前缀/本地常量清单检查及 017 的恒真占位已删除。002 的时钟扫描证明归 time-capability-004；其中“旧模块名消失”和“某函数可调用”不证明不存在第二运行时，已删除。
- 004 保留真实控制结构扫描器的正反例，移除教程必须至少 512 行、9302 字符及固定示例词的要求。检查器能否拒绝嵌套结构，与教程长短无关。
- 010 只保留 Reconcile scheduler 的突发合并、停止排空和停止后拒绝三项用例。通用映射的输入顺序、并发上限、错误和取消用例迁到 `wanxiangli/src/bounded-map/tests/`，通过公开的 `wanxiangli/bounded-map` 与原生 `AbortSignal` 验证；主体不再维护 ParallelSurface／TokenHandle 测试入口。
- 011/012/013/014/016 的源清单、依赖图、签名与编译计划证据继续有效。受控编译器调用和真实 Fable 检查分开计数；没有启动编译器的计划测试，不证明编译一定成功。012 的 integration 用例默认跳过。
- 16 站补 012 的真实回归：同一输出目录先编译 Core，再局部编译 Alpha，必须真正产生 Alpha.js 且不编译无关 Beta。它抓到 Fable 在共享输出中复用旧 project cache 的问题；构建报告成功、目录已有某个 JS 文件都不是当前闭包已生成的证据。见 GAP-087 的修复记录。
- 012 新增默认层生产图回归守护（2026-10-04，W1 第三批 build-closure 卡）：planImpactCompile 对 PluginSessionScope.fs 的 .fs-only 改动，断言选中闭包含 Context/Companion 的 Host.fsi 与 Host.fs 且类型定义先于消费者（CompanionHost 回归）。该守护只覆盖 PluginSessionScope 一条边，不宣称 shard 图整体完整——系统性缺边排查另立构建待办（见分册02 文件头）。DevOps 真实跑绿（012 默认层 41 条 0 fail）。
- 015 验证生成产物辅助设施，但其旧 locality/authority 模型与现行单一 subsystem 权威之间仍需进一步核对；不能将辅助设施测试自动解释为现行发布门禁。
- 018 原有表格只在测试内定义，proof 路径已失效，守恒验证器也只在测试内运行。已移除这种自证，保留明确 TODO；生产证明义务仍然有效，没有凭空新建注册系统。
- 019 验证真实顺序 traversal 的顺序、基数、Error 短路、空输入、异常与取消传播。

原 004/006 等文件还混有 Host 或退休领域的业务案例。54 复查发现 Retirement.Surface 自写决策与 freeze，未接入生产；003/006 中相应断言已改为明确 TODO，保留真实工具角色门禁及其它有效覆盖。未冒充已完成全部跨包归属审计。后续迁移需核对目标命题、运行入口和原覆盖，不能为了编号整齐批量改标签。

## 运行

在仓库根目录执行 `node scripts/build.mjs`，然后通过正式运行器选择本包存在的 `NNN.test.mjs`。018 的 TODO 明确阻断完整验收。真实 Fable 边界检查在 012，开启 integration 后按用例选择执行；只使用 Fable，不使用 `dotnet build`。

```sh
TESTS_MJS_FILES="$(rg --files requirements/structured-workflow/tests | rg '/[0-9]{3}\.test\.mjs$' | sort | paste -sd, -)" node requirements/verification-system/tests/run.mjs
```

[本批记录](../../../proposals/archive/2026-10-03/35模块PR施工记录-2026-09-28.md) 保存新基线验证范围；[GAP](../../GAP.md) 保存未闭合的证明边界。编译 fixture 是被测输入，不是另一套测试语言。
