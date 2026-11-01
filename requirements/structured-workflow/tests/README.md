# structured-workflow 测试说明

[WHAT](../WHAT.md) 定义控制流、知识边界与编译要求。本文件只说明设施与缺口。

- 003–009：保留部分领域样本与源码扫描；这不是「任意流程都没有程序计数器」的说明。003/006 对多轴状态的范围仍有文字冲突，005 的注释分类与部分词形检查仍需语义审阅。
- 原 001、007、008 的入口名/前缀/本地常量清单检查与 017 的恒真占位已删除。002 的时钟扫描归 time-capability-004。
- 004：真实控制结构扫描器的正反例；教程长度与嵌套结构能否被拒无关。
- 010：并发顺序、上限、错误与预取消样本，另有「排队期间取消、获许可后不能开始」的受控 Promise 屏障反例。部分样本仍用墙钟安排完成顺序。
- 011/012/013/014/016：源清单、依赖图、签名与编译计划；受控编译器调用与真实 Fable 检查分开计数。012 的 integration 用例默认跳过。012 另有真实回归：同一输出目录先编译 Core 再局部编译 Alpha，必须真的产生 Alpha.js 且不编译 Beta，抓到过 Fable 复用旧 project cache 的问题；构建报告成功或目录里已有某个 JS 文件都不算当前闭包已生成。012 还守护 PluginSessionScope.fs 的 `.fs` 改动会选中含 Context/Companion 的 Host.fsi 与 Host.fs，且类型定义先于消费者。
- 015：验证生成产物辅助设施；其旧 locality/authority 模型与现行单一 subsystem 权威仍需核对。
- 018：原表格与守恒验证器只在测试内运行，已移除，保留明确 TODO。
- 019：真实顺序 traversal 的顺序、基数、Error 短路、空输入、异常与取消传播。

原 004/006 等文件混有 Host 或退休领域的业务案例；`Retirement.Surface` 自写决策与 freeze 未接入生产，相应断言已改为 TODO，保留真实工具角色门禁与其他有效覆盖。

## 运行

先 `node scripts/build.mjs`，再经正式运行器选本包存在的 `NNN.test.mjs`。018 的 TODO 阻断完整验收；真实 Fable 边界检查在 012，开 integration 后按用例执行，只用 Fable，不用 `dotnet build`。

```sh
TESTS_MJS_FILES="$(rg --files requirements/structured-workflow/tests | rg '/[0-9]{3}\.test\.mjs$' | sort | paste -sd, -)" node requirements/verification-system/tests/run.mjs
```

编译 fixture 是被测输入，不是另一套测试语言。
