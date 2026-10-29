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
    let private resetRuns =
        System.Collections.Generic.Dictionary<string, string>()

    let private asString (value: obj) : string =
        if isNullish value then "" else string value

    let private asInt (value: obj) (fallback: int) : int =
        if isNullish value then fallback
        else
            match System.Int32.TryParse(string value) with
            | true, n -> n
            | _ -> fallback

    /// Apply the required `scope` argument. Returns Error prose on refusal.
    /// For resetScope, validation-only here — durable archive is ScopeReset.commit.
    let apply
        (toolName: string)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        (epochOf: HostToolContext -> string)
        : Result<obj, string> =
        let scope = field args.Raw "scope"

        if isNullish scope then
            Error "scope annotation required on every tool call (see common law «Scope»)"
        else
            match ctx.ToolCallId with
            | None -> Error "scope annotation requires tool call id"
            | Some call ->
                let callText = ToolCallId.value call
                let runText =
                    match ctx.ProviderRunId with
                    | Some run -> ProviderRunIdentity.value run
                    | None -> callText

                let beatId = runText

                let epoch =
                    let e = epochOf ctx
                    if String.IsNullOrWhiteSpace e then "0" else e

                let kind = asString (field scope "kind")
                // resetScope: validate fields without mutating retention on the
                // old epoch — ScopeReset.commit seeds the new epoch.
                if kind = "resetScope" then
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
                elif kind = "meToo"
                     && (match resetRuns.TryGetValue ctx.SessionId with
                         | true, run -> run = runText
                         | _ -> false) then
                    Ok(createObj [ "ok" ==> true; "kind" ==> "meToo"; "reanchor" ==> false ])
                else
                    let receipt: obj =
                        scopeRetention?apply (
                            ctx.SessionId,
                            epoch,
                            toolName,
                            runText,
                            callText,
                            beatId,
                            scope
                        )

                    if isNullish receipt || not (unbox<bool> receipt?ok) then
                        let reason =
                            if isNullish receipt then
                                "scope annotation refused"
                            else
                                string receipt?reason

                        Error reason
                    else
                        let rem = receipt?remaining

                        if not (isNullish rem) then
                            try
                                Surface.setRetentionBudget ctx.SessionId (unbox<int> rem)
                            with _ ->
                                ()

                        Ok receipt

    /// Tools that clear a consultation gate (first negotiate, first
    /// askPreviousScope after a reset). A resetScope annotation on one of them
    /// commits after the tool ran: the reset is gated on exactly the call that
    /// carries it, so committing first would refuse the unlocking call itself.
    let private unlocksReset (toolName: string) =
        toolName = "negotiate" || toolName = "askPreviousScope" || toolName = "askPrevious"

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
            let runText =
                match ctx.ProviderRunId with
                | Some run -> ProviderRunIdentity.value run
                | None -> callText
            let epoch =
                let e = epochOf ctx
                if String.IsNullOrWhiteSpace e then "0" else e
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
            | Ok _ ->
                if not replay then decideResetTurn toolName args ctx epochOf
                return Ok()
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
                match apply toolName args ctx epochOf with
                | Error reason -> return reason
                | Ok receipt ->
                    if unbox<bool> (receipt?reanchor) = true then
                        if unlocksReset toolName then
                            let! result = inner args ctx
                            let! committed = commitReset toolName args ctx epochOf journalOpt snapshotPort receipt
                            match committed with
                            | Ok() -> return result
                            | Error reason -> return result + "\n\n" + reason
                        else
                            let! committed = commitReset toolName args ctx epochOf journalOpt snapshotPort receipt
                            match committed with
                            | Error reason -> return reason
                            | Ok() -> return! inner args ctx
                    else
                        return! inner args ctx
            }

    let injectArgument (factory: HostToolFactory) (spec: ToolSpec) : ToolSpec =
        let already = spec.Arguments |> List.exists (fun (name, _) -> name = "scope")

        if already then
            spec
        else
            { spec with
                Arguments = spec.Arguments @ [ "scope", ToolHostCodec.scopeAnnotationSchema factory ] }
