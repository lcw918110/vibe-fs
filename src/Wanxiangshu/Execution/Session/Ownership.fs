namespace Wanxiangshu.Execution.Session

open Wanxiangshu.Foundation.Identity

/// HOST-008: orthogonal session ownership — ExecutionClass × Ownership.
///
/// Long-lived association is no longer a single `SatelliteKind` axis. Dedicated
/// SyncInspector/SyncCoder/SyncEngineer are Work+Attached (MAY hold a Companion); Companion /
/// Companion / Bookkeeper are InternalLeaf+Attached.
/// HOST-008: whether the session is ordinary work or an internal leaf.
[<RequireQualifiedAccess>]
type SessionExecutionClass =
    | Work
    | InternalLeaf

/// HOST-008: why an Attached session is bound to its owner.
[<RequireQualifiedAccess>]
type AttachmentKind =
    | Companion
    | SyncInspector
    | SyncCoder
    /// EXEC-026: the Engineer role's dedicated attachment identity.
    | SyncEngineer
    /// work-028: the plan ask delegation's fixed DevOps attachment
    /// identity (Work+Attached, same family as the other sync kinds).
    | SyncDevOps
    | Bookkeeper of transactionId: string


/// HOST-008: Root work vs Attached ownership under one ownerSessionId.
[<RequireQualifiedAccess>]
type SessionOwnership =
    | Root
    | Attached of ownerSessionId: SessionId * attachment: AttachmentKind

module SessionExecutionClass =

    let isWork =
        function
        | SessionExecutionClass.Work -> true
        | SessionExecutionClass.InternalLeaf -> false

    let isInternalLeaf =
        function
        | SessionExecutionClass.Work -> false
        | SessionExecutionClass.InternalLeaf -> true

module SessionOwnership =

    /// Owner of an Attached session; `None` for Root.
    let tryOwner =
        function
        | SessionOwnership.Root -> None
        | SessionOwnership.Attached(owner, _) -> Some owner
