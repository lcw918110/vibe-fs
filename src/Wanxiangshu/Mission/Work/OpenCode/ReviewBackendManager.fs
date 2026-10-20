namespace Wanxiangshu.Mission.Work.OpenCode

open System
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git
open Wanxiangshu.Host
open Wanxiangshu.Mission.Lifecycle
open Wanxiangshu.Mission.Work
open Wanxiangshu.Mission.Work.Review
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal

/// work-038: the Manager backend of the shared `review` shell.
/// Owns the review binding, transaction and failure paths; the
/// shell (ReviewShell) dispatches to it for every non-Plan caller. The logic
/// below is the former ReviewTool body, moved without behavioral change.
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
        let MissingRequest = "tool/review/missing-request"

        [<Literal>]
        let InvalidFindings = "tool/review/invalid-findings"

        [<Literal>]
        let RecordFailed = "tool/review/record-failed"

        [<Literal>]
        let ReplayConflict = "tool/review/replay-conflict"

        [<Literal>]
        let AlreadySubmitted = "tool/review/already-submitted"

    /// The reviewer is never the incumbency owner. A manager work opens with
    /// the session id as owner, so the reviewer must be a distinct identity;
    /// the kernel refuses an owner reviewing its own incumbency.
    [<Literal>]
    let private ReviewerId = "manager-reviewer"

    type private ReviewFailure =
        | BindingUnavailable
        | MissingRequest
        | InvalidFindings
        | RecordFailed
        | ReplayConflict
        | AlreadySubmitted

    let private failurePath =
        function
        | BindingUnavailable -> Path.BindingUnavailable
        | MissingRequest -> Path.MissingRequest
        | InvalidFindings -> Path.InvalidFindings
        | RecordFailed -> Path.RecordFailed
        | ReplayConflict -> Path.ReplayConflict
        | AlreadySubmitted -> Path.AlreadySubmitted

    let private providerText (path: string) substitutions =
        ProviderProse.render (ProviderLanguageBinding.readGlobalPreference ()) path substitutions

    let private rejectedResult failure =
        ToolHostCodec.tomlObjectWithInstructions
            [ providerText (failurePath failure) Map.empty ]
            [ "recorded", ToolHostCodec.TBool false ]

    let private requireSome error =
        function
        | Some value -> Ok value
        | None -> Error error

    let private narrativeOf texts =
        texts |> List.rev |> String.concat "\n" |> (fun text -> text.Trim()) |> Ok

    let private publicNarrativeBefore (toolCallId: ToolCallId) (message: SessionMessage) =
        let expected = ToolCallId.value toolCallId

        let nextPart index texts part =
            match part with
            | MessagePart.ToolCall(callId, name, _) when callId = expected && name = "review" ->
                narrativeOf texts |> Result.map Choice2Of2
            | MessagePart.Text text -> Choice1Of2(index + 1, text :: texts) |> Ok
            | MessagePart.Reasoning _
            | MessagePart.ToolCall _
            | MessagePart.ToolResult _
            | MessagePart.Activity _ -> Choice1Of2(index + 1, texts) |> Ok

        let rec loop index texts =
            if index >= message.Parts.Length then
                Error BindingUnavailable
            else
                nextPart index texts message.Parts.[index]
                |> Result.bind (function
                    | Choice1Of2(nextIndex, nextTexts) -> loop nextIndex nextTexts
                    | Choice2Of2 narrative -> Ok narrative)

        loop 0 []

    let private messageText (message: SessionMessage) =
        message.Parts
        |> Array.choose (function
            | MessagePart.Text text -> Some text
            | _ -> None)
        |> String.concat "\n"
        |> fun value -> value.Trim()

    let private requireRootText text =
        if String.IsNullOrWhiteSpace text then
            Error MissingRequest
        else
            Ok text

    let private rootText (physicalUserMessageId: string) (messages: SessionMessage list) =
        match messages |> List.tryFind (fun message -> message.Id = physicalUserMessageId) with
        | None -> Error MissingRequest
        | Some message -> message |> messageText |> requireRootText

    let private validateLocation providerRun (location: SessionSnapshot.ToolCallLocation) =
        if location.ToolName <> "review" then
            Error BindingUnavailable
        elif location.ProviderRun <> providerRun then
            Error BindingUnavailable
        else
            Ok location

    let private bindingFromMessages physicalUserMessageId rootAuthorityUserMessageId toolCallId providerRun messages =
        SessionSnapshot.locateToolCall toolCallId messages
        |> Result.mapError (fun _ -> BindingUnavailable)
        |> Result.bind (validateLocation providerRun)
        |> Result.bind (fun location ->
            messages
            |> List.tryFind (fun message -> message.Id = ProviderRunIdentity.value providerRun)
            |> requireSome BindingUnavailable
            |> Result.bind (fun message ->
                rootText rootAuthorityUserMessageId messages
                |> Result.bind (fun root ->
                    publicNarrativeBefore toolCallId message
                    |> Result.map (fun narrative ->
                        let narrativeDigest = HostDigest.sha256Hex narrative
                        let rootDigest = HostDigest.sha256Hex root
                        let payloadDigest = HostDigest.sha256Hex location.InputCanonical

                        let requirementSetDigest =
                            HostDigest.sha256Hex (
                                "requirements-v1\n" + rootAuthorityUserMessageId + "\n" + rootDigest
                            )

                        let evidenceFrontierDigest =
                            HostDigest.sha256Hex (
                                String.concat
                                    "\n"
                                    [ ProviderRunIdentity.value providerRun
                                      ToolCallId.value toolCallId
                                      narrativeDigest
                                      rootDigest
                                      payloadDigest ]
                            )

                        { PhysicalUserMessageId = physicalUserMessageId
                          ProviderRunId = ProviderRunIdentity.value providerRun
                          ToolCallId = ToolCallId.value toolCallId
                          NarrativeDigest = narrativeDigest
                          PayloadDigest = payloadDigest
                          RootRequestDigest = rootDigest
                          RequirementSetDigest = requirementSetDigest
                          EvidenceFrontierDigest = evidenceFrontierDigest }))))

    let private captureBinding
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (physicalUserMessageId: string)
        (rootAuthorityUserMessageId: string)
        (toolCallId: ToolCallId)
        (providerRun: ProviderRunIdentity)
        : Task<Result<ReviewBinding, ReviewFailure>> =
        match scope.Snapshot with
        | None -> Task.FromResult(Error BindingUnavailable)
        | Some snapshot ->
            task {
                let! loaded = snapshot.GetMessages(SessionId.create context.SessionId)

                return
                    loaded
                    |> Result.mapError (fun _ -> BindingUnavailable)
                    |> Result.bind (
                        bindingFromMessages physicalUserMessageId rootAuthorityUserMessageId toolCallId providerRun
                    )
            }

    /// The one lifecycle work this session owns. It is independent of the
    /// workspace EventStore; only the git common directory selects its journal.
    let private lifecycleWorkOf
        (directory: string)
        (sessionId: string)
        : Task<Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option> =
        task {
            let! commonDir = RuntimePath.stateRoot directory

            if String.IsNullOrWhiteSpace commonDir then
                return None
            else
                return Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected commonDir sessionId
        }

    type private BoundReviewInvocation =
        { Findings: ReviewFindings
          ToolCallId: ToolCallId
          ProviderRun: ProviderRunIdentity
          PhysicalUserMessageId: string
          Directory: string
          Journal: AgentJournal }

    type private PreparedAssessment =
        { Bound: BoundReviewInvocation
          SessionId: SessionId
          RootAuthorityUserMessageId: string
          IncumbencyId: string }

    let private boundInvocation (scope: ToolRuntimeScope) (args: HostToolArguments) (context: HostToolContext) =
        Model.tryParse args.Raw
        |> Result.mapError (fun _ -> InvalidFindings)
        |> Result.bind (fun findings ->
            match
                context.ToolCallId,
                context.ProviderRunId,
                scope.CurrentPhysicalUserMessage context.SessionId,
                scope.WorkspaceDirectory,
                scope.Journal
            with
            | None, _, _, _, _ -> Error BindingUnavailable
            | _, None, _, _, _ -> Error BindingUnavailable
            | _, _, None, _, _ -> Error MissingRequest
            | _, _, _, None, _ -> Error BindingUnavailable
            | _, _, _, _, None -> Error BindingUnavailable
            | Some toolCallId, Some providerRun, Some physicalUserMessageId, Some directory, Some journal ->
                Ok
                    { Findings = findings
                      ToolCallId = toolCallId
                      ProviderRun = providerRun
                      PhysicalUserMessageId = physicalUserMessageId
                      Directory = directory
                      Journal = journal })

    let private resolveAssessment
        (scope: ToolRuntimeScope)
        (sessionId: SessionId)
        (directorySessionId: string)
        (bound: BoundReviewInvocation)
        : Task<Result<PreparedAssessment, ReviewFailure>> =
        task {
            let! workOpt = lifecycleWorkOf bound.Directory directorySessionId

            return
                workOpt
                |> requireSome BindingUnavailable
                |> Result.bind (fun work ->
                    match work.ActiveId, work.Reviewed with
                    | None, _ -> Error BindingUnavailable
                    | Some _, true -> Error AlreadySubmitted
                    | Some activeId, false ->
                        scope.ActiveProfileFor sessionId
                        |> Option.map (fun profile ->
                            AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId)
                        |> requireSome BindingUnavailable
                        |> Result.map (fun rootAuthorityUserMessageId ->
                            { Bound = bound
                              SessionId = sessionId
                              RootAuthorityUserMessageId = rootAuthorityUserMessageId
                              IncumbencyId = activeId }))
        }

    let private prepareAssessment
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (bound: BoundReviewInvocation)
        : Task<Result<PreparedAssessment, ReviewFailure>> =
        task {
            let sessionId = SessionId.create context.SessionId

            try
                return! resolveAssessment scope sessionId context.SessionId bound
            with _ ->
                return Error BindingUnavailable
        }

    let private acceptedResult (findings: ReviewFindings) =
        let instructionPath =
            if ReviewFindings.isEmpty findings then
                Path.Finish
            else
                Path.Work

        let substitutions =
            if instructionPath = Path.Finish then
                // The review just recorded, so no confirmation is durable yet.
                Map [ "closeout", Wanxiangshu.Mission.Manager.ManagerWorkflow.finishCloseoutText false ]
            else
                Map.empty

        ToolHostCodec.tomlObjectWithInstructions
            [ providerText instructionPath substitutions ]
            [ "recorded", ToolHostCodec.TBool true ]

    /// The write receipt decides the result. A duplicate review is a structured
    /// failure; a committed or replayed write is accepted.
    let private reviewOutcome
        (findings: ReviewFindings)
        (receipt: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.HostReceipt)
        : Result<string, ReviewFailure> =
        if receipt.Reason = "Incumbency already reviewed" then
            Error RecordFailed
        elif receipt.Status = "Committed" || receipt.Status = "Replayed" then
            Ok(acceptedResult findings)
        else
            Error RecordFailed

    let private runRecorded
        (context: HostToolContext)
        (prepared: PreparedAssessment)
        : Task<Result<string, ReviewFailure>> =
        task {
            match! lifecycleWorkOf prepared.Bound.Directory context.SessionId with
            | None -> return Error RecordFailed
            | Some _ ->
                let! gitCommonDir = Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot prepared.Bound.Directory

                let requestId = prepared.IncumbencyId + ":review"

                let findings =
                    prepared.Bound.Findings
                    |> Wanxiangshu.Mission.Work.ReviewFindings.values
                    |> List.map (fun finding -> finding.AcceptanceCriteria + "\n" + finding.WorkPlan)
                    |> List.toArray
                    |> box

                let command =
                    Wanxiangshu.Mission.Lifecycle.DurableWorkHost.command
                        "Review"
                        [ "incumbencyId", box prepared.IncumbencyId
                          "reviewerId", box ReviewerId
                          "findings", findings ]

                let receipt =
                    Wanxiangshu.Mission.Lifecycle.DurableWorkHost.execute
                        gitCommonDir
                        context.SessionId
                        requestId
                        command

                return reviewOutcome prepared.Bound.Findings receipt
        }

    /// The one review write: send `Review` to the wanxiangli work owner. The
    /// binding is still validated first (a rejected invocation writes nothing
    /// and consumes no slot), but its eight fields do
    /// not enter the event.
    let private runPrepared
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (prepared: PreparedAssessment)
        : Task<Result<string, ReviewFailure>> =
        task {
            let! binding =
                captureBinding
                    scope
                    context
                    prepared.Bound.PhysicalUserMessageId
                    prepared.RootAuthorityUserMessageId
                    prepared.Bound.ToolCallId
                    prepared.Bound.ProviderRun

            match binding with
            | Error failure -> return Error failure
            | Ok _ -> return! runRecorded context prepared
        }

    let private renderExecution =
        function
        | Ok value -> value
        | Error failure -> rejectedResult failure

    let execute (scope: ToolRuntimeScope) (args: HostToolArguments) (context: HostToolContext) =
        task {
            let! outcome =
                taskResult {
                    let! bound = boundInvocation scope args context
                    let! plan = prepareAssessment scope context bound
                    return! runPrepared scope context plan
                }

            return renderExecution outcome
        }
