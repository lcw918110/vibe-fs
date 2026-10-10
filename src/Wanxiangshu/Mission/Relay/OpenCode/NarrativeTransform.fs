namespace Wanxiangshu.Mission.Relay.OpenCode

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Mission.Relay
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.Persistence.Journal

[<RequireQualifiedAccess>]
type RelayProjectionDisposition =
    | Unchanged
    | CurrentIteration
    | RetiredAttemptStopped

module RelayNarrativeTransform =
    let private relayRoad (journal: AgentJournal) (sessionId: SessionId) =
        AgentProjection.tryFind sessionId (AgentJournal.snapshot journal).AgentProjections
        |> Option.bind (fun session -> session.Relay)
        |> Option.bind (fun relay -> Fold.view relay (RoadId.create (SessionId.value sessionId)))

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

    let private managerLoopGatePhysical (journal: AgentJournal) (sessionId: SessionId) (retirement: RetirementSummary) =
        let gateKind = ManagerLoopGate.gateKind retirement.Id
        let terminalRun = ProviderRunIdentity.create retirement.ProjectionCut.ProviderRunId

        PromptAuthorityProjectionQueries.activeProfile sessionId (AgentJournal.snapshot journal).AgentProjections
        |> Option.bind (fun profile ->
            (PromptDispatcher.forPrompts (PromptJournalAdapter.create journal))
                .GateNudgeAcceptedPhysical
                profile
                PromptAuthority.ContinuationKind.ManagerGuard
                gateKind
                terminalRun)

    let private managerLoopGateAdmitted (journal: AgentJournal) (sessionId: SessionId) (retirement: RetirementSummary) =
        let gateKind = ManagerLoopGate.gateKind retirement.Id
        let terminalRun = ProviderRunIdentity.create retirement.ProjectionCut.ProviderRunId
        let payloadDigest = PromptAuthority.gateNudgePayloadDigest gateKind terminalRun

        let snapshot = AgentJournal.snapshot journal

        let authority =
            AgentProjection.tryFind sessionId snapshot.AgentProjections
            |> Option.bind (fun s -> s.PromptAuthority)

        let pendingKeys =
            authority
            |> Option.map (fun a ->
                a.PendingClaims
                |> Map.toList
                |> List.map (fun (_, claim) -> claim.PayloadDigest))
            |> Option.defaultValue []

        let acceptedKeys =
            authority
            |> Option.map (fun a ->
                a.AcceptedDispatches
                |> Map.toList
                |> List.map (fun (_, dispatch) -> dispatch.PayloadDigest))
            |> Option.defaultValue []

        let hasProfile =
            PromptAuthorityProjectionQueries.activeProfile sessionId snapshot.AgentProjections
            |> Option.isSome

        PromptAuthorityProjectionQueries.activeProfile sessionId snapshot.AgentProjections
        |> Option.map (fun profile ->
            (PromptDispatcher.forPrompts (PromptJournalAdapter.create journal))
                .GateNudgeAlreadyAdmitted
                profile
                PromptAuthority.ContinuationKind.ManagerGuard
                gateKind
                terminalRun)
        |> Option.defaultValue false

    let private cutToolIndex (cut: ProjectionCut) messages =
        messages
        |> List.tryFindIndex (fun message ->
            ProviderWireDecode.rawPartsOf message
            |> List.choose ProviderWireDecode.decodePart
            |> List.exists (function
                | WireToolCall(toolCallId, _, _)
                | WireToolResult(toolCallId, _) -> ToolCallId.value toolCallId = cut.ToolCallId
                | _ -> false))

    let private activeRetirement (road: RoadView) =
        road.LatestRetirement |> Option.filter (fun _ -> road.ActiveIncumbency.IsSome)

    let private dispositionAfterProjection (road: RoadView) =
        activeRetirement road
        |> Option.map (fun _ -> RelayProjectionDisposition.CurrentIteration)
        |> Option.defaultValue RelayProjectionDisposition.Unchanged

    let private requestBelongsToSuccessor
        (physical: string option)
        afterCut
        freshRoot
        acceptedRequest
        gatePhysical
        gateAdmitted
        cutMissing
        isFreshPhysical
        =
        match physical with
        | Some current ->
            freshRoot = Some current
            || gatePhysical = Some current
            || (afterCut && acceptedRequest)
            || (gateAdmitted && isFreshPhysical && not afterCut)
            || (gateAdmitted && cutMissing)
        | _ -> false

    let private isSuccessorRequest
        journal
        sessionId
        (road: RoadView)
        (retirement: RetirementSummary)
        acceptedRequest
        messages
        =
        let currentUser =
            messages
            |> List.indexed
            |> List.choose (fun (index, message) ->
                if messageRoleIsUser message then
                    messageId message |> Option.map (fun physical -> index, physical)
                else
                    None)
            |> List.tryLast

        let cutIndex = cutToolIndex retirement.ProjectionCut messages

        let afterCut =
            match cutIndex, currentUser with
            | Some toolIndex, Some(userIndex, _) -> userIndex > toolIndex
            | _ -> false

        let cutMissing = cutIndex.IsNone

        let projection =
            AgentProjection.tryFind sessionId (AgentJournal.snapshot journal).AgentProjections
            |> Option.bind (fun session -> session.PromptAuthority)

        let freshRoot =
            projection
            |> Option.bind (fun authority -> authority.ActiveLogicalRun)
            |> Option.map (fun profile -> AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId)
            |> Option.filter (fun root ->
                road.AuthorityMessageIds
                |> List.exists (fun oldRoot -> PhysicalUserMessageId.value oldRoot = root)
                |> not)

        let physical = currentUser |> Option.map snd

        let gatePhysical =
            managerLoopGatePhysical journal sessionId retirement
            |> Option.map Wanxiangshu.Foundation.Identity.PhysicalUserMessageId.value

        let gateAdmitted = managerLoopGateAdmitted journal sessionId retirement

        // A gate nudge is admitted (pending) before its physical prompt lands on
        // the wire, so `gateAdmitted` alone cannot distinguish the nudge's own
        // prompt from a stale pre-retirement request. Only treat an admitted gate
        // as successor evidence when the current physical is not one of the road's
        // already-consumed authority messages (i.e. a genuinely fresh head).
        let isFreshPhysical =
            physical
            |> Option.map (fun current ->
                road.AuthorityMessageIds
                |> List.exists (fun oldRoot -> PhysicalUserMessageId.value oldRoot = current)
                |> not)
            |> Option.defaultValue false

        requestBelongsToSuccessor
            physical
            afterCut
            freshRoot
            acceptedRequest
            gatePhysical
            gateAdmitted
            cutMissing
            isFreshPhysical

    let private staleRetirement journal sessionId (road: RoadView) acceptedRequest messages =
        road.LatestRetirement
        |> Option.filter (fun retirement ->
            not (isSuccessorRequest journal sessionId road retirement acceptedRequest messages))

    // The provider view keeps the full physical history after a retirement:
    // the next iteration sees every prior message, the retirement tool call
    // and its own fresh-head prompt. The cut now only decides request
    // identity for stale attempts; it never filters the provider message set.
    let private projectActive (road: RoadView) outObj =
        let messages = ProviderWireDecode.messagesFromTransformOutput outObj

        HostMessageProjection.replaceMessagesInPlace outObj messages
        dispositionAfterProjection road

    let private project journal (interruptAttempt: SessionId -> Task<unit>) sessionId road acceptedRequest outObj =
        task {
            let messages = ProviderWireDecode.messagesFromTransformOutput outObj

            // An active LogicalRun or a claimed loop gate does not identify this
            // physical request: both can coexist with the retired attempt.
            match staleRetirement journal sessionId road acceptedRequest messages with
            | Some _ ->
                do! interruptAttempt sessionId
                HostMessageProjection.replaceMessagesInPlace outObj []
                return RelayProjectionDisposition.RetiredAttemptStopped
            | None -> return projectActive road outObj
        }

    let apply
        (journal: AgentJournal option)
        (acceptedRequest: bool)
        (interruptAttempt: SessionId -> Task<unit>)
        (sessionId: string option)
        (outObj: obj)
        : Task<RelayProjectionDisposition> =
        task {
            let resolved =
                journal
                |> Option.bind (fun durable ->
                    sessionId
                    |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))
                    |> Option.bind (fun value ->
                        let sid = SessionId.create value
                        relayRoad durable sid |> Option.map (fun road -> durable, sid, road)))

            match resolved with
            | Some(durable, currentSessionId, road) ->
                return! project durable interruptAttempt currentSessionId road acceptedRequest outObj
            | None -> return RelayProjectionDisposition.Unchanged
        }
