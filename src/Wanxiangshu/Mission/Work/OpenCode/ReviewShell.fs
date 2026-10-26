namespace Wanxiangshu.Mission.Work.OpenCode

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Host
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Mission.Work
open Wanxiangshu.Mission.Work.Review
open Wanxiangshu.Mission.Lifecycle
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Repository.Programming.Js

/// The one `review` tool. Plan and Manager both send the same Review command
/// into Work. The shell does not choose retirement, relay, advance, or close.
module ReviewShell =

    [<RequireQualifiedAccess>]
    module private Path =
        [<Literal>]
        let Description = "tool/review/description"

        [<Literal>]
        let FindingsArgument = "tool/review/findings-argument"

    let private providerText (path: string) substitutions =
        ProviderProse.render (ProviderLanguageBinding.readGlobalPreference ()) path substitutions

    let private rejected reason =
        ToolResultText.tomlObjectWithInstructions
            [ reason ]
            [ "recorded", ToolResultText.TBool false ]

    let private accepted (reply: obj) =
        let kind = string reply?kind
        let milestone = string reply?milestone
        let wait = string reply?wait

        ToolResultText.tomlObjectWithInstructions
            [ sprintf "kind = %s\nmilestone = %s\nwait = %s" kind milestone wait ]
            [ "recorded", ToolResultText.TBool true
              "kind", ToolResultText.TString kind ]

    let private updatesOf (findings: ReviewFindings) : obj =
        ReviewFindings.values findings
        |> List.map (fun finding ->
            let value: obj =
                match finding.Value with
                | Some text -> box text
                | None -> null

            createObj [ "key", box finding.Key; "value", value ])
        |> Array.ofList
        |> box

    let private digestOf (raw: obj) =
        if isNull raw then
            ""
        else
            let note: obj = raw?note
            if isNull note then "" else string note

    let private blockersOf (sessionId: string) : obj =
        JsPrograms.deliveryBlockersWithChoicesForSession sessionId
        |> List.map fst
        |> Array.ofList
        |> box

    let private officeOf (scope: ToolRuntimeScope) (context: HostToolContext) =
        if scope.IsRole(context, Role.Plan) then
            Ok "Plan"
        elif scope.IsRole(context, Role.Manager) then
            Ok "Manager"
        else
            Error "review is admitted only for Plan and Manager"

    let private fenceOf (context: HostToolContext) =
        match context.ProviderRunId, context.ToolCallId with
        | Some run, Some call -> Ok(ProviderRunIdentity.value run, ToolCallId.value call)
        | _ -> Error "review requires the calling provider run and tool call"

    /// Findings are the obligation updates. Work applies them and decides the wait.
    let private run
        (gitCommonDir: string)
        (scope: ToolRuntimeScope)
        (args: HostToolArguments)
        (context: HostToolContext)
        : obj =
        if String.IsNullOrWhiteSpace gitCommonDir then
            createObj [ "status", box "Conflict"; "kind", box ""; "reason", box "git common directory is missing" ]
        else
            match Model.tryParse args.Raw, officeOf scope context, fenceOf context with
            | Error reason, _, _
            | _, Error reason, _
            | _, _, Error reason ->
                createObj [ "status", box "Conflict"; "kind", box ""; "reason", box reason ]
            | Ok findings, Ok office, Ok(providerRunId, toolCallId) ->
                DurableWorkHost.submitReview
                    gitCommonDir
                    context.SessionId
                    (providerRunId + ":" + toolCallId)
                    office
                    providerRunId
                    toolCallId
                    (updatesOf findings)
                    (blockersOf context.SessionId)
                    (digestOf args.Raw)
                    (scope.ReviewPorts context)

    let spec (factory: HostToolFactory) (scope: ToolRuntimeScope) : ToolSpec =
        let findingSchema =
            ToolHostCodec.objectSchemaOf
                [ "key", ToolHostCodec.stringSchema factory
                  "value", ToolHostCodec.anySchema factory ]
                factory

        let findingsSchema =
            ToolHostCodec.arraySchemaOf findingSchema (providerText Path.FindingsArgument Map.empty) factory

        let noteArgument = "note", ToolHostCodec.optionalStringSchema factory

        let dispatch args context : Task<string> =
            task {
                let! gitCommonDir =
                    match scope.WorkspaceDirectory with
                    | Some ws -> RuntimePath.stateRoot ws
                    | None -> Task.FromResult ""

                let produced = run gitCommonDir scope args context

                let! reply =
                    if produced <> null && emitJsExpr produced "typeof $0.then === 'function'" then
                        unbox<Task<obj>> produced
                    else
                        Task.FromResult produced

                let status = string reply?status

                if status = "Committed" || status = "Replayed" || status = "Observed" then
                    return accepted reply
                else
                    return rejected (string reply?reason)
            }

        { Name = "review"
          Description = providerText Path.Description Map.empty
          Arguments = [ "findings", findingsSchema; noteArgument ]
          Execute = dispatch }
