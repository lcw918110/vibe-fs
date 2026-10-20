namespace Wanxiangshu.Participant.Provider.Attempt

[<RequireQualifiedAccess>]
type ProviderRequestKind =
    | WorkMain
    | InteractionRepair

[<RequireQualifiedAccess>]
module ProviderRequestKind =
    val label: kind: ProviderRequestKind -> string
    val clearsFailureCountOnSuccess: kind: ProviderRequestKind -> bool
