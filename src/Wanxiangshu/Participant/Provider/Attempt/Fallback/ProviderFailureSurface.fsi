namespace Wanxiangshu.Participant.Provider.Attempt.Fallback

open System
open System.Threading.Tasks
open Wanxiangshu.Persistence.Journal

[<RequireQualifiedAccess>]
module ProviderFailureSurface =
    val budget: obj
    val providerFailureProjection: obj

    val authorityRootAccepted: value: obj -> obj
    val providerFailureRecorded: value: obj -> obj
    val providerRetryExhausted: value: obj -> obj
    val providerSuccessRecorded: value: obj -> obj
    val envelope: value: obj -> obj
    val fold: values: obj array -> obj
    val providerFailureFactCaseNames: string array

    /// Exercise the real retry engine with observable admission and redispatch ports.
    /// Failure, execution key, ProviderRun and RequestKind come from the policy input;
    /// current logical identity and budget come from the supplied projection.
    val retryAttempt:
        input: obj -> current: obj -> admit: (obj -> Task<string>) -> redispatch: (obj -> Task<unit>) -> Task<string>

    val acceptHumanRoot:
        handle: JournalHandle -> session: string -> physicalMessage: string -> agent: string -> Task<obj>

    val recordConfirmedFailure:
        handle: JournalHandle -> budget: int -> session: string -> providerRun: string -> reason: string -> Task<obj>

    /// provider-attempt-recovery-021: the durable `ProviderRetryAttempt` dispatch fact of one failed
    /// physical attempt — the fact the recovery target settlement consumes.
    /// True only when the failed provider run is the exact run that established
    /// the request's durable `ProviderStarted`.
    /// provider-attempt-recovery-021 test seam: establish durable `Accepted` + `ProviderStarted`
    /// facts for one physical request and provider run.
    val establishProviderRun:
        handle: JournalHandle -> session: string -> physicalMessage: string -> providerRun: string -> Task<obj>

    /// provider-attempt-recovery-023 test seam: establish only the durable `Accepted` fact for one
    /// physical request (the `Accepted ∧ ¬ProviderStarted` obligation shape).
    val establishAcceptedExecution: handle: JournalHandle -> session: string -> physicalMessage: string -> Task<obj>

    /// provider-attempt-recovery-003 test seam: the request kind a confirmed failure continues with.
    val requestKindFor: handle: JournalHandle -> session: string -> physicalMessage: string -> string

    val wasSameProviderRetryAttempt:
        handle: JournalHandle -> session: string -> physicalMessage: string -> providerRun: string -> bool

    val snapshot: handle: JournalHandle -> session: string -> obj
