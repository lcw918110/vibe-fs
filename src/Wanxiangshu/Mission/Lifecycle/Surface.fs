namespace Wanxiangshu.Mission.Lifecycle

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Host
open Wanxiangshu.Persistence.Journal

module LifecycleSurface =

    let observeDurable (requestId: string) (durableResult: obj) =
        let seen = DurableWorkHost.observe requestId durableResult

        box
            {| requestId = seen.RequestId
               status = seen.Status
               kind = seen.Kind
               reason = seen.Reason |}

    let mayStartDelivery (durableResult: obj) = DurableWorkHost.mayStart durableResult

    let journalPath (gitCommonDir: string) =
        DurableWorkHost.journalPath gitCommonDir

    let execute (gitCommonDir: string) (workId: string) (requestId: string) (command: obj) =
        DurableWorkHost.execute gitCommonDir workId requestId command

    let submitReview
        (gitCommonDir: string)
        (workId: string)
        (requestId: string)
        (office: string)
        (providerRunId: string)
        (toolCallId: string)
        (updates: obj)
        (blockers: obj)
        (digest: string)
        =
        DurableWorkHost.submitReview
            gitCommonDir
            workId
            requestId
            office
            providerRunId
            toolCallId
            updates
            blockers
            digest
            null

    let inspect (gitCommonDir: string) (workId: string) =
        DurableWorkHost.inspect gitCommonDir workId

    let inspected (gitCommonDir: string) (workId: string) =
        DurableWorkHost.inspected gitCommonDir workId

    let private stampedDir (store: obj) : string option =
        let raw =
            emitJsExpr store "$0 && typeof $0.gitCommonDir === 'string' ? $0.gitCommonDir : ''"

        let text = if isNull raw then "" else string raw
        if String.IsNullOrWhiteSpace text then None else Some text

    let private emptyWorkView () =
        box
            {| found = false
               sequence = ""
               active = false
               reviewed = ""
               delivered = false |}

    let private presentWorkView (work: DurableWorkHost.InspectedWork) =
        box
            {| found = true
               sequence = String.concat "," work.Sequence
               active = work.ActiveId.IsSome
               reviewed = work.ReviewOutcome |> Option.defaultValue ""
               delivered = work.Delivered |}

    let workView (store: obj) (workId: string) =
        match stampedDir store with
        | None -> emptyWorkView ()
        | Some dir ->
            DurableWorkHost.inspected dir workId
            |> Option.map presentWorkView
            |> Option.defaultWith emptyWorkView

    let private bindReceipt (dir: string) (workId: string) (devopsId: string) (target: obj) =
        let targetValue: obj = if isNull target then null else box (string target)

        let command =
            DurableWorkHost.command
                "BindDevOps"
                [ "trigger", box "Ask"
                  "devopsId", box devopsId
                  "target", targetValue ]

        let receipt = DurableWorkHost.execute dir workId (workId + ":bind:" + devopsId) command

        if
            receipt.Status = "Committed"
            || receipt.Status = "Replayed"
            || receipt.Status = "Observed"
        then
            box {| ok = true; error = "" |}
        else
            let reason = if receipt.Reason = "" then receipt.Status else receipt.Reason
            box {| ok = false; error = reason |}

    let bindDevOps (store: obj) (workId: string) (devopsId: string) (target: obj) : Task<obj> =
        task {
            let dir = stampedDir store

            let inspected =
                dir |> Option.bind (fun common -> DurableWorkHost.inspected common workId)

            match dir, inspected with
            | Some common, Some work when work.DevOpsId.IsNone -> return bindReceipt common workId devopsId target
            | Some _, Some _ ->
                // First binding wins; a repeated binding writes nothing and keeps the stored target.
                return box {| ok = true; error = "" |}
            | _ ->
                return
                    box
                        {| ok = false
                           error = "lifecycle work is missing" |}
        }

    let private lastDevOpsTarget (work: obj) : string option =
        let frames: obj array = unbox (work?history ())

        frames
        |> Array.collect (fun frame -> unbox<obj array> frame?events)
        |> Array.choose (fun event ->
            let target = event?target

            if string (event?``type``) <> "DevOpsBound" || isNull target then
                None
            else
                Some(string target))
        |> Array.tryLast

    let workDevOps (store: obj) (workId: string) =
        match stampedDir store with
        | None -> null
        | Some dir ->
            DurableWorkHost.inspected dir workId
            |> Option.bind (fun work -> work.DevOpsId)
            |> Option.map (fun devopsId ->
                let target = DurableWorkHost.useWork dir workId (fun live -> lastDevOpsTarget live)

                box
                    {| devopsId = devopsId
                       modelTarget = target |> Option.toObj |})
            |> Option.defaultValue null

    let executeOwnershipDurable
        (gitCommonDir: string)
        (registryId: string)
        (requestId: string)
        (message: obj)
        : DurableWorkHost.HostReceipt =
        DurableWorkHost.executeOwnershipDurable gitCommonDir registryId requestId message

    let mayStartResource (receipt: DurableWorkHost.HostReceipt) : DurableWorkHost.OwnershipLease option =
        DurableWorkHost.mayStartResource receipt

    let ownershipBlockersDurable
        (gitCommonDir: string)
        (registryId: string)
        (scopeId: string)
        : DurableWorkHost.OwnershipBlocker array =
        DurableWorkHost.ownershipBlockersDurable gitCommonDir registryId scopeId
        |> List.toArray
