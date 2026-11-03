namespace Wanxiangshu.Repository.Programming.Js

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

/// DSL-class: DurableFact — JS-012/JS-013/JS-015: one as-is mutation in a
/// js-* program. `Rewrite` edits an existing file; `Create` writes a missing
/// one (JS-008/009). `Remove` deletes a file or an empty directory; `Move`
/// renames a file or directory (JS-020/030). Each mutation is atomically
/// persisted at call time; the staging buffer records what was actually
/// written, so a later failure keeps earlier writes on disk.
[<RequireQualifiedAccess>]
type JsStagedMutation =
    | Rewrite of path: string * originalText: string * newText: string
    | Create of path: string * text: string
    | Remove of path: string
    | Move of fromPath: string * toPath: string

type JsReadSnapshot = { Path: string; Text: string }

type JsExplicitFileRead = { Path: string; ResultBytes: string }

[<RequireQualifiedAccess>]
type JsCommitMutation =
    | RewriteFile of path: string * expectedCurrent: string * newText: string
    | CreateFile of path: string * newText: string

[<RequireQualifiedAccess>]
type JsRollbackMutation =
    | RestoreFile of path: string * expectedCurrent: string * originalText: string
    | RemoveCreatedFile of path: string * expectedCurrent: string

module JsStagedMutation =

    let path (mutation: JsStagedMutation) : string =
        match mutation with
        | JsStagedMutation.Rewrite(path, _, _) -> path
        | JsStagedMutation.Create(path, _) -> path
        | JsStagedMutation.Remove(path) -> path
        | JsStagedMutation.Move(fromPath, _) -> fromPath

/// DSL-class: Decision — JS-012/013/014: the pure transaction rules,
/// bridged to `wanxiangli/runtime` (CUT-24). The decision algorithm lives in the
/// library; this module keeps the main-repo signature stable. The
/// filesystem facts (existence, current content) are injected as functions
/// here and materialized into plain data at the boundary, so the decision
/// stays deterministic; the commit/rollback side effects are the adapter's
/// job, never this module's.
module JsTransaction =

    [<Import("Filesystem", "wanxiangli/runtime")>]
    let private Filesystem: obj = jsNative

    [<Emit("$0({ root: '' })")>]
    let private createFilesystem (factory: obj) : obj = jsNative

    let private api = createFilesystem Filesystem

    let private failureOf (result: obj) : JsFailure =
        JsFailure.ofWire (string (result?code)) (string (result?reason))

    let private unitOf (result: obj) : Result<unit, JsFailure> =
        if unbox<bool> (result?ok) then
            Ok()
        else
            Error(failureOf result)

    let private mutationToJs (mutation: JsStagedMutation) : obj =
        match mutation with
        | JsStagedMutation.Rewrite(path, originalText, newText) ->
            createObj
                [ "kind" ==> "rewrite"
                  "path" ==> path
                  "originalText" ==> originalText
                  "newText" ==> newText ]
        | JsStagedMutation.Create(path, text) -> createObj [ "kind" ==> "create"; "path" ==> path; "text" ==> text ]
        | JsStagedMutation.Remove _
        | JsStagedMutation.Move _ ->
            invalidArg "mutation" "remove/move are committed as-is and never enter the batched fs decision"

    let private mutationsToJs (mutations: JsStagedMutation list) : obj array =
        mutations |> List.map mutationToJs |> Array.ofList

    let private commitOf (value: obj) : JsCommitMutation =
        match string (value?kind) with
        | "rewrite" ->
            JsCommitMutation.RewriteFile(string (value?path), string (value?originalText), string (value?newText))
        | "create" -> JsCommitMutation.CreateFile(string (value?path), string (value?text))
        | other -> invalidArg "kind" (sprintf "unknown commit mutation kind: %s" other)

    let private rollbackOf (value: obj) : JsRollbackMutation =
        match string (value?kind) with
        | "restore" ->
            JsRollbackMutation.RestoreFile(
                string (value?path),
                string (value?expectedCurrent),
                string (value?originalText)
            )
        | "removeCreated" -> JsRollbackMutation.RemoveCreatedFile(string (value?path), string (value?expectedCurrent))
        | other -> invalidArg "kind" (sprintf "unknown rollback mutation kind: %s" other)

    let private existingOf (exists: string -> bool) (mutations: JsStagedMutation list) : string array =
        mutations
        |> List.map JsStagedMutation.path
        |> List.filter exists
        |> Array.ofList

    let private currentOf (readCurrent: string -> string option) (paths: string list) : obj =
        let fields =
            paths
            |> List.choose (fun path -> readCurrent path |> Option.map (fun text -> path, box text))

        createObj fields

    let private readSnapshotsToJs (snapshots: JsReadSnapshot list) : obj array =
        snapshots
        |> List.map (fun snapshot -> createObj [ "path" ==> snapshot.Path; "text" ==> snapshot.Text ])
        |> Array.ofList

    /// JS-026 same-path-once: one program may mutate each path exactly once.
    let validateSingleIntent (mutations: JsStagedMutation list) : Result<JsStagedMutation list, JsFailure> =
        match unitOf (api?validateSingleIntent (mutationsToJs mutations)) with
        | Ok() -> Ok mutations
        | Error failure -> Error failure

    /// JS-008/009: rewrite targets must exist, create targets must not.
    let validateTargets (exists: string -> bool) (mutations: JsStagedMutation list) : Result<unit, JsFailure> =
        unitOf (api?validateTargets (existingOf exists mutations, mutationsToJs mutations))

    /// JS-014: a rewrite whose original text no longer matches the current
    /// file content is a conflict; no implicit retry. Create targets have no
    /// freshness constraint (they must be absent — enforced by validateTargets).
    let validateFreshness
        (readCurrent: string -> string option)
        (mutations: JsStagedMutation list)
        : Result<unit, JsFailure> =
        let paths = mutations |> List.map JsStagedMutation.path
        unitOf (api?validateFreshness (currentOf readCurrent paths, mutationsToJs mutations))

    /// JS-013: full preflight — every rule must pass before any file effect.
    let preflight
        (exists: string -> bool)
        (readCurrent: string -> string option)
        (readSnapshots: JsReadSnapshot list)
        (mutations: JsStagedMutation list)
        : Result<unit, JsFailure> =
        let snapshotPaths = readSnapshots |> List.map (fun snapshot -> snapshot.Path)

        let paths =
            List.distinct (snapshotPaths @ (mutations |> List.map JsStagedMutation.path))

        unitOf (
            api?preflight (
                existingOf exists mutations,
                currentOf readCurrent paths,
                readSnapshotsToJs readSnapshots,
                mutationsToJs mutations
            )
        )

    /// JS-013: the commit plan — one write per mutation, in declaration order.
    /// The adapter applies this plan; a failure anywhere rolls the whole plan
    /// back (rollback restores each original text).
    let commitPlan (mutations: JsStagedMutation list) : JsCommitMutation list =
        let result = api?commitPlan (mutationsToJs mutations)
        unbox<obj array> (result?value) |> Array.toList |> List.map commitOf

    /// JS-015: rollback plan — restore every original text (rewrites only;
    /// creates are removed by the adapter). Order is reversed so a partial
    /// commit unwinds last-write-first.
    let rollbackPlan (mutations: JsStagedMutation list) : JsRollbackMutation list =
        let result = api?rollbackPlan (mutationsToJs mutations)
        unbox<obj array> (result?value) |> Array.toList |> List.map rollbackOf

/// Persisted mutation kind. Remove records a path, not the removed bytes;
/// Move records the destination in OriginalText. This is not an undo log.
[<RequireQualifiedAccess>]
type JsDurableMutationKind =
    | Rewrite
    | Create
    | Remove
    | Move

/// One mutation recorded in a Prepared event. A pending record alone does not
/// establish whether the physical operation completed.
type JsDurableMutation =
    {
        Path: string
        /// Previous text for Rewrite; destination for Move; absent for Create/Remove.
        OriginalText: string option
        NewText: string
        Kind: JsDurableMutationKind
    }

/// DSL-class: DurableFact — JS-012: the durable prepare fact. Written to the
/// unified EventStore BEFORE any filesystem effect; a committed transaction
/// is a pair (Prepared, Committed) on the same stream. Each Prepared records
/// exactly one mutation (PLAN S2 step 8 / algorithm H1): the durable ledger
/// tracks per-mutation granularity, so the multi-mutation array form is gone.
type JsTransactionPrepared =
    { TransactionId: JsTransactionId
      WorkspaceRoot: string
      Mutation: JsDurableMutation }

/// DSL-class: DurableFact — JS-012: the durable commit fact. Its presence
/// after a Prepared fact is what makes the transaction committed.
type JsTransactionCommitted = { TransactionId: JsTransactionId }

[<RequireQualifiedAccess>]
type JsLifecycleEventType =
    | Parked
    | Unparked
    | Abandoned
    | Reaped

/// DSL-class: DurableFact — PLAN S2 step 8 / algorithm H3: one lifecycle
/// audit trace for a js-* parked site. Only the audit trace is durable; the
/// parked site itself is a process-live object and is never serialized. The
/// event carries no payload or value beyond the four audit fields.
type JsLifecycleEvent =
    { EventType: JsLifecycleEventType
      Handle: string
      SessionId: string
      At: string }

/// DSL-class: Vocabulary — PLAN S2 step 8 / algorithm H4: the lifecycle audit
/// append failure, kept deliberately separate from the mutation-ledger append
/// failure because the audit stream never participates in write replay.
type JsLifecycleAppendFailure =
    { EventType: JsLifecycleEventType
      Handle: string
      SessionId: string
      EventId: EventId
      Error: AppendError }

/// Incremental recovery Current owned by the canonical Integrator. Pending holds
/// exactly Prepared facts not yet followed by their matching Committed fact;
/// Head is the current EventStore stream parent for the next transaction fact.
type JsTransactionProjection =
    { Head: EventId option
      Pending: Map<JsTransactionId, JsTransactionPrepared> }

module JsTransactionProjection =
    let empty = { Head = None; Pending = Map.empty }

    let prepared (eventId: EventId) (value: JsTransactionPrepared) (projection: JsTransactionProjection) =
        { Head = Some eventId
          Pending = Map.add value.TransactionId value projection.Pending }

    let committed (eventId: EventId) (value: JsTransactionCommitted) (projection: JsTransactionProjection) =
        { Head = Some eventId
          Pending = Map.remove value.TransactionId projection.Pending }

    let pending projection =
        projection.Pending |> Map.toList |> List.map snd

module JsTransactionFacts =

    /// Durable mutation from a single staged mutation (JS-012 / PLAN H1).
    let ofMutation (mutation: JsStagedMutation) : JsDurableMutation =
        match mutation with
        | JsStagedMutation.Rewrite(path, originalText, newText) ->
            { Path = path
              OriginalText = Some originalText
              NewText = newText
              Kind = JsDurableMutationKind.Rewrite }
        | JsStagedMutation.Create(path, text) ->
            { Path = path
              OriginalText = None
              NewText = text
              Kind = JsDurableMutationKind.Create }
        | JsStagedMutation.Remove(path) ->
            { Path = path
              OriginalText = None
              NewText = ""
              Kind = JsDurableMutationKind.Remove }
        | JsStagedMutation.Move(fromPath, toPath) ->
            { Path = fromPath
              OriginalText = Some toPath
              NewText = ""
              Kind = JsDurableMutationKind.Move }
