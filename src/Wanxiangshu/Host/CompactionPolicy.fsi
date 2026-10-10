namespace Wanxiangshu.Host

open Wanxiangshu.Foundation.Identity

type CompactionSetting =
    { Path: string list
      Required: bool
      Clause: string
      Reason: string }

/// HOST-006 startup probe: one Host message reduced to the two facts the
/// first-turn window needs.
type CompactionWindowMessage =
    { CompletedAssistant: bool
      Compaction: bool }

[<RequireQualifiedAccess>]
type CompactionGateVerdict =
    | Satisfied
    | SettingUnavailable of CompactionSetting
    | CompactedDespiteSettings of session: SessionId * runs: int

[<RequireQualifiedAccess>]
module HostCompactionPolicy =
    val requiredSettings: CompactionSetting list
    val autoContinueEnabled: bool
    val isContainableCompaction: isCompaction: bool -> bool

    val nextReanchor:
        observed: ProviderRunIdentity list -> isReanchored: (ProviderRunIdentity -> bool) -> ProviderRunIdentity option

    val firstTurnCompactionRuns: messages: CompactionWindowMessage list -> int option

    val judgeFirstTurn:
        unavailable: CompactionSetting option ->
        session: SessionId ->
        pseudoRunsOnFirstTurn: int ->
            CompactionGateVerdict

    val describeVerdict: verdict: CompactionGateVerdict -> string
