# 万象通（wanxiangtong）职责与边界说明

本文档是万象通在宿主工作树内的落点说明。万象通作为主工作树外层子目录存在，暂不开独立仓库；负责承担所有宿主特定的翻译税与脏活，隔离宿主细节，保护中立核（万象理）与产品面。

---

## 1. 三层架构定位

根据宿主仓与子模块 `wanxiangli` 的总宪法与纠偏令，系统划分为清晰的三层结构：

```text
用户 / 操作员
    ↓ 唯一总流程（用户提出需求 -> 了解需求 -> 办完做成 -> 结束）
本仓产品面（wanxiangshu：提示词、工具资源、交互编排，逐步收敛至变薄）
    ↓
万象通（wanxiangtong：宿主翻译税，外层子目录，暂不开独立仓）
    ↓ 仅通过公开契约消费
万象理（wanxiangli：中立核，Smalltalk / A-B-C 架构，状态有主人）
```

- **本仓产品面（wanxiangshu）**：面向具体产品的组装与流程，向 empty 收敛，最终不拥有核心业务状态。
- **万象通（wanxiangtong）**：负责面对具体宿主（OpenCode、omp、Cline 等），吃下所有宿主协议怪癖、生命周期钩子次序、消息双向编解码、工具注册与错误文案翻译。
- **万象理（wanxiangli）**：中立内核，遵循 Smalltalk / A-B-C 思想，对外暴露中立能力与状态边界；绝对不引入任何特定宿主的概念、类型或生命周期标记。

---

## 2. 职责与边界契约

### 2.1 分内职责（翻译税要狠，进万象通）
1. **宿主事件摄取与中立翻译**：
   - 监听宿主事件（如 OpenCode 的 hook、omp 的 ContextEvent / ExtensionAPI 等）。
   - 将宿主特有的消息格式、用户输入、会话生命周期调用转换为万象理与产品面理解的中立消息与意图。
2. **中立动作转换为宿主操作**：
   - 将内核或产品面发出的能力调用、工具执行请求、反馈呈现翻译为宿主原生工具回调、输出通道调用（如 `sendUserMessage`、`registerTool`、PTY 通道等）。
3. **宿主怪癖与错误兜底**：
   - 处理宿主特有的重试机制、错误包装、连接断开与并发异常。
   - 翻译税可以厚、可以繁复，这是万象通的职责，绝不让宿主脏语义渗入万象理。

### 2.2 严守边界（不越界、不拥有核心状态）
1. **不拥有领域状态**：万象通不持有业务会话状态、任务演进记录与领域决策；领域状态归万象理各 Owner 拥有。
2. **单向依赖万象理**：万象通只作为万象理的消费者，仅通过公开契约调用万象理；禁止万象理反向依赖万象通。
3. **不自造证明装置**：严禁在万象通引入 witness、oracle、AI 验收门禁等证明宗教；正确性依赖清晰的类型转换与小接口。
4. **不设空壳代码**：在迁移闸门（盘点、纠偏、调结构、清理支撑程序）未获负责人确认通过前，万象通仅建立职责说明，不创建无实际行为的空壳代码，不提前搬迁职责。

---

## 3. 现有实际代码与资源分布调查（事实依据）

经全面排查，目前本仓中承担或耦合了宿主特定逻辑的存量资产分布如下：

1. **F# 模块中的宿主边界（src/Wanxiangshu/）**：
   - 共有约 68 个工程文件（.fsproj）涉及 host、opencode、omp 边界。
   - 典型模块包括：
     - `Wanxiangshu.Owner.host-boundary.omp-adapter.fsproj` / `omp-extension.fsproj`（针对 omp 宿主的适配）。
     - `Wanxiangshu.Owner.host-boundary.host-message-codec.fsproj` / `host-event-envelope.fsproj`（消息编解码）。
     - `Wanxiangshu.Owner.execution-model-routing.opencode-host-opencodeport-adapter.fsproj`（OpenCode 端口适配）。
     - `Wanxiangshu.Owner.action-affordance.opencode-tools-*.fsproj`（OpenCode 专属工具实现）。
     - `Wanxiangshu.Owner.managed-session-lifecycle.opencode-host-*.fsproj`（宿主生命周期管理）。
2. **原型与规范适配器（proposals/host-neutral-interface/）**：
   - `proposals/host-neutral-interface/adapters/omp/adapter.ts`：已实现针对 omp (oh-my-pi) 的 TypeScript 适配器原型，直接依赖 `@oh-my-pi/pi-coding-agent`。
3. **宿主资源与提示词（resources/provider/host/）**：
   - 包含 `advisor-question/`、`pair-programming-*`、`restart-guidance/`、`scope-protocol/` 等宿主环境注入文本。
4. **宿主辅助与检查脚本（scripts/）**：
   - `scripts/lib/ensure-native-opencode.mjs`、`scripts/warmup-opencode.mjs`。
   - `scripts/checks/omp-entry-decision-gate.mjs`、`scripts/checks/opencode-import-baseline.json`。

---

## 4. 程序待改清单（待过闸门后依授权执行）

以下事项属于后续职责迁移与结构调整的程序待改事项。**当前处于闸门期，按军令严禁擅自提前启动迁移或创建空壳**，待负责人验收确认过闸后，逐项按授权推进：

1. **[待改] 宿主适配器归拢至万象通**：
   - 将 `proposals/host-neutral-interface/adapters/omp/` 中的成熟适配逻辑迁移至 `wanxiangtong/omp/`。
   - 提取 OpenCode 真实运行时交互通道，建立 `wanxiangtong/opencode/`，将散落在 `src/Wanxiangshu/` 各处对 OpenCode 的 hook、工具定义与消息编解码统一归并。
2. **[待改] 剥离本仓 F# 项目中的特定宿主依赖**：
   - 清理 `src/Wanxiangshu/` 中带有 `opencode-*` 与 `omp-*` 的专用 fsproj。
   - 将宿主特定协议翻译逻辑从内核剥离至万象通，中立业务逻辑收敛进万象理，其余无用旧桥接彻底删除。
3. **[待改] 净化万象理（wanxiangli）中立性**：
   - 严格审查万象理公开契约，禁止出现 OpenCode/omp/Cline 专有名词与特有生命周期类型。
   - 确保万象理只暴露通用能力接口（如 PTY、文件系统、事件流），由万象通负责具体实例化与绑定。
4. **[待改] 迁移宿主运行与检查脚本**：
   - 将仅服务于特定宿主环境预热、决策的脚本（如 `warmup-opencode.mjs`、`ensure-native-opencode.mjs`）根据实际需要精简后移至万象通内部或清理废弃门禁脚本。
