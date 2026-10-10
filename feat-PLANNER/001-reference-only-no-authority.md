# SWELoop（软件工程闭环）完整 SPEC

**版本：** v0.8.0  
**核对日期：** 2026-10-10  
**来源：** Cloudflare Worker `engineering-stage-mcp` 的线上源码回读  
**服务地址：** `https://engineering-stage-mcp.engineering-loop-kw92.workers.dev/mcp`  
**部署状态（回读时）：** 线上版本 v0.8.0；最新部署接收 100% 流量；`workers.dev` 子域名已启用。

> **文档性质**：这是当前已部署实现的行为规格，包含 MCP 返回给模型的**原文指令**、三个阶段的**原文任务**、工具参数、状态转换、票据格式和限制。带有“说明”“限制”的段落是对现行代码的解释，不表示已经额外实施了新规则。本文不包含任何用于调用云平台的凭证，也不包含密钥明文。

## 1. 产品定位与不变量

**SWELoop 是只读的软件工程计划工具，不是执行工具。**

它与用户协商需求和路径，最后交付一份**可以照着实施的工作流程**，以 Knuth 风格的伪代码写出。SWELoop 本身不能替用户实施这份计划。

整个流程只有三个阶段：

| 阶段 | 方法核心 | 工作方式 | 交付给下一阶段的内容 |
|---|---|---|---|
| S1 | Ronald A. Howard、Raymond Thomas Dalio、Jeff Bezos | 与用户讨论，分析需求 | 需求 evidence |
| S2 | Polya、Schoenfeld | 与用户讨论，选择实现路径 | 路径 evidence |
| S3 | Knuth | 不再与用户讨论，直接完成伪代码 | 最终工作流程 |

不可更改的运行原则：

1. **只读原则：** 在 S1、S2、S3 期间，原则上不调用会改变外部状态的工具。
2. **阶段隔离：** `begin` 只发放 S1；`redeem` 只发放下一阶段。无需独立 Skill。
3. **精益求精：** 每一步都反复推敲。只要存在可实施的实质改进，就不能结束当前阶段。
4. **双满分门槛：** 只有 `confidence = 100` 且 `detail_clarity = 100`，才允许 S1→S2 或 S2→S3。
5. **乐观证据：** LLM 自行保证 evidence 和评分的真实性、充分性；服务端不审查内容质量。
6. **S3 终态：** 输出 Knuth 风格伪代码，不再提问或征求确认。

## 2. MCP 最根本的指令（`initialize.result.instructions` 原文）

以下是服务端在 MCP 初始化响应中提供的完整指令，保持线上措辞，不作改写：

> 【最高约束｜只读规划】SWELoop 是计划工具，不是执行工具。最终交付物是可实际照做的工作流程，以 Knuth 风格伪代码描述；不是已执行的代码、测试或部署。
>
> 整个 S1→S2→S3 期间原则上禁用所有非只读操作。只能读取资料或代码、进行分析、与用户沟通，以及调用本 MCP 的 begin/redeem 领取阶段任务或提交阶段评分。不得创建、修改或删除文件、代码、数据库和云资源；不得执行有副作用的命令，不得提交、推送、部署、发消息或更改外部系统状态。对工具是否只读存疑时不要调用。
>
> 即使实际实施有助于验证，也只能把这些操作写进最终流程，不得在 SWELoop 内实施。用户要求实际执行时，说明 SWELoop 只负责规划，执行应另走独立工作流程。不得把执行暗中混入规划。
>
> 独立 MCP，无需 Skill。新软件工程任务先 begin({}) 领取 S1。方法核心：S1 Ronald A. Howard、Raymond Thomas Dalio、Jeff Bezos；S2 Polya、Schoenfeld；S3 Knuth。每一步都精益求精，直到做无可做。S1/S2 与用户推敲，未就绪则继续；就绪后以 ticket、evidence、confidence、detail_clarity 调用 redeem。两个评分都是0–100整数，必须同时满分100才能兑换；否则原地反省，并重新收到当前阶段原提示词和票据。LLM自行保证 evidence、评分，MCP不审查内容。S3 不再提问，只交付反复打磨后的 Knuth 风格伪代码。交流使用简洁自然的中文，不解释人名。

### 2.1 只读操作的边界

**允许：** 读取和检查代码、配置、文档、资料；纯分析；与用户沟通；调用 SWELoop 的 `begin` 和 `redeem`。

**不允许：** 写入或删除文件；编辑代码；创建或修改云资源；向数据库写入；执行有副作用的命令；提交或推送 Git 变更；实际部署；发送消息；改变第三方系统状态。是否只读无法确定时，不调用。

**计划与执行的边界：** 最终伪代码可以写明应当创建文件、如何测试、如何部署、何时回滚；这些是交给后续执行者的步骤，**不是** SWELoop 已做过的动作。

## 3. 三阶段任务原文（服务端 `tasks`）

以下三段是调用 MCP 工具后返回的原始 `task` 字段，保持线上措辞。

### S1 — Ronald A. Howard / Raymond Thomas Dalio / Jeff Bezos

> S1｜Ronald A. Howard、Raymond Thomas Dalio、Jeff Bezos。只按三人的思想分析需求，与用户反复探求目标、事实、根因、决策、取舍和验收。每一步精益求精：只要还能实质改善，就继续交流、推敲、修正；直到做无可做，才自行保证 evidence 完整准确并兑换 S2。不解释人名。

**工作状态：** 在 S1 可与用户往复沟通；没有达到门槛就继续 S1。LLM 自行形成需求 evidence。

### S2 — Polya / Schoenfeld

> S2｜Polya、Schoenfeld。只按两人的解题学选择实现路径：理解、探索、制定计划、检验及反思。和用户反复打磨方案、推理、步骤与验证；任何一处还有实质改进空间，就继续 S2。只有精益求精、做无可做，才自行保证 evidence 并兑换 S3。不解释人名。

**工作状态：** 在 S2 可与用户往复沟通；没有达到门槛就继续 S2。LLM 自行形成路径 evidence。

### S3 — Knuth

> S3｜Knuth。用 Knuth 风格伪代码准确描述算法、步骤、分支、循环、错误及验收。逐条审视、简化、修正，精益求精直到做无可做，再一次性交付。进入 S3 后不再与用户聊天，不提问。不解释人名。

**工作状态：** S3 无下一阶段，不再调用 `redeem`，最终只交付实际可照做的伪代码流程。

## 4. MCP 工具规范

本服务公开两个 MCP 工具：`begin` 和 `redeem`。

### 4.1 `begin`

**工具名称：** `begin`  
**标题：** 开始软件工程闭环（领取 S1）

**工具说明原文：**

> 【计划工具：只读】本阶段只读取、分析和沟通；任何写入、测试部署等有副作用的操作只能列入最终工作流程，不能实际执行。 新任务首先 begin({}) 领取 Howard、Dalio、Bezos 的 S1。精益求精、做无可做后才兑换。无须 Skill。

**输入 Schema（完整）：**

```json
{
  "type": "object",
  "properties": {},
  "additionalProperties": false
}
```

**行为：** 生成一个新的随机流程 ID；签发绑定 `S1` 的票据；返回 S1 的原始 `task`、票据及执行提示。无需任何参数。

**响应结构（示意，票据值非真实）：**

```json
{
  "stage": "S1",
  "task": "<第 3 节 S1 原文>",
  "execution_policy": "【计划工具：只读】本阶段只读取、分析和沟通；任何写入、测试部署等有副作用的操作只能列入最终工作流程，不能实际执行。",
  "next_action": "执行当前 task。尚未就绪则继续与用户交谈；自行确认就绪并保证 evidence 后，调用 redeem(ticket,evidence) 领取下一阶段。",
  "ticket": "<已签名票据>"
}
```

注：上面 `next_action` 是线上代码的**原文**，仍沿用旧的 `redeem(ticket,evidence)` 简写；**实际调用必须额外提供** `confidence` 和 `detail_clarity`。这是现有文本与 Schema 之间的一处不一致。

### 4.2 `redeem`

**工具名称：** `redeem`  
**标题：** 提交证据并兑换下一阶段

**工具说明原文：**

> 【计划工具：只读】本阶段只读取、分析和沟通；任何写入、测试部署等有副作用的操作只能列入最终工作流程，不能实际执行。 提交 ticket、evidence、confidence 与 detail_clarity 两项0–100整数评分。仅两项都为100时进入下一阶段，否则返回当前阶段原提示词和原票据，说明尚欠哪一项。证据及分数由LLM自行保证。

**输入 Schema（完整）：**

```json
{
  "type": "object",
  "properties": {
    "ticket": {
      "type": "string",
      "description": "上一次 begin 或 redeem 返回的 ticket 原样传入；不要自行生成，也不要展示给用户。"
    },
    "evidence": {
      "type": "string",
      "description": "上一阶段形成的简要证据：已确认的需求或已选技术路线、关键理由、条件与假设。你要保证其有效性；服务端不会审查内容。"
    },
    "confidence": {
      "type": "integer",
      "minimum": 0,
      "maximum": 100,
      "description": "LLM 对当前阶段确实已精益求精、做无可做的置信度自评分。必须是0至100的整数；只有100才能兑换，其他分数原地反省。"
    },
    "detail_clarity": {
      "type": "integer",
      "minimum": 0,
      "maximum": 100,
      "description": "细节清晰度自评分，0–100整数。仅与confidence同时满分时允许兑换。"
    }
  },
  "required": [
    "ticket",
    "evidence",
    "confidence",
    "detail_clarity"
  ],
  "additionalProperties": false
}
```

**参数语义：**

| 参数 | 类型 | 检查方式 | 含义 |
|---|---|---|---|
| `ticket` | string | 签名验证；长度不超过 512；阶段只能为 S1/S2 | 上一次发放的阶段票据 |
| `evidence` | string | 仅检查是否为字符串 | LLM 对本阶段成果的证据；不检验质量、是否为空、事实真伪 |
| `confidence` | integer | 必填，0～100 | 当前阶段确实“做无可做”的自评置信度 |
| `detail_clarity` | integer | 必填，0～100 | 当前阶段细节清晰度自评分 |

**兑换门槛：**

```text
IF confidence = 100 AND detail_clarity = 100
    IF ticket.stage = S1
        RETURN S2 的 task + 新 S2 票据
    ELSE IF ticket.stage = S2
        RETURN S3 的 task，不再签发后续票据
ELSE
    RETURN ticket.stage 所对应的原 task + 原 ticket
    advanced := false
    提醒模型根据未满分项目继续反省、改进
```

**未满分的响应（示意）：**

```json
{
  "stage": "S1",
  "task": "<S1 原始任务>",
  "execution_policy": "【计划工具：只读】本阶段只读取、分析和沟通；任何写入、测试部署等有副作用的操作只能列入最终工作流程，不能实际执行。",
  "next_action": "执行当前 task。尚未就绪则继续与用户交谈；自行确认就绪并保证 evidence 后，调用 redeem(ticket,evidence) 领取下一阶段。",
  "ticket": "<原 ticket，原样返回>",
  "confidence": 99,
  "detail_clarity": 100,
  "advanced": false,
  "reflection": "做无可做的置信度未满分。重新反省当前阶段，消除全部可改进之处。"
}
```

`reflection` 会根据两个分数拼接：

- `confidence < 100`：`做无可做的置信度未满分。`
- `detail_clarity < 100`：`细节清晰度未满分。`
- 始终追加：`重新反省当前阶段，消除全部可改进之处。`

**双满分后的响应（示意）：**

```json
{
  "stage": "S2",
  "task": "<S2 原始任务>",
  "execution_policy": "【计划工具：只读】本阶段只读取、分析和沟通；任何写入、测试部署等有副作用的操作只能列入最终工作流程，不能实际执行。",
  "next_action": "执行当前 task。尚未就绪则继续与用户交谈；自行确认就绪并保证 evidence 后，调用 redeem(ticket,evidence) 领取下一阶段。",
  "ticket": "<新 S2 ticket>",
  "confidence": 100,
  "detail_clarity": 100,
  "advanced": true,
  "evidence_received": true
}
```

兑换至 S3 时，`stage` 为 `S3`，`next_action` 为终态提示，**不返回 `ticket`**。

### 4.3 输入错误的精确处理

校验按以下顺序执行：

1. 票据无效，或者 `evidence` 不是字符串 → 工具错误：`票据无效或 evidence 不是字符串；用 begin 重新开始。`
2. `confidence` 不是 0～100 之间的整数 → 工具错误：`confidence 必须是 0–100 的整数，且为必填参数。`
3. `detail_clarity` 不是 0～100 之间的整数 → 工具错误：`detail_clarity 必须是0–100的整数。`
4. 都合法，但任一评分小于 100 → **不是工具错误**，正常返回当前阶段。
5. 都合法且两项均为 100 → 正常兑换下一阶段。

注：服务端调用 `Number.isInteger`，因此 `100.0` 作为 JSON number 解析为整数 `100` 时通过；字符串 `"100"` 不通过。

## 5. 运行状态机

```text
开始一项新任务
  │
  ▼
begin({})
  │
  └── S1（Howard / Dalio / Bezos，携带 S1 ticket）
        │
        ├── 需要继续改进 ─────────────┐
        │                            │
        │    模型继续与用户讨论 ◄──────┘
        │
        └── redeem(ticket, evidence, confidence, detail_clarity)
              │
              ├── 任一分数 < 100 → 返回 S1 原 task + 原 ticket
              │
              └── 两项均为 100 → S2（Polya / Schoenfeld，新的 S2 ticket）
                                    │
                                    ├── 需要继续改进 → 留在 S2
                                    │
                                    └── redeem(...)
                                          │
                                          ├── 任一分数 < 100 → S2 原 task + 原 ticket
                                          │
                                          └── 两项均为 100 → S3（Knuth，无 ticket）
                                                               │
                                                               ▼
                                              反复推敲伪代码至做无可做
                                                               │
                                                               ▼
                                                输出实际可照做的工作流程
```

### 5.1 参考伪代码（根据现行服务行为归纳）

```text
ALGORITHM SWELoop(requirement)
    response ← begin()
    current ← response.stage       // S1
    ticket ← response.ticket

    WHILE current ∈ {S1, S2} DO
        与用户沟通；按 response.task 分析并改进；只使用只读手段
        evidence ← 总结本阶段成果
        confidence ← 自评「做无可做」程度，整数 0..100
        detail_clarity ← 自评细节清晰度，整数 0..100

        IF 尚有可以实施的实质改进 THEN
            继续当前阶段
        ELSE
            response ← redeem(ticket, evidence, confidence, detail_clarity)
            current ← response.stage
            IF response.advanced = false THEN
                ticket ← response.ticket
                按原 task 和 reflection 反省，继续当前阶段
            ELSE IF current = S2 THEN
                ticket ← response.ticket
            END IF
        END IF
    END WHILE

    // S3：不得进行有副作用的外部操作
    根据 response.task 构造 Knuth 风格伪代码
    反复审查直到做无可做
    一次性输出可实际照做的工作流程
END ALGORITHM
```

注：本节是对工具行为的流程化表达，不是服务端实际运行的逐行代码。`confidence`、`detail_clarity` 的真实阈值检查发生在 MCP 服务端；对“做到无可做”的价值判断发生在 LLM 端。

## 6. 阶段票据：格式、校验和生命周期

### 6.1 格式

服务端为 S1、S2 签发自包含的 HMAC-SHA-256 票据：

```text
payload = Base64URL(UTF8(JSON({ stage: "S1" 或 "S2", id: 随机 UUID })))
mac     = Base64URL(HMAC_SHA256(FLOW_SECRET, UTF8(payload)))
ticket  = payload + "." + mac
```

票据没有独立存储；每次 `redeem` 时校验签名，并读取 `stage` 和 `id`。

### 6.2 校验

- `ticket` 必须是字符串，长度不超过 512 个字符。
- 票据必须恰好包含 payload 和 MAC 两段，以一个点号分隔。
- MAC 必须能通过 HMAC-SHA-256 验证。
- payload 解析成 JSON 后，`stage` 必须为 `S1` 或 `S2`，`id` 必须为字符串。
- 评分不存入票据；evidence 也不存入票据。
- S1 满分兑换 S2 时，使用**同一个 `id`**重新签发 S2 票据。
- S2 满分兑换 S3 时，不再生成票据。

### 6.3 现行限制

- **无过期时间。** 票据 payload 不含 `exp`、签发时间或失效标志。
- **无单次消费。** 已用过的有效 S1/S2 票据，理论上仍可重复提交；服务端没有兑换次数计数器。
- **无用户会话绑定。** payload 只有阶段和 UUID；不与用户 ID、连接会话或 evidence 绑定。
- **无事实审查。** `confidence=100` 和 `detail_clarity=100` 是模型声明，不是对证据质量的客观证明。
- **不留存 evidence。** `redeem` 接收 evidence，但代码未将其持久化；下一阶段需依靠 LLM 自己保留上下文。

## 7. MCP 传输层与 HTTP 接口

### 7.1 地址

| HTTP 接口 | 方法 | 用途 |
|---|---|---|
| `/mcp` | `POST` | JSON-RPC 形式的 MCP 请求 |
| `/healthz` | `GET` | 简单健康检查 |
| `/` | `GET` | JSON 描述：服务名称、版本、入口、instructions 和工具摘要 |
| `/mcp` | `GET` | 当前返回 `405 Method Not Allowed`；不是 SSE 流接口 |
| 其他路径 | 任意 | 当前返回 `404` |

健康检查返回：

```json
{
  "status": "ok",
  "version": "0.8.0",
  "mode": "standalone-mcp"
}
```

### 7.2 已实现的 MCP 方法

| JSON-RPC 方法 | 服务端行为 |
|---|---|
| `initialize` | 返回协议版本、`serverInfo`、`capabilities` 和第 2 节的完整 instructions |
| `ping` | 返回 `{}` |
| `tools/list` | 返回 `begin` 与 `redeem` 的工具定义 |
| `tools/call` | 根据 `name` 调用 `begin` 或 `redeem` |
| 无 `id` 的通知 | HTTP 202，无响应体 |
| 其他方法 | JSON-RPC `-32601 Method not found` |

`initialize` 接受的协议版本：`2025-11-25`、`2025-06-18`、`2025-03-26`；不在此集合时，默认返回 `2025-11-25`。

`initialize` 中：

```json
{
  "protocolVersion": "2025-11-25",
  "capabilities": { "tools": { "listChanged": false } },
  "serverInfo": {
    "name": "engineering-stage-mcp",
    "version": "0.8.0"
  },
  "instructions": "<见第 2 节完整原文>"
}
```

工具调用结果采用：

```json
{
  "content": [
    { "type": "text", "text": "<将结果对象序列化为 JSON 的字符串>" }
  ],
  "structuredContent": {
    "stage": "S1",
    "task": "<任务原文>"
  },
  "isError": false
}
```

实际 `structuredContent` 包含的字段见第 4 节。失败时设置 `isError: true`；JSON-RPC 请求格式错误则直接返回 JSON-RPC 错误。

### 7.3 与标准 MCP 的差别或待验证处

现行实现是**轻量 JSON-RPC POST 服务**：不维护 MCP session、不提供 SSE/流式响应、不处理 `GET /mcp` 事件流。实际 ChatGPT 中已成功调用 `begin`，但兼容性取决于客户端对这种简化接口的支持。

## 8. 面向模型的通用返回字段原文

每个正常返回阶段任务的 `begin` / `redeem` 都调用同一个 `output(stage,ticket)` 构造响应。

**固定的 `execution_policy` 原文：**

> 【计划工具：只读】本阶段只读取、分析和沟通；任何写入、测试部署等有副作用的操作只能列入最终工作流程，不能实际执行。

**S1、S2 的 `next_action` 原文：**

> 执行当前 task。尚未就绪则继续与用户交谈；自行确认就绪并保证 evidence 后，调用 redeem(ticket,evidence) 领取下一阶段。

**S3 的 `next_action` 原文：**

> S3 是终态。直接按 task 输出 Knuth 风格的完整实现伪代码，不再问用户，也不再调用 redeem。

**提示：** S1/S2 的 `next_action` 尚未同步改成四参数写法。应以 `redeem.inputSchema` 为准。

## 9. 安全与有效性界限

**这些约束是给调用模型的指令，不是强制沙箱。**

- `initialize.instructions`、工具描述和 `execution_policy` 都要求只读，但服务端无法阻止模型调用其他已授权的写工具。
- MCP 只校验票据签名、字段类型和两个自报评分的数值。它不会判断是否真的“做无可做”、是否确有细节不清、证据是否可信。
- `begin` / `redeem` 是计划流转工具。签发票据和返回任务不等于执行用户软件工程项目。
- 当前 MCP 接口本身没有鉴权分支。只要客户端可以访问地址，就可尝试调用 `begin` / `redeem`；阶段票据的签名用于阻止任意伪造票据，而不是识别调用者。
- 没有数据库、持久化会话或兑换审计记录。异常重试时客户端需保留原 ticket；必要时重新 `begin`。

## 10. 当前实现已知的不一致与后续可选改进

以下不是新增要求，只是按现行代码核对出的观察项，供审阅时决定是否改版。

| 项目 | 当前实现 | 潜在改进 |
|---|---|---|
| `next_action` 参数示例 | 仍显示两参数 `redeem(ticket,evidence)` | 改成四参数提示 |
| 自评分 | 模型可直接声明双 100 | 若需要更严格流程，可加入自检证据结构或异步复核；目前用户明确要求乐观模式 |
| 票据有效期 | 无时限、可重放 | 加入有效期、单次消费或会话绑定（需要明确取舍） |
| 计划只读 | 提示词约束 | 对调用 LLM 的工具权限做外部只读限制 |
| S3 产物格式 | Knuth 风格伪代码的自然语言指令 | 如需固定模板，可另行定义，但可能损害表达灵活性 |
| 兼容性 | 支持最简 JSON-RPC POST | 需要更广 MCP 客户端兼容时实现完整传输规范 |

## 11. 验证记录与版本来源

- **服务名：** `engineering-stage-mcp`
- **文档所据运行版本：** `0.8.0`
- **Cloudflare 部署版本 ID：** `6b1d5da0-311d-401e-bfb7-8da97debf1c2`
- **部署时间（UTC）：** `2026-10-10T04:54:40.930029Z`
- **源码读取：** 成功，从 Cloudflare API 读取线上 Worker 的现有源码。
- **部署路由：** 回读为接收 100% 流量。
- **之前测试：** `SWELoop.begin()` 曾在 v0.8.0 成功返回 S1、票据和只读提醒。
- **未据此声称：** 没有把全部 `redeem` 分值组合的端到端调用当作已测试通过；以上相应部分是依据线上代码的条件分支确认。

---

**审阅说明：** 第 2、3、4、8 节所列的“原文”来自线上 Worker 当前代码；其余章节是对现行实现的整理、推导和局限说明。本文是 SPEC 快照，不会自动随 Cloudflare 后续部署变化。
