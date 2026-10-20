namespace Wanxiangshu.Context.Companion

open Fable.Core
open Fable.Core.JsInterop
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

    let private optionalText (value: obj) : string option =
        if isNullish value then None else Some(text value)

    let private requestKindOf (value: obj) : ProviderRequestKind option =
        match text value |> fun value -> value.ToLowerInvariant() with
        | "workmain"
        | "work-main" -> Some ProviderRequestKind.WorkMain
        | "interactionrepair"
        | "interaction-repair" -> Some ProviderRequestKind.InteractionRepair
        | _ -> None


    let private requestKindResult value : Result<ProviderRequestKind, string> =
        if isNullish value then
            Ok ProviderRequestKind.WorkMain
        else
            match requestKindOf value with
            | Some requestKind -> Ok requestKind
            | None -> Error(sprintf "unknown request kind: %s" (text value))



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

    let private attemptPlanCore (value: obj) : Result<AttemptPlan, string> =
        match roleResult value?role, requestKindResult value?kind with
        | Ok role, Ok requestKind ->
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
                    requestKind)

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
        | ToolPermission.Fetch -> "Fetch"
        | ToolPermission.Finality -> "Finality"
        | ToolPermission.BashHoneypot -> "BashHoneypot"
        | ToolPermission.JsPlan -> "JsPlan"
        | ToolPermission.Ask -> "Ask"
        | ToolPermission.DevOpsConsultation -> "DevOpsConsultation"
        | ToolPermission.Deliver -> "Deliver"
        | ToolPermission.Review -> "Review"
        | ToolPermission.Advisor -> "Advisor"

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
        box
            {| participant = plan.Profile.Authority.SelectedAgent
               canonicalRole = Roles.roleLabel plan.Profile.CanonicalRole
               role = Roles.roleLabel plan.Profile.CanonicalRole
               requestKind = ProviderRequestKind.label plan.Profile.RequestKind
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

    /// JSON/opaque owner API for semantic tests.

    let private attemptPlanWithHandle (value: obj) : obj =
        match attemptPlanCore value with
        | Error error -> box {| ok = false; error = error |}
        | Ok plan ->
            let view = attemptPlanView plan
            let viewObject = unbox<obj> view

            box
                {| choice = viewObject?choice
                   probeId = viewObject?probeId
                   participant = viewObject?participant
                   canonicalRole = viewObject?canonicalRole
                   role = viewObject?role
                   requestKind = viewObject?requestKind
                   participantIdentity = viewObject?participantIdentity
                   systemPromptId = viewObject?systemPromptId
                   toolCapabilities = viewObject?toolCapabilities
                   handle = box (AttemptPlanHandle plan) |}

    let attemptPlanner =
        box
            {| plan = (fun value -> attemptPlanWithHandle value)
               attemptPlan = (fun value -> attemptPlan value) |}


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
