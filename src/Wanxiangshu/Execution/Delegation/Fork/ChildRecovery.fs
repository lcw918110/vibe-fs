namespace Wanxiangshu.Execution.Delegation.Fork

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Foundation.Identity

/// P0-RECOVERY-JOIN-001 + clean-break: Aborted is not a host observation and not durable finality.
/// Join consumes only JoinableCompletion from a decoded current terminal; no raw JSON / kind+body.
module ChildRecovery =

    /// Non-empty list (local; no shared collection dependency).
    type NonEmpty<'a> = { Head: 'a; Tail: 'a list }

    module NonEmpty =
        let one (value: 'a) : NonEmpty<'a> = { Head = value; Tail = [] }

        let ofList (values: 'a list) : NonEmpty<'a> option =
            match values with
            | [] -> None
            | head :: tail -> Some { Head = head; Tail = tail }

        let toList (values: NonEmpty<'a>) : 'a list = values.Head :: values.Tail

    /// Proven business terminal of a child handle. No Aborted case.
    [<RequireQualifiedAccess>]
    type ChildFinality =
        /// Durable join payload body already encoded for the completion blob.
        | Succeeded of body: string
        /// Proven business failure (not transport abort-only).
        | Failed of body: string
        | Abandoned of HandleAbandonReason

    /// Evidence that a terminal business outcome exists. Private construction:
    /// only Domain pure functions may mint. No Aborted-only path.
    type TerminalEvidence =
        private
        | ProvenCompleted of agentId: string * handle: HandleId * childSession: SessionId * body: string
        | ProvenFailed of agentId: string * handle: HandleId * childSession: SessionId * body: string

    module TerminalEvidence =
        let completed (agentId: string) (handle: HandleId) (childSession: SessionId) (body: string) : TerminalEvidence =
            ProvenCompleted(agentId, handle, childSession, body)

        let failed (agentId: string) (handle: HandleId) (childSession: SessionId) (body: string) : TerminalEvidence =
            ProvenFailed(agentId, handle, childSession, body)

    /// Completion blob finality (schemaVersion=3). No aborted.
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

    /// Decoded current-schema agent completion. No Aborted case.
    type DurableAgentCompletionV2 =
        | CompletedV2 of CompletedPayload
        | FailedV2 of FailedPayload

    /// Legacy abort observation planted as if it were a terminal (status=aborted).
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

    /// First decode of a durable completion blob. JoinDrain branches on this.
    type DurableCompletionDecode =
        | Current of DurableAgentCompletionV2
        | LegacyFalseAbort of LegacyAbortPayload
        | Invalid of CompletionDecodeError

    /// Single-assignment completion cell proof. Private: only fromDecoded /
    /// tryFromProvenTerminal may construct. Carries kind + body so
    /// HandleController writes blob + HandleCompleted without external kind/body.
    type JoinableCompletion =
        private
            { AgentId: string
              Handle: HandleId
              ChildSession: SessionId
              Finality: ChildFinality
              Kind: HandleCompletionKind
              Body: string option }

    module JoinableCompletion =
        let agentId (c: JoinableCompletion) = c.AgentId
        let handle (c: JoinableCompletion) = c.Handle
        let childSession (c: JoinableCompletion) = c.ChildSession
        let finality (c: JoinableCompletion) = c.Finality
        let kind (c: JoinableCompletion) = c.Kind
        let body (c: JoinableCompletion) = c.Body

        /// Sole constructor from a decoded current terminal. Never touches raw JSON,
        /// HandleCompletionKind, or an arbitrary body string as proof.
        let fromDecoded
            (agentId: string)
            (handle: HandleId)
            (childSession: SessionId)
            (decoded: DurableAgentCompletionV2)
            (encodedBody: string)
            : JoinableCompletion =
            match decoded with
            | CompletedV2 _ ->
                { AgentId = agentId
                  Handle = handle
                  ChildSession = childSession
                  Finality = ChildFinality.Succeeded encodedBody
                  Kind = HandleCompletionKind.Terminal
                  Body = Some encodedBody }
            | FailedV2 _ ->
                { AgentId = agentId
                  Handle = handle
                  ChildSession = childSession
                  Finality = ChildFinality.Failed encodedBody
                  Kind = HandleCompletionKind.SendFailure
                  Body = Some encodedBody }

        /// Proven terminal only. No fromAborted.
        let tryFromProvenTerminal (evidence: TerminalEvidence) : Result<JoinableCompletion, string> =
            match evidence with
            | ProvenCompleted(agentId, handle, childSession, body) when body <> "" ->
                Ok
                    { AgentId = agentId
                      Handle = handle
                      ChildSession = childSession
                      Finality = ChildFinality.Succeeded body
                      Kind = HandleCompletionKind.Terminal
                      Body = Some body }
            | ProvenFailed(agentId, handle, childSession, body) when body <> "" ->
                Ok
                    { AgentId = agentId
                      Handle = handle
                      ChildSession = childSession
                      Finality = ChildFinality.Failed body
                      Kind = HandleCompletionKind.SendFailure
                      Body = Some body }
            | ProvenCompleted _
            | ProvenFailed _ -> Error "proven terminal body must be non-empty"

    /// Domain view of durable handle lifecycle (no Journal types).
    [<RequireQualifiedAccess>]
    type DurableHandleEvidence =
        | Unknown
        | Active
        | CompletedAwaitingJoin of JoinableCompletion
        | Abandoned of HandleAbandonReason
        | Retired

    /// Snapshot terminal evidence. Aborted-only never appears as SnapshotTerminal.
    [<RequireQualifiedAccess>]
    type ChildSnapshotEvidence =
        | Missing
        | Active
        | Terminal of TerminalEvidence
        | Unreadable of reason: string

    /// Host observations that may enter recovery resolution. Abort is not one of them.
    /// RecoveryInFlight avoids dsl-ownership behaviour-bool tokens (*Pending/*Running).
    [<RequireQualifiedAccess>]
    type HostObservation =
        
        | ParentCancelled
        | DeadlineExceeded
        | HostSessionGone
        | RecoveryInFlight
        | SessionActive

    /// Proven abandonment of a child handle (durable reason).
    type ProvenAbandonment =
        { Handle: HandleId
          Reason: HandleAbandonReason }

    /// Child still live after recovery work finished (not incomplete).
    type ActiveChildReceipt =
        { Handle: HandleId
          ChildSession: SessionId }

    /// Why recovery cannot finish yet (not a terminal, not blocked).
    type RecoveryDependency =
        | AwaitingTerminalEvidence of HandleId * SessionId
        | HostRestoreInFlight of HandleId * SessionId

    [<RequireQualifiedAccess>]
    type ChildRecoveryBlock =
        | Reason of string
        | SnapshotUnreadable of SessionId * reason: string

    /// Post-recovery child outcome (GREEN-4). RecoveredActive ≠ RecoveryIncomplete.
    /// Permit may issue only for RecoveredActive | RecoveredTerminal | RecoveredAbandoned.
    [<RequireQualifiedAccess>]
    type ChildRecoveryResult =
        | RecoveredActive of ActiveChildReceipt
        | RecoveredTerminal of JoinableCompletion
        | RecoveredAbandoned of ProvenAbandonment
        | RecoveryIncomplete of RecoveryDependency
        | RecoveryBlocked of NonEmpty<ChildRecoveryBlock>

    /// Pure resolution before commit. Maps 1:1 to ChildRecoveryResult after effects.
    [<RequireQualifiedAccess>]
    type ChildResolution =
        | RecoveredTerminal of JoinableCompletion
        | RecoveredAbandoned of HandleAbandonReason
        | RecoveryIncomplete
        | RecoveredActive
        | RecoveryBlocked of reason: string

    /// Pure resolution order (P0-RECOVERY-JOIN-001 §四 + GREEN-4 split):
    /// durable Abandoned → RecoveredAbandoned
    /// durable CompletedAwaitingJoin → RecoveredTerminal
    /// snapshot legal terminal → RecoveredTerminal
    /// snapshot Unreadable (true read error) → RecoveryIncomplete (wait; no permit; not hard block)
    /// session active → RecoveredActive (recovery work done; child continues)
    /// restore in flight / unknown → RecoveryIncomplete (must not issue permit)
    /// ParentCancelled / DeadlineExceeded / HostSessionGone → RecoveredAbandoned
    /// conflict / retired → RecoveryBlocked
    /// CUT-13: the resolution order is owned by wanxiangli/handle-lifecycle
    /// `recoverChild`. This module hands over the evidence kinds, validates the
    /// terminal proof itself and builds the typed resolution from what it holds.
    [<Import("HandleLifecycleLaw", "wanxiangli/handle-lifecycle")>]
    let private lawClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : obj = jsNative

    let private law: obj = construct lawClass

    let private reasonName (reason: HandleAbandonReason) =
        match reason with
        | HandleAbandonReason.ParentCancelled -> "ParentCancelled"
        | HandleAbandonReason.DeadlineExceeded -> "DeadlineExceeded"
        | HandleAbandonReason.HostSessionGone -> "HostSessionGone"
        | HandleAbandonReason.Voided -> "Voided"

    let private reasonOf (name: string) =
        match name with
        | "ParentCancelled" -> HandleAbandonReason.ParentCancelled
        | "DeadlineExceeded" -> HandleAbandonReason.DeadlineExceeded
        | "HostSessionGone" -> HandleAbandonReason.HostSessionGone
        | "Voided" -> HandleAbandonReason.Voided
        | other -> invalidOp ("wanxiangli/handle-lifecycle returned an unknown abandon reason: " + other)

    let private observationName (observation: HostObservation) =
        match observation with
        
        | HostObservation.ParentCancelled -> "ParentCancelled"
        | HostObservation.DeadlineExceeded -> "DeadlineExceeded"
        | HostObservation.HostSessionGone -> "HostSessionGone"
        | HostObservation.RecoveryInFlight -> "RecoveryInFlight"
        | HostObservation.SessionActive -> "SessionActive"

    let private recoveredTerminalOf
        (source: string)
        (durable: DurableHandleEvidence)
        (proof: Result<JoinableCompletion, string> option)
        : ChildResolution =
        match source, durable, proof with
        | "durable", DurableHandleEvidence.CompletedAwaitingJoin completion, _ ->
            ChildResolution.RecoveredTerminal completion
        | "snapshot", _, Some(Ok completion) -> ChildResolution.RecoveredTerminal completion
        | source, _, _ -> invalidOp ("wanxiangli/handle-lifecycle named a terminal it was not given: " + source)

    let resolveChild
        (durable: DurableHandleEvidence)
        (snapshot: ChildSnapshotEvidence)
        (observations: HostObservation list)
        : ChildResolution =
        let proof =
            match snapshot with
            | ChildSnapshotEvidence.Terminal evidence -> Some(JoinableCompletion.tryFromProvenTerminal evidence)
            | _ -> None

        let durableKind, durableReason =
            match durable with
            | DurableHandleEvidence.Unknown -> "Unknown", null
            | DurableHandleEvidence.Active -> "Active", null
            | DurableHandleEvidence.CompletedAwaitingJoin _ -> "CompletedAwaitingJoin", null
            | DurableHandleEvidence.Abandoned reason -> "Abandoned", box (reasonName reason)
            | DurableHandleEvidence.Retired -> "Retired", null

        let snapshotKind =
            match snapshot with
            | ChildSnapshotEvidence.Missing -> "Missing"
            | ChildSnapshotEvidence.Active -> "Active"
            | ChildSnapshotEvidence.Terminal _ -> "Terminal"
            | ChildSnapshotEvidence.Unreadable _ -> "Unreadable"

        let verdict: obj =
            law?recoverChild (
                createObj
                    [ "durable" ==> durableKind
                      "durableReason" ==> durableReason
                      "snapshot" ==> snapshotKind
                      "proofError"
                      ==> (match proof with
                           | Some(Error reason) -> box reason
                           | _ -> null)
                      "observations" ==> (observations |> List.map observationName |> List.toArray) ]
            )

        match unbox<string> verdict?resolution with
        | "terminal" -> recoveredTerminalOf (unbox<string> verdict?source) durable proof
        | "abandoned" -> ChildResolution.RecoveredAbandoned(reasonOf (unbox<string> verdict?reason))
        | "active" -> ChildResolution.RecoveredActive
        | "incomplete" -> ChildResolution.RecoveryIncomplete
        | "blocked" -> ChildResolution.RecoveryBlocked(unbox<string> verdict?reason)
        | other -> invalidOp ("wanxiangli/handle-lifecycle returned an unknown child resolution: " + other)

    /// §九 join-recovery timeline events (pure observation log, not a program counter).
    /// agentId is the AgentHandle key string (same as JoinableCompletion.agentId).
    [<RequireQualifiedAccess>]
    type JoinRecoveryTrace =
        | RawAbortObserved of SessionId
        | ChildRecoveryStarted of SessionId
        | TerminalProofIssued of agentId: string
        | HandleCompletionCommitted of agentId: string
        | JoinReturned of agentId: string * ChildFinality

    /// Pure invariant over a join-recovery timeline:
    /// ∀ JoinReturned(agent) ∃ TerminalProofIssued(agent) before HandleCompletionCommitted(agent)
    /// before that JoinReturned; RawAbortObserved never adjacent to HandleCompletionCommitted
    /// or JoinReturned (a raw abort is not a completion step).
    let joinReturnedImpliesProofBeforeCommit (events: JoinRecoveryTrace list) : bool =
        let adjacentForbidden a b =
            match a, b with
            | JoinRecoveryTrace.RawAbortObserved _, JoinRecoveryTrace.HandleCompletionCommitted _
            | JoinRecoveryTrace.RawAbortObserved _, JoinRecoveryTrace.JoinReturned _
            | JoinRecoveryTrace.HandleCompletionCommitted _, JoinRecoveryTrace.RawAbortObserved _
            | JoinRecoveryTrace.JoinReturned _, JoinRecoveryTrace.RawAbortObserved _ -> true
            | _ -> false

        let rec adjacencyOk =
            function
            | []
            | [ _ ] -> true
            | a :: b :: rest -> not (adjacentForbidden a b) && adjacencyOk (b :: rest)

        let joinReturnHasProofBeforeCommit events index agentId =
            let before = List.take index events

            let proofIdx =
                before
                |> List.tryFindIndex (function
                    | JoinRecoveryTrace.TerminalProofIssued id when id = agentId -> true
                    | _ -> false)

            let commitIdx =
                before
                |> List.tryFindIndex (function
                    | JoinRecoveryTrace.HandleCompletionCommitted id when id = agentId -> true
                    | _ -> false)

            match proofIdx, commitIdx with
            | Some p, Some c -> p < c
            | _ -> false

        let orderForJoinReturns =
            events
            |> List.indexed
            |> List.forall (fun (i, event) ->
                match event with
                | JoinRecoveryTrace.JoinReturned(agentId, _) -> joinReturnHasProofBeforeCommit events i agentId
                | _ -> true)

        adjacencyOk events && orderForJoinReturns
