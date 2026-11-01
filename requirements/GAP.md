# 程序待改

> 只记还没做完、且会挡住总流程或挡住迁移闸门的技术实事。写法是人话：哪里不对、影响什么、解除条件是什么。
> 不记覆盖率、不记 oracle、不记 witness 账目、不按条数报进展。历史细节在 git 里，需要考古再翻。
> 每完成一条，删掉这一段，不留下"已关闭"的墓碑。

## 宿主适配

1. **宿主自行重试**：OpenCode 1.18.29 在 `experimental.chatMaxRetries = 0` 下，503 仍会发第二次 provider 请求。核只认一次执行或显式未知，双重请求会让状态含混。**程序待改**：重试识别与吸收归万象通；核内不调和。
2. **流异常落成正常完成**：宿主中断时事件可能落盘为 `completed + finish="unknown"` 且无 error 字段。若把它当正常结束，重入会丢活跃绑定。**程序待改**：万象通区分合法完成与断流，向核只交干净终态。
3. **终端事实生产者不全**：宿主侧 `appendAuthorityLogicalRunClosed` 仍部分硬编码（只认 `HumanRoot`），部分子会话终态没有生产者接线，只能靠上层删除或超时兜底。**程序待改**：每类逻辑运行在做成、作废、失败时都写出对应闭包事实。

## 长程与恢复

4. **Fission 收敛**：多路 lane 不单独建 authority root，必须继承发起者的 logical run；重启后残留 `Open` 的 `FissionGroup` 由 `ChildWorkRecovery.settleOrphanedChildRuns` 以中断结算。汇聚必须一次原子提交。**程序待改**：并发分道与收敛状态机留在核；宿主只负责多会话消息传递。
5. **重启后的在途执行**：快照里 `Started` 而无 `Terminal` 的执行，加载期分类器不能直接认定为终态，部分落入 manual 或等会话清理。**程序待改**：定一条简单规则——已 `Accepted` 且无最终事实者，重启统一作废并结清阻塞，允许重新调度。
6. **编程面隔离**：JS 编程面跑在进程内 `node:vm`，没有独立的 V8 内存硬栅栏（`PROGRAM_RESOURCE_LIMIT` 无独立隔离进程生产者），目前靠 `deadlineEpochMs` 时限防挂起。**程序待改**：等 program/runtime clean-break 落地后再简化沙箱与退出逻辑；在此之前保持时限与精准切片编辑。

## 工作生命周期

7. **旧交割审查残留**：Work/Review 已能驱动阶段演进，但仍有陈旧的交割审查法庭代码与提示。**程序待改**：继续删，使 Work 的状态流转只由真实事实推进。

## 交付与分发

8. **子任务交付视图**：生产路径 `childViewForRun` 恒为 `None`，真实交付物走进程内 `JsDeliveredValues`。**程序待改**：保持直接数据交付，后续平移到核的中立消息层。
9. **安装包白名单**：发布产物只含 `dist/`、`resources/` 与元数据及 bundled `node_modules/wanxiangli/**`，不得夹带 `src/`、`requirements/`。现状由 `scripts/verify-package.mjs` 守住；改包结构时同步改白名单，别把开发文件带进安装包。

## 文档与程序不一致

10. **源码里的证明注释**：4 处 `semantic-decorator-proof:` 注释指向不存在的测试文件（如 `requirements/guidance-delivery/tests/...`，该包已不存在）。它们不参与构建，是死引用。**程序待改**：删注释或改成现存测试的真实名字，不要为此重建包。
