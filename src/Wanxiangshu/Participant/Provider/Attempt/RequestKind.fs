namespace Wanxiangshu.Participant.Provider.Attempt

/// Which physical request this is.
///
/// Real request semantics, not a flow stage. Each case determines which projection
/// to build, which instruction to send, and what success or failure does to the cursor.
[<RequireQualifiedAccess>]
type ProviderRequestKind =
    | WorkMain
    | InteractionRepair

[<RequireQualifiedAccess>]
module ProviderRequestKind =

    let label (kind: ProviderRequestKind) =
        match kind with
        | ProviderRequestKind.WorkMain -> "work-main"
        | ProviderRequestKind.InteractionRepair -> "interaction-repair"

    let clearsFailureCountOnSuccess (kind: ProviderRequestKind) =
        match kind with
        | ProviderRequestKind.WorkMain -> true
        | ProviderRequestKind.InteractionRepair -> false
