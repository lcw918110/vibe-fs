namespace Wanxiangshu.Execution.Session.Recovery

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// P0-RECOVERY-FAMILY: closed recovery program + private family permit (FLOW-002/007).
///
/// Recovery is not a background flag. A parent resumes only with a
/// `FamilyRecoveryPermit` produced by this program.
module SessionRecovery =

    /// Non-empty list without inventing a shared collection type.
    type NonEmpty<'a> = { Head: 'a; Tail: 'a list }

    module NonEmpty =
        let one (value: 'a) : NonEmpty<'a> = { Head = value; Tail = [] }

        let ofList (values: 'a list) : NonEmpty<'a> option =
            match values with
            | [] -> None
            | head :: tail -> Some { Head = head; Tail = tail }

        let toList (values: NonEmpty<'a>) : 'a list = values.Head :: values.Tail

        let map (f: 'a -> 'b) (values: NonEmpty<'a>) : NonEmpty<'b> =
            { Head = f values.Head
              Tail = List.map f values.Tail }

    [<RequireQualifiedAccess>]
    type RecoveryBlock =
        | SnapshotUnreadable of SessionId * reason: string
        | MissingSession of SessionId
        | LinkageConflict of parent: SessionId * child: SessionId
        | RecoveryCycle of NonEmpty<SessionId>
        | PendingClaimUnknown of SessionId * PromptKey
        | ChildRecoveryFailed of SessionId * reason: string
        /// Family recovery ports never attached (composition root gap).
        | RecoveryCoordinatorUnavailable of SessionId

    [<RequireQualifiedAccess>]
    type RecoveryNode =
        | WorkSession of SessionId
        | AgentChild of parent: SessionId * child: SessionId * AgentHandleId
        | Companion of main: SessionId * companion: SessionId
        | ManagerJob of ManagerJobId * manager: SessionId

    module RecoveryNode =
        /// One member's stable token. Owned here rather than in the projection because two things
        /// read it — the closure digest and a permit's membership check — and a member identity
        /// spelled twice is a member identity that can disagree with itself.
        let token (node: RecoveryNode) : string =
            match node with
            | RecoveryNode.WorkSession id -> "W:" + SessionId.value id
            | RecoveryNode.AgentChild(parent, child, handle) ->
                "A:"
                + SessionId.value parent
                + ">"
                + SessionId.value child
                + ":"
                + AgentHandleId.value handle
            | RecoveryNode.Companion(main, companion) -> "C:" + SessionId.value main + ">" + SessionId.value companion
            | RecoveryNode.ManagerJob(jobId, manager) -> "M:" + ManagerJobId.value jobId + ":" + SessionId.value manager

    type RecoveryReceipt =
        private
            { SessionId: SessionId
              JournalSequence: int64
              SnapshotDigest: string option
              ResolvedClaims: PromptKey list
              RestoredHandles: AgentHandleId list }

    module RecoveryReceipt =
        let create
            (sessionId: SessionId)
            (journalSequence: int64)
            (snapshotDigest: string option)
            (resolvedClaims: PromptKey list)
            (restoredHandles: AgentHandleId list)
            : RecoveryReceipt =
            { SessionId = sessionId
              JournalSequence = journalSequence
              SnapshotDigest = snapshotDigest
              ResolvedClaims = resolvedClaims
              RestoredHandles = restoredHandles }

        let sessionId (receipt: RecoveryReceipt) = receipt.SessionId
        let journalSequence (receipt: RecoveryReceipt) = receipt.JournalSequence
        let snapshotDigest (receipt: RecoveryReceipt) = receipt.SnapshotDigest
        let resolvedClaims (receipt: RecoveryReceipt) = receipt.ResolvedClaims
        let restoredHandles (receipt: RecoveryReceipt) = receipt.RestoredHandles

    [<RequireQualifiedAccess>]
    type SessionRecovery =
        | NoRecoveryRequired of RecoveryReceipt
        | Recovered of RecoveryReceipt
        /// Recovery still in flight / transient unreadable. No permit; not a hard block.
        | Waiting of NonEmpty<RecoveryBlock>
        | Blocked of NonEmpty<RecoveryBlock>

    /// Private: only authorizeFamilyResume may construct.
    /// EXEC-023: proof that this family's recovery closed. Carries the closure it closed over —
    /// the member tokens, not only their digest — because the question a consumer must answer is
    /// "is everything I recovered still here", and a digest can only answer "is the family
    /// byte-identical to what it was".
    ///
    /// Those are different questions, and the difference was a race: a child forking a grandchild
    /// while the parent joins changes the digest without invalidating any recovery. Measured in
    /// `temporal-ownership-unhappy-path`, which failed deterministically with
    /// `closureDigest mismatch: permit=…|A:P>C:h|… current=…|C:C>G|…` — the extra member was a
    /// live fork, and the join it refused was a legitimate one.
    type FamilyRecoveryPermit =
        private | FamilyRecoveryPermit of root: SessionId * journalSequence: int64 * closureMembers: Set<string>

    module FamilyRecoveryPermit =
        let root (FamilyRecoveryPermit(root, _, _)) = root
        let journalSequence (FamilyRecoveryPermit(_, sequence, _)) = sequence
        let closureMembers (FamilyRecoveryPermit(_, _, members)) = members

        /// Current-process admission has no cross-process recovery claim. Empty
        /// closure means old durable members are not certified/resumed implicitly.
        let currentProcess (root: SessionId) (journalSequence: int64) =
            FamilyRecoveryPermit(root, journalSequence, Set.empty)

        /// Stable rendering for diagnostics. Sorted, so two permits over the same closure read the
        /// same regardless of discovery order.
        let describeClosure (permit: FamilyRecoveryPermit) =
            closureMembers permit |> Set.toList |> String.concat "|"

        /// Monotone admission: every member the permit recovered must still be in the family.
        /// New members are legal — a session created after recovery closed was never in need of
        /// recovery — so growth is admitted and only loss is refused.
        let missingFrom (current: Set<string>) (permit: FamilyRecoveryPermit) : string list =
            Set.difference (closureMembers permit) current |> Set.toList

    [<RequireQualifiedAccess>]
    type FamilyRecovery =
        | FamilyReady of FamilyRecoveryPermit
        /// Incomplete family recovery: no permit, consumers must wait (not RECOVERY_BLOCKED).
        | FamilyWaiting of NonEmpty<RecoveryBlock>
        | FamilyBlocked of NonEmpty<RecoveryBlock>

    type RecoveryClosure =
        {
            Root: SessionId
            /// Child-first: dependents before ancestors; siblings by SessionId.
            Nodes: RecoveryNode list
            Digest: string
            JournalSequence: int64
        }

    type ValidatedClosure = private ValidatedClosure of RecoveryClosure

    module RecoveryClosure =
        /// The closure as a membership set. What a permit must still find, member by member.
        let members (closure: RecoveryClosure) : Set<string> =
            closure.Nodes |> List.map RecoveryNode.token |> Set.ofList

    module ValidatedClosure =
        let value (ValidatedClosure closure) = closure

    type RecoveredClosure =
        { Closure: RecoveryClosure
          Results: Map<SessionId, SessionRecovery> }

    type ClaimRecovery =
        { SessionId: SessionId
          Outcome: SessionRecovery }


    type HandleRecovery =
        { SessionId: SessionId
          Outcome: SessionRecovery }

    type JobRecovery =
        { JobId: ManagerJobId
          Outcome: SessionRecovery }

    /// Per-handle recovery wait (incomplete; must not issue FamilyRecoveryPermit).
    type HandleRecoveryWait =
        { Handle: AgentHandleId
          ChildSession: SessionId
          Reason: string }

    /// Per-handle recovery block.
    type HandleRecoveryBlock =
        { Handle: AgentHandleId
          ChildSession: SessionId
          Reason: string }

    /// One recovered agent handle under a parent session.
    type RecoveredHandle =
        {
            Handle: AgentHandleId
            ChildSession: SessionId
            /// terminal | active | abandoned — determined family outcomes only.
            Kind: string
        }

    /// Query result for RestoreHandles (GREEN-4). Missing work = NoLinkedHandles, not missing port.
    [<RequireQualifiedAccess>]
    type HandleFamilyRecovery =
        | NoLinkedHandles
        | HandlesRecovered of NonEmpty<RecoveredHandle>
        | HandlesWaiting of NonEmpty<HandleRecoveryWait>
        | HandlesBlocked of NonEmpty<HandleRecoveryBlock>

    /// Query result for RecoverJobs (GREEN-4).
    [<RequireQualifiedAccess>]
    type JobFamilyRecovery =
        | NoRelatedJobs
        | JobsRecovered of NonEmpty<ManagerJobId>
        | JobRecoveryUnknown of ManagerJobId * reason: string
        | JobsBlocked of NonEmpty<RecoveryBlock>

    /// Map handle-family query into SessionRecovery for merge / authorize.
    /// Determined: NoLinkedHandles / HandlesRecovered → permit-eligible.
    /// HandlesWaiting → Waiting (no permit, join waits). HandlesBlocked → Blocked.
    let private waitingRecovery sessionId reasons =
        NonEmpty.ofList reasons
        |> Option.defaultValue (NonEmpty.one (RecoveryBlock.ChildRecoveryFailed(sessionId, "handles waiting")))
        |> SessionRecovery.Waiting

    let private blockedRecovery sessionId reasons =
        NonEmpty.ofList reasons
        |> Option.defaultValue (NonEmpty.one (RecoveryBlock.ChildRecoveryFailed(sessionId, "handles blocked")))
        |> SessionRecovery.Blocked

    let sessionRecoveryOfHandleFamily
        (sessionId: SessionId)
        (sequence: int64)
        (family: HandleFamilyRecovery)
        : SessionRecovery =
        match family with
        | HandleFamilyRecovery.NoLinkedHandles ->
            SessionRecovery.NoRecoveryRequired(RecoveryReceipt.create sessionId sequence None [] [])
        | HandleFamilyRecovery.HandlesRecovered handles ->
            let restored = NonEmpty.toList handles |> List.map (fun h -> h.Handle)
            SessionRecovery.Recovered(RecoveryReceipt.create sessionId sequence None [] restored)
        | HandleFamilyRecovery.HandlesWaiting waits ->
            let reasons =
                NonEmpty.toList waits
                |> List.map (fun w ->
                    RecoveryBlock.ChildRecoveryFailed(
                        w.ChildSession,
                        sprintf "handle %s waiting: %s" (AgentHandleId.value w.Handle) w.Reason
                    ))

            waitingRecovery sessionId reasons
        | HandleFamilyRecovery.HandlesBlocked blocks ->
            let reasons =
                NonEmpty.toList blocks
                |> List.map (fun b ->
                    RecoveryBlock.ChildRecoveryFailed(
                        b.ChildSession,
                        sprintf "handle %s blocked: %s" (AgentHandleId.value b.Handle) b.Reason
                    ))

            blockedRecovery sessionId reasons

    /// Map job-family query into SessionRecovery.
    let sessionRecoveryOfJobFamily
        (sessionId: SessionId)
        (sequence: int64)
        (family: JobFamilyRecovery)
        : SessionRecovery =
        match family with
        | JobFamilyRecovery.NoRelatedJobs ->
            SessionRecovery.NoRecoveryRequired(RecoveryReceipt.create sessionId sequence None [] [])
        | JobFamilyRecovery.JobsRecovered _ ->
            SessionRecovery.Recovered(RecoveryReceipt.create sessionId sequence None [] [])
        | JobFamilyRecovery.JobRecoveryUnknown(jobId, reason) ->
            // Transient / unknown job evidence → wait, not hard FamilyBlocked.
            SessionRecovery.Waiting(
                NonEmpty.one (
                    RecoveryBlock.ChildRecoveryFailed(
                        sessionId,
                        sprintf "job %s unknown: %s" (ManagerJobId.value jobId) reason
                    )
                )
            )
        | JobFamilyRecovery.JobsBlocked blocks -> SessionRecovery.Blocked blocks

    /// Priority fold over per-port outcomes (rabbit §14.2):
    /// Blocked > Waiting > Recovered > other (first within a tier wins).
    let combine (outcomes: SessionRecovery list) : SessionRecovery =
        let firstBlocked =
            outcomes
            |> Seq.tryPick (function
                | SessionRecovery.Blocked bs -> Some(SessionRecovery.Blocked bs)
                | _ -> None)

        let firstWaiting =
            outcomes
            |> Seq.tryPick (function
                | SessionRecovery.Waiting ws -> Some(SessionRecovery.Waiting ws)
                | _ -> None)

        let firstRecovered =
            outcomes
            |> Seq.tryPick (function
                | SessionRecovery.Recovered r -> Some(SessionRecovery.Recovered r)
                | _ -> None)

        firstBlocked
        |> Option.orElse firstWaiting
        |> Option.orElse firstRecovered
        |> Option.orElse (Seq.tryHead outcomes)
        |> Option.defaultValue (
            SessionRecovery.NoRecoveryRequired(RecoveryReceipt.create (SessionId.create "") 0L None [] [])
        )

    let private sessionOfNode =
        function
        | RecoveryNode.WorkSession id
        | RecoveryNode.AgentChild(_, id, _)
        | RecoveryNode.Companion(_, id)
        | RecoveryNode.ManagerJob(_, id) -> id

    /// Pure: duplicate session in ordered nodes → cycle block; else wrap.
    let validateClosurePure (closure: RecoveryClosure) : Result<ValidatedClosure, NonEmpty<RecoveryBlock>> =
        let rec check seen nodes =
            match nodes with
            | [] -> Ok(ValidatedClosure closure)
            | node :: rest -> checkNode seen rest node

        and checkNode seen rest node =
            let sessionId = sessionOfNode node

            if Set.contains sessionId seen then
                Error(NonEmpty.one (RecoveryBlock.RecoveryCycle(NonEmpty.one sessionId)))
            else
                check (Set.add sessionId seen) rest

        check Set.empty closure.Nodes

    /// Pure authorize:
    /// any Blocked → FamilyBlocked (hard, no permit);
    /// else any Waiting → FamilyWaiting (no permit, consumers wait);
    /// else FamilyReady (private permit).
    let private blockedReasons (recovered: RecoveredClosure) =
        recovered.Results
        |> Map.toList
        |> List.collect (fun (_, outcome) ->
            match outcome with
            | SessionRecovery.Blocked bs -> NonEmpty.toList bs
            | SessionRecovery.Waiting _
            | SessionRecovery.NoRecoveryRequired _
            | SessionRecovery.Recovered _ -> [])
        |> NonEmpty.ofList

    let private waitingReasons (recovered: RecoveredClosure) =
        recovered.Results
        |> Map.toList
        |> List.collect (fun (_, outcome) ->
            match outcome with
            | SessionRecovery.Waiting ws -> NonEmpty.toList ws
            | SessionRecovery.Blocked _
            | SessionRecovery.NoRecoveryRequired _
            | SessionRecovery.Recovered _ -> [])
        |> NonEmpty.ofList

    let authorizeFamilyResume
        (root: SessionId)
        (journalSequence: int64)
        (recovered: RecoveredClosure)
        : FamilyRecovery =
        match blockedReasons recovered, waitingReasons recovered with
        | Some nonEmpty, _ -> FamilyRecovery.FamilyBlocked nonEmpty
        | None, Some nonEmpty -> FamilyRecovery.FamilyWaiting nonEmpty
        | None, None ->
            FamilyRecovery.FamilyReady(
                FamilyRecoveryPermit(root, journalSequence, RecoveryClosure.members recovered.Closure)
            )
