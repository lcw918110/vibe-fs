namespace Wanxiangshu.Execution.Delegation.Fork

open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Foundation.Identity

module ChildRecovery =
    type NonEmpty<'a> = { Head: 'a; Tail: 'a list }

    module NonEmpty =
        val one: value: 'a -> NonEmpty<'a>
        val ofList: values: 'a list -> NonEmpty<'a> option
        val toList: values: NonEmpty<'a> -> 'a list

    [<RequireQualifiedAccess>]
    type ChildFinality =
        | Succeeded of body: string
        | Failed of body: string
        | Abandoned of HandleAbandonReason

    type TerminalEvidence =
        private
        | ProvenCompleted of agentId: string * handle: HandleId * childSession: SessionId * body: string
        | ProvenFailed of agentId: string * handle: HandleId * childSession: SessionId * body: string

    module TerminalEvidence =
        val completed:
            agentId: string -> handle: HandleId -> childSession: SessionId -> body: string -> TerminalEvidence

        val failed: agentId: string -> handle: HandleId -> childSession: SessionId -> body: string -> TerminalEvidence

    type CompletedPayload =
        { RunId: string
          View: string
          ChildSessionId: string
          AuthorityRoot: string
          ProviderRun: string
          Directory: string }

    type FailedPayload =
        { RunId: string
          Code: string
          Message: string
          ChildSessionId: string }

    type DurableAgentCompletionV2 =
        | CompletedV2 of CompletedPayload
        | FailedV2 of FailedPayload

    type LegacyAbortPayload =
        { Status: string
          RunId: string
          Code: string
          Message: string
          ChildSessionId: string
          RawBody: string }

    [<RequireQualifiedAccess>]
    type CompletionDecodeError =
        | MissingSchemaVersion
        | UnknownSchemaVersion of version: string
        | UnknownFinality of finality: string
        | MissingFinality
        | InvalidJson of reason: string
        | IncompletePayload of reason: string

    type DurableCompletionDecode =
        | Current of DurableAgentCompletionV2
        | LegacyFalseAbort of LegacyAbortPayload
        | Invalid of CompletionDecodeError

    type JoinableCompletion =
        private
            { AgentId: string
              Handle: HandleId
              ChildSession: SessionId
              Finality: ChildFinality
              Kind: HandleCompletionKind
              Body: string option }

    module JoinableCompletion =
        val agentId: c: JoinableCompletion -> string
        val handle: c: JoinableCompletion -> HandleId
        val childSession: c: JoinableCompletion -> SessionId
        val finality: c: JoinableCompletion -> ChildFinality
        val kind: c: JoinableCompletion -> HandleCompletionKind
        val body: c: JoinableCompletion -> string option

        val fromDecoded:
            agentId: string ->
            handle: HandleId ->
            childSession: SessionId ->
            decoded: DurableAgentCompletionV2 ->
            encodedBody: string ->
                JoinableCompletion

        val tryFromProvenTerminal: evidence: TerminalEvidence -> Result<JoinableCompletion, string>

    [<RequireQualifiedAccess>]
    type DurableHandleEvidence =
        | Unknown
        | Active
        | CompletedAwaitingJoin of JoinableCompletion
        | Abandoned of HandleAbandonReason
        | Retired

    [<RequireQualifiedAccess>]
    type ChildSnapshotEvidence =
        | Missing
        | Active
        | Terminal of TerminalEvidence
        | Unreadable of reason: string

    [<RequireQualifiedAccess>]
    type HostObservation =
        | AbortedObserved of reason: string
        | ParentCancelled
        | DeadlineExceeded
        | HostSessionGone
        | RecoveryInFlight
        | SessionActive

    type ProvenAbandonment =
        { Handle: HandleId
          Reason: HandleAbandonReason }

    type ActiveChildReceipt =
        { Handle: HandleId
          ChildSession: SessionId }

    type RecoveryDependency =
        | AwaitingTerminalEvidence of HandleId * SessionId
        | HostRestoreInFlight of HandleId * SessionId

    [<RequireQualifiedAccess>]
    type ChildRecoveryBlock =
        | Reason of string
        | SnapshotUnreadable of SessionId * reason: string

    [<RequireQualifiedAccess>]
    type ChildRecoveryResult =
        | RecoveredActive of ActiveChildReceipt
        | RecoveredTerminal of JoinableCompletion
        | RecoveredAbandoned of ProvenAbandonment
        | RecoveryIncomplete of RecoveryDependency
        | RecoveryBlocked of NonEmpty<ChildRecoveryBlock>

    [<RequireQualifiedAccess>]
    type ChildResolution =
        | RecoveredTerminal of JoinableCompletion
        | RecoveredAbandoned of HandleAbandonReason
        | RecoveryIncomplete
        | RecoveredActive
        | RecoveryBlocked of reason: string

    val resolveChild:
        durable: DurableHandleEvidence ->
        snapshot: ChildSnapshotEvidence ->
        observations: HostObservation list ->
            ChildResolution

    [<RequireQualifiedAccess>]
    type JoinRecoveryTrace =
        | RawAbortObserved of SessionId
        | ChildRecoveryStarted of SessionId
        | TerminalProofIssued of agentId: string
        | HandleCompletionCommitted of agentId: string
        | JoinReturned of agentId: string * ChildFinality

    val joinReturnedImpliesProofBeforeCommit: events: JoinRecoveryTrace list -> bool
