namespace Wanxiangshu.Repository.Knowledge.Casebook

open Wanxiangshu.Persistence.EventStore

/// DSL-class: DurableFact — CASE-003: one typed observation captured from the
/// final execution layer (builtin read/glob/grep Host execution). Never
/// inferred from transcript text; capture may be incomplete.
[<RequireQualifiedAccess>]
type Observation =
    | FileRead of path: string * contentHash: string
    | GlobResult of pattern: string * paths: string list
    | GrepResult of pattern: string * matches: (string * int * string) list

/// DSL-class: DurableFact — CASE-002 / KR-002: minimal Case model with dual baselines.
/// Identity is the stable logical case identity (scoped to invocation).
type Case =
    { Identity: string
      SourceTrace: string
      Q: string
      A: string
      RelatedPaths: string list
      CompletionFileState: string
      MaintenanceFileState: string
      AccessOrder: int64
      Observations: Observation list }

    member SessionId: string

/// DSL-class: DurableFact — CASE-007 / KR-007: the Casebook domain events. Physical
/// persistence is the unified EventStore; these are the fold inputs.
[<RequireQualifiedAccess>]
type CasebookEvent =
    | CaseCaptured of Case
    | CaseRefreshed of
        identity: string *
        q: string *
        a: string *
        maintenanceFileState: string *
        relatedPaths: string list *
        observations: Observation list
    | CaseAccessed of identity: string
    | CaseEvicted of identity: string

/// DSL-class: Decision — CASE-004/005: the replay classification. No-delta is
/// only a freshness hint, never a correctness proof.
[<RequireQualifiedAccess>]
type ReplayResult =
    | Fresh
    | Stale

module Observations =

    /// CASE-003: normalize — dedupe by identity and fix a canonical order so
    /// the same captured evidence always folds to the same Case bytes.
    val normalize: observations: Observation list -> Observation list

    /// CASE-003: replay classification — every stored observation must match
    /// the replayed result set exactly (same normalized set).
    val classifyReplay: stored: Observation list -> replayed: Observation list -> ReplayResult

/// Converts decoded storage events and reads the immutable Current held by the Integrator.
module CasebookProjection =

    type State = private State of obj

    /// CASE-008: empty projection state.
    val emptyState: State

    /// CASE-008: apply one CasebookEvent to the projection state.
    val apply: state: State -> event: CasebookEvent -> State

    /// Reads the cases visible at the requested capacity without appending events.
    val projectCases: store: IEventStore -> capacity: int -> Map<string, Case>

    /// 解析公开 shelfmark 到内部 Case。
    val resolve: store: IEventStore -> capacity: int -> shelfmark: string -> Result<Case option, string>
