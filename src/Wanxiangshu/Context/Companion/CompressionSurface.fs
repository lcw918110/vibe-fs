namespace Wanxiangshu.Context.Companion

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt

/// Context-compression decision owner. Attempt choice, retry request dispatch
/// and terminal validity cross this JSON boundary; prefix selection and epoch
/// behavior are owned by `PrefixSurface`.
[<RequireQualifiedAccess>]
module CompressionSurface =

    type private AttemptPlanHandle(plan: AttemptPlan) =
        member _.Value = plan

    [<Emit("$0 == null")>]
    let private isNullish (value: obj) : bool = jsNative

    let private text (value: obj) : string =
        if isNullish value then "" else string value

    let private intValue (value: obj) : int = int (text value)

    let private int64Value (value: obj) : int64 = int64 (text value)

    let private optionalText (value: obj) : string option =
        if isNullish value then None else Some(text value)

    let private snapshotOfJs (value: obj) : PrefixSnapshot =
        { FrozenRecordPrefixRef = BlobRef.create (text value?ref)
          FrozenRecordPrefixDigest = BlobDigest.create (text value?frozenDigest)
          CutoffExclusive = intValue value?cutoff
          CoveredPrefixDigest = text value?prefixDigest
          SealRoot = text value?sealRoot
          SyntheticMessageId = text value?syntheticId }

    let private probeOfJs (value: obj) : PrefixProbe =
        { ProbeId = text value?probeId
          BasedOnEpochId = PrefixEpochId.create (int64Value value?basedOnEpoch)
          Candidate = snapshotOfJs value?candidate }

    let private reasonName (reason: NoCandidateReason) : string =
        match reason with
        | NoCandidateReason.NoCoverage -> "NoCoverage"
        | NoCandidateReason.WouldRetreat _ -> "WouldRetreat"
        | NoCandidateReason.NotNewerThanCommitted -> "NotNewerThanCommitted"
        | NoCandidateReason.CutoffProofFailed _ -> "CutoffProofFailed"
        | NoCandidateReason.BeyondPhaseBoundary _ -> "BeyondPhaseBoundary"
        | NoCandidateReason.MaterialBeyondBoundary _ -> "MaterialBeyondBoundary"

    let private reasonOf (value: obj) : NoCandidateReason =
        match text value with
        | "WouldRetreat" -> NoCandidateReason.WouldRetreat(0, 0)
        | "NotNewerThanCommitted" -> NoCandidateReason.NotNewerThanCommitted
        | "CutoffProofFailed" -> NoCandidateReason.CutoffProofFailed("", "")
        | "BeyondPhaseBoundary" -> NoCandidateReason.BeyondPhaseBoundary(0, 0)
        | "MaterialBeyondBoundary" -> NoCandidateReason.MaterialBeyondBoundary(0, 0)
        | _ -> NoCandidateReason.NoCoverage

    let private optionObj (value: 'a option) : obj =
        match value with
        | None -> null
        | Some item -> box item

    let private requestKindOf (value: obj) : ProviderRequestKind option =
        match text value |> fun value -> value.ToLowerInvariant() with
        | "workmain"
        | "work-main" -> Some ProviderRequestKind.WorkMain
        | "bloggermain"
        | "blogger-main" -> Some ProviderRequestKind.BloggerMain
        | "bloggersquash"
        | "blogger-squash" -> Some ProviderRequestKind.BloggerSquash
        | "interactionrepair"
        | "interaction-repair" -> Some ProviderRequestKind.InteractionRepair
        | "strengthreplica"
        | "strength-replica" -> Some ProviderRequestKind.StrengthReplica
        | _ -> None

    let private requestKindResult value : Result<ProviderRequestKind, string> =
        if isNullish value then
            Ok ProviderRequestKind.WorkMain
        else
            match requestKindOf value with
            | Some requestKind -> Ok requestKind
            | None -> Error(sprintf "unknown request kind: %s" (text value))

    let private bloggerRetryErrorName (error: BloggerRetryError) : string =
        match error with
        | BloggerRetryError.MissingProjection -> "MissingProjection"
        | BloggerRetryError.NoActiveBloggerRun -> "NoActiveBloggerRun"

    let nextBloggerRequest (failedKind: string) (hasSquashMaterial: bool) : string =
        match requestKindOf failedKind with
        | None -> bloggerRetryErrorName BloggerRetryError.MissingProjection
        | Some kind ->
            match BloggerRetryPolicy.nextRequest kind hasSquashMaterial with
            | Ok next -> ProviderRequestKind.label next
            | Error error -> bloggerRetryErrorName error

    let private outcomeResult (value: obj) : Result<AttemptOutcome, string> =
        match text value with
        | "Completed" -> Ok AttemptOutcome.Completed
        | "CompletedInvalid" -> Ok AttemptOutcome.CompletedInvalid
        | "Failed" -> Ok AttemptOutcome.Failed
        | "Aborted" -> Ok AttemptOutcome.Aborted
        | unknown -> Error(sprintf "unknown attempt outcome: %s" unknown)


    let private roleResult value : Result<Role, string> =
        if isNullish value then
            Ok Role.Engineer
        else
            match Roles.tryParseRole (text value) with
            | Some role -> Ok role
            | None -> Error(sprintf "unknown role: %s" (text value))

    let private attemptProbeOf (value: obj) : PrefixProbe = probeOfJs value

    let private attemptPlanCore (value: obj) : Result<AttemptPlan, string> =
        match roleResult value?role, requestKindResult value?kind with
        | Ok role, Ok requestKind ->

            let selectProbe () =
                if not (isNullish value?probe) then
                    Ok(attemptProbeOf value?probe)
                else
                    Error(reasonOf value?noCandidateReason)

            ParticipantIdentity.resolveAtRoot (ManagedAgentCatalog.nameOf role)
            |> Result.mapError (fun error -> sprintf "invalid participant identity: %A" error)
            |> Result.bind (fun participantIdentity ->
                PromptAuthority.createAuthorityExecutionProfile
                    (SessionId.create "surface-session")
                    (LogicalRunId.create "surface-run")
                    (AuthorityRootUserMessageId.create "surface-root")
                    PromptAuthority.RootAuthorityKind.HumanRoot
                    participantIdentity)
            |> Result.map (fun authority ->
                AttemptPlanner.plan
                    authority
                    (PhysicalUserMessageId.create "surface-user")
                    (ProviderRunIdentity.create "surface-provider-run")
                    (PromptAuthority.PromptOrigin.AuthorityRoot PromptAuthority.RootAuthorityKind.HumanRoot)
                    requestKind
                    (not (isNullish value?policyAllowsProbe) && unbox<bool> value?policyAllowsProbe
                     || not (isNullish value?mayRecover) && unbox<bool> value?mayRecover)
                    selectProbe)
        | Error error, _
        | _, Error error -> Error error

    let private permissionLabel (permission: ToolPermission) : string =
        match permission with
        | ToolPermission.Fork -> "Fork"
        | ToolPermission.Resume -> "Resume"
        | ToolPermission.Join -> "Join"
        | ToolPermission.Horizon -> "Horizon"
        | ToolPermission.Fission -> "Fission"
        | ToolPermission.Read -> "Read"
        | ToolPermission.Write -> "Write"
        | ToolPermission.Edit -> "Edit"
        | ToolPermission.Glob -> "Glob"
        | ToolPermission.Grep -> "Grep"
        | ToolPermission.Move -> "Move"
        | ToolPermission.Remove -> "Remove"
        | ToolPermission.Exec -> "Exec"
        | ToolPermission.Pty -> "Pty"
        | ToolPermission.ReviewAssessment -> "ReviewAssessment"
        | ToolPermission.Chronicle -> "Chronicle"
        | ToolPermission.Fetch -> "Fetch"
        | ToolPermission.Finality -> "Finality"
        | ToolPermission.BashHoneypot -> "BashHoneypot"
        | ToolPermission.JsPlan -> "JsPlan"
        | ToolPermission.Ask -> "Ask"
        | ToolPermission.Handoff -> "Handoff"
        | ToolPermission.Deliver -> "Deliver"

    let private participantIdentityToJs (identity: ParticipantIdentityEvidence) : obj =
        box
            {| participant = ParticipantIdentity.selectedAgent identity
               selectedAgent = ParticipantIdentity.selectedAgent identity
               canonicalRole = ParticipantIdentity.roleLabel identity
               role = ParticipantIdentity.roleLabel identity
               selectedTier = "deep"
               persona = ParticipantIdentity.persona identity
               personaCatalogVersion = ParticipantIdentity.personaCatalogVersion identity
               origin =
                match ParticipantIdentity.origin identity with
                | PersonaOrigin.ResolvedAtRoot -> "ResolvedAtRoot"
                | PersonaOrigin.InheritedFromOwner -> "InheritedFromOwner" |}

    let private attemptPlanView (plan: AttemptPlan) : obj =
        let choice, probeId =
            match plan.Profile.ProjectionChoice with
            | XProjectionChoice.UseCommittedEpoch -> "UseCommittedEpoch", None
            | XProjectionChoice.UsePrefixProbe probe -> "UsePrefixProbe", Some probe.ProbeId

        box
            {| choice = choice
               probeId = optionObj probeId
               noProbeReason = optionObj (plan.NoProbeReason |> Option.map reasonName)
               participant = plan.Profile.Authority.SelectedAgent
               canonicalRole = Roles.roleLabel plan.Profile.CanonicalRole
               role = Roles.roleLabel plan.Profile.CanonicalRole
               requestKind = ProviderRequestKind.label plan.Profile.RequestKind
               projectionChoice = choice
               participantIdentity = participantIdentityToJs plan.Profile.Authority.ParticipantIdentity
               systemPromptId = SystemPromptId.value plan.Profile.SystemPromptId
               toolCapabilities =
                plan.Profile.ToolCapabilitySet
                |> Set.toList
                |> List.map permissionLabel
                |> List.sort
                |> List.toArray |}

    /// Build the production AttemptPlan from plain request labels. The caller
    /// supplies either a probe or a named no-candidate result; the planner itself
    /// still owns the choice and defers probe selection until it is allowed.
    let attemptPlan (value: obj) : obj =
        match attemptPlanCore value with
        | Ok plan -> attemptPlanView plan
        | Error error -> box {| ok = false; error = error |}

    /// JSON/opaque owner API for semantic tests. `handle` is deliberately opaque:
    /// only `promotableProbeId` can consume it, while the profile observations stay
    /// plain JSON.
    let private attemptPlanWithHandle (value: obj) : obj =
        match attemptPlanCore value with
        | Error error -> box {| ok = false; error = error |}
        | Ok plan ->
            let view = attemptPlanView plan
            let viewObject = unbox<obj> view

            box
                {| choice = viewObject?choice
                   probeId = viewObject?probeId
                   noProbeReason = viewObject?noProbeReason
                   participant = viewObject?participant
                   canonicalRole = viewObject?canonicalRole
                   role = viewObject?role
                   requestKind = viewObject?requestKind
                   projectionChoice = viewObject?projectionChoice
                   participantIdentity = viewObject?participantIdentity
                   systemPromptId = viewObject?systemPromptId
                   toolCapabilities = viewObject?toolCapabilities
                   handle = box (AttemptPlanHandle plan) |}

    let private promotableProbeId (value: obj) (outcome: string) : obj =
        let handleValue = value?handle

        if isNullish handleValue then
            null
        else
            let handle = unbox<AttemptPlanHandle> handleValue

            match outcomeResult (box outcome) with
            | Error _ -> null
            | Ok outcome ->
                match AttemptPlanner.promotableProbe handle.Value outcome with
                | None -> null
                | Some probe -> box probe.ProbeId

    let attemptPlanner =
        box
            {| plan = (fun value -> attemptPlanWithHandle value)
               attemptPlan = (fun value -> attemptPlan value)
               promotableProbeId = (fun value outcome -> promotableProbeId value outcome) |}

    let private terminalValidityResult (value: string) : Result<unit, TerminalValidity.Rejection> =
        TerminalValidity.check value

    let private terminalRejectionName rejection =
        match rejection with
        | TerminalValidity.Rejection.Empty -> "Empty"
        | TerminalValidity.Rejection.XmlOnly -> "XmlOnly"

    let terminalValidityCheck (value: string) : obj =
        match terminalValidityResult value with
        | Ok() -> box {| ok = true |}
        | Error rejection ->
            box
                {| ok = false
                   error = terminalRejectionName rejection |}

    let terminalValidityIsValid (value: string) : bool =
        match terminalValidityResult value with
        | Ok() -> true
        | Error _ -> false

    let terminalValidityDescription (value: string) : string =
        match value with
        | "Empty" -> TerminalValidity.describe TerminalValidity.Rejection.Empty
        | "XmlOnly" -> TerminalValidity.describe TerminalValidity.Rejection.XmlOnly
        | _ -> "unknown terminal rejection"

    let terminalValidity (value: string) : obj =
        match terminalValidityResult value with
        | Ok() -> box {| valid = true; rejection = null |}
        | Error rejection ->
            box
                {| valid = false
                   rejection = terminalRejectionName rejection |}

    let terminalRequestOwnership (value: obj) : string =
        let requestId = BloggerRequestId.create (text value?requestId)

        let openRequestId =
            optionalText value?openRequestId |> Option.map BloggerRequestId.create

        let openPromptKey = optionalText value?openPromptKey |> Option.map PromptKey.create

        let parent: BloggerTerminalParentEvidence option =
            optionalText value?parentPromptKey
            |> Option.map (fun promptKey ->
                let isInteractionRepair = optionalText value?parentOrigin = Some "InteractionRepair"

                { PromptKey = PromptKey.create promptKey
                  IsRequestScopedRepair =
                    isInteractionRepair
                    && PromptAuthority.repairPayloadBelongsToRequest requestId (text value?parentPayloadDigest) })

        match BloggerRequestOwnership.decide requestId openRequestId openPromptKey parent with
        | BloggerTerminalRequestOwnership.Current -> "Current"
        | BloggerTerminalRequestOwnership.Superseded -> "Superseded"
        | BloggerTerminalRequestOwnership.Unproven -> "Unproven"

    let diagnosticEmit (operation: string) (fields: obj array) : unit =
        let pairs =
            fields
            |> Array.toList
            |> List.map (fun pair ->
                let values = unbox<obj array> pair
                text values.[0], text values.[1])

        Wanxiangshu.OpenCode.Diagnostic.emit operation pairs

    let diagnosticFatal (operation: string) (fields: obj array) : unit =
        let pairs =
            fields
            |> Array.toList
            |> List.map (fun pair ->
                let values = unbox<obj array> pair
                text values.[0], text values.[1])

        Wanxiangshu.OpenCode.Diagnostic.fatal operation pairs
