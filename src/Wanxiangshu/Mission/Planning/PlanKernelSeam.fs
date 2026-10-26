namespace Wanxiangshu.Mission.Planning

open System
open Fable.Core.JsInterop
open Wanxiangshu.Mission.Lifecycle

/// Adapter over the wanxiangli lifecycle kernel for Plan-side review and
/// deliver. Reads only the typed InspectedWork projection, so Plan code keeps
/// its dependency on the kernel's projected fields explicit.
module PlanKernelSeam =

    /// One keyed obligation: the key names the finding, the value carries the
    /// work plan.
    type Obligation = { Key: string; Value: string }

    let private inspected (gitCommonDir: string) (workId: string) : DurableWorkHost.InspectedWork option =
        if String.IsNullOrWhiteSpace gitCommonDir || String.IsNullOrWhiteSpace workId then
            None
        else
            DurableWorkHost.inspected gitCommonDir workId

    let private activeOf (work: DurableWorkHost.InspectedWork) (incumbencyId: string) : bool =
        match work.ActiveId with
        | Some active -> active = incumbencyId
        | None -> false

    let activeObligations (gitCommonDir: string) (workId: string) : Obligation array =
        match inspected gitCommonDir workId with
        | None -> [||]
        | Some work ->
            work.Obligations
            |> Array.map (fun o ->
                { Key = if isNull o?key then "" else unbox<string> o?key
                  Value = if isNull o?value then "" else unbox<string> o?value })

    let obligationsEmpty (gitCommonDir: string) (workId: string) : bool =
        Array.isEmpty (activeObligations gitCommonDir workId)

    let private waiting (work: DurableWorkHost.InspectedWork) =
        work.Wait = "AwaitReview" || work.Wait = "AwaitEmptyReview" || work.Wait = "ConfirmEffect"

    let isReviewFinished (gitCommonDir: string) (workId: string) (incumbencyId: string) : bool =
        match inspected gitCommonDir workId with
        | None -> false
        | Some work -> activeOf work incumbencyId && work.Wait <> "AwaitOperatorAnswer" && work.Wait <> ""

    let isEligible (gitCommonDir: string) (workId: string) (incumbencyId: string) : bool =
        match inspected gitCommonDir workId with
        | None -> false
        | Some work -> activeOf work incumbencyId && waiting work
