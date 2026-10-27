namespace Wanxiangshu.Participant.Provider.Attempt

/// What one provider attempt produced.
[<RequireQualifiedAccess>]
type AttemptOutcome =
    | Completed
    | CompletedInvalid
    | Failed
    | Aborted
