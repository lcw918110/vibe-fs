namespace Wanxiangshu.Participant.Provider.Attempt

/// What one provider attempt produced.
[<RequireQualifiedAccess>]
type AttemptOutcome =
    | Completed
    | CompletedInvalid
    | Failed
    | Aborted

[<RequireQualifiedAccess>]
type ProviderRecoveryStep =
    | RetrySameProvider
    | BlacklistProviderThenRetry

[<RequireQualifiedAccess>]
module ProviderAttemptRecovery =
    /// priorSameProviderRetry is the confirmed fact that this failure is itself
    /// the verbatim retry of that provider. It is not a timeout and not an unknown result.
    val next: priorSameProviderRetry: bool -> ProviderRecoveryStep
