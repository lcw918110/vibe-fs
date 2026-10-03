namespace Wanxiangshu.Execution.Delegation

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact

/// JS-native fold replay boundary for durable handle lifecycle
/// (managed-session-lifecycle-006/008/015).
///
/// This surface calls the production `ExecutionFactFold.fold` directly — no
/// second interpreter. JS sends a list of fact envelopes; the surface
/// constructs typed `ExecutionFactCases` and folds them through the real fold.
/// The typed `AgentProjectionSet`, `ExecutionFactCases` union, and
/// `FoldRejection` never cross to JS.
///
/// Compile position: after `ExecutionFactFold.fs` and `Composition/Durable/Fold.fs`
/// so the production fold is in scope.
module HandleFoldSurface =

    // ── Fail-closed input parsers ────────────────────────────────────────────
    //
    // Every parser returns `Result`. An unrecognized value yields
    // `{ ok: false, error: { kind, value } }` — never a silent default.

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

    let private inputError (err: {| kind: string; value: string |}) : obj = box {| ok = false; error = err |}

    // ── Fact construction from JS objects ────────────────────────────────────

    let private buildHandleLinked (payload: obj) : Result<ExecutionFactCases, obj> =
        match parseHandleId (payload?Handle) with
        | Error e -> Error(inputError e)
        | Ok h ->
            match parseRole (payload?CanonicalRole) with
            | Error e -> Error(inputError e)
            | Ok role ->
                let ownership =
                    match payload?Ownership with
                    | null -> Ok HandleOwnership.DurableParentHandle
                    | value -> parseOwnership value

                match ownership with
                | Error e -> Error(inputError e)
                | Ok ownership ->
                    let child = SessionId.create (string (payload?ChildSessionId))
                    let parent = SessionId.create (string (payload?ParentSessionId))
                    let agent = string (payload?TargetAgent)

                    let byname =
                        match payload?Byname with
                        | null -> agent
                        | value -> string value

                    Ok(
                        ExecutionFactCases.HandleLinked
                            {| ParentSessionId = parent
                               ChildSessionId = child
                               Handle = h
                               TargetAgent = agent
                               Byname = byname
                               CanonicalRole = role
                               Ownership = ownership |}
                    )

    let private buildHandleCompleted (payload: obj) : Result<ExecutionFactCases, obj> =
        match parseHandleId (payload?Handle) with
        | Error e -> Error(inputError e)
        | Ok h ->
            let kindResult =
                match payload?Kind with
                | null -> Ok HandleCompletionKind.Terminal
                | value -> parseCompletionKind value

            match kindResult with
            | Error e -> Error(inputError e)
            | Ok kind ->
                let parent = SessionId.create (string (payload?ParentSessionId))

                let ref =
                    match payload?CompletionRef with
                    | null -> None
                    | value -> Some(BlobRef.create (string value))

                let digest =
                    match payload?CompletionDigest with
                    | null -> None
                    | value -> Some(BlobDigest.create (string value))

                Ok(
                    ExecutionFactCases.HandleCompleted
                        {| ParentSessionId = parent
                           Handle = h
                           Kind = kind
                           CompletionRef = ref
                           CompletionDigest = digest |}
                )

    let private buildHandleRetired (payload: obj) : Result<ExecutionFactCases, obj> =
        match parseHandleId (payload?Handle) with
        | Error e -> Error(inputError e)
        | Ok h ->
            let parent = SessionId.create (string (payload?ParentSessionId))

            Ok(
                ExecutionFactCases.HandleRetired
                    {| ParentSessionId = parent
                       Handle = h |}
            )

    let private buildHandleAbandoned (payload: obj) : Result<ExecutionFactCases, obj> =
        match parseHandleId (payload?Handle) with
        | Error e -> Error(inputError e)
        | Ok h ->
            let reasonResult =
                match payload?Reason with
                | null -> Ok HandleAbandonReason.ParentCancelled
                | value -> parseAbandonReason value

            match reasonResult with
            | Error e -> Error(inputError e)
            | Ok reason ->
                let parent = SessionId.create (string (payload?ParentSessionId))

                Ok(
                    ExecutionFactCases.HandleAbandoned
                        {| ParentSessionId = parent
                           Handle = h
                           Reason = reason
                           AbandonedAt = DateTimeOffset.MinValue |}
                )

    let private buildScopedFact caseName (payload: obj) =
        let rawWork = payload?Work

        match parseHandleId (rawWork?Handle), parseCompletionKind (payload?Kind) with
        | Error error, _
        | _, Error error -> Error(inputError error)
        | Ok handle, Ok kind ->
            let work =
                { Handle = handle
                  ChildSessionId = SessionId.create (string (rawWork?ChildSessionId))
                  AuthorityRoot = AuthorityRootUserMessageId.create (string (rawWork?AuthorityRoot)) }

            let parent = SessionId.create (string (payload?ParentSessionId))

            let reference =
                match payload?CompletionRef with
                | null -> None
                | value -> Some(BlobRef.create (string value))

            let digest =
                match payload?CompletionDigest with
                | null -> None
                | value -> Some(BlobDigest.create (string value))

            if caseName = "HandleWorkCompleted" then
                Ok(
                    ExecutionFactCases.HandleWorkCompleted
                        {| ParentSessionId = parent
                           Work = work
                           Kind = kind
                           CompletionRef = reference
                           CompletionDigest = digest |}
                )
            else
                Ok(
                    ExecutionFactCases.HandleWorkConsumed
                        {| ParentSessionId = parent
                           Work = work
                           Kind = kind
                           CompletionRef = reference
                           CompletionDigest = digest
                           ConsumptionId = string (payload?ConsumptionId) |}
                )

    let private buildFact (factObj: obj) : Result<ExecutionFactCases, obj> =
        let caseName = string (factObj?``case``)
        let payload = factObj?payload

        match caseName with
        | "HandleWorkCompleted"
        | "HandleWorkConsumed" -> buildScopedFact caseName payload
        | "HandleLinked" -> buildHandleLinked payload
        | "HandleCompleted" -> buildHandleCompleted payload
        | "HandleRetired" -> buildHandleRetired payload
        | "HandleAbandoned" -> buildHandleAbandoned payload
        | other ->
            Error(
                box
                    {| ok = false
                       error =
                        {| kind = "UnknownFactCase"
                           value = other |} |}
            )

    // ── Fold state ───────────────────────────────────────────────────────────

    /// A fold state holds an `AgentProjectionSet`. JS treats it as opaque.
    type FoldState internal (projection: AgentProjectionSet) =
        member _.Internal = projection

    let foldEmpty () : FoldState = FoldState AgentProjection.empty

    /// Fold a list of fact envelopes through the production `ExecutionFactFold`.
    /// Each envelope is `{ seq, stream, fact }` where `fact` is
    /// `{ case, payload }`. Returns `{ ok: true, state }` or
    /// `{ ok: false, error: { Fact, Reason } }` (fold rejection) or
    /// `{ ok: false, error: { kind, value } }` (invalid input).
    let foldApply (state: FoldState) (envelopes: obj array) : obj =
        let rec foldAll current remaining =
            match remaining with
            | [] -> Ok current
            | envelope :: tail ->
                let factObj = envelope?fact

                match buildFact factObj with
                | Error errorBox -> Error errorBox
                | Ok fact ->
                    match DelegationProjectionBridge.foldExecution current fact with
                    | Ok next -> foldAll next tail
                    | Error rejection ->
                        Error(
                            box
                                {| ok = false
                                   error =
                                    {| Fact = rejection.Fact
                                       Reason = rejection.Reason |} |}
                        )

        match foldAll state.Internal (Array.toList envelopes) with
        | Ok current ->
            box
                {| ok = true
                   state = FoldState current |}
        | Error errorBox -> errorBox

    /// Extract the handle projection for one parent session from a fold state.
    /// Returns a `HandleProjectionState` (from `HandleSurface`) that the
    /// `read`/`views`/etc. helpers accept.
    let foldSession (state: FoldState) (parentId: string) : HandleSurface.HandleProjectionState =
        let parentSessionId = SessionId.create parentId

        match AgentProjection.tryFind parentSessionId state.Internal with
        | Some session ->
            match session.Handles with
            | Some handles -> HandleSurface.HandleProjectionState handles
            | None -> HandleSurface.HandleProjectionState HandleProjection.empty
        | None -> HandleSurface.HandleProjectionState HandleProjection.empty
