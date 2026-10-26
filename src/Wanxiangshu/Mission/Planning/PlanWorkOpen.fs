namespace Wanxiangshu.Mission.Planning

open System
open System.Threading.Tasks
open Wanxiangshu.Host
open Wanxiangshu.Foundation.Identity

/// 第一次 review 开立工作。本模块不写 Open。DevOps 身份仍在第一次成功的 ask 上绑定。
module PlanWorkOpen =

    // Shared with PlanTools.executeAsk's single-flight binding so the entry
    // and the first ask resolve the identical DevOps identity (work-028).
    let internal deriveDevOpsId (workId: string) : string =
        "devops-" + (HostDigest.sha256Hex ("plan-devops:" + workId)).[..15]

    /// 第一次 review 开立工作。本入口不写 Open，也不因缺游标中断回合。
    let ensurePlanWorkOpened
        (gitCommonDir: string)
        (root: string)
        (sessionId: string)
        (observedHead: int64 option)
        : Task<Result<unit, string>> =
        ignore gitCommonDir
        ignore root
        ignore observedHead

        if String.IsNullOrWhiteSpace sessionId then
            Task.FromResult(Error "Plan work session id cannot be empty")
        else
            Task.FromResult(Ok())
