# verification-system 测试说明

本目录是测试设施的所在地，不是第二套规则。规则见 [AXIOMS.md](../AXIOMS.md)。

## 跑什么

| 场景 | 命令 |
|---|---|
| 单条用例 | `node --test requirements/<package>/tests/NNN.test.mjs`（先 `node scripts/build.mjs`） |
| 受改动牵连的若干包 | `node scripts/verify.mjs --suites <pkg,...>` |
| 提交前 | `npm run format-build-test`（format:check → check → build → unit → integration） |
| 发布 | `npm run verify:release`（另加 Long Stroke、真实包） |

`unit` 跑 `tests/run.mjs`，它按 `TESTS_MJS_FILES` 或完整发现集执行，并检查构建产物新鲜度。`integration` 跑 `tests/integration/run.mjs`，`e2e` 跑 `014.test.mjs`。

## 结果怎么读

- **pass**：断言通过。
- **fail**：断言失败，或门禁/设施异常。异常按失败处理，不按通过处理。
- **skip**：本轮未执行；不等于通过。
- **TODO**：证据缺口，使监督器非零退出；不等于通过。

一次真实红绿之外，不再为同一结论加派二次审查。

## 设施

- `tests/run.mjs`：unit 运行器；`support/verdict-transport.mjs` 在用例之间让出一次调度，避免原生 reporter 判决被上一用例卡住。
- `tests/support/watchdog-harness.mjs`：挂死监测，按距最近真实进展的静默时长判断（[006](../AXIOMS.md)）。
- `tests/integration/harness/`：隔离辅助进程与协议替身，用于超时、就绪、回收等物理协议；不是多个真实端到端世界。
- `tests/e2e/`：Long Stroke 的真实宿主场景，只在发布口径运行。
- `scripts/lib/verification-*.mjs`：工具、SDK、NuGet、npm 与源码输入的准备与核对。

## 边界

- 虚拟时间证明不了真实进程退出、泄漏与监测接线；这些看 `integration/harness/`。
- 测试绿不等于产品做成；种子用户体验验收另走专门通道。
- 历史批次数字、GAP 编号与旧证明栏已不在活文档中，需要时翻 git。
