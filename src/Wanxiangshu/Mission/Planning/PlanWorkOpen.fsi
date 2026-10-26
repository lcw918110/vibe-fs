namespace Wanxiangshu.Mission.Planning

open System.Threading.Tasks

module PlanWorkOpen =
    /// sessionId 是工作身份。第一次 review 开立工作。本入口不写日志。
    val ensurePlanWorkOpened:
        gitCommonDir: string ->
        root: string ->
        sessionId: string ->
        observedHead: int64 option ->
            Task<Result<unit, string>>

    /// work-036 / work-028: the single runtime derivation of the
    /// fixed DevOps identity for a plan work. The entry binding and the
    /// single-flight ask binding must resolve the same devopsId for the same
    /// work, so the derivation lives here once.
    val internal deriveDevOpsId: workId: string -> string
