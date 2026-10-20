namespace Wanxiangshu.Context.Prefix

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Trace
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.Participant.Provider.Attempt

/// host-boundary-020/021: X-wire transform decision surface.
///
/// The production `XWire.applyTransform` is async and coupled to `AgentJournal`,
/// `PluginRuntimeScope`, and `ISessionSnapshotPort`. Its surviving decisions are
/// attempt-plan admission and the exact-accepted-retry gate; message content is
/// never rewritten (request-level truncation belongs to the model's `view`
/// declaration). This surface exposes those decisions with JS-native I/O so the
/// fail-closed and no-op laws can be proven without a live runtime.
///
/// The surface IS the production algorithm: every branch delegates to the same
/// pure functions `applyTransform` calls. No support-model copy.
[<RequireQualifiedAccess>]
module XWireSurface =

    [<Emit("$0 == null")>]
    let private isNullish (value: obj) : bool = jsNative

    [<Emit("Boolean($0)")>]
    let private isTruthy (value: obj) : bool = jsNative

    let private text (value: obj) : string =
        if isNullish value then "" else string value

    // ── JS → Domain decoders (same shapes PrefixSurface uses) ───────────────

    let private semanticMessageOfJs (msg: obj) : SemanticMessage =
        let role = text msg?role

        let parts =
            if isNullish msg?parts then
                []
            else
                msg?parts
                |> unbox<obj array>
                |> Array.toList
                |> List.choose (fun part ->
                    let kind = text part?kind

                    match kind with
                    | "text" -> Some(SemanticText(text part?text))
                    | "reasoning" -> Some(SemanticReasoning(text part?text))
                    | "tool-call" -> Some(SemanticToolCall(text part?name, text part?args))
                    | "tool-result" -> Some(SemanticToolResult(text part?result))
                    | "media" ->
                        let mt =
                            if isNullish part?mediaType then
                                None
                            else
                                Some(text part?mediaType)

                        Some(SemanticMedia(mt, text part?contentDigest))
                    | _ -> None)

        { Role = role; Parts = parts }

    let private semanticProjectionOfJs (value: obj) : ProviderSemanticProjection =
        let messages =
            if isNullish value?messages then
                []
            else
                value?messages
                |> unbox<obj array>
                |> Array.toList
                |> List.map semanticMessageOfJs

        { ProviderId = None
          ModelId = None
          Variant = None
          Tools = []
          System = []
          Messages = messages }


    // ── sha256: exact production owner used by XWire.applyTransform ─────────

    let private sha256Hex (value: string) : string = HostDigest.sha256Hex value

    let private attemptOutcomeOfJs (value: obj) : AttemptOutcome option =
        if isNullish value then
            None
        else
            match text value with
            | "completed"
            | "tool-calls" -> Some AttemptOutcome.Completed
            | "completed-invalid" -> Some AttemptOutcome.CompletedInvalid
            | "failed" -> Some AttemptOutcome.Failed
            | "aborted" -> Some AttemptOutcome.Aborted
            | _ -> None

    // ── The transform decision (delegates to XWire's pure decisions) ───────
    //
    // Every branch delegates to the same pure production functions that
    // `applyTransform` calls. The surface extracts the async Host I/O
    // boundaries (journal blob reads, session-snapshot awaits, in-place
    // message replacement) and exposes the *decision* with JS-native I/O.

    /// Return the canonical digest for a provider-visible prefix cutoff. This is
    /// the same proof input used by `transform` when validating Companion coverage.
    let coveredPrefixDigest (projection: obj) (cutoff: int) : string =
        let typed = semanticProjectionOfJs projection

        let snapshot = { CurrentProjection = typed }

        ProjectionRenderer.cutoffDigest HostDigest.sha256Hex snapshot cutoff


    let suppressHostMessagesByIds (rawMessages: obj array) (hostMessageIds: string array) : obj array =
        XWire.suppressHostMessagesByIds
            (if isNull rawMessages then [] else Array.toList rawMessages)
            (if isNull hostMessageIds then
                 Set.empty
             else
                 Set.ofArray hostMessageIds)
        |> List.toArray

    /// host-boundary-020/021: the X-wire transform decision.
    ///
    /// Input fields (JS object):
    ///   journal:       truthy = a durable journal is available.
    ///   sessionId:     the managed session id found in the transform output.
    ///   acceptedRetry: true when Host accepted this physical retry.
    ///   physicalUser:  the current physical user message id.
    ///   acceptedPhysicalUser: the exact Host-accepted retry message id.
    ///   currentProjection: X provider-visible semantic projection (messages array).
    ///
    /// Output fields (JS object):
    ///   ok, noop, changed, consumed, error, output. Message content is never
    ///   rewritten: request-level truncation belongs to the model's `view`
    ///   declaration, and an accepted retry is planned as-is.
    let transform (input: obj) : obj =
        let noop () =
            box
                {| ok = true
                   noop = true
                   changed = false
                   consumed = false
                   error = null
                   output = input?currentProjection |}

        let journal = isTruthy input?journal
        let sessionId = text input?sessionId

        let acceptedRetry =
            not (isNullish input?acceptedRetry) && (input?acceptedRetry |> unbox<bool>)

        let physicalUser = text input?physicalUser
        let acceptedPhysicalUser = text input?acceptedPhysicalUser

        if
            not journal
            || String.IsNullOrEmpty sessionId
            || not acceptedRetry
            || String.IsNullOrEmpty physicalUser
            || not (String.Equals(physicalUser, acceptedPhysicalUser, StringComparison.Ordinal))
        then
            noop ()
        else
            // The exact accepted retry is consumed by this plan.
            box
                {| ok = true
                   noop = false
                   changed = false
                   consumed = true
                   error = null
                   output = input?currentProjection |}

    /// host-boundary-021: reconcile decision — does a completed attempt clear
    /// the plan? Delegates to the pure decision used by `XWire.reconcileAttempt`.
    ///
    /// Input fields (JS object):
    ///   hasPlan:    true when an attempt plan exists for this (session, run).
    ///   outcome:    "completed" | "failed" | "aborted" | "in-progress" | "unknown".
    ///
    /// Output fields (JS object):
    ///   cleared:   true when the plan is cleared (terminal failure/abandon).
    ///   keptPlan:  true when the plan is kept across a non-terminal reread.
    let reconcile (input: obj) : obj =
        let hasPlan = not (isNullish input?hasPlan) && (input?hasPlan |> unbox<bool>)

        let decision =
            XWire.reconciliationDecision hasPlan (attemptOutcomeOfJs input?outcome)

        box
            {| cleared = decision.Cleared
               keptPlan = decision.KeptPlan |}

    // ── R02: real attempt-plan construction + typed owner bridges ───────────
    //
    // Plan construction below delegates to the compiled production
    // implementation: `AttemptPlanner.freezePreInference` (with a production
    // authority built by `ParticipantIdentity.resolveAtRoot` +
    // `PromptAuthority.createAuthorityExecutionProfile`). Binding is the real
    // `AttemptPlanner.bindProviderRun`; observations are plain projections of
    // the real plans. The surface holds no policy of its own and never names
    // the OpenCode recovery owner.
    //
    // Admission/binding/record/peek/consume/ownership live in the OpenCode
    // owner surface (`PluginRecoveryScopeSurface`), which delegates to the real
    // recovery scope behind the opaque scope handle. The typed bridges below are
    // the only seam: they wrap/unwrap the opaque JS handles and project the
    // JSON views, so the owner surface never copies plan equality or binding.

    type private PendingPlanHandle(plan: PendingAttemptPlan) =
        member _.Value = plan

    type private BoundPlanHandle(plan: AttemptPlan) =
        member _.Value = plan

    let private requestKindOfSurfaceJs (value: obj) : Result<ProviderRequestKind, string> =
        match text value |> fun value -> value.ToLowerInvariant() with
        | ""
        | "workmain"
        | "work-main" -> Ok ProviderRequestKind.WorkMain
        | "interactionrepair"
        | "interaction-repair" -> Ok ProviderRequestKind.InteractionRepair
        | unknown -> Error(sprintf "unknown request kind: %s" unknown)

    /// Project a real pending plan to its semantic JSON view. The view carries
    /// authority/request-kind evidence only; lifecycle counters stay out.
    let internal pendingPlanView (plan: PendingAttemptPlan) : obj =
        box
            {| session = SessionId.value plan.Authority.SessionId
               logicalRunId = LogicalRunId.value plan.Authority.LogicalRunId
               root = AuthorityRootUserMessageId.value plan.Authority.AuthorityRootUserMessageId
               physical = PhysicalUserMessageId.value plan.PhysicalUserMessageId
               agent = plan.Authority.SelectedAgent
               role = Roles.roleLabel plan.Authority.CanonicalRole
               kind = ProviderRequestKind.label plan.RequestKind |}

    /// Project a real bound plan to its semantic JSON view.
    let internal boundPlanView (plan: AttemptPlan) : obj =
        box
            {| session = SessionId.value plan.Profile.SessionId
               logicalRunId = LogicalRunId.value plan.Profile.LogicalRunId
               root = AuthorityRootUserMessageId.value plan.Profile.AuthorityRootUserMessageId
               physical = PhysicalUserMessageId.value plan.Profile.PhysicalUserMessageId
               providerRun = ProviderRunIdentity.value plan.Profile.ProviderRun
               agent = plan.Profile.SelectedAgent
               role = Roles.roleLabel plan.Profile.CanonicalRole
               kind = ProviderRequestKind.label plan.Profile.RequestKind |}

    /// Typed owner bridges: wrap/unwrap the opaque JS handles without copying
    /// any admission or binding policy. The owner surface passes handles back;
    /// only these bridges see the typed plans.
    let internal wrapPendingPlan (plan: PendingAttemptPlan) : obj = box (PendingPlanHandle plan)

    let internal unwrapPendingPlan (handle: obj) : PendingAttemptPlan = (unbox<PendingPlanHandle> handle).Value

    let internal wrapBoundPlan (plan: AttemptPlan) : obj = box (BoundPlanHandle plan)

    let internal unwrapBoundPlan (handle: obj) : AttemptPlan = (unbox<BoundPlanHandle> handle).Value

    /// Build a real `PendingAttemptPlan` via the production planner. Input fields:
    /// session, logicalRun, root, physical, kind ("WorkMain" | "InteractionRepair"),
    /// agent/role (optional canonical managed name overriding the default coder
    /// identity, resolved through the real `ParticipantIdentity.resolveAtRoot`).
    let pendingPlan (input: obj) : obj =
        match requestKindOfSurfaceJs input?kind with
        | Error error ->
            box
                {| ok = false
                   error = error
                   handle = null
                   view = null |}
        | Ok requestKind ->
            let session = SessionId.create (text input?session)
            let logicalRun = LogicalRunId.create (text input?logicalRun)
            let root = AuthorityRootUserMessageId.create (text input?root)
            let physical = PhysicalUserMessageId.create (text input?physical)

            let canonicalName =
                let agent = text input?agent
                let role = text input?role

                if not (String.IsNullOrEmpty agent) then agent
                elif not (String.IsNullOrEmpty role) then role
                else ManagedAgentCatalog.nameOf Role.Engineer

            let authorityResult =
                ParticipantIdentity.resolveAtRoot canonicalName
                |> Result.mapError (fun error -> sprintf "invalid participant identity: %A" error)
                |> Result.bind (fun participantIdentity ->
                    PromptAuthority.createAuthorityExecutionProfile
                        session
                        logicalRun
                        root
                        PromptAuthority.RootAuthorityKind.HumanRoot
                        participantIdentity
                    |> Result.mapError (fun error -> sprintf "invalid authority: %s" error))

            match authorityResult with
            | Error error ->
                box
                    {| ok = false
                       error = error
                       handle = null
                       view = null |}
            | Ok authority ->
                let plan =
                    AttemptPlanner.freezePreInference
                        authority
                        physical
                        (PromptAuthority.PromptOrigin.Continuation PromptAuthority.ContinuationKind.ProviderRetryAttempt)
                        requestKind


                box
                    {| ok = true
                       error = null
                       handle = wrapPendingPlan plan
                       view = pendingPlanView plan |}

    /// Bind a pending handle to a run via the real `AttemptPlanner.bindProviderRun`.
    let bindProviderRun (pending: obj) (providerRun: string) : obj =
        let plan = unwrapPendingPlan pending

        let bound =
            AttemptPlanner.bindProviderRun (ProviderRunIdentity.create providerRun) plan

        box
            {| view = boundPlanView bound
               handle = wrapBoundPlan bound |}
