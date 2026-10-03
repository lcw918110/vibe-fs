namespace Wanxiangshu.Execution.Delegation

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// JS-native projection boundary for durable handle lifecycle. The projection
/// remains the delegation owner's typed implementation; JS receives snapshots,
/// never the union or map representation.
///
/// This surface owns the direct-projection command API (link / complete /
/// abandon / retire / read / views). Fact-based fold replay lives in
/// `Handle/FoldSurface.fs`, which calls the production `ExecutionFactFold`
/// directly — no second interpreter here.
module HandleSurface =
    let private handle = HandleId.Agent(AgentHandleId.create "h1")

    let private completion kind : HandleCompletion =
        { Kind = kind
          CompletionRef = None
          CompletionDigest = None }

    let private lifecycleName lifecycle =
        match lifecycle with
        | HandleLifecycle.Active -> "Active"
        | HandleLifecycle.CompletedAwaitingJoin _ -> "CompletedAwaitingJoin"
        | HandleLifecycle.Abandoned _ -> "Abandoned"
        | HandleLifecycle.Retired -> "Retired"

    let private snapshot projection =
        match Map.tryFind handle projection.Handles with
        | None -> null
        | Some record ->
            box
                {| handle = HandleId.describe record.Handle
                   child = SessionId.value record.ChildSessionId
                   targetAgent = record.TargetAgent
                   role = record.CanonicalRole.ToString()
                   lifecycle = lifecycleName record.Lifecycle
                   creationOrder = record.CreationOrder |}

    let scenario (action: string) : obj =
        let linked =
            HandleProjection.link
                handle
                (SessionId.create "ses_child")
                "coder"
                Role.Coder
                HandleOwnership.DurableParentHandle
                HandleProjection.empty

        match linked with
        | Error _ -> null
        | Ok projection ->
            let finalProjection =
                match action with
                | "complete" ->
                    match HandleProjection.complete handle (completion HandleCompletionKind.Terminal) projection with
                    | Ok value -> value
                    | Error _ -> projection
                | "abandon" ->
                    match HandleProjection.abandon handle HandleAbandonReason.ParentCancelled projection with
                    | Ok value -> value
                    | Error _ -> projection
                | "retire" ->
                    match HandleProjection.complete handle (completion HandleCompletionKind.Terminal) projection with
                    | Ok completed ->
                        match HandleProjection.retire handle completed with
                        | Ok value -> value
                        | Error _ -> projection
                    | Error _ -> projection
                | _ -> projection

            box
                {| ok = true
                   action = action
                   record = snapshot finalProjection
                   listable = HandleProjection.listable finalProjection |> List.length
                   horizonVisible = HandleProjection.horizonVisible finalProjection |> List.length
                   joinable = HandleProjection.joinable finalProjection |> List.length |}

    /// Crash-reconciliation matrix at the handle owner. Duplicate completion
    /// and retirement are replayed through the same projection transitions.
    let crashScenario (action: string) : obj =
        let linked =
            HandleProjection.link
                handle
                (SessionId.create "ses_child")
                "coder"
                Role.Coder
                HandleOwnership.DurableParentHandle
                HandleProjection.empty

        let projection =
            match linked with
            | Ok value -> value
            | Error _ -> HandleProjection.empty

        let completeProjection value =
            match HandleProjection.complete handle (completion HandleCompletionKind.Terminal) value with
            | Ok next -> next
            | Error _ -> value

        let retireProjection value =
            match HandleProjection.retire handle value with
            | Ok next -> next
            | Error _ -> value

        let completed =
            match action with
            | "completed"
            | "replayed-completed" -> completeProjection projection
            | "retired"
            | "replayed-retired" -> projection |> completeProjection |> retireProjection
            | _ -> projection

        let replayed =
            match action with
            | "replayed-completed" ->
                match HandleProjection.complete handle (completion HandleCompletionKind.Terminal) completed with
                | Ok value -> value
                | Error _ -> completed
            | "replayed-retired" ->
                let retired =
                    match HandleProjection.retire handle completed with
                    | Ok value -> value
                    | Error _ -> completed

                match HandleProjection.complete handle (completion HandleCompletionKind.Terminal) retired with
                | Ok value -> value
                | Error _ -> retired
            | _ -> completed

        match Map.tryFind handle replayed.Handles with
        | None -> null
        | Some record ->
            box
                {| lifecycle = lifecycleName record.Lifecycle
                   completion =
                    match record.LastCompletion with
                    | Some completion -> box {| kind = completion.Kind.ToString() |}
                    | None -> null
                   abandonReason = null
                   joinable = HandleProjection.joinable replayed |> List.length
                   retired = record.Lifecycle = HandleLifecycle.Retired |}

    // ── JS-native handle lifecycle surface (managed-session-lifecycle-006/007/008/009/015) ─
    //
    // The handle projection vertical slice. JS tests send a command and
    // receive plain-object snapshots; the typed HandleProjection (Map, DU,
    // record) stays private to the owner. No opaque handle is needed for pure
    // projection — the projection itself is the state, returned as JSON.
    //
    // Every input parser is fail-closed: an unrecognized role, handle kind,
    // completion kind, abandon reason, or ownership returns
    // `{ ok: false, error: { kind, value } }` rather than silently defaulting.

    /// JS-visible handle identity. The string form is `kind:value`, matching
    /// `HandleId.describe`. The surface parses it back into the typed union
    /// so the projection never sees a raw string key.
    let private parseHandleId (value: obj) : Result<HandleId, {| kind: string; value: string |}> =
        let text = string value

        if text.StartsWith("agent:", StringComparison.Ordinal) then
            Ok(HandleId.Agent(AgentHandleId.create (text.Substring(6))))
        elif text.StartsWith("pty:", StringComparison.Ordinal) then
            Ok(HandleId.Pty(PtyHandleId.create (text.Substring(4))))
        elif text.StartsWith("manager-job:", StringComparison.Ordinal) then
            Ok(HandleId.ManagerJob(ManagerJobId.create (text.Substring(12))))
        else
            Error
                {| kind = "UnknownHandleKind"
                   value = text |}

    let private parseRole (value: obj) : Result<Role, {| kind: string; value: string |}> =
        match string value with
        | "Engineer" -> Ok Role.Engineer
        | "Coder" -> Ok Role.Coder
        | "DevOps" -> Ok Role.DevOps
        | "Manager" -> Ok Role.Manager
        | "Inspector" -> Ok Role.Inspector
        | "Blogger" -> Ok Role.Blogger
        | "Distiller" -> Ok Role.Distiller
        | "Orchestrator" -> Ok Role.Orchestrator
        | "Browser" -> Ok Role.Browser
        | "Inquiry" -> Ok Role.Inquiry
        | other ->
            Error
                {| kind = "UnknownRole"
                   value = other |}

    let private parseCompletionKind (value: obj) : Result<HandleCompletionKind, {| kind: string; value: string |}> =
        match string value with
        | "Terminal" -> Ok HandleCompletionKind.Terminal
        | "SendFailure" -> Ok HandleCompletionKind.SendFailure
        | "Cancelled" -> Ok HandleCompletionKind.Cancelled
        | other ->
            Error
                {| kind = "UnknownCompletionKind"
                   value = other |}

    let private parseAbandonReason (value: obj) : Result<HandleAbandonReason, {| kind: string; value: string |}> =
        match string value with
        | "ParentCancelled" -> Ok HandleAbandonReason.ParentCancelled
        | "DeadlineExceeded" -> Ok HandleAbandonReason.DeadlineExceeded
        | "HostSessionGone" -> Ok HandleAbandonReason.HostSessionGone
        | other ->
            Error
                {| kind = "UnknownAbandonReason"
                   value = other |}

    let private parseOwnership (value: obj) : Result<HandleOwnership, {| kind: string; value: string |}> =
        match string value with
        | "DurableParentHandle" -> Ok HandleOwnership.DurableParentHandle
        | "HostOwnedHidden" -> Ok HandleOwnership.HostOwnedHidden
        | other ->
            Error
                {| kind = "UnknownOwnership"
                   value = other |}

    let private rejectionName (rejection: HandleTransitionRejection) : string =
        match rejection with
        | UnknownHandle -> "UnknownHandle"
        | HandleIdentityConflict -> "HandleIdentityConflict"
        | HandleIsRetired -> "HandleIsRetired"
        | AlreadyCompleted -> "AlreadyCompleted"
        | AlreadyAbandoned -> "AlreadyAbandoned"
        | NotCompleted -> "NotCompleted"
        | WorkNotAdmitted -> "WorkNotAdmitted"
        | WorkStillActive -> "WorkStillActive"
        | ConsumptionMismatch -> "ConsumptionMismatch"
        | LegacyWorkAmbiguous -> "LegacyWorkAmbiguous"

    /// JS `undefined` for missing optional fields. Fable's `null` would fail
    /// `deepStrictEqual` against `undefined` in the test suite.
    [<Emit("undefined")>]
    let private jsUndefined: obj = jsNative

    let private optStr (value: 'T option) (extract: 'T -> string) : obj =
        match value with
        | Some item -> box (extract item)
        | None -> jsUndefined

    /// JS-native proof surface for the optional string adapter used by record
    /// snapshots. `hasValue` carries the option case without exposing Fable's
    /// option representation to JS.
    let optionalStringTraversal (hasValue: bool) (value: obj) (extract: obj -> string) : obj =
        optStr (if hasValue then Some value else None) extract

    /// Full record snapshot — the shape every `read(tryFind(...))` call expects.
    /// Missing optional fields are `undefined` (not `null`) to match JS
    /// `deepStrictEqual` semantics in the test suite.
    let private recordView (record: HandleRecord) : obj =
        let completion, completionRef, completionDigest, abandonReason =
            match record.Lifecycle with
            | HandleLifecycle.CompletedAwaitingJoin c ->
                box (c.Kind.ToString()),
                optStr c.CompletionRef BlobRef.value,
                optStr c.CompletionDigest BlobDigest.value,
                jsUndefined
            | HandleLifecycle.Abandoned reason -> jsUndefined, jsUndefined, jsUndefined, box (reason.ToString())
            | HandleLifecycle.Retired ->
                // The tombstone retains the last completion that was consumed
                // by join — EXEC-005 requires `list` to say WHICH completion
                // landed, and that survives retirement.
                match record.LastCompletion with
                | Some c ->
                    box (c.Kind.ToString()),
                    optStr c.CompletionRef BlobRef.value,
                    optStr c.CompletionDigest BlobDigest.value,
                    jsUndefined
                | None -> jsUndefined, jsUndefined, jsUndefined, jsUndefined
            | _ -> jsUndefined, jsUndefined, jsUndefined, jsUndefined

        box
            {| handle = HandleId.describe record.Handle
               child = SessionId.value record.ChildSessionId
               targetAgent = record.TargetAgent
               role = record.CanonicalRole.ToString()
               lifecycle = lifecycleName record.Lifecycle
               creationOrder = record.CreationOrder
               completion = completion
               completionRef = completionRef
               completionDigest = completionDigest
               abandonReason = abandonReason |}

    /// A projection state that JS holds as an opaque token. JS never reads its
    /// fields; it passes it back to `apply` / `view` / `read`.
    type HandleProjectionState internal (projection: AgentLinkageProjection) =
        member _.Internal = projection

    let private okResult (state: HandleProjectionState) : obj = box {| ok = true; state = state |}

    let private errorResult (error: obj) : obj = box {| ok = false; error = error |}

    let private inputError (err: {| kind: string; value: string |}) : obj = errorResult err

    let private resultOf (outcome: Result<AgentLinkageProjection, HandleTransitionRejection>) : obj =
        match outcome with
        | Ok projection -> okResult (HandleProjectionState projection)
        | Error rejection ->
            errorResult (
                box
                    {| kind = "TransitionRejected"
                       reason = rejectionName rejection |}
            )

    /// The empty projection. JS starts every scenario from this.
    let empty () : HandleProjectionState =
        HandleProjectionState HandleProjection.empty

    let emptyState () : HandleProjectionState = empty ()

    /// Apply one lifecycle command to a projection state.
    ///
    /// Commands:
    ///   { op: "link", handle, child, agent, role, ownership? } — journal replay
    ///     semantics: a Retired binding absorbs the replay and stays Retired.
    ///   { op: "link-command", handle, child, agent, role, ownership? } —
    ///     explicit command semantics: a Retired binding reopens Labor on the
    ///     same child session, keeping the LastCompletion tombstone.
    ///   { op: "complete", handle, kind?, ref?, digest? }
    ///   { op: "abandon", handle, reason? }
    ///   { op: "retire", handle }
    ///
    /// Unrecognized inputs return `{ ok: false, error: { kind, value } }`.
    let apply (state: HandleProjectionState) (command: obj) : obj =
        let projection = state.Internal
        let op = string (command?op)

        match op with
        | "link"
        | "link-command" ->
            match parseHandleId (command?handle) with
            | Error e -> inputError e
            | Ok h ->
                match parseRole (command?role) with
                | Error e -> inputError e
                | Ok role ->
                    let ownership =
                        match command?ownership with
                        | null -> Ok HandleOwnership.DurableParentHandle
                        | value -> parseOwnership value

                    match ownership with
                    | Error e -> inputError e
                    | Ok ownership ->
                        let child = SessionId.create (string (command?child))
                        let agent = string (command?agent)

                        (if op = "link" then
                             HandleProjection.replayLink h child agent agent role ownership projection
                         else
                             HandleProjection.link h child agent role ownership projection)
                        |> resultOf

        | "complete" ->
            match parseHandleId (command?handle) with
            | Error e -> inputError e
            | Ok h ->
                let kindResult =
                    match command?kind with
                    | null -> Ok HandleCompletionKind.Terminal
                    | value -> parseCompletionKind value

                match kindResult with
                | Error e -> inputError e
                | Ok kind ->
                    let ref =
                        match command?ref with
                        | null -> None
                        | value -> Some(BlobRef.create (string value))

                    let digest =
                        match command?digest with
                        | null -> None
                        | value -> Some(BlobDigest.create (string value))

                    let c: HandleCompletion =
                        { Kind = kind
                          CompletionRef = ref
                          CompletionDigest = digest }

                    HandleProjection.complete h c projection |> resultOf

        | "abandon" ->
            match parseHandleId (command?handle) with
            | Error e -> inputError e
            | Ok h ->
                let reasonResult =
                    match command?reason with
                    | null -> Ok HandleAbandonReason.ParentCancelled
                    | value -> parseAbandonReason value

                match reasonResult with
                | Error e -> inputError e
                | Ok reason -> HandleProjection.abandon h reason projection |> resultOf

        | "retire" ->
            match parseHandleId (command?handle) with
            | Error e -> inputError e
            | Ok h -> HandleProjection.retire h projection |> resultOf

        | _ ->
            errorResult (
                box
                    {| kind = "UnknownCommand"
                       value = op |}
            )

    /// Read one handle record from a projection state. Returns `null` if the
    /// handle is not in the map (never linked, or — impossible for retired —
    /// removed).
    let read (state: HandleProjectionState) (handle: obj) : obj =
        let projection = state.Internal

        match parseHandleId handle with
        | Error _ -> null
        | Ok typedHandle ->
            match HandleProjection.tryFind typedHandle projection with
            | None -> null
            | Some record -> recordView record

    /// `isRetired(handle, projection)` — the tombstone question.
    let isRetired (state: HandleProjectionState) (handle: obj) : bool =
        match parseHandleId handle with
        | Error _ -> false
        | Ok h -> HandleProjection.isRetired h state.Internal

    /// `isAbandoned(handle, projection)`.
    let isAbandoned (state: HandleProjectionState) (handle: obj) : bool =
        match parseHandleId handle with
        | Error _ -> false
        | Ok h -> HandleProjection.isAbandoned h state.Internal

    /// `tryFind(handle, projection)` — returns the record or `null`. JS uses
    /// `isSome(tryFind(...))` to distinguish "retired" from "never existed".
    let tryFind (state: HandleProjectionState) (handle: obj) : obj = read state handle

    /// `tryFindByChildSession(child, projection)`.
    let tryFindByChildSession (state: HandleProjectionState) (child: obj) : obj =
        let projection = state.Internal
        let childId = SessionId.create (string child)

        match HandleProjection.tryFindByChildSession childId projection with
        | None -> null
        | Some record -> recordView record

    /// The three derived views, as sorted `describe()` strings.
    let views (state: HandleProjectionState) : obj =
        let projection = state.Internal

        let describeRecords (records: HandleRecord list) =
            records
            |> List.map (fun r -> HandleId.describe r.Handle)
            |> List.sort
            |> List.toArray

        box
            {| listable =
                describeRecords (
                    if projection.Works.IsEmpty then
                        HandleProjection.auditListable projection
                    else
                        HandleProjection.listable projection
                )
               joinable = describeRecords (HandleProjection.joinable projection)
               active =
                describeRecords (
                    if projection.Works.IsEmpty then
                        HandleProjection.auditActiveHandles projection
                    else
                        HandleProjection.activeHandles projection
                ) |}

    /// `linkedChildren(projection)` — every child session ever linked, as
    /// record snapshots sorted by creation order.
    let linkedChildren (state: HandleProjectionState) : obj array =
        HandleProjection.linkedChildren state.Internal
        |> List.sortBy (fun record -> record.CreationOrder)
        |> List.map recordView
        |> List.toArray

    /// `reportableAbandoned(projection)` — count of Abandoned handles join
    /// must include in the next batch.
    let reportableAbandonedCount (state: HandleProjectionState) : int =
        HandleProjection.reportableAbandoned state.Internal |> List.length

    // ── Handle identity helpers (typed union never crosses to JS) ────────────

    /// `handleId.agent('h1')` → `"agent:h1"`. The JS-visible handle identity
    /// is the `describe()` string; the surface parses it back into the typed
    /// union on every command.
    let handleIdAgent (value: string) : string = "agent:" + value
    let handleIdPty (value: string) : string = "pty:" + value
    let handleIdManagerJob (value: string) : string = "manager-job:" + value
    let handleIdDescribe (value: string) : string = value

    let handleIdTryAgent (value: string) : obj =
        if value.StartsWith("agent:", StringComparison.Ordinal) then
            box (value.Substring(6))
        else
            null

    // ── Journal serialize/deserialize (0.5.1 codec migration) ────────────────
    //
    // The journal line is the JSON serialization of the fact object. The
    // 0.5.1 migration test strips CompletionRef/CompletionDigest from a
    // modern line and verifies the fold still absorbs it (missing fields →
    // None). The surface serializes/deserializes the JS fact object directly;
    // the fold's parseFact treats missing fields as None.

    let serializeFact (value: obj) : string = Fable.Core.JS.JSON.stringify (value)

    let deserializeFact (line: string) : obj =
        try
            let value = Fable.Core.JS.JSON.parse (line)
            box {| ok = true; value = value |}
        with _ ->
            box {| ok = false; error = "InvalidJson" |}
