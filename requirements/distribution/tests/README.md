# 分发测试及证明范围

WHAT 定义合同。先构建，再通过正式 runner 运行编号文件。完整 artifact 验证入口是 `node scripts/verify-package.mjs`；完整发布入口是 `npm run verify:release`。前者通过也不能代替后者的全部测试/E2E。

## 候选一致性与检查范围

以下实现核对基于主仓 `01b04e055`，只读检查代码，未执行测试；它说明检查能力，不记录某个候选已经通过。运行结果只引用当前工作包或 PR 中绑定候选和环境的交付记录；下方带日期的记录仅说明当时范围。

- `011.test.mjs` 已检查 `file:wanxiangli`、锁文件的 link 与版本、安装链接的实际位置及 gitlink 条目形状，不再固定历史 tarball 版本。它尚未把工作包指定的目标 SHA、gitlink 的值与库实际 HEAD 三者相互比较；存在 gitlink 不等于消费了目标提交。
- `scripts/verify-package.mjs` 检查真实归档成员与来源产物的字节闭包，并以隔离消费者加载 bundle。它能发现交付内容与检查输入不同，但不能仅凭自洽的旧输入证明任务承诺的新候选已接入。
- **待实现：** 在现有 011 与发行检查中接入明确的目标候选，核对目标、锁定来源、实际安装、测试加载内容及最终 bundle；以来源 SHA 和产物摘要补足版本字符串。应覆盖「主体与测试一起停在旧候选」及「版本号相同、实际内容不同」的反例。复用现有检查入口，不另建平行交付流程。本次文档修订不表示这些检查已实现或运行。
- `012.test.mjs` 检查所扫描资源目录的语言与占位符；不存在的资源树目前会被跳过，全部缺失的必需目录也不能仅靠扫描发现。它不能单独证明全部必需资源齐备或宿主启动前已检查。

## 其他检查与历史记录

- 001/003/004/008/010 检查当前仓库的产物、manifest、资源或注册投影，不是假设已经安装。旧003把目录复制到临时位置并传入 mock.tgz，现已撤下这项“真实包”称谓；独立消费者由实际 pack 验证入口负责。
- 002 在新进程和外部 CWD 读取真实资源、导入仓库产物，证明 CWD 独立；不是独立安装。
- 005 保留 manifest 缺失/损坏/产物陈旧反例和真实清理。full build暂存旧dist，在复制输出至manifest提交之间真实进程exit23后，恢复必须保旧完整字节与manifest；目标非空不足以证明已提交。七类不可用manifest/不完整output identity拒绝与合法提交正控见[24c合并修复](../../../proposals/archive/2026-10-06/Upstream增量与施工续接-2026-10-06.md)。此有限恢复证据不证明clean/incremental字节一致，目录相等不证明loader没有其他fallback。
- 006 实际读取器缺失抛错及存在资源正例。源码字符串扫描不能证明全部 I/O 已收口，改为明确待证。
- 007 的受控步骤记录只证明发布调度顺序；真实合规/破坏归档由同一个归档校验器处理，覆盖缺项、多项、digest错及链接。真实 npm pack/解包/消费者不能由这组合成归档替代。
- 009 保留真实归档流与 pack JSON 解析的结构正反例。

GAP-210记录尚未闭合的完整发布证据。GAP-211（2026-10-18 CLOSED）已把原[007]“成员仅限dist/resources”与[003]必需manifest及npm根文件之间的文字冲突写成合同本身：[003]的package.json为必需根成员，[004]显式列出三类归档成员（payload=files前缀、包根元数据=必需manifest＋可选README.md/LICENSE、显式bundle=wanxiangli），[007]引用该边界；checker的ROOT_REQUIRED_FILES/ROOT_OPTIONAL_FILES/classifyMember只实现该合同。003/007的unit oracle覆盖必需成员缺失、非合同根成员与禁用前缀出现（合成归档）。历史角色词汇允许用于解码，不等于活跃注册；不使用扫描所有代码禁词作为[010]证明。

2026-09-28 迁移至 upstream `1450f49d`：保留真实 role/tool 注册投影，撤下通过 module 路径猜角色的检查及其旧 Sphinx 特例。路径名称不能证明某模块属于废止角色，新的 sphinx-v2 也不能靠沿用旧路径豁免来处理。上游误提交的 `010.test.mjs:84-` 只是同一段断言的残片，既没有导入也不是可运行测试，已删除；正式 [010] 覆盖及安装场景 TODO 保留。

2026-10-18 GAP-210 裁决（只做静态核对与 `assertBuildFresh` 实测，未执行完整流水线）：隔离消费者是**实装**而非仅声明——release 才追加的 package 步骤调 `scripts/verify-package.mjs`（`scripts/verify.mjs:87-89`，release 构建 `--clean` 在 :49，e2e 在 :80），其 `runExternalConsumer`（`verify-package.mjs:577-772`）在临时 root 以空 user/global npmrc 真跑 `npm ci --omit=dev --ignore-scripts --no-audit --no-fund`（:641-642），把解包产物放进 `node_modules/wanxiangshu`（:658-661）、删除顶层另装的 `wanxiangli`（:675-677），再在仅 PATH/HOME 的环境（:750-757）裸 `import('wanxiangshu')` 断言默认导出 id/server、3 个资源文件非空与 bundled wanxiangli 版本等于锁（:704-748）；008 的真实 pack 闭包 integration 用例已经 `verify.mjs:69` → `verification-system/tests/integration/run.mjs:40-41` → 本目录子入口发现（`008.test.mjs:115-144`），接线与发布顺序各有 oracle（`verification-system/tests/009.test.mjs:219-223`、`007.test.mjs:38-83`）。未闭合的是：完整 release 在同一 generation 上的执行证据（最新 `verify-logs/latest` 只有 format-check.log；package 步骤前置 `assertBuildFresh`（`verify-package.mjs:819`）在陈旧树上直接失败——本轮实测当前树 `generated-input-stale`）；consumer 与 artifact 注册两个 release-only 阶段无红例（`runExternalConsumer`/`assertActiveRegistrations` 除 verify-package 自身 CLI 外无测试导入）；consumer 表面窄（不枚举声明闭包的全部资源，也不证明插件实际注册，见 `010.test.mjs:84` 的 todo）；单 OS/单 Node（CI ubuntu-latest＋Node 22，本地 Node 26）。真实 registry 发布不属于 WHAT[007] 义务，不计缺口。
