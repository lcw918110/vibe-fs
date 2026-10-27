namespace Wanxiangshu.Interaction.Dispatch

/// ARCH-010: runtime instruction-only payload semantic paths (PROMPT-019).
///
/// Prose lives in `resources/provider/runtime/...`. Call sites bind language via
/// `ProviderProse.documentFor` / `instructionLines`. Domain owns path constants only.
///
/// ARCH-011: repair identity lives in typed `repairKind`, never in payload bytes.
[<RequireQualifiedAccess>]
module RuntimeNudge =

    /// FALLBACK: continuation after a provider failure inside one Logical Run.
    let ProviderRetry = "runtime/provider-retry"

    /// EXEC-016: join-capable role tried to finish while resources remain unjoined.
    let BackgroundJoin = "runtime/background-join"

    /// Interaction repair after tool work with no final report.
    let MissingClosingReport = "runtime/missing-closing-report"

    /// Interaction repair that continues an in-progress turn.
    let InteractionContinue = "runtime/interaction-continue"

    /// Latency watchdog: the request far exceeded this session's normal
    /// request duration and was interrupted; continue from where it stopped.
    let LatencyDegeneration = "runtime/latency-degeneration"
