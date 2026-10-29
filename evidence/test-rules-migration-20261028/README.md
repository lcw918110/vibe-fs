# 测试规则迁移记录

本文件及所附日志记录原优化分支的迁移过程，结果不适用于变基后的上游代码。当前变基范围与验证见[上游适配记录](../test-rules-upstream-20261028/README.md)。

日期：2026-10-28。基线提交：`fcae7e1d7`。工作分支：`codex/test-optimization-20261024`。

## 目标与完成范围

将仍在当前验证设施中使用的测试规则改写为 AXIOM → LEMMA → SPEC，并让现有 prompt 和规范读取代码共同使用新正文。根目录 `AGENTS.md` 未修改，`wanxiangli` gitlink 与源码未修改，主仓生产行为及测试运行层级未改动。

新的唯一正文是 [AXIOMS.md](../../requirements/verification-system/AXIOMS.md)。4 条 AXIOM 解释关切与理由，7 条 LEMMA 写前提、推理和适用条件，20 条 SPEC 承接原条款。原有 001–012、014–021 编号及旧正文中的有效义务全部保留；013 未复用。OpenCode 连续场景、日志观察器、主仓验证层级和扫描禁令分别注明当前适用边界，不据此声称旧宿主设施已迁入库。

资源所有权、结束等待与结束工作、真实释放与截止保护、不可变准备复用、环境与用例身份核对、排空与回收计时、无效轮次处理，均已合入新规则。此前完整 unit 节省约 32.1% 的实验仍只作为有边界的证据，不是以后任务的提速承诺。

## 入口与权威

- [旧 WHAT](../../requirements/verification-system/WHAT.md) 只保留导航，无条款定义。旧 `WHAT[verification-system-NNN]` 测试标题定位新 SPEC，不再决定读取旧文件。
- [高效测试附加段落](../../proposals/高效测试Prompt.md)与[当时的规范施工 prompt](https://github.com/dyx13/vibe-fs/blob/a979a3f7707b30f5ac96d6f96c7f6a4b7ced8b45/proposals/规范施工Prompt.md)均要求开始测试工作前阅读新规则。README、规范索引、人工巡检及测试说明同步改为新入口。
- requirement-system [005]、[006]、[007]、[008] 明确该包的唯一正文位置。其它未迁移包继续读取自己的 WHAT；被测业务的行为合同没有被测试规则取代。
- `scripts/lib/spec-rules.mjs` 集中声明已迁移包的文件名，并只从 AXIOMS 的 SPEC 部分取得正式条款。规范完整性、实际全树扫描、唯一归属、语义接口清单和消融清单发现均使用这一选择。不存在“新文件缺失则继续读旧 WHAT”的路径。
- 构建命令和测试运行器执行代码，不会解释 Markdown。规范检查能验证发现与引用，不能自动证明一条自然语言规则已经被全部实现。文档迁移与实现验收分别记账。

## 验证

运行器为 Node 26.6.0（`/opt/homebrew/bin/node`），没有构建 Fable、运行完整 unit 或启动真实宿主场景。

| 范围 | 修改前 | 修改后 |
| --- | --- | --- |
| requirement-system 全部顶级测试 | 15 passed / 1 failed，共 16 | 18 passed / 1 failed，共 19 |
| js-semantic-surface/003 | 7 passed / 0 failed | 8 passed / 0 failed |
| feature-ablation/001 的新增迁移发现回归 | 未有该用例 | 1 passed / 0 failed；其余用例未在本次运行 |
| 当前实际 ablation-manifest 门禁 | 通过 | 通过 |

新增的前四项迁移回归先在旧读取代码上运行，0 passed / 4 failed，失败原因分别为拒绝 AXIOMS、仍要求 WHAT、未发现新 SPEC 和接口所有者仍读旧文件。修改实际读取代码后，这四项及新增消融发现回归均通过。另覆盖新文件类型错误、旧文件重新定义条款、SPEC 外的伪定义、重复编号、删除新条款后遗留测试及跨包 law owner；既有正常控制与旧格式包仍通过。

首次修改后运行曾有一项诊断文本断言不匹配：提示从 `owner WHAT is missing` 改为 `owner rule document is missing`。已改为完整错误数组比较，保留所有者和实际文件路径，未放宽行为断言。

最终合并运行的 requirement-system 与 js-semantic-surface 结果为 26 passed / 1 failed，共 27；完整输出见 [focused-tests.log](focused-tests.log)，日志只去除了两处空白行的尾随空格。单独的消融迁移回归为 1 passed。全树扫描的唯一失败在前后均由下面两项既有问题构成，身份和内容一致：

1. `requirements/delegation/tests/034.test.mjs` 对应的条款 034 不存在。
2. `requirements/repository-programming/tests/053.test.mjs:41` 使用 `WHAT[repository-programming-006]`，与文件主归属 053 不一致。

没有改写或跳过这个全树失败，也没有修理范围外的业务测试。本次不能报告全仓规范巡检全绿。

静态核对另确认：20 个旧条款 ID 和全部旧正文义务进入新文件；旧 WHAT 无正式条款；新规则与附加段落的本地链接可解析；根 AGENTS 与基线逐字相同；`git diff --check` 通过。

## 之后如何生效

这条独立分支上的现有测试 prompt 已指向新规则，规范读取代码也已切换。主分支和其他工作树需在合并或取得此提交后才具备同样入口。按新 prompt 工作的 agent 应以 AXIOMS 为测试规则依据，并结合被测模块的现行合同；仅仅启动旧分支上的构建命令不会自动获得本次迁移。

## 二次精炼：2026-10-28

本轮基线为 `1c9a18cba`。对照当时依赖版本 `3bcb47be43c4ba1225c5a1928e2f7b974e9aad74` 的[规则写法](https://github.com/dyx13/wanxiangli/blob/3bcb47be43c4ba1225c5a1928e2f7b974e9aad74/AGENTS.md)、[构建与交付规则](https://github.com/dyx13/wanxiangli/blob/3bcb47be43c4ba1225c5a1928e2f7b974e9aad74/scripts/AGENTS.md)、[时间规则](https://github.com/dyx13/wanxiangli/blob/3bcb47be43c4ba1225c5a1928e2f7b974e9aad74/src/time/AGENTS.md)和[等待规则](https://github.com/dyx13/wanxiangli/blob/3bcb47be43c4ba1225c5a1928e2f7b974e9aad74/src/wait/AGENTS.md)，并以主仓 [requirement-system](../../requirements/requirement-system/WHAT.md) 的规范文件与条款编号约定为准。

人工核对与修改：

| 核对项 | 调整 |
| --- | --- |
| AXIOM 表达关切而非实现习惯 | 标明使用者、开发者及理由；将同等验证的等待、计算和维护成本说清。 |
| LEMMA 有前提、推理和适用条件 | 保留 7 条推导，逐项核对引用；操作要求集中到 SPEC，减少重复。 |
| SPEC 有验收与证据边界 | 20 条各自就近列证明入口或审阅方法，缺口显式标记；不把测试文件存在写成已经通过。 |
| 技术选择不冒充公理的必然结论 | 明示 OpenCode、Journal Observer、宿主入口、扫描禁令及输出协议的适用范围或项目选择。 |
| 指令易于执行 | 区分日常全量入口与普通改动的聚焦验证；区分可共享的不可变准备与须隔离的可写目录。 |
| 优化结论可复核 | 对照记录代码版本，固定目标改动之外的条件；保留真实释放、在途保护、排空计时和无效轮次处理要求。具体提速数字保留在实验记录，不放入长期规则正文。 |
| 证明入口对应实际职责 | 层级选择指向实际调度与报告回归；资源释放指向调用结束及挂起恢复回归。断言充分性、测试边界和准备隔离保留人工审阅责任，不另建措辞扫描。 |

4 条 AXIOM、7 条 LEMMA 与 20 个 SPEC 编号保持不变，前提引用均指向已有条目，无循环推导。继续使用 `## SPEC` 与 `### [NNN]`，符合主仓解析约定；不为模仿库侧排版改动稳定 ID。原有验收义务逐项保留，未改运行器、生产代码或测试断言。根 `AGENTS.md`、依赖和两个现有 prompt 均未修改；prompt 继续读取同一 AXIOMS 路径。

验证使用 Node 26.6.0，重跑 requirement-system 全部顶级测试：19 项，18 passed / 1 failed，0 skip / 0 TODO。原始输出见 [refine-requirements.log](refine-requirements.log)，仅去除两处空白行的尾随空格。全树失败仍只包含上文两项既有问题；另直接调用真实扫描器比较完整问题列表，未新增或隐藏问题。20 个条款 ID 与基线一致，AXIOMS 中 30 个本地链接及其标题锚点可解析，`git diff --check` 通过。没有重跑 Fable 构建、完整 unit、integration 或真实发布；本轮不更新业务测试通过状态或性能结论。

原独立分支随后修复了这两处归属问题，详见[当时的归属修复记录](https://github.com/dyx13/vibe-fs/blob/fbed0b763fd7691bcb9b2e833e84141ae26dabf3/evidence/spec-test-ownership-20261028/README.md)。当时未改规范检查器，正式规范套件为 19 passed / 0 failed；本记录保留各次运行当时的结果，不作为变基后上游的测试结果。

随后对照迁移前 WHAT 再次复核适用范围，修正 010 的合同限定及其他过宽措辞，详见[测试规则适用范围复核](../test-rules-scope-review-20261028/README.md)。记录包括 20 条逐项比较、合法替代场景的推理，以及修订后的规范套件结果。
