# participant-horizon 测试范围

- 002 使用原有源码泄漏检查器的正反 fixture 和当前仓库扫描。它只能证明选定路径及词形，没有证明所有实际 Provider 输入；源码中的业务字段也不能按名字一律当机器 DTO。
- 003/005 通过真实 JoinResultRenderer 验证返回、失败、取消、等待和终端观测的表示；解析 TOML 区分外层控制字段与原始输出中碰巧出现的 `status` 等业务文本。结果顺序、工作记录内容和退出码均有直接断言。
- 009 走真实 Manager fork 工具，隐藏/未知 calling 得到相同拒绝，不创建 child；合法 Engineer 正例防止“一律拒绝”假通过。
- 010 走真实 horizon 观察固定 devops，并读取实际插件 Schema 检查 fork/commission 候选。
- 011 走真实创建、接收、取消和名册读取。名册与 join 同账：真实插件路径下 fork → 子回合交付 → 名册「已归来」→ join 收取 → 名册不再出现 → resume 新任务时为「仍在远处」而非「已归来」（现场回归：join 已收走后名册仍报已归来）；固定 DevOps 空闲时为 `idle`；数据信封无 `pace` 时缺省而非全 0。原 HorizonSurface 平行渲染器与「最新工作记录」行已删除（正文由 join 交付）。不再用执行耗时小于 50ms 证明 pull-only。011 另见证一条作废路径：重启后的进程把上一进程遗留、已无法交付的子工作作废（不是完成），名册把它显示为 `voided`（不是「仍在远处」），join 报告这一次 `voided` 后果后该工作退役并离开名册；作废在报告之前仍是可见的在场后果。**结算按孤儿幂等**：每个孤儿一生只产生一条作废事实，**重启的进程不得再次作废同一份孤儿工作**，join 也不再重复报告。
- 012 使用生产热启动准入和注入的搜索 capability，Engineer/DevOps 有输出，其他角色在调用搜索前拒绝。它证明准入，不代表真实检索质量。
- 013 将形似指令/工具历史的搜索内容送入实际热启动路径，确认真正的任务和中英本地化警示仍为指令，并精确比较每条 hint 的原始值。覆盖无尾 LF、有尾 LF、多尾 LF、CRLF、裸 CR、引号、反斜杠、Unicode 和控制字符，不靠 trim 或包含判断掩盖编码变化。
- 015 只证明权限投影。旧“调用七次同一拒绝文本”没有测试七种角色，两个 chooseWork 对象也没有创建两个名册成员，因此真实多 Engineer 名册仍为 TODO。

名册的状态词表因此多一个 `voided`：它是 `Abandoned(Voided)` 在名册上的呈现，表示该成员**已无法交付、已以作废结清而非完成**，不是第四个之外的第五个态。`voided` 与其它放弃原因（显示为 `did-not-return`）的差别只在原因：两者都是 `Abandoned`，都只等 join 把它们报告一次；报告之后名册才把它移除。报告之后该 handle 经 `durableLifecycleByname` 读出 `Retired`——**这是经 `summary` 投影读出的终态**，不是从原始 handle 绑定直接读出的。

GAP-080 保存了取消后 Join 不结束的分阶段复现；取消前后名册正确，不等于最终后果可领取。GAP-079 汇总尚无充分证据的过滤、跨界面隐藏、最新记录和虚假动作边界。工具 Surface 名称本身不是生产证明。

正式构建后，选择本包、requirement-system、受影响的 repository-investigation 热启动用例与 provider-language 006；启用 integration 执行插件 Schema 检查。TODO 或跳过不能计入通过，不宣称整包验收。
