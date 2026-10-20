namespace Wanxiangshu.Context.Companion

/// Context-compression decision owner. Attempt choice, retry request dispatch
/// and terminal validity cross this JSON boundary; prefix selection and epoch
/// behavior are owned by `PrefixSurface`.
[<RequireQualifiedAccess>]
module CompressionSurface =

    /// Build the production AttemptPlan from plain request labels. The caller
    /// supplies either a probe or a named no-candidate result; the planner itself
    /// still owns the choice and defers probe selection until it is allowed.
    val attemptPlan: value: obj -> obj

    val attemptPlanner: obj
    val terminalValidityCheck: value: string -> obj
    val terminalValidityIsValid: value: string -> bool
    val terminalValidityDescription: value: string -> string
    val terminalValidity: value: string -> obj
    val diagnosticEmit: operation: string -> fields: obj array -> unit
    val diagnosticFatal: operation: string -> fields: obj array -> unit
