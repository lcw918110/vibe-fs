# distribution 测试说明

WHAT 定义合同。先构建，再经正式 runner 运行编号文件。完整的安装包检查走 `node scripts/verify-package.mjs`；完整发布走 `npm run verify:release`，前者通过不代替后者。

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
