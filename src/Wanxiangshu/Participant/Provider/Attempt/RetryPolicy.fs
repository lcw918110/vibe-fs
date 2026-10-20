namespace Wanxiangshu.Participant.Provider.Attempt

/// What one provider attempt produced.
[<RequireQualifiedAccess>]
type AttemptOutcome =
    | Completed
    | CompletedInvalid
    | Failed
    | Aborted

/// What a confirmed failure of one provider does next.
/// The fact is "this failure is itself the verbatim retry of that provider",
/// not a clock and not an ambiguous result.
[<RequireQualifiedAccess>]
type ProviderRecoveryStep =
    | RetrySameProvider
    | BlacklistProviderThenRetry

[<RequireQualifiedAccess>]
module ProviderAttemptRecovery =

    /// First confirmed failure retries the same provider.
    /// A later confirmed failure of that same provider blacklists it, then retries.
    /// Callers must not pass an unknown or ambiguous outcome as either case.
    let next (priorSameProviderRetry: bool) : ProviderRecoveryStep =
        if priorSameProviderRetry then
            ProviderRecoveryStep.BlacklistProviderThenRetry
        else
            ProviderRecoveryStep.RetrySameProvider
