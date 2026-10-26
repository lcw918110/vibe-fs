namespace Wanxiangshu.Mission.Work.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Host
open Wanxiangshu.OpenCode

/// 文件保留给 provider-leak 扫描。Manager 的生命周期判断已退出。
/// 真实 review 只走 DurableWorkHost.submitReview。
module ReviewBackendManager =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        let Finish = "runtime/manager-finish"

        [<Literal>]
        let Work = "runtime/manager-work"

        [<Literal>]
        let BindingUnavailable = "tool/review/binding-unavailable"

        [<Literal>]
        let InvalidFindings = "tool/review/invalid-findings"

        [<Literal>]
        let RecordFailed = "tool/review/record-failed"

        [<Literal>]
        let AlreadySubmitted = "tool/review/already-submitted"

    let execute (scope: ToolRuntimeScope) (args: HostToolArguments) (context: HostToolContext) : Task<string> =
        ignore scope
        ignore args
        ignore context
        Task.FromResult "review is the only lifecycle entry"

    let executeMember (scope: ToolRuntimeScope) (context: HostToolContext) (findings: obj) : Task<obj> =
        ignore scope
        ignore context
        ignore findings

        Task.FromResult(
            box
                {| ok = false
                   code = "review-retired"
                   reason = "review is the only lifecycle entry" |}
        )
