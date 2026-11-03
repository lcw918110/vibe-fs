# distribution 测试说明

先构建，再经正式 runner 运行编号文件。安装包检查走 `node scripts/verify-package.mjs`；完整发布走 `npm run verify:release`，前者通过不代替后者。

## 候选一致性与检查范围

本节说明当前检查能力；运行结果放对应工作包或 PR，绑定候选和环境。

- `011.test.mjs` 与发行入口共用 `checkCandidateDependency`：核对 index 中的 gitlink、库实际 HEAD、干净源码、声明、锁版本和安装链接。`WANXIANGLI_EXPECTED_SHA` 接收本次预先选定的完整库 SHA；设置后还须与此目标一致。缺省时只证明当前输入一致，不能声称接入了本次承诺的候选。依赖 pin 须先暂存，测试才会把它作为本次待提交输入。
- `scripts/verify-package.mjs` 在打包前后核对同一候选，检查真实归档成员与来源产物的字节闭包，并以隔离消费者加载 bundle。消费者删除顶层另装的库，再经主体 `LlmFacing` 生成指令与数据，覆盖 `llm-text` 的实际消费。返回结果的 `libraryCandidate` 是此次检查的源码 SHA。
- 011 的隔离 Git 夹具覆盖同版本不同 SHA、未暂存 gitlink、同 HEAD 修改源码／增加未跟踪文件、锁或安装错位及错误目标格式。库的忽略产物由既有构建新鲜度与归档逐文件摘要检查承接；源码身份不能替代产物检查。将完整目标写入 `WANXIANGLI_EXPECTED_SHA` 后运行 011 和实际 pack 入口，才把两项检查绑定到本次交付。
- `012.test.mjs` 检查所扫描资源目录的语言与占位符；不存在的资源树目前会被跳过，全部缺失的必需目录也不能仅靠扫描发现。它不能单独证明全部必需资源齐备或宿主启动前已检查。

## 其他检查

- 001/003/004/008/010：检查当前仓库的产物、manifest、资源与注册投影，不是假设已安装。003 原先复制目录到临时位置并传 `mock.tgz`，已撤下「真实包」的说法；独立消费者交给实际的 pack 入口。
- 002：在新进程和外部 CWD 读取真实资源、导入仓库产物，说明 CWD 独立；不是独立安装。
- 006：读取器缺失时抛错、资源存在时通过。源码字符串扫描不能说明 I/O 已收口。
- 007：受控步骤记录只说明发布调度顺序；真实合规与破坏归档由同一个归档校验器处理，覆盖缺项、多项、digest 错与链接。合成归档不代替真实 `npm pack`、解包与消费者。
- 009：真实归档流与 pack JSON 解析的结构正反例。

## 成员边界

[003] 的 `package.json` 是必需根成员；[004] 列出三类归档成员：payload（`files` 前缀）、包根元数据（必需 manifest 加可选 `README.md`/`LICENSE`）、显式 bundle（`wanxiangli`）；[007] 引用这条边界。`scripts/verify-package.mjs` 的 `ROOT_REQUIRED_FILES` / `ROOT_OPTIONAL_FILES` / `classifyMember` 实现它。003/007 的用例覆盖必需成员缺失、非合同根成员与禁用前缀（合成归档）。历史角色词汇可以解码，但不等于活跃注册；不用全代码禁词扫描代替 [010]。

## 未完成

- 完整发布证据仍缺：release 才追加的 package 步骤调 `scripts/verify-package.mjs`，其 `runExternalConsumer` 在临时 root 用空 npmrc 真跑 `npm ci --omit=dev --ignore-scripts --no-audit --no-fund`，把解包产物放进 `node_modules/wanxiangshu`，删掉顶层另装的 `wanxiangli`，再在只留 PATH/HOME 的环境裸 `import('wanxiangshu')`，断言默认导出与 bundled `wanxiangli` 版本。这条链只在 release 跑；日常 `format-build-test` 不覆盖，完整发布仍要在干净环境跑一次。
- 安装场景与 [010] 的覆盖仍是 TODO。

## 运行

先 `node scripts/build.mjs`；本目录用例经 `requirements/verification-system/tests/run.mjs` 的 `TESTS_MJS_FILES` 指定运行。归档与安装检查见 `node scripts/verify-package.mjs`。
