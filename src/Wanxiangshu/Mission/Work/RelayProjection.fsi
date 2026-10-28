namespace Wanxiangshu.Mission.Work

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
    /// The retirement request for this work, when a relayed tenure with a
    /// durable projection cut exists. Whether the request is stale is the
    /// library's verdict; this returns the inputs it decides on.
    val staleRequest:
        journal: AgentJournal ->
        sessionId: Wanxiangshu.Foundation.Identity.SessionId ->
        work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork ->
        acceptedRequest: bool ->
        messages: RelayMessageFact list ->
            RelayRetirementRequest option

    /// The projection disposition when no retirement applies. An active tenure
    /// means the message set belongs to the current iteration; a work with no
    /// active tenure has no iteration to project and stays unchanged. Absence is
    /// not a previous tenure and not permission to continue.
    val disposition:
        work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option -> RelayProjectionDisposition

    /// Whether a physical user message id is owned by the relayed successor,
    /// judged from committed ChatExecution acceptance and the loop-gate nudge.
    val successorOwns:
        journal: AgentJournal ->
        sessionId: Wanxiangshu.Foundation.Identity.SessionId ->
        work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork ->
            (string -> bool)
