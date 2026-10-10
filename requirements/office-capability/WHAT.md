# office-capability — WHAT

## [001] Office 由后果定义

Office 由其有权产生的后果（Entitled Consequence）及禁止的后果定义，不由 Persona 名称、工具可达性或权限清单定义。调用方按这些承诺与边界委托工作。

## [003] 同一 Office 的权能不变

同一 Office 的权能与权限集合一致，不随执行档位改变。Role、Persona 及名字解析由 participant-identity 定义，不另设 fast/deep 职位或组合身份。

当前证书、清理阻塞、已接纳评审和退任冻结等事实可按既有门禁收窄具体动作的准入，不扩大 Office 权能。

## [004] 权限是后果模型的投影

执行权限由 Office 后果模型投影，不能从工具白名单反向推导职责，或用工具清单替代后果定义。

## [005] 多处表达同一职责

同一 Entitled Consequence 在管理者认知、派工与续做契约、角色自述及调用方镜像中使用同一语义标识，含义一致；措辞可以随语境调整。

角色变化必须同步更新完整可分发工作链，包括共同法、角色与协作提示、工具及错误说明、案例与纪律提示、截断说明和模型配置。中英文共同表达当前职责，不能只改名字或在旧说明前追加新规则。

Engineer 的调查不含真实命令执行，即使只读；只读调研是任务约束，不是新角色。DevOps 可在既定需求内作工程判断，无需唯一机械解法或逐次批准。Manager 区分实现完成、执行与自修后的重新验证、自己的验收判断。

资源中不得保留非当前合法活跃角色的可加载提示词、工具建议、别名或模型池。普通浏览器测试及一般探究用语不因此违规；回归覆盖实际分发资源与调用接点，不止角色目录。

## [006] Office 不可互换

Engineer 不是真实命令执行器；DevOps 不代作架构或产品决定、不差遣其他代理；Manager 不直接实现源码，其直接调查限于 007 的评审取证；Sphinx 是程序工作流，不是通用代理。

## [007] Manager 的直接操作边界

Manager 不修改工作树、不执行命令、不使用 Fission；原生 read/grep/glob 始终拒绝。评审接纳前可通过专用只读工具 `js-manager` 直接取证，接纳后关闭；该工具不提供副本语义，当前事实门禁见 capability-enforcement-025。

Manager 只能新建 Engineer，续做既有 Engineer 或调用固定 DevOps；续做不得改变已有身份、绑定、控制权或创建替代 DevOps，具体语义见 delegation-003/024。

## [011] Manager 的管理权能

Manager 在任何活跃任期阶段，包括评审前与接责后，均具备完整的委托、续做、汇合、视界、记账、评审与完成权能。拥有权能不代表已承担具体任务。直接操作禁限见 007。

Review 接纳前不得向固定 DevOps 派工，已有只读 Engineer 的合法 resume 不受影响；具体准入见 capability-enforcement-026。

## [012] Orchestrator

Orchestrator 只委任或接续顶层道路的 Manager，不直接委任其他 Office，不介入具体执行，不使用 Fission。`commission` 的 `calling` 可选，省略时推导为 Manager persona；显式 `calling` 与推导不一致须 typed 拒绝。

## [015] Predictor

Predictor 只为 Strength 降级选择廉价 provider/model，不进入普通调度、Manager 的公开 fork 候选、工具门禁或用户可见接口。

## [016] Engineer

Engineer 负责本地事实调查与源码工作，包括仓库文件的读、建、改、移、删，以及实现、重构和测试源码。它不执行真实命令、不调用或差遣 DevOps、不承担外部网络浏览；工作完成或遇到需 Manager 决定的边界即返回。

Engineer 是唯一具有 Fission 角色能力的 Office；Fission 是同一 Engineer 的多个执行分支，必须收敛为一次返回。

## [017] DevOps

DevOps 具有全部本地工程能力以及真实命令、终端和进程管理能力。执行中遇到非架构级缺陷或测试失败，应自行调查、修改源码、补回归并重新验证；该修复权是角色固有权能，不受逐次授权或 `allowRepair` 开关控制。

DevOps 不发明架构、产品含义、兼容性或安全政策，不削弱断言或绕过门禁，不 fork/resume 其他代理，不使用 Fission。到达架构或产品边界时交回 Manager。

## [018] Sphinx

Sphinx 以独立 MCP stdio 服务存在（`Sphinx/V2/ServeEntry` 与 `Hosts/Mcp` 七件套），插件不提供 `sphinx` 原生工具面或对应 capability。程序内部的标准 Engineer 调用遵循 delegation 与 capability-enforcement 的现有资格、authority 检查，不另设只读 profile，不增加真实执行或 DevOps 调度权。

Sphinx 不是 Role、Persona 或普通 subagent，不拥有独立 Fission 身份、不增加 session 层级，不设 Inquiry 或其他中间模型驾驶层。
