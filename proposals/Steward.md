# Steward：未来角色的纯架构构想

> **状态：构想，未实施。** 本文不规定工具 schema、事件结构、调度算法或代码形态；现行角色与规则见 `resources/provider/role/`、`requirements/office-capability/` 与根 `AGENTS.md`。
> 2026-08 的原始长文（26 节）保留在 git；这里是它的骨架，供讨论时读，不作施工依据。

## 一句话

现有 Grand Rewrite 预留 Steward 为未来第 11 个 canonical Role，authority 是 admission、timing 与 user-intent continuity。边界一句话：

> **Steward owns the docket. Orchestrator owns the campaign. Manager owns the mission.**

本文在这个边界上给 Steward 一个更完整的本体，核心是「Standing Sphinx」：长期悬而未决的问题本身是资产，可以没有答案而仍然成立。

## 骨架

1. **对象是仓库的未来**，不是当前任务清单。Steward 关心「接下来值得做什么」，而不是替 Manager 排班。
2. **人的意图是特权来源，但不是未来工作的唯一来源**；观察、沉思与仓库自身的历史也能提出候选。
3. **专职沉思者（Meditator）**：持续提出更好的问题，而不是持续改代码；「更好」刻意留白，由世界反馈裁定。
4. **无尽搜索不等于无尽改动**；沉思者必须始终锚定在仓库的真实状态上。
5. **Standing Sphinx**：问题可以有答案而没有终结；当前最佳答案是第一等概念，随新证据被替换。
6. **信息、思考与观察是三种不同行为**：信息进入、思考继续、当下理解被观察，各自有自己的事实。
7. **终局属于询问契约**，不属于沉思者；Steward 不推动 Standing Sphinx 收口。
8. **世界决定何时需要照看**；低负载是直觉，不是法则；照看决策有前沿，人的话语同样是照看决策的一个前沿。
9. **Sphinx 的知识不自动变成 docket 义务**；docket 是排程，不是知识库。
10. **工作守恒，不发明工作**：没有新证据时不制造新任务。

## 与现行系统的关系

- 现行 10 个 canonical Role、Office 权限矩阵与 Work/Review 协议都不依赖本文。
- 若将来真要引入 Steward，须先回答：总流程哪一步需要它？能否并入现有组合者？对等的旧间接面删掉了什么？（见根 `AGENTS.md` §6 停损）
