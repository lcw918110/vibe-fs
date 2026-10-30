# distribution — WHAT

## [001] 单一交付物

安装产物同时包含生产编译代码 `dist/**` 和全部 runtime semantic resources `resources/**`，作为同一个自足 artifact 交付，不分开发送代码与资源。

## [002] 资源定位

资源路径以模块自身的绝对位置（import.meta.url）上溯包根，再按固定相对路径解析。不依赖 caller CWD，不探测候选路径，不回退到 src 或 dist 内备用资源。

## [003] 生产入口与必需根文件

package.json 的 main 与 exports["."] 均指向 artifact 内实际存在的 `./dist/OpenCode/Plugin/Plugin.js`，消费者按 manifest 可正常加载。package.json 本身是本 artifact 的必需根成员：它必须逐字节进入归档，缺失即发布不成立（来源树缺失同样立即失败，不得由校验器的便利放行代为决定）。

## [004] 内容白名单与归档成员边界

package.json 的 files 白名单固定为 `["dist/", "resources/"]`；这是本仓自身内容（payload）的唯一生产白名单：payload 即这两个前缀下的全部文件，不额外声明第二处内容清单。显式 `bundleDependencies` 可以把已经作为 npm dependency 安装的独立运行时库带入 artifact，但每个 bundle 必须逐文件进入发布闭包校验，不能借此允许任意 `node_modules`、源码工作树、测试或内部工具混入。当前唯一 bundle 是 `wanxiangli`。

归档成员边界由本条显式规定，恰好三类，此外一律拒绝：

1. payload：files 白名单前缀 `dist/**` 与 `resources/**`。
2. 包根元数据：必需根文件 `package.json`（见 [003]），以及允许的可选根文件 `README.md` 与 `LICENSE`；可选者仅在来源树存在时携带，缺失不构成违约。它们的价值是消费者可见的许可与用法入口，因此允许与 payload 同级出现在包根。
3. 显式 bundle：`node_modules/<bundleDependencies 中声明的名字>/**`，当前即 `node_modules/wanxiangli/**`。

清单外的包根成员（例如 `CHANGELOG.md`、`SECURITY.md`、`.npmignore`）以及任何其它前缀（`src/`、`tests/`、`scripts/`、`requirements/`、`.fable-build/`、`.git/`、`.github/`、未声明 bundle 的 `node_modules/`、`*.log`）都不得进入归档，即使 npm 的自动收录策略会把它们放入。此三类清单即合同本身：校验器只实现上述边界，不得自行增设白名单、也不得把便利放行写成新的合同。

## [005] 同一份 Production Bytes

dist 是唯一编译产物。发布与日常使用同一条增量构建：只编译变更文件的闭包。全量编译被禁止，它掩盖影响集的错误。测试直接消费这些产物，并以 manifest 摘要拒绝陈旧构建；发布真实 pack 同一份产物，不在 dist 复制资源。

## [006] 资源读取与缺失

语义资源读取和 I/O 统一归资源基础设施。缺失立即抛出致命错误中断，不用内置 fallback 清单静默降级；规则库元数据由实际目录决定，不以 catalog.json 建立第二权威。

## [007] 发布证明

verify:release 覆盖构建、真实 npm pack、解包和消费者验证。校验归档成员只允许 [004] 规定的三类成员（payload、包根元数据、显式 bundle）的完整字节闭包：[003] 的必需根文件在来源树或归档中缺失、清单外根成员出现、禁用前缀出现都必须立即失败，逐成员字节与来源树一致。在非仓库目录删除另装的顶层 bundle 后，仍须按生产入口导入。dry-run 不构成打包证明。

## [008] 资源闭合

所有声明 runtime resources 的语义包，其所需目录与双语文件均须完整进入发布产物。本包只负责交付，不决定资源的业务语义。

## [009] 归档校验

解析归档流时校验成员结构、完整性及 npm pack 结果的结构与包名。成员须为包根内安全相对路径，拒绝绝对路径、`..`、符号链接等非普通文件及开发/测试/源码/内部脚本/构建元数据；`node_modules/**` 只有 package.json 显式声明的 bundle、且字节与安装的受控依赖完全一致时才允许。异常立即拒绝。

## [011] 配套库候选的可复现交付

当前实现中，`wanxiangshu` 经 `file:wanxiangli` 协议依赖 Git submodule 提供的源码，构建、测试与 pack 使用该源码版本；仓库内不跟踪任何 `wanxiangli` 归档（`scripts/wanxiangli-source.mjs check` 机械拒绝跟踪的 `.tgz`，`.gitignore` 声明 `*.tgz`）。源码 checkout 须初始化 submodule，`npm ci` 把 `node_modules/wanxiangli` 安装为指向源码目录的链接，package-lock 记录 `resolved: "wanxiangli"` 的 link 条目而非归档 integrity。最终 `wanxiangshu` tarball 把同一候选作为 bundled dependency 实体化进归档、自包含交付。删除消费者顶层另装的 `wanxiangli` 后，安装产物仍可真实加载 lifecycle。并列目录是已决定、尚待实施的目标；切换时须同步调整此处接线步骤与检查，不能把当前实现写成永久目录要求。

本次承诺接入的库候选由工作包或 PR 明确给出，不能只从当前 manifest 反推目标。主体集成负责人须核对该候选与依赖声明、锁定输入、实际安装解析位置、测试消费内容和最终 bundle 一致。当前源码消费方式须核对目标 SHA、主体记录的 gitlink 与实际库 checkout，并核对候选构建产物与交付闭包的摘要；版本字符串相同不足以证明内容相同。库维护者提供可取得的候选，验证入口负责发现不一致。未接入主体只报告库侧完成。

历史与兼容性用例可以固定其他版本，但须明确标明用途和范围，不能计入本次候选通过。不得用另一个版本的通过结果、源码回退或邻目录产物替代本次交付；依赖版本、锁、来源或打包方式改变，即影响本项及真实消费者检查。检查的现有覆盖与缺口见 [测试说明](tests/README.md#候选一致性与检查范围)，文字修订不表示机械检查已经实现。

## [010] 活跃注册一致

产物的代码和资源须与现行活跃角色一致，不含废止角色的注册代码、死资源或旧工具映射。todowrite-for-engineer、todowrite-for-devops、todowrite-for-bookkeeper 等现行工具在编译代码、surface manifest 和资源清单中保持完整一致，无孤立注册。

## [012] 语言完整性为发行检查，不进入每次读取

运行时读取只校验所选语言的文件与占位符替换；所选语言缺失即报出指明该文件/语言的资源缺失错误，绝不换成另一语言。发行完整性——每个声明的语言文件都存在、双语占位符集合一致——由发行/构建检查承担：`scripts/checks/language-parity-gate.mjs` 对 `resources/provider` 扫描，分发验收（`tests/012.test.mjs`）对所交付资源树进行完整性核验；缺项或占位符不一致必须指名具体文件或资源对，不得以笼统“资源不健康”替代。

读取失败、发行检查失败和启动失败是不同范围。若产品承诺启动前发现全部必需资源缺失，必需路径与语种由各语义 owner 提供，启动组合层执行检查，宿主启动测试覆盖缺失时的实际行为；不能把资源库创建成功或局部检查通过当作启动保证。目录扫描看不到未提供的必需目录，当前 012 还会跳过不存在的资源树；这些检查单独不足以证明该启动承诺，完整必需资源输入及启动接线未核实前不得宣称完成。
