namespace Wanxiangshu.Execution.Session

open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
type SessionExecutionClass =
    | Work
    | InternalLeaf

[<RequireQualifiedAccess>]
type AttachmentKind =
    | Companion
    | SyncInspector
    | SyncCoder
    /// EXEC-026: the Engineer role's dedicated attachment identity.
    | SyncEngineer
    /// work-028: the plan ask delegation's fixed DevOps attachment identity.
    | SyncDevOps
    | Bookkeeper of transactionId: string


[<RequireQualifiedAccess>]
type SessionOwnership =
    | Root
    | Attached of ownerSessionId: SessionId * attachment: AttachmentKind

module SessionExecutionClass =
    val isWork: SessionExecutionClass -> bool
    val isInternalLeaf: SessionExecutionClass -> bool

module SessionOwnership =
    val tryOwner: SessionOwnership -> SessionId option
