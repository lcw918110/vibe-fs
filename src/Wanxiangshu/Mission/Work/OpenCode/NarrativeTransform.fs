namespace Wanxiangshu.Mission.Work.OpenCode

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Mission.Work
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Mission.Lifecycle
open Wanxiangshu.Mission.Planning
open Wanxiangshu.Persistence.Journal

[<RequireQualifiedAccess>]
type RelayProjectionDisposition =
    | Unchanged
    | CurrentTenure
    | RetiredAttemptStopped

module RelayNarrativeTransform =
    let private messageId message =
        ProviderWireDecode.hostMessageId message

    let private readField (value: obj) (name: string) : obj =
        if isNull value then
            null
        else
            emitJsExpr (value, name) "$0[$1]"

    let private messageRole message =
        readField (readField message "info") "role"
        |> Option.ofObj
        |> Option.orElseWith (fun () -> readField message "role" |> Option.ofObj)
        |> Option.map (fun value -> unbox<string> value)

    let private messageRoleIsUser message =
        messageRole message
        |> Option.exists (fun role -> role.ToLowerInvariant() = "user")

    let private cutToolIndex (cutToolCallId: string) messages =
        messages
        |> List.tryFindIndex (fun message ->
            ProviderWireDecode.rawPartsOf message
            |> List.choose ProviderWireDecode.decodePart
            |> List.exists (function
                | WireToolCall(toolCallId, _, _)
                | WireToolResult(toolCallId, _) -> ToolCallId.value toolCallId = cutToolCallId
                | _ -> false))

    /// The projection disposition of one request. An active tenure means the
    /// message set belongs to the current iteration; a work with no active
    /// tenure has no iteration to project and stays unchanged. Absence is not
    /// a previous tenure and not permission to continue.
    let private dispositionAfterProjection (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option) =
        match work with
        | Some current when current.ActiveId.IsSome -> RelayProjectionDisposition.CurrentTenure
        | _ -> RelayProjectionDisposition.Unchanged

    let private requestBelongsToSuccessor (physical: string option) afterCut freshRoot acceptedRequest gatePhysical =
        match physical with
        | Some current ->
            freshRoot = Some current
            || gatePhysical = Some current
            || (afterCut && acceptedRequest)
        | _ -> false

    /// A request is after the cut when its last real user message sits past
    /// the message that carries the cut tool call.
    let private afterCut (cutToolCallId: string) (lastUserIndex: int option) messages =
        match lastUserIndex, cutToolIndex cutToolCallId messages with
        | Some userIndex, Some cutIndex -> userIndex > cutIndex
        | _ -> false

    /// The retired attempt is decided from the unified lifecycle work: an S4
    /// work whose current tenure was Relayed, with a durable retirement cut.
    /// The request is stale unless it belongs to the successor (the loop-gate
    /// nudge, or an accepted request observed after the cut).
    let private staleRetirement
        (journal: AgentJournal)
        (sessionId: SessionId)
        (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork)
        (acceptedRequest: bool)
        (messages: obj list)
        =
        let relayedCut =
            work.Retirements
            |> List.tryFind (fun retirement -> retirement.ProjectionCut && retirement.Outcome = "Relayed")

        let sessionProjection =
            AgentProjection.tryFind sessionId (AgentJournal.snapshot journal).AgentProjections

        let lastUserIndex =
            messages
            |> List.indexed
            |> List.rev
            |> List.tryPick (fun (index, message) -> if messageRoleIsUser message then Some index else None)

        let physical =
            lastUserIndex
            |> Option.bind (fun index -> messages |> List.tryItem index)
            |> Option.bind messageId

        let requestIsSuccessor (retirement: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.HostRetirement) =
            let physicalAfterCut =
                work.ProjectionCut
                |> Option.map (fun cut -> afterCut cut.ToolCallId lastUserIndex messages)
                |> Option.defaultValue false

            let freshRoot =
                if physicalAfterCut then
                    sessionProjection
                    |> Option.bind (fun session -> session.PromptAuthority)
                    |> Option.bind (fun authority -> authority.ActiveLogicalRun)
                    |> Option.map (fun profile -> AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId)
                else
                    None

            let gatePhysical =
                sessionProjection
                |> Option.bind (fun session -> session.PromptAuthority)
                |> Option.bind (fun authority ->
                    PromptAuthority.gateNudgeAcceptedPhysical
                        sessionId
                        PromptAuthority.ContinuationKind.ManagerGuard
                        (ManagerLoopGate.gateKind (RetirementId.create retirement.RetirementId))
                        (ProviderRunIdentity.create retirement.Fence.ProviderRunId)
                        authority)
                |> Option.map PhysicalUserMessageId.value

            let successorIdentity =
                requestBelongsToSuccessor physical physicalAfterCut freshRoot acceptedRequest gatePhysical

            // A request whose own last user message carries a durable
            // ChatExecution acceptance under the session's CURRENT logical run is
            // a legitimate current-tenure request — but only when the retired
            // attempt's cut tool call is no longer present in the wire (the
            // tenure projection strips the predecessor's interactions). If the
            // wire still carries the cut, the message set demonstrably belongs
            // to the retired attempt, and its acceptance evidence is that
            // attempt's own — never a successor's.
            let cutPresent =
                work.ProjectionCut
                |> Option.map (fun cut -> (cutToolIndex cut.ToolCallId messages).IsSome)
                |> Option.defaultValue false

            let acceptedRunIdentity =
                (not cutPresent)
                && (match physical with
                    | Some physicalId ->
                        let key =
                            { ChatExecutionKey.SessionId = sessionId
                              PhysicalUserMessageId = PhysicalUserMessageId.create physicalId }

                        let accepted =
                            ChatExecutionProjection.byKey
                                key
                                (AgentJournal.snapshot journal).AgentProjections.ChatExecutions

                        let activeRoot =
                            sessionProjection
                            |> Option.bind (fun session -> session.PromptAuthority)
                            |> Option.bind (fun authority -> authority.ActiveLogicalRun)
                            |> Option.map (fun profile -> profile.AuthorityRootUserMessageId)

                        match accepted, activeRoot with
                        | Some execution, Some active -> execution.acceptedEvidence.AuthorityRootUserMessageId = active
                        | _ -> false
                    | None -> false)

            successorIdentity || acceptedRunIdentity

        relayedCut
        |> Option.filter (fun retirement -> not (requestIsSuccessor retirement))

    let isStaleRetiredAttempt
        (journal: AgentJournal option)
        (acceptedRequest: bool)
        (sessionId: string option)
        (lifecycle: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option)
        (outObj: obj)
        : bool =
        match journal, sessionId, lifecycle with
        | Some durable, Some value, Some work when not (String.IsNullOrWhiteSpace value) ->
            let messages = ProviderWireDecode.messagesFromTransformOutput outObj

            staleRetirement durable (SessionId.create value) work acceptedRequest messages
            |> Option.isSome
        | _ -> false

    /// S1/S2/S3/S4 relay no longer clears context. Opening cursor records the
    /// tenure boundary; it does not strip predecessor assistant or tool
    /// messages. Retention and truncation belong to the view declaration.
    let private projectActive
        (_journal: AgentJournal)
        (_sessionId: SessionId)
        (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option)
        _outObj
        =
        task { return dispositionAfterProjection work }

    let private project
        journal
        (interruptAttempt: SessionId -> Task<unit>)
        sessionId
        acceptedRequest
        (lifecycle: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option)
        outObj
        =
        task {
            let messages = ProviderWireDecode.messagesFromTransformOutput outObj

            // An active LogicalRun or a claimed loop gate does not identify this
            // physical request: both can coexist with the retired attempt.
            let stale =
                lifecycle
                |> Option.bind (fun work -> staleRetirement journal sessionId work acceptedRequest messages)

            match stale with
            | Some _ ->
                do! interruptAttempt sessionId
                HostMessageProjection.replaceMessagesInPlace outObj []
                return RelayProjectionDisposition.RetiredAttemptStopped
            | None -> return! projectActive journal sessionId lifecycle outObj
        }

    let apply
        (journal: AgentJournal option)
        (acceptedRequest: bool)
        (interruptAttempt: SessionId -> Task<unit>)
        (sessionId: string option)
        (lifecycle: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option)
        (outObj: obj)
        : Task<RelayProjectionDisposition> =
        task {
            // No unified work means this session is not on the lifecycle track:
            // the projection must leave the message set untouched rather than
            // re-encode it.
            match journal, sessionId, lifecycle with
            | Some durable, Some value, Some work when not (String.IsNullOrWhiteSpace value) ->
                return! project durable interruptAttempt (SessionId.create value) acceptedRequest (Some work) outObj
            | _ -> return RelayProjectionDisposition.Unchanged
        }
