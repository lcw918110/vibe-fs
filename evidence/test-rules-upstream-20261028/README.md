# 测试规则变基到 upstream

日期：2026-10-28。目标仓库：`https://github.com/PamelaSprin47685ghall/vibe-fs.git`。

目标基线：`upstream/master`，`27b5073c5b98c8256ac3b9560f31bdf0e72fdc35`。本轮先 fetch 后固定此提交，不把原优化分支作为新基线。

工作分支：`codex/test-rules-upstream-20261028`。原分支 `codex/test-optimization-20261024` 保留在 `a979a3f7707b30f5ac96d6f96c7f6a4b7ced8b45`，没有改写其已发布历史。

## 范围与冲突处理

使用 `git rebase --onto upstream/master fcae7e1d7` 重放测试规则迁移及两轮精炼；交互计划跳过旧业务测试归属修复。

| 原提交 | 变基后的提交 | 处理 |
| --- | --- | --- |
| `1c9a18cba` | `2be988649` | 保留测试专用 AXIOMS、规范读取支持与迁移回归。 |
| `a769e34d9` | `68b888814` | 保留前提、适用条件与验证范围的精炼。 |
| `fbed0b763` | 未重放 | 上游已调整这些业务测试；保留上游现行 delegation 和 repository-programming 内容。 |
| `a979a3f77` | `c4e11e499` | 保留 010 的“现行正式合同已确定的验收判据”以及其他范围修正。 |

后续适配只处理上游冲突、文档引用和本次验证记录：

- README、功能目录、人工巡检与功能导览保留上游重写内容；仅在现有开发入口补测试规则和附加 prompt 链接。
- 上游已删除 `proposals/规范施工Prompt.md`，不恢复它。保留本次新增的[测试效率附加段落](../../proposals/高效测试Prompt.md)，供现有测试 prompt 引用。
- 上游 018 新增的常驻编译器适配例外并入 AXIOMS；明确 `scripts/compiler/Checker.fs` 与 `scripts/compiler/toolchain.mjs` 可使用构建 API，仍不得增加扫描取证入口。
- 原定时器优化实现、运行器提效实现及业务测试改动均未带入。本次没有将此前约 32.1% 的完整 unit 收益转记为新上游收益。
- 资源回归与性能实验引用固定到原提交；008 标明这些实现未随规则变基移植，当前上游路径仍须单独验证。历史审阅记录加版本说明，已删除的指南和旧库规则改用固定提交链接。
- 上游规范检查器已有的过渡文件与物理场景处理保留原样；本轮只增加指定规则文件的发现、完整性和唯一正文处理，没有新增跳过名单或放宽失败断言。

根 `AGENTS.md`、`wanxiangli` gitlink、产品源码、业务测试和测试运行器与目标 upstream 完全相同。依赖工作树检出到上游固定的 `359a1ec0a7f09c3381aada36f3362bfee417b656`，没有自行升级依赖。

上游已重新定位产品说明书及 SPEC。本次按用户明确要求保留已审阅的测试专用 AXIOM → LEMMA → SPEC；未改上游总体指南，也未将测试规则扩张为产品整体的唯一验收流程。此前基于旧版万象理规则作出的格式审阅，不作为符合新版总体体例的结论。

## 验证方法

本轮改动是 Markdown 与 JS 规范发现逻辑，直接使用 Node 26.6.0 运行对应测试，不启动 Fable 构建或真实宿主。

对照副本由目标提交的 `requirements/`、`scripts/`、`src/`、`resources/`、`package.json` 和 `package-lock.json` 导出；没有应用本次改动。两边使用同一 Node、现有依赖和现有 `dist/`，以区分代码变更影响与编译输出条件。这个共享 `dist/` 没有重建，不能作为新 upstream 的新鲜产物证明。

两边运行同一选集：

```sh
PATH=/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin \
/opt/homebrew/bin/node --test --test-concurrency=1 \
  requirements/requirement-system/tests/*.test.mjs \
  requirements/js-semantic-surface/tests/003.test.mjs \
  requirements/verification-system/tests/018.test.mjs
```

另执行本次新增的消融规则发现回归，未运行该文件中依赖编译产物的其他业务用例：

```sh
PATH=/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin \
/opt/homebrew/bin/node --test \
  --test-name-pattern='manifest discovery uses migrated package rules' \
  requirements/feature-ablation/tests/001.test.mjs
```

| 范围 | 未修改 upstream | 变基后 |
| --- | --- | --- |
| requirement-system 全部 9 个文件 | 14 passed / 2 failed | 17 passed / 2 failed |
| js-semantic-surface 003 | 6 passed / 1 failed | 7 passed / 1 failed |
| verification-system 018 | 3 passed / 0 failed | 3 passed / 0 failed |
| 上述合计 | 23 passed / 3 failed，共 26 | 27 passed / 3 failed，共 30 |
| 新增消融规则发现回归 | 尚无此用例 | 1 passed / 0 failed |

合并选集两边均退出 1；新增消融回归退出 0。没有取消、skip 或 TODO。运行日志分别为 [upstream-baseline.log](upstream-baseline.log)、[rebased-tests.log](rebased-tests.log)、[ablation-migration.log](ablation-migration.log)；仅去除前两份日志中共 12 个空白行上的空格，诊断和结果未改。不比较这些运行的耗时来宣称提效。

本次新增的 5 项迁移回归全部通过：唯一正文与旧文件重复定义、AXIOMS 缺失或类型错误、真实发现及禁止旧文件回退、语义入口与跨包条款读取、消融节点发现。原有失败没有被跳过或改成通过。

## 未通过项目及对照结果

三项失败的测试身份在两边相同：

1. `requirement-system-001`：`latency-degeneration` 目录未列入包索引。
2. `requirement-system-017`：完整树检查报告 22 项索引、文件名或测试条款归属问题，涉及 latency-degeneration、host-boundary、delegation、advisor、intra-participant-parallelism、provider-attempt-recovery 和 work。
3. `js-semantic-surface-003` 的实际注册清单：当前 `dist/` 缺少 `Context/Scope/Surface.js`、`Execution/Session/RecoveryClosureSurface.js`、`Repository/Programming/Js/Programs.js`。这是本地尚未构建新基线的限制，不据此判定产品或上游构建失败。

另分别调用两边原有检查函数，并用完整数组相等比较结果。`inspectRequirements` 的 22 项、`validateSurfaceManifest` 的 3 项及消融检查的 2 项全部相同，见 [findings-comparison.json](findings-comparison.json)。消融检查的两项均指向 latency-degeneration 缺少索引和主节点；迁移夹具通过不代表真实全树消融检查通过。

对照说明本次变基没有新增这些检查发现，不能说明上游的既有问题已经解决。本轮没有为绿色扩大跳过范围、补造条款或改动业务测试，也没有运行完整 unit、integration、发布流水线或新的性能实验。

## 内容与差异核对

测试规则仍有 4 条 AXIOM、7 条 LEMMA、20 条 SPEC；001–012、014–021 的 ID 不变，013 未复用。010 的限定与用户确认版本一致。正文的实质新增仅为上游 018 的既有构建例外；其他适配修改限于引用和历史结果的适用范围。

旧 WHAT 仍只有导航，规范发现不回退；附加 prompt 与 README 均指向 AXIOMS。正文、附加段落及三份审阅记录的 45 个本地链接和标题锚点可解析；前提引用有效，010 正文与用户确认版本逐字相同。不新增措辞扫描门禁。原优化分支与主工作树的用户文件均保留。
