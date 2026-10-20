namespace Wanxiangshu.Execution.Session.Recovery

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

module SessionRecovery =
    type NonEmpty<'a> = { Head: 'a; Tail: 'a list }

    module NonEmpty =
        val one: value: 'a -> NonEmpty<'a>
        val ofList: values: 'a list -> NonEmpty<'a> option
        val toList: values: NonEmpty<'a> -> 'a list
        val map: f: ('a -> 'b) -> values: NonEmpty<'a> -> NonEmpty<'b>

    [<RequireQualifiedAccess>]
    type RecoveryBlock =
        | SnapshotUnreadable of SessionId * reason: string
        | MissingSession of SessionId
        | LinkageConflict of parent: SessionId * child: SessionId
        | RecoveryCycle of NonEmpty<SessionId>
        | PendingClaimUnknown of SessionId * PromptKey
        | ChildRecoveryFailed of SessionId * reason: string
        | RecoveryCoordinatorUnavailable of SessionId

    [<RequireQualifiedAccess>]
    type RecoveryNode =
        | WorkSession of SessionId
        | AgentChild of parent: SessionId * child: SessionId * AgentHandleId
        | Companion of main: SessionId * companion: SessionId
        | ManagerJob of ManagerJobId * manager: SessionId

    module RecoveryNode =
        val token: node: RecoveryNode -> string

    type RecoveryReceipt

    module RecoveryReceipt =
        val create:
            sessionId: SessionId ->
            journalSequence: int64 ->
            snapshotDigest: string option ->
            resolvedClaims: PromptKey list ->
            restoredHandles: AgentHandleId list ->
                RecoveryReceipt

        val sessionId: receipt: RecoveryReceipt -> SessionId
        val journalSequence: receipt: RecoveryReceipt -> int64
        val snapshotDigest: receipt: RecoveryReceipt -> string option
        val resolvedClaims: receipt: RecoveryReceipt -> PromptKey list
        val restoredHandles: receipt: RecoveryReceipt -> AgentHandleId list

    [<RequireQualifiedAccess>]
    type SessionRecovery =
        | NoRecoveryRequired of RecoveryReceipt
        | Recovered of RecoveryReceipt
        | Waiting of NonEmpty<RecoveryBlock>
        | Blocked of NonEmpty<RecoveryBlock>

    type FamilyRecoveryPermit

    module FamilyRecoveryPermit =
        val root: permit: FamilyRecoveryPermit -> SessionId
        val journalSequence: permit: FamilyRecoveryPermit -> int64
        val closureMembers: permit: FamilyRecoveryPermit -> Set<string>
        val currentProcess: root: SessionId -> journalSequence: int64 -> FamilyRecoveryPermit
        val describeClosure: permit: FamilyRecoveryPermit -> string
        val missingFrom: current: Set<string> -> permit: FamilyRecoveryPermit -> string list

    [<RequireQualifiedAccess>]
    type FamilyRecovery =
        | FamilyReady of FamilyRecoveryPermit
        | FamilyWaiting of NonEmpty<RecoveryBlock>
        | FamilyBlocked of NonEmpty<RecoveryBlock>

    type RecoveryClosure =
        { Root: SessionId
          Nodes: RecoveryNode list
          Digest: string
          JournalSequence: int64 }

    type ValidatedClosure

    module RecoveryClosure =
        val members: closure: RecoveryClosure -> Set<string>

    module ValidatedClosure =
        val value: validated: ValidatedClosure -> RecoveryClosure

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

    type HandleRecoveryWait =
        { Handle: AgentHandleId
          ChildSession: SessionId
          Reason: string }

    type HandleRecoveryBlock =
        { Handle: AgentHandleId
          ChildSession: SessionId
          Reason: string }

    type RecoveredHandle =
        { Handle: AgentHandleId
          ChildSession: SessionId
          Kind: string }

    [<RequireQualifiedAccess>]
    type HandleFamilyRecovery =
        | NoLinkedHandles
        | HandlesRecovered of NonEmpty<RecoveredHandle>
        | HandlesWaiting of NonEmpty<HandleRecoveryWait>
        | HandlesBlocked of NonEmpty<HandleRecoveryBlock>

    [<RequireQualifiedAccess>]
    type JobFamilyRecovery =
        | NoRelatedJobs
        | JobsRecovered of NonEmpty<ManagerJobId>
        | JobRecoveryUnknown of ManagerJobId * reason: string
        | JobsBlocked of NonEmpty<RecoveryBlock>

    val sessionRecoveryOfHandleFamily:
        sessionId: SessionId -> sequence: int64 -> family: HandleFamilyRecovery -> SessionRecovery

    val sessionRecoveryOfJobFamily:
        sessionId: SessionId -> sequence: int64 -> family: JobFamilyRecovery -> SessionRecovery

    val combine: outcomes: SessionRecovery list -> SessionRecovery
    val validateClosurePure: closure: RecoveryClosure -> Result<ValidatedClosure, NonEmpty<RecoveryBlock>>

    val authorizeFamilyResume:
        root: SessionId -> journalSequence: int64 -> recovered: RecoveredClosure -> FamilyRecovery
