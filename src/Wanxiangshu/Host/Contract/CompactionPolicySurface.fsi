namespace Wanxiangshu.Host.Contract

/// JS-native boundary for HOST-006 compaction prevention + containment policy.
///
/// The policy module (`HostCompactionPolicy`) is pure F# with typed unions
/// (`CompactionGateVerdict`, `CompactionSetting`) and `ProviderRunIdentity`
/// parameters. This surface translates those into JSON-shaped objects so
/// semantic tests observe the real policy without touching Fable representation.
module CompactionPolicySurface =

    /// Required prevention settings as JSON: `{ path, required, clause, reason }`.
    val requiredSettings: unit -> obj

    /// `experimental.compaction.autocontinue` answer (always false).
    val autoContinueEnabled: unit -> bool

    /// HOST-006 containment: is this message a Host compaction pseudo-run.
    val isContainableCompaction: isCompaction: bool -> bool

    /// HOST-006 containment: newest unhandled compaction run, or null.
    ///
    /// `observed` is an array of raw run-id strings (oldest first).
    /// `isReanchored` is a JS function `(runId: string) => boolean`.
    val nextReanchor: observed: string array -> isReanchored: obj -> obj

    /// HOST-006 startup probe: pseudo-runs inside the first-turn window (count or null).
    val firstTurnCompactionRuns: messages: obj array -> obj

    /// HOST-006 startup probe verdict as JSON:
    ///   `{ kind: "Satisfied", message }`
    ///   `{ kind: "SettingUnavailable", path, required, clause, reason, message }`
    ///   `{ kind: "CompactedDespiteSettings", session, runs, message }`
    val judgeFirstTurn: session: string -> pseudoRunsOnFirstTurn: int -> obj

    /// HOST-006 startup verdict when one required setting could not be established.
    val judgeFirstTurnWithUnavailable: unavailablePath: string -> session: string -> pseudoRunsOnFirstTurn: int -> obj
