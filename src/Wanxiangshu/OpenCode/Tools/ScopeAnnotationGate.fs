namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Context.Scope
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Persistence.Journal

/// Host-side Scope annotation gate. Schema is injected onto every ToolSpec;
/// semantics live in wanxiangli/scope-retention and common law «Scope».
/// scope.kind=resetScope on any tool drives ScopeReset.commit (no standalone tool).
module ScopeAnnotationGate =

    [<Import("scopeRetention", "wanxiangli/scope-retention")>]
    let private scopeRetention: obj = jsNative

    [<Emit("$0 == null")>]
    let private isNullish (_value: obj) : bool = jsNative

    [<Emit("$0[$1]")>]
    let private field (_value: obj) (_name: string) : obj = jsNative

    // DSL-MUTABLE: resource — the provider run whose real annotation is a
    // resetScope, per session. The reset commits asynchronously on a new epoch;
    // a sibling meToo in the same run must not depend on whether it arrives
    // before or after that commit.
    let private resetRuns = System.Collections.Generic.Dictionary<string, string>()

    let private asString (value: obj) : string =
        if isNullish value then "" else string value

    let private asInt (value: obj) (fallback: int) : int =
        let parse () =
            match System.Int32.TryParse(string value) with
            | true, n -> n
            | _ -> fallback

        if isNullish value then fallback else parse ()

    let private runTextOf (ctx: HostToolContext) callText =
        ctx.ProviderRunId
        |> Option.map ProviderRunIdentity.value
        |> Option.defaultValue callText

    let private currentEpoch epochOf ctx =
        let value = epochOf ctx
        if String.IsNullOrWhiteSpace value then "0" else value

    let private validateReset (ctx: HostToolContext) runText scope =
        let goal = asString (field scope "newScopeGoal")
        let budget = asInt (field scope "newScopeRetentionBudget") 0
        let oldV = asString (field scope "oldScopeVerboseVerbatim")

        if String.IsNullOrWhiteSpace goal then
            Error "resetScope requires newScopeGoal"
        elif budget < 1 then
            Error "resetScope requires newScopeRetentionBudget in 1..48"
        else
            resetRuns[ctx.SessionId] <- runText

            Ok(
                createObj
                    [ "ok" ==> true
                      "reanchor" ==> true
                      "remaining" ==> budget
                      "goal" ==> goal.Trim()
                      "oldVerbatim" ==> oldV.Trim() ]
            )

    let private updateBudget session (remaining: obj) =
        let set () =
            try
                Surface.setRetentionBudget session (unbox<int> remaining)
            with _ ->
                ()

        if not (isNullish remaining) then
            set ()

    let private acceptReceipt session (receipt: obj) =
        let reason () =
            if isNullish receipt then
                "scope annotation refused"
            else
                string receipt?reason

        if isNullish receipt || not (unbox<bool> receipt?ok) then
            Error(reason ())
        else
            updateBudget session receipt?remaining
            Ok receipt

    let private resetInRun session runText =
        match resetRuns.TryGetValue session with
        | true, run -> run = runText
        | _ -> false

    let private applyCall toolName scope (ctx: HostToolContext) epochOf call =
        let callText = ToolCallId.value call
        let runText = runTextOf ctx callText
        let epoch = currentEpoch epochOf ctx
        let kind = asString (field scope "kind")

        if kind = "resetScope" then
            validateReset ctx runText scope
        elif kind = "meToo" && resetInRun ctx.SessionId runText then
            Ok(createObj [ "ok" ==> true; "kind" ==> "meToo"; "reanchor" ==> false ])
        else
            scopeRetention?apply (ctx.SessionId, epoch, toolName, runText, callText, runText, scope)
            |> acceptReceipt ctx.SessionId

    /// Apply the required `scope` argument. Returns Error prose on refusal.
    /// For resetScope, validation-only here — durable archive is ScopeReset.commit.
    let apply
        (toolName: string)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        (epochOf: HostToolContext -> string)
        : Result<obj, string> =
        let scope = field args.Raw "scope"

        match isNullish scope, ctx.ToolCallId with
        | true, _ -> Error "scope annotation required on every tool call (see common law «Scope»)"
        | _, None -> Error "scope annotation requires tool call id"
        | false, Some call -> applyCall toolName scope ctx epochOf call

    /// Tools that clear a consultation gate (first negotiate, first
    /// askPreviousScope after a reset). A resetScope annotation on one of them
    /// commits after the tool ran: the reset is gated on exactly the call that
    /// carries it, so committing first would refuse the unlocking call itself.
    let private unlocksReset (toolName: string) =
        toolName = "negotiate"
        || toolName = "askPreviousScope"
        || toolName = "askPrevious"

    /// The reset decided this turn on the new epoch: sibling calls may say
    /// meToo, and the new epoch's retention journal starts with its ScopeReset.
    let private decideResetTurn
        (toolName: string)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        (epochOf: HostToolContext -> string)
        =
        match ctx.ToolCallId with
        | None -> ()
        | Some call ->
            let callText = ToolCallId.value call
            let runText = runTextOf ctx callText
            let epoch = currentEpoch epochOf ctx

            scopeRetention?apply (ctx.SessionId, epoch, toolName, runText, callText, runText, field args.Raw "scope")
            |> ignore

    let private commitReset
        (toolName: string)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        (epochOf: HostToolContext -> string)
        (journalOpt: AgentJournal option)
        (snapshotPort: ISessionSnapshotPort option)
        (receipt: obj)
        : Task<Result<unit, string>> =
        task {
            let replay = ScopeReset.alreadyCommitted journalOpt ctx
            let goal = asString receipt?goal
            let oldV = asString receipt?oldVerbatim
            let budget = asInt receipt?remaining 0
            let! committed = ScopeReset.commit journalOpt snapshotPort ctx goal oldV budget

            match committed with
            | Error reason ->
                // A refused reset decided nothing; siblings fall back to the ordinary rule.
                resetRuns.Remove ctx.SessionId |> ignore
                return Error reason
            | Ok _ when not replay ->
                decideResetTurn toolName args ctx epochOf
                return Ok()
            | Ok _ -> return Ok()
        }

    let private runAfterReset commit inner args ctx =
        task {
            let! committed = commit ()

            match committed with
            | Error reason -> return reason
            | Ok() -> return! inner args ctx
        }

    let private runBeforeReset commit inner args ctx =
        task {
            let! result = inner args ctx
            let! committed = commit ()

            return
                committed
                |> Result.map (fun () -> result)
                |> Result.defaultWith (fun reason -> result + "\n\n" + reason)
        }

    let wrap
        (toolName: string)
        (epochOf: HostToolContext -> string)
        (journalOpt: AgentJournal option)
        (snapshotPort: ISessionSnapshotPort option)
        (inner: HostToolArguments -> HostToolContext -> Task<string>)
        : HostToolArguments -> HostToolContext -> Task<string> =
        fun args ctx ->
            task {
                let reset receipt () =
                    commitReset toolName args ctx epochOf journalOpt snapshotPort receipt

                match apply toolName args ctx epochOf with
                | Error reason -> return reason
                | Ok receipt when unbox<bool> (receipt?reanchor) <> true -> return! inner args ctx
                | Ok receipt when unlocksReset toolName -> return! runBeforeReset (reset receipt) inner args ctx
                | Ok receipt -> return! runAfterReset (reset receipt) inner args ctx
            }

    let injectArgument (factory: HostToolFactory) (spec: ToolSpec) : ToolSpec =
        let already = spec.Arguments |> List.exists (fun (name, _) -> name = "scope")

        if already then
            spec
        else
            { spec with
                Arguments = spec.Arguments @ [ "scope", ToolHostCodec.scopeAnnotationSchema factory ] }
