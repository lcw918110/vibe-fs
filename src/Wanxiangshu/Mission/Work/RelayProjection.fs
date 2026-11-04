namespace Wanxiangshu.Mission.Work

open System
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Mission.Lifecycle
open Wanxiangshu.Persistence.Journal


/// How the relay projection of one request ended.
[<RequireQualifiedAccess>]
type RelayProjectionDisposition =
    | Unchanged
    | CurrentTenure
    | RetiredAttemptStopped

/// What the wire carried for one message, reduced to the facts the retirement
/// rule asks about. The role stays on the adapter side: it is handed to the
/// projection library verbatim and is never a decision input.
type RelayMessageFact =
    { Id: string option
      ToolCallIds: string list }

/// The retirement rule's verdict for one message: the id the library sees,
/// whether this message carries the durable cut, and the authority root it was
/// accepted under.
type RelayMessagePolicy =
    { Id: string option
      Cut: bool
      Accepted: string option }

/// The relayed-retirement request the projection library decides on.
type RelayRetirementRequest =
    { ActiveRoot: string option
      GatePhysical: string option
      AcceptedRequest: bool
      Messages: RelayMessagePolicy list }

module RelayProjection =
    /// A blank id never matches: host message ids are never blank
    /// (`ProviderWireDecode.hostMessageId`), so blank roots and physical ids
    /// stay outside the library's comparison.
    let private nonBlank (value: string option) =
        value |> Option.filter (String.IsNullOrWhiteSpace >> not)

    /// The successor's anchors after a Relayed retirement: the active authority
    /// root and the loop-gate nudge accepted for that retirement.
    let private relayAnchor
        (journal: AgentJournal)
        (sessionId: SessionId)
        (retirement: DurableWorkHost.HostRetirement)
        =
        let projections = (AgentJournal.snapshot journal).AgentProjections
        let sessionProjection = AgentProjection.tryFind sessionId projections

        let authority =
            sessionProjection |> Option.bind (fun session -> session.PromptAuthority)

        let activeRoot =
            authority
            |> Option.bind (fun value -> value.ActiveLogicalRun)
            |> Option.map (fun profile -> AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId)

        let gatePhysical =
            authority
            |> Option.bind (fun value ->
                PromptAuthority.gateNudgeAcceptedPhysical
                    sessionId
                    PromptAuthority.ContinuationKind.ManagerGuard
                    (ManagerLoopGate.gateKind (RetirementId.create retirement.RetirementId))
                    (ProviderRunIdentity.create retirement.Fence.ProviderRunId)
                    value)
            |> Option.map PhysicalUserMessageId.value

        nonBlank activeRoot, nonBlank gatePhysical

    /// The retired attempt is decided from the unified lifecycle work: an S4
    /// work whose current tenure was Relayed, with a durable retirement cut.
    /// The request is stale unless it belongs to the successor (the loop-gate
    /// nudge, or an accepted request observed after the cut).
    let staleRequest
        (journal: AgentJournal)
        (sessionId: SessionId)
        (work: DurableWorkHost.InspectedWork)
        (acceptedRequest: bool)
        (messages: RelayMessageFact list)
        : RelayRetirementRequest option =
        work.Retirements
        |> List.tryFindBack (fun retirement -> retirement.ProjectionCut && retirement.Outcome = "Relayed")
        |> Option.map (fun retirement ->
            let projections = (AgentJournal.snapshot journal).AgentProjections
            let activeRoot, gatePhysical = relayAnchor journal sessionId retirement

            let accepted (id: string option) =
                id
                |> Option.bind (fun physicalId ->
                    ChatExecutionProjection.byKey
                        { ChatExecutionKey.SessionId = sessionId
                          PhysicalUserMessageId = PhysicalUserMessageId.create physicalId }
                        projections.ChatExecutions)
                |> Option.map (fun execution ->
                    AuthorityRootUserMessageId.value execution.acceptedEvidence.AuthorityRootUserMessageId)

            let cutOf (message: RelayMessageFact) =
                match work.ProjectionCut with
                | Some cut -> List.contains cut.ToolCallId message.ToolCallIds
                | None -> false

            { ActiveRoot = activeRoot
              GatePhysical = gatePhysical
              AcceptedRequest = acceptedRequest
              Messages =
                messages
                |> List.map (fun message ->
                    { Id = message.Id
                      Cut = cutOf message
                      Accepted = nonBlank (accepted message.Id) }) })

    let private dispositionAfterProjection (work: DurableWorkHost.InspectedWork option) =
        match work with
        | Some current when current.ActiveId.IsSome -> RelayProjectionDisposition.CurrentTenure
        | _ -> RelayProjectionDisposition.Unchanged

    let disposition (work: DurableWorkHost.InspectedWork option) = dispositionAfterProjection work

    /// Durable ownership for the successor's own messages after a relay. A
    /// physical user message belongs to the successor when its accepted
    /// ChatExecution names the active authority root, or when it is the
    /// loop-gate nudge accepted for the Relayed retirement. Everything else
    /// stays unattributed.
    let successorOwns
        (journal: AgentJournal)
        (sessionId: SessionId)
        (work: DurableWorkHost.InspectedWork)
        : string -> bool =
        match work.Retirements |> List.tryLast with
        | None -> fun _ -> false
        | Some retirement ->
            let activeRoot, gatePhysical = relayAnchor journal sessionId retirement
            let projections = (AgentJournal.snapshot journal).AgentProjections

            let acceptedRoot (physicalId: string) =
                ChatExecutionProjection.byKey
                    { ChatExecutionKey.SessionId = sessionId
                      PhysicalUserMessageId = PhysicalUserMessageId.create physicalId }
                    projections.ChatExecutions
                |> Option.map (fun execution ->
                    AuthorityRootUserMessageId.value execution.acceptedEvidence.AuthorityRootUserMessageId)
                |> nonBlank

            fun physicalId ->
                gatePhysical = Some physicalId
                || (activeRoot.IsSome && acceptedRoot physicalId = activeRoot)
