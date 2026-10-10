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
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Trace
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.Participant.Provider.Attempt

/// host-boundary-020/021: X-wire transform decision surface.
///
/// The production `XWire.applyTransform` is async and coupled to `AgentJournal`,
/// `PluginRuntimeScope`, and `ISessionSnapshotPort` — it orchestrates blob reads,
/// session-snapshot awaits, and in-place message replacement. The *decisions* it
/// makes are pure: `PrefixProbeSelection.select`, and
/// `XPrefixProjection.forChoice` / `XPrefixProjection.render`. This surface exposes that decision pipeline
/// as a single JS-callable function with JS-native input/output, so the
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

    let private intValue (value: obj) : int = int (text value)

    // ── JS → Domain decoders (same shapes PrefixSurface uses) ───────────────

    let private snapshotOfJs (value: obj) : PrefixSnapshot =
        { FrozenRecordPrefixRef = BlobRef.create (text value?ref)
          FrozenRecordPrefixDigest = BlobDigest.create (text value?frozenDigest)
          CutoffExclusive = intValue value?cutoff
          CoveredPrefixDigest = text value?prefixDigest
          SealRoot = text value?sealRoot
          SyntheticMessageId = text value?syntheticId }

    let private snapshotOptionOfJs (value: obj) : PrefixSnapshot option =
        if isNullish value then None else Some(snapshotOfJs value)

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

    // ── Domain → JS encoders ────────────────────────────────────────────────

    let private semanticPartToJs (part: SemanticPart) : obj =
        match part with
        | SemanticText t -> box {| kind = "text"; text = t |}
        | SemanticReasoning t -> box {| kind = "reasoning"; text = t |}
        | SemanticToolCall(name, args) ->
            box
                {| kind = "tool-call"
                   name = name
                   args = args |}
        | SemanticToolResult result ->
            box
                {| kind = "tool-result"
                   result = result |}
        | SemanticMedia(mediaType, digest) ->
            box
                {| kind = "media"
                   mediaType = mediaType
                   contentDigest = digest |}

    let private semanticMessageToJs (msg: SemanticMessage) : obj =
        box
            {| role = msg.Role
               parts = msg.Parts |> List.map semanticPartToJs |> List.toArray |}

    let private noCandidateReasonLabel (reason: NoCandidateReason) : string =
        match reason with
        | NoCandidateReason.NoCoverage -> "NoCoverage"
        | NoCandidateReason.WouldRetreat _ -> "WouldRetreat"
        | NoCandidateReason.NotNewerThanCommitted -> "NotNewerThanCommitted"
        | NoCandidateReason.CutoffProofFailed(expected, recomputed) -> $"CutoffProofFailed:{expected}:{recomputed}"
        | NoCandidateReason.BeyondPhaseBoundary(desired, material) -> $"BeyondPhaseBoundary:{desired}:{material}"
        | NoCandidateReason.MaterialBeyondBoundary(material, limit) -> $"MaterialBeyondBoundary:{material}:{limit}"

    let private probeToJs (probe: PrefixProbe) : obj =
        let c = probe.Candidate

        box
            {| probeId = probe.ProbeId
               basedOnEpoch = PrefixEpochId.value probe.BasedOnEpochId
               candidate =
                box
                    {| ref = BlobRef.value c.FrozenRecordPrefixRef
                       frozenDigest = BlobDigest.value c.FrozenRecordPrefixDigest
                       cutoff = c.CutoffExclusive
                       prefixDigest = c.CoveredPrefixDigest
                       sealRoot = c.SealRoot
                       syntheticId = c.SyntheticMessageId |} |}

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

    let presentationHorizon (hasProbe: bool) : string =
        match XWire.presentationHorizonForProbe hasProbe with
        | PrefixPresentationHorizon.Current -> "Current"
        | PrefixPresentationHorizon.TentativeCold -> "TentativeCold"

    let retiredRetryMessageIds (horizon: string) (rawMessages: obj array) : string array =
        let typedHorizon =
            match horizon with
            | "TentativeCold" -> PrefixPresentationHorizon.TentativeCold
            | _ -> PrefixPresentationHorizon.Current

        XWire.retryTransportRetirement typedHorizon (Array.toList rawMessages)
        |> Set.toArray

    let replacePrefixByHostIds
        (rawMessages: obj array)
        (coveredHostMessageIds: string array)
        (openingHostMessageId: obj)
        (syntheticMessageId: string)
        (memory: string)
        : obj array =
        XWire.replacePrefixByHostIds
            (if isNull rawMessages then [] else Array.toList rawMessages)
            (if isNull coveredHostMessageIds then
                 []
             else
                 Array.toList coveredHostMessageIds)
            (if isNullish openingHostMessageId then
                 None
             else
                 Some(text openingHostMessageId))
            syntheticMessageId
            memory
        |> List.toArray

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
    ///   failures:      current consecutive failure count after visible tool-success settlement.
    ///   prefixEpoch:   current durable prefix epoch (the probe's base epoch).
    ///   physicalUser:  the current physical user message id.
    ///   acceptedPhysicalUser: the exact Host-accepted retry message id.
    ///   snapshotPort:  truthy = the public session snapshot port is available.
    ///   currentProjection: X provider-visible semantic projection (messages array).
    ///   committedSnapshot: committed prefix snapshot (or null).
    ///   coverableCutoff:  Companion's coverable turn cutoff.
    ///   coveredDigest:    Companion's covered prefix digest.
    ///   requestStartCutoff: turns preceding this request's physical user message.
    ///   frozenRecordPrefixRef: blob ref for the frozen record prefix.
    ///   frozenRecordPrefixDigest: blob digest for the frozen record prefix.
    ///   frozenRecordPrefixBody: already-materialized frozen record prefix body.
    ///   memoryPreamble: localized companion memory preamble.
    ///   outcome:        "completed" | "failed" | "aborted" | "in-progress" | null.
    ///
    /// Output fields (JS object):
    ///   ok, noop, changed, consumed, promoted, probe, noProbeReason, error, output.
    ///   `promoted` is plan-time promotability at the observed prefix epoch;
    ///   durable promotion is committed only by `reconcile`, which rechecks
    ///   the probe epoch.
    let transform (input: obj) : obj =
        // ── host-boundary-021: no journal → no-op ──
        let journal = isTruthy input?journal

        if not journal then
            box
                {| ok = true
                   noop = true
                   changed = false
                   consumed = false
                   promoted = false
                   probe = null
                   noProbeReason = null
                   error = null
                   output = input?currentProjection |}
        else
            // ── host-boundary-021: no session id → no-op ──
            let sessionId = text input?sessionId

            if String.IsNullOrEmpty sessionId then
                box
                    {| ok = true
                       noop = true
                       changed = false
                       consumed = false
                       promoted = false
                       probe = null
                       noProbeReason = null
                       error = null
                       output = input?currentProjection |}
            else
                // ── provider-attempt-recovery-011: only the exact Host-accepted physical retry may plan ──
                let acceptedRetry =
                    not (isNullish input?acceptedRetry) && (input?acceptedRetry |> unbox<bool>)

                let physicalUser = text input?physicalUser
                let acceptedPhysicalUser = text input?acceptedPhysicalUser

                if
                    not acceptedRetry
                    || String.IsNullOrEmpty physicalUser
                    || not (String.Equals(physicalUser, acceptedPhysicalUser, StringComparison.Ordinal))
                then
                    box
                        {| ok = true
                           noop = true
                           changed = false
                           consumed = false
                           promoted = false
                           probe = null
                           noProbeReason = null
                           error = null
                           output = input?currentProjection |}
                else
                    // The transform is pre-inference: there is intentionally no
                    // current assistant run/public-snapshot dependency here.
                    let prefixEpoch =
                        if isNullish input?prefixEpoch then
                            None
                        else
                            match Int64.TryParse(text input?prefixEpoch) with
                            | true, value -> Some(PrefixEpochId.create value)
                            | false, _ -> None

                    let prefixEpochAvailable = Option.isSome prefixEpoch
                    let frozenBodyAvailable = not (isNullish input?frozenRecordPrefixBody)

                    if not prefixEpochAvailable || not frozenBodyAvailable then
                        let error =
                            if not prefixEpochAvailable then
                                "X-wire cannot plan a retry without the current prefix epoch"
                            else
                                "X-wire cannot plan a retry without the frozen record prefix body"

                        box
                            {| ok = false
                               noop = false
                               changed = false
                               consumed = false
                               promoted = false
                               probe = null
                               noProbeReason = null
                               error = error
                               output = null |}
                    else
                        let coverableCutoff = intValue input?coverableCutoff

                        // ── Probe selection (CTX-011) ──
                        let committedSnapshot = snapshotOptionOfJs input?committedSnapshot
                        let committedEpoch = prefixEpoch |> Option.defaultValue PrefixEpochId.initial

                        let coveredDigest = text input?coveredDigest
                        let requestStartCutoff = intValue input?requestStartCutoff
                        let frozenRef = BlobRef.create (text input?frozenRecordPrefixRef)
                        let frozenDigest = BlobDigest.create (text input?frozenRecordPrefixDigest)

                        let currentProjection = semanticProjectionOfJs input?currentProjection

                        let projectionSnapshot = { CurrentProjection = currentProjection }

                        let recomputeDigest =
                            ProjectionRenderer.cutoffDigest HostDigest.sha256Hex projectionSnapshot

                        let allowProbe =
                            XWire.mayProbe { ConsecutiveFailureCount = intValue input?failures }

                        // CTX-028/029: `phaseWindow` carries the window boundary when one
                        // spoke; otherwise this slot is failure recovery, whose only bound
                        // is proven coverage. `materialCutoff` defaults to the coverage
                        // cutoff because recovery freezes every coverable frame.
                        let window =
                            if isNullish input?phaseCutoff then
                                ProbeBound.CoverageOnly
                            else
                                ProbeBound.PhaseBoundary(intValue input?phaseCutoff)

                        let materialCutoff =
                            if isNullish input?materialCutoff then
                                coverableCutoff
                            else
                                intValue input?materialCutoff

                        let probeResult =
                            if allowProbe then
                                PrefixProbeSelection.select
                                    sha256Hex
                                    (SessionId.create sessionId)
                                    committedEpoch
                                    committedSnapshot
                                    window
                                    coverableCutoff
                                    coveredDigest
                                    materialCutoff
                                    requestStartCutoff
                                    frozenRef
                                    frozenDigest
                                    recomputeDigest
                            else
                                Error NoCandidateReason.NoCoverage

                        // ── Prefix intent (CTX-010) ──
                        let choice, noProbeReason =
                            match allowProbe, probeResult with
                            | false, _ -> XProjectionChoice.UseCommittedEpoch, None
                            | true, Ok probe -> XProjectionChoice.UsePrefixProbe probe, None
                            | true, Error reason -> XProjectionChoice.UseCommittedEpoch, Some reason

                        let frozenBody = text input?frozenRecordPrefixBody
                        let memoryPreamble = text input?memoryPreamble

                        let prefixIntent =
                            XPrefixProjection.forChoice choice committedSnapshot memoryPreamble frozenBody

                        // ── Prefix-owner render decision ──
                        let rendered = XPrefixProjection.render prefixIntent

                        let changed =
                            match rendered with
                            | PrefixRendered.Synthetic _ -> true
                            | PrefixRendered.Physical -> false

                        // The exact accepted retry is consumed by this plan.
                        // No probe result can migrate to a later physical request.
                        let consumed = true

                        // ── Reconcile: promotableProbe (CTX-012) ──
                        // AttemptPlanner.promotableProbe reads only
                        // `plan.Profile.ProjectionChoice` and the outcome.
                        // We have the choice and probe result directly,
                        // so the promote decision is: Completed + has probe.
                        let reconcileDecision =
                            XWire.reconciliationDecision
                                true
                                (attemptOutcomeOfJs input?outcome)
                                (Result.isOk probeResult)
                                true

                        let promoted = reconcileDecision.Promoted

                        let probeJs =
                            match probeResult with
                            | Ok probe -> probeToJs probe
                            | Error _ -> null

                        let noProbeJs =
                            noProbeReason |> Option.map noCandidateReasonLabel |> Option.defaultValue null

                        // ── Output: the transformed projection ──
                        let output =
                            if changed then
                                match rendered with
                                | PrefixRendered.Synthetic activation ->
                                    let head: SemanticMessage =
                                        { Role = "user"
                                          Parts = [ SemanticText activation.Memory ] }

                                    let tail = currentProjection.Messages |> List.skip activation.CutoffExclusive

                                    let transformed =
                                        { currentProjection with
                                            Messages = head :: tail }

                                    box
                                        {| messages =
                                            transformed.Messages |> List.map semanticMessageToJs |> List.toArray |}
                                | _ -> input?currentProjection
                            else
                                input?currentProjection

                        box
                            {| ok = true
                               noop = false
                               changed = changed
                               consumed = consumed
                               promoted = promoted
                               probe = probeJs
                               noProbeReason = noProbeJs
                               error = null
                               output = output |}

    /// host-boundary-021: reconcile decision — does a completed attempt promote
    /// a prefix rebase, and does a failed/aborted attempt clear the plan?
    ///
    /// Delegates to the pure decision used by `XWire.reconcileAttempt`. The
    /// production function is async and writes a durable fact; this surface
    /// exposes that shared decision.
    ///
    /// Input fields (JS object):
    ///   hasPlan:    true when an attempt plan exists for this (session, run).
    ///   outcome:    "completed" | "failed" | "aborted" | "in-progress" | "unknown".
    ///   hasProbe:   true when the plan carries a prefix probe.
    ///   currentEpoch: current durable prefix epoch.
    ///   probeEpoch: probe's durable base epoch.
    ///
    /// Output fields (JS object):
    ///   promoted:  true when a PrefixRebaseCommitted fact would be written.
    ///   cleared:   true when the plan is cleared (terminal failure/abandon).
    ///   keptPlan:  true when the plan is kept across a non-terminal reread.
    let reconcile (input: obj) : obj =
        let hasPlan = not (isNullish input?hasPlan) && (input?hasPlan |> unbox<bool>)
        let hasProbe = not (isNullish input?hasProbe) && (input?hasProbe |> unbox<bool>)

        let currentEpoch =
            if isNullish input?currentEpoch then
                None
            else
                Some(PrefixEpochId.create (int64 (text input?currentEpoch)))

        let probeEpoch =
            if isNullish input?probeEpoch then
                None
            else
                Some(PrefixEpochId.create (int64 (text input?probeEpoch)))

        let epochMatches =
            match currentEpoch, probeEpoch with
            | Some current, Some probe -> current = probe
            | _ -> false

        let decision =
            XWire.reconciliationDecision hasPlan (attemptOutcomeOfJs input?outcome) hasProbe epochMatches

        box
            {| promoted = decision.Promoted
               cleared = decision.Cleared
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

    let private probeOfSurfaceJs (value: obj) : PrefixProbe =
        { ProbeId = text value?probeId
          BasedOnEpochId = PrefixEpochId.create (int64 (text value?basedOnEpoch))
          Candidate = snapshotOfJs value?candidate }

    let private requestKindOfSurfaceJs (value: obj) : Result<ProviderRequestKind, string> =
        match text value |> fun value -> value.ToLowerInvariant() with
        | ""
        | "workmain"
        | "work-main" -> Ok ProviderRequestKind.WorkMain
        | "interactionrepair"
        | "interaction-repair" -> Ok ProviderRequestKind.InteractionRepair
        | unknown -> Error(sprintf "unknown request kind: %s" unknown)

    /// Project a real pending plan to its semantic JSON view. The view carries
    /// authority/probe/request-kind evidence only; lifecycle counters stay out.
    let internal pendingPlanView (plan: PendingAttemptPlan) : obj =
        let choice, probe =
            match plan.ProjectionChoice with
            | XProjectionChoice.UseCommittedEpoch -> "UseCommittedEpoch", null
            | XProjectionChoice.UsePrefixProbe probe ->
                "UsePrefixProbe",
                box
                    {| probeId = probe.ProbeId
                       basedOnEpoch = string (PrefixEpochId.value probe.BasedOnEpochId)
                       cutoff = probe.Candidate.CutoffExclusive
                       coveredDigest = probe.Candidate.CoveredPrefixDigest
                       frozenDigest = BlobDigest.value probe.Candidate.FrozenRecordPrefixDigest |}

        box
            {| session = SessionId.value plan.Authority.SessionId
               logicalRunId = LogicalRunId.value plan.Authority.LogicalRunId
               root = AuthorityRootUserMessageId.value plan.Authority.AuthorityRootUserMessageId
               physical = PhysicalUserMessageId.value plan.PhysicalUserMessageId
               agent = plan.Authority.SelectedAgent
               role = Roles.roleLabel plan.Authority.CanonicalRole
               kind = ProviderRequestKind.label plan.RequestKind
               choice = choice
               probe = probe |}

    /// Project a real bound plan to its semantic JSON view.
    let internal boundPlanView (plan: AttemptPlan) : obj =
        let choice, probe =
            match plan.Profile.ProjectionChoice with
            | XProjectionChoice.UseCommittedEpoch -> "UseCommittedEpoch", null
            | XProjectionChoice.UsePrefixProbe probe ->
                "UsePrefixProbe",
                box
                    {| probeId = probe.ProbeId
                       basedOnEpoch = string (PrefixEpochId.value probe.BasedOnEpochId)
                       cutoff = probe.Candidate.CutoffExclusive
                       coveredDigest = probe.Candidate.CoveredPrefixDigest
                       frozenDigest = BlobDigest.value probe.Candidate.FrozenRecordPrefixDigest |}

        box
            {| session = SessionId.value plan.Profile.SessionId
               logicalRunId = LogicalRunId.value plan.Profile.LogicalRunId
               root = AuthorityRootUserMessageId.value plan.Profile.AuthorityRootUserMessageId
               physical = PhysicalUserMessageId.value plan.Profile.PhysicalUserMessageId
               providerRun = ProviderRunIdentity.value plan.Profile.ProviderRun
               agent = plan.Profile.SelectedAgent
               role = Roles.roleLabel plan.Profile.CanonicalRole
               kind = ProviderRequestKind.label plan.Profile.RequestKind
               choice = choice
               probe = probe |}

    /// Typed owner bridges: wrap/unwrap the opaque JS handles without copying
    /// any admission or binding policy. The owner surface passes handles back;
    /// only these bridges see the typed plans.
    let internal wrapPendingPlan (plan: PendingAttemptPlan) : obj = box (PendingPlanHandle plan)

    let internal unwrapPendingPlan (handle: obj) : PendingAttemptPlan = (unbox<PendingPlanHandle> handle).Value

    let internal wrapBoundPlan (plan: AttemptPlan) : obj = box (BoundPlanHandle plan)

    let internal unwrapBoundPlan (handle: obj) : AttemptPlan = (unbox<BoundPlanHandle> handle).Value

    /// Build a real `PendingAttemptPlan` via the production planner. Input fields:
    /// session, logicalRun, root, physical, kind ("WorkMain" | "InteractionRepair"),
    /// probe (null or { probeId, basedOnEpoch, candidate: { ref, frozenDigest, cutoff,
    /// prefixDigest, sealRoot, syntheticId } }), agent/role (optional canonical
    /// managed name overriding the default coder identity, resolved through the
    /// real `ParticipantIdentity.resolveAtRoot`).
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
                let selectProbe () =
                    if isNullish input?probe then
                        Error NoCandidateReason.NoCoverage
                    else
                        Ok(probeOfSurfaceJs input?probe)

                let plan =
                    AttemptPlanner.freezePreInference
                        authority
                        physical
                        (PromptAuthority.PromptOrigin.Continuation PromptAuthority.ContinuationKind.ProviderRetryAttempt)
                        requestKind
                        None
                        true
                        selectProbe

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

    /// Materialize candidate probe through the full production pipeline from a journal port.
    /// Drives: readFrameBodies -> materializeFrozenRecordPrefix -> port.WriteBlob -> PrefixProbeSelection.select
    let candidateFromJournal (input: obj) : Task<obj> =
        task {
            try
                let jsPort = input?port

                if isNullish jsPort then
                    return
                        box
                            {| ok = false
                               error = "missing port"
                               probe = null |}
                else
                    let readBlobFn (blobRef: BlobRef) : Task<Result<string, string>> =
                        task {
                            try
                                let refStr = BlobRef.value blobRef

                                let! res =
                                    if not (isNullish jsPort?readBlob) then
                                        jsPort?readBlob (refStr)
                                    elif not (isNullish jsPort?ReadBlob) then
                                        jsPort?ReadBlob (refStr)
                                    else
                                        failwith "port missing readBlob"

                                if isNullish res then
                                    return Error(sprintf "blob %s not found" refStr)
                                elif emitJsExpr res "$0 && typeof $0 === 'object' && 'ok' in $0" then
                                    if unbox<bool> res?ok then
                                        return Ok(string res?value)
                                    else
                                        return Error(string res?error)
                                else
                                    return Ok(string res)
                            with ex ->
                                return Error ex.Message
                        }

                    let writeBlobFn (content: string) : Task<Result<WireBlobRecord, string>> =
                        task {
                            try
                                let! res =
                                    if not (isNullish jsPort?writeBlob) then
                                        jsPort?writeBlob (content)
                                    elif not (isNullish jsPort?WriteBlob) then
                                        jsPort?WriteBlob (content)
                                    else
                                        failwith "port missing writeBlob"

                                if isNullish res then
                                    return Error "writeBlob returned null"
                                elif emitJsExpr res "$0 && typeof $0 === 'object' && 'ok' in $0 && !$0.ok" then
                                    return Error(string res?error)
                                else
                                    let payload =
                                        if emitJsExpr res "$0 && typeof $0 === 'object' && 'value' in $0" then
                                            res?value
                                        else
                                            res

                                    let bRef =
                                        string (
                                            if isNullish payload?blobRef then
                                                payload?ref
                                            else
                                                payload?blobRef
                                        )

                                    let bDigest =
                                        string (
                                            if isNullish payload?blobDigest then
                                                payload?digest
                                            else
                                                payload?blobDigest
                                        )

                                    return
                                        Ok
                                            { BlobRef = BlobRef.create bRef
                                              BlobDigest = BlobDigest.create bDigest }
                            with ex ->
                                return Error ex.Message
                        }

                    let port: WireJournalPort =
                        { ReadView = fun _ -> failwith "not used in candidate"
                          ReadBlob = readBlobFn
                          WriteBlob = writeBlobFn
                          CurrentProjection = fun _ -> failwith "not used in candidate"
                          RecordConfirmedSuccess = fun _ _ -> failwith "not used in candidate"
                          CommitPrefixRebase = fun _ _ _ -> failwith "not used in candidate" }

                    let sessionId =
                        SessionId.create (
                            if isNullish input?sessionId then
                                "test-session"
                            else
                                string input?sessionId
                        )

                    let xTraceOpening =
                        if not (isNullish input?opening) then
                            let op = input?opening

                            Some
                                { AssignmentText =
                                    if isNullish op?assignmentText then
                                        ""
                                    else
                                        string op?assignmentText
                                  AuthoritativeRequirements = []
                                  ConstitutiveBody = "" }
                        else
                            None

                    let frames =
                        if not (isNullish input?frames) then
                            let rawFrames =
                                if emitJsExpr input?frames "Array.isArray($0)" then
                                    unbox<obj array> input?frames
                                else
                                    [||]

                            rawFrames
                            |> Array.map (fun f ->
                                { Kind =
                                    if string f?kind = "Squash" then
                                        BlogFrameKind.Squash
                                    else
                                        BlogFrameKind.Entry
                                  Digest = BlobDigest.create (if isNullish f?digest then "" else string f?digest)
                                  TextRef = BlobRef.create (if isNullish f?ref then "" else string f?ref)
                                  CoveredFromSequence = if isNullish f?coveredFrom then 0L else int64 f?coveredFrom
                                  CoveredThroughSequence =
                                    if isNullish f?coveredThrough then
                                        0L
                                    else
                                        int64 f?coveredThrough
                                  CutoffExclusive = if isNullish f?cutoff then 0 else int f?cutoff })
                            |> Array.toList
                            // Stored newest-first, as the projection documents; the
                            // material subset is read back oldest-first.
                            |> List.rev
                        else
                            []

                    let blogState: BlogProjectionState =
                        { FrameEpochId =
                            FrameEpochId.create (
                                if isNullish input?frameEpoch then
                                    0L
                                else
                                    int64 input?frameEpoch
                            )
                          Frames = frames
                          Coverage =
                            { IngestedThroughSequence =
                                if isNullish input?ingestedThrough then
                                    0L
                                else
                                    int64 input?ingestedThrough
                              CoverableTurnCutoffExclusive =
                                if isNullish input?coverableCutoff then
                                    2
                                else
                                    unbox<int> input?coverableCutoff
                              CoveredPrefixDigest =
                                if isNullish input?coveredDigest then
                                    ""
                                else
                                    string input?coveredDigest
                              CoverableFrameCount = frames.Length } }

                    let wireState: WireSessionState =
                        { XTrace =
                            xTraceOpening
                            |> Option.bind (fun op ->
                                match XTraceProjection.applyOpening op.AssignmentText [] XTraceProjection.empty with
                                | Ok st -> Some st
                                | Error _ -> None)
                          Blog = Some blogState
                          TodoCheckpoints =
                            (if isNullish input?todoCheckpoints then
                                 [||]
                             else
                                 unbox<obj array> input?todoCheckpoints)
                            |> Array.fold
                                (fun window checkpoint ->
                                    let callId = ToolCallId.create (string checkpoint?callId)
                                    PhaseWindow.appendCheckpoint callId window)
                                PhaseWindow.emptyWindow
                          PrefixEpoch =
                            if isNullish input?prefixEpoch then
                                None
                            else
                                Some
                                    { EpochId = PrefixEpochId.create (int64 input?prefixEpoch)
                                      Snapshot = None
                                      ReanchoredRuns = Set.empty
                                      ReanchoredTenures = Set.empty } }

                    let currentProjection =
                        if isNullish input?currentProjection then
                            { ProviderId = None
                              ModelId = None
                              Variant = None
                              Tools = []
                              System = []
                              Messages = [] }
                        else
                            semanticProjectionOfJs input?currentProjection

                    let snapshot: ProjectionSnapshot = { CurrentProjection = currentProjection }

                    let requestCutoff =
                        if isNullish input?requestCutoff then
                            2
                        else
                            unbox<int> input?requestCutoff

                    let committedSnapshot =
                        if isNullish input?committedSnapshot then
                            None
                        else
                            Some(snapshotOfJs input?committedSnapshot)

                    let! candidateResult =
                        XWire.candidate
                            port
                            (if isNullish input?phaseCutoff then
                                 ProbeBound.CoverageOnly
                             else
                                 ProbeBound.PhaseBoundary(intValue input?phaseCutoff))
                            sessionId
                            snapshot
                            committedSnapshot
                            wireState
                            requestCutoff

                    match candidateResult with
                    | Ok probe ->
                        let candidate = probe.Candidate

                        let probeJs =
                            box
                                {| probeId = probe.ProbeId
                                   basedOnEpoch = string (PrefixEpochId.value probe.BasedOnEpochId)
                                   candidate =
                                    box
                                        {| ref = BlobRef.value candidate.FrozenRecordPrefixRef
                                           frozenDigest = BlobDigest.value candidate.FrozenRecordPrefixDigest
                                           cutoff = candidate.CutoffExclusive
                                           prefixDigest = candidate.CoveredPrefixDigest
                                           sealRoot = candidate.SealRoot
                                           syntheticId = candidate.SyntheticMessageId |}
                                   cutoff = candidate.CutoffExclusive
                                   sealRoot = candidate.SealRoot
                                   syntheticId = candidate.SyntheticMessageId |}

                        return
                            box
                                {| ok = true
                                   error = null
                                   probe = probeJs |}
                    | Error reason ->
                        let desc =
                            match reason with
                            | NoCandidateReason.NoCoverage -> "no coverage"
                            | NoCandidateReason.WouldRetreat(committed, proposed) ->
                                sprintf "cutoff %d behind %d" proposed committed
                            | NoCandidateReason.NotNewerThanCommitted -> "not newer than committed"
                            | NoCandidateReason.CutoffProofFailed(expected, recomputed) ->
                                sprintf "cutoff proof failed: expected %s, recomputed %s" expected recomputed
                            | NoCandidateReason.BeyondPhaseBoundary(desired, material) ->
                                sprintf "material cutoff %d folds past the window %d" material desired
                            | NoCandidateReason.MaterialBeyondBoundary(material, limit) ->
                                sprintf "material cutoff %d reaches past the covered boundary %d" material limit

                        return
                            box
                                {| ok = false
                                   error = desc
                                   probe = null |}
            with ex ->
                return
                    box
                        {| ok = false
                           error = ex.Message
                           probe = null |}
        }
