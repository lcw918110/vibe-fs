namespace Wanxiangshu.Sphinx.V2.Core

/// The single v2 inquiry state.
///
/// WHAT[sphinx-v2-018]: physical bindings — session ids, provider request ids, SSE
/// cursors, transport receipts — are stored because recovery and billing need them,
/// but they are kept in `PhysicalBindings` and excluded from the semantic projection.
/// A semantic projection that included them could not claim to be Host-independent.

[<RequireQualifiedAccess>]
type InquiryStatus =
    | Active
    | InputRequired of authorization: string
    | Suspended of reason: string
    | Cancelling
    | StopReached of stopReason: string
    | Failed of reason: string
    | Cancelled of reason: string

type RoundRecord =
    { RoundId: RoundId
      ScopeId: string
      ExpectedWork: Set<WorkId>
      ReceivedWork: Set<WorkId>
      TerminalWork: Set<WorkId>
      Closed: bool
      Outcome: string option }

type InterpretationRecord =
    { ObservationId: ObservationId
      WorkId: WorkId
      Attempt: Attempt
      InterpretationId: string option
      PluginRef: string option
      Status: string
      Reason: string option }

/// A reservation keyed by work identity. A tuple key would work in F# but not as a
/// canonical hash participant, and this record is hashed.
type ReservationKey = { WorkId: WorkId; Attempt: Attempt }

/// An overrun fact. Recorded, never absorbed into a rejection.
type OverrunFact =
    { WorkId: WorkId
      Attempt: Attempt
      Resources: Map<string, float> }

/// A physical binding recovered from a Host. Never participates in semantic hashing.
type PhysicalBinding =
    { WorkId: WorkId
      Attempt: Attempt
      DispatchIntentId: string
      PhysicalRef: string option
      Receipt: string option }

/// One durable command identity owns its content fingerprint and original receipt.
type CommandReceipt =
    { Fingerprint: string
      Revision: Revision
      EventId: EventId }

type InquiryState =
    {
        Id: InquiryId
        ApiVersion: string
        Revision: Revision
        EventHead: EventId option
        Goal: GoalSpec
        ResourceSpecs: ResourceSpec list
        RenderReserve: Map<string, float>
        ConfigHash: string
        ProfileRef: string
        Graph: Map<NodeId, GraphNode>
        Edges: Map<EdgeId, HyperEdge>
        /// Certificates are addressed by their full scope key, never by node alone.
        Certificates: Map<string, CertificateSlotPatch list>
        /// Outstanding reservations: work identity -> reserved resources.
        Work: Map<WorkId, WorkItem>
        Reservations: Map<string, ReservationKey * Map<string, float>>
        SettledUsage: Map<string, float>
        SettledMoneyMinor: int64
        /// Overruns recorded as facts, never absorbed.
        Overruns: OverrunFact list
        Observations: Map<string, ResultAcceptedBody>
        Interpretations: Map<string, InterpretationRecord>
        Rounds: Map<RoundId, RoundRecord>
        Decisions: Map<string, string>
        Answer: AnswerCommittedBody option
        /// Command identity -> immutable content-bound receipt, rebuilt from transitions.
        CommandReceipts: Map<string, CommandReceipt>
        PhysicalBindings: Map<string, PhysicalBinding>
        Status: InquiryStatus
    }

module InquiryState =

    /// The command receipt lookup deliberately precedes the stale-revision check: a
    /// network retry of an already-applied command must return the original receipt
    /// rather than turn into a conflict.
    let commandRevision (state: InquiryState) (commandId: string) : Revision option =
        state.CommandReceipts
        |> Map.tryFind commandId
        |> Option.map (fun receipt -> receipt.Revision)

    let commandReceipt (state: InquiryState) (commandId: string) : CommandReceipt option =
        state.CommandReceipts |> Map.tryFind commandId

    /// Certificate lookup by the full scope address. A certificate addressed only by
    /// node would silently merge statements made under different goals, budgets or
    /// observation models.
    let certificateKey
        (targetRef: string)
        (valueSpaceId: string)
        (scopeId: string)
        (semanticsModelRef: string)
        : string =
        String.concat "|" [ targetRef; valueSpaceId; scopeId; semanticsModelRef ]

    /// Reservation identity as one canonical string. A work identity split across a
    /// tuple would let the same (work, attempt) appear twice under two spellings.
    let reservationKey (key: ReservationKey) : string =
        String.concat "|" [ WorkId.value key.WorkId; string (Attempt.value key.Attempt) ]

    let trySlots
        (state: InquiryState)
        (targetRef: string)
        (valueSpaceId: string)
        (scopeId: string)
        (semanticsModelRef: string)
        : CertificateSlotPatch list option =
        state.Certificates
        |> Map.tryFind (certificateKey targetRef valueSpaceId scopeId semanticsModelRef)

    let isTerminal (status: InquiryStatus) : bool =
        match status with
        | InquiryStatus.StopReached _
        | InquiryStatus.Failed _
        | InquiryStatus.Cancelled _ -> true
        | _ -> false

    /// Work whose dependencies have actually succeeded. Membership of the current
    /// dispatch batch does not count as satisfaction: a plan may name P→Q, but only P
    /// is dispatched, and Q must wait for P's real completion.
    let readyWork (state: InquiryState) : WorkItem list =
        let dependenciesSucceeded (item: WorkItem) =
            item.Spec.Dependencies
            |> Set.forall (fun dependency ->
                state.Work
                |> Map.tryFind dependency
                |> Option.map (fun dependencyItem -> dependencyItem.State)
                |> Option.map (fun state ->
                    match state with
                    | WorkState.Succeeded _ -> true
                    | _ -> false)
                |> Option.defaultValue false)

        state.Work
        |> Map.toList
        |> List.map snd
        |> List.filter (fun item ->
            match item.State with
            | WorkState.Planned -> dependenciesSucceeded item
            | WorkState.Ready -> true
            | _ -> false)
