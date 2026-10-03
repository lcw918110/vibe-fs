namespace Wanxiangshu.OpenCode

open System
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona

module ChatAdmissionIntentSurface =

    let private rawOptionalString (value: obj) : string option =
        if isNull value then None else Some(string value)

    let private optionalString (value: obj) : string option =
        if isNull value then
            None
        else
            let text = string value

            if String.IsNullOrWhiteSpace text then
                None
            else
                Some(text.Trim())

    let private requiredString (name: string) (value: obj) : string =
        optionalString value
        |> Option.defaultWith (fun () -> invalidArg name (name + " must be non-empty"))

    let private rootIdentity (participant: string) : ParticipantIdentityEvidence =
        ParticipantIdentity.resolveAtRoot participant
        |> Result.bind (fun identity ->
            match ParticipantIdentity.role identity with
            | Some _ -> Ok identity
            | None -> Error ParticipantIdentityError.OwnerRequired)
        |> Result.defaultWith (fun _ -> invalidArg "participant" "participant must name a managed public agent")

    let private originOf (label: string) : PromptAuthority.PromptOrigin =
        match label with
        | "HumanRoot" -> PromptAuthority.PromptOrigin.AuthorityRoot PromptAuthority.RootAuthorityKind.HumanRoot
        | "AgentOwnerRoot" ->
            PromptAuthority.PromptOrigin.AuthorityRoot PromptAuthority.RootAuthorityKind.AgentOwnerRoot
        | "HostInternal" -> PromptAuthority.PromptOrigin.HostInternal
        | "UnknownOrigin" -> PromptAuthority.PromptOrigin.UnknownOrigin
        | continuation ->
            PromptAuthority.tryParseContinuationKind continuation
            |> Option.map PromptAuthority.PromptOrigin.Continuation
            |> Option.defaultWith (fun () -> invalidArg "origin" ("unknown prompt origin: " + continuation))

    let private originName (origin: PromptAuthority.PromptOrigin) : string =
        match origin with
        | PromptAuthority.PromptOrigin.AuthorityRoot PromptAuthority.RootAuthorityKind.HumanRoot -> "HumanRoot"
        | PromptAuthority.PromptOrigin.AuthorityRoot PromptAuthority.RootAuthorityKind.AgentOwnerRoot ->
            "AgentOwnerRoot"
        | PromptAuthority.PromptOrigin.Continuation continuation ->
            PromptAuthority.originLabel (PromptAuthority.PromptOrigin.Continuation continuation)
        | PromptAuthority.PromptOrigin.HostInternal -> "HostInternal"
        | PromptAuthority.PromptOrigin.UnknownOrigin -> "UnknownOrigin"

    let private identitySeedName (identitySeed: PromptAuthority.IdentitySeed) : string =
        match identitySeed with
        | PromptAuthority.IdentitySeed.RootSelection _ -> "RootSelection"
        | PromptAuthority.IdentitySeed.InheritedFromOwner _ -> "InheritedFromOwner"

    let private activeProfile
        (snapshot: obj)
        (sessionId: SessionId)
        : PromptAuthority.AuthorityExecutionProfile option =
        optionalString snapshot?activeParticipant
        |> Option.orElseWith (fun () -> optionalString snapshot?activeAgent)
        |> Option.map (fun participant ->
            match optionalString snapshot?activeKind with
            | Some "AgentOwnerRoot" ->
                let owner =
                    PromptAuthority.createAuthorityExecutionProfileFromSeed
                        (SessionId.create "ses-surface-owner")
                        (LogicalRunId.create "run-surface-owner")
                        (AuthorityRootUserMessageId.create "root-surface-owner")
                        PromptAuthority.RootAuthorityKind.HumanRoot
                        (PromptAuthority.IdentitySeed.RootSelection(rootIdentity "manager"))
                    |> Result.defaultWith invalidOp

                let inherited =
                    PromptAuthority.issueInheritedIdentitySeed participant owner
                    |> Result.defaultWith (fun _ ->
                        invalidArg "activeParticipant" "invalid owner-derived active participant")

                PromptAuthority.createAuthorityExecutionProfileFromSeed
                    sessionId
                    (LogicalRunId.create "run-surface-active")
                    (AuthorityRootUserMessageId.create "root-surface-active")
                    PromptAuthority.RootAuthorityKind.AgentOwnerRoot
                    inherited
                |> Result.defaultWith invalidOp
            | Some "HumanRoot"
            | None ->
                PromptAuthority.createAuthorityExecutionProfileFromSeed
                    sessionId
                    (LogicalRunId.create "run-surface-active")
                    (AuthorityRootUserMessageId.create "root-surface-active")
                    PromptAuthority.RootAuthorityKind.HumanRoot
                    (PromptAuthority.IdentitySeed.RootSelection(rootIdentity participant))
                |> Result.defaultWith invalidOp
            | Some kind -> invalidArg "activeKind" ("unknown active authority kind: " + kind))

    let private claimOf (value: obj) : PromptAuthority.PromptClaim =
        let sessionId = requiredString "claim.sessionId" value?sessionId |> SessionId.create
        let promptKey = requiredString "claim.promptKey" value?promptKey |> PromptKey.create

        let participant =
            optionalString value?participant
            |> Option.orElseWith (fun () -> optionalString value?selectedAgent)
            |> Option.defaultWith (fun () -> invalidArg "claim.participant" "claim.participant must be non-empty")

        let claim: PromptAuthority.PromptClaim =
            { PromptKey = promptKey
              SessionId = sessionId
              Origin = requiredString "claim.origin" value?origin |> originOf
              LogicalRunId = Some(LogicalRunId.create "run-surface-claim")
              AuthorityRootUserMessageId = Some(AuthorityRootUserMessageId.create "root-surface-claim")
              IdentitySeed = PromptAuthority.IdentitySeed.RootSelection(rootIdentity participant)
              PayloadDigest = "surface-payload"
              Receipt = None
              ClaimedAtRuntimeStartCount = 0 }

        claim

    let private acceptedContinuationOf (value: obj) : PhysicalUserMessageId * PromptAuthority.ContinuationKind =
        let physical =
            requiredString "accepted.physicalUserMessageId" value?physicalUserMessageId
            |> PhysicalUserMessageId.create

        let continuation =
            requiredString "accepted.origin" value?origin
            |> PromptAuthority.tryParseContinuationKind
            |> Option.defaultWith (fun () -> invalidArg "accepted.origin" "accepted origin must be a continuation")

        physical, continuation

    let private authoritySnapshot
        (decoded: ChatAdmissionIntent.DecodedMessage)
        (snapshot: obj)
        : ChatAdmissionIntent.DurableSnapshot =
        if isNull snapshot || snapshot?available = box false then
            { ChatAdmissionIntent.DurableSnapshot.Authority = None }
        else
            let sessionId =
                decoded.SessionId
                |> Option.defaultValue (SessionId.create "surface-missing-session")

            let claims: obj array =
                if isNull snapshot?claims then
                    [||]
                else
                    unbox<obj array> snapshot?claims

            let accepted: obj array =
                if isNull snapshot?acceptedContinuations then
                    [||]
                else
                    unbox<obj array> snapshot?acceptedContinuations

            let projection: PromptAuthority.PromptAuthorityProjection =
                { PromptAuthority.empty with
                    ActiveLogicalRun = activeProfile snapshot sessionId
                    PendingClaims =
                        claims
                        |> Array.map claimOf
                        |> Array.map (fun claim -> claim.PromptKey, claim)
                        |> Map.ofArray
                    AcceptedContinuationIds = accepted |> Array.map acceptedContinuationOf |> Map.ofArray }

            { ChatAdmissionIntent.DurableSnapshot.Authority = Some projection }

    let private decodedMessage (value: obj) : ChatAdmissionIntent.DecodedMessage =
        { InvalidIdentityCarrier = None
          SessionId = optionalString value?sessionId |> Option.map SessionId.create
          PhysicalUserMessageId =
            optionalString value?physicalUserMessageId
            |> Option.map PhysicalUserMessageId.create
          ExplicitAgent =
            optionalString value?explicitParticipant
            |> Option.orElseWith (fun () -> optionalString value?explicitAgent)
          PromptKey = optionalString value?promptKey |> Option.map PromptKey.create
          IsHostCompaction =
            if isNull value?hostCompaction then
                false
            else
                unbox<bool> value?hostCompaction
          IsHostSynthetic =
            if isNull value?hostSynthetic then
                false
            else
                unbox<bool> value?hostSynthetic
          Text = rawOptionalString value?text }

    let private rejectionName (rejection: ChatAdmissionIntent.Rejection) : string =
        match rejection with
        | ChatAdmissionIntent.Rejection.MalformedIdentityCarrier _ -> "MalformedIdentityCarrier"
        | ChatAdmissionIntent.Rejection.ManagedIntentMissingSessionId -> "ManagedIntentMissingSessionId"
        | ChatAdmissionIntent.Rejection.ManagedIntentMissingPhysicalUserMessageId ->
            "ManagedIntentMissingPhysicalUserMessageId"
        | ChatAdmissionIntent.Rejection.DurableAuthorityUnavailable -> "DurableAuthorityUnavailable"
        | ChatAdmissionIntent.Rejection.InvalidExplicitAgent _ -> "InvalidExplicitAgent"
        | ChatAdmissionIntent.Rejection.PromptKeyNotClaimed _ -> "PromptKeyNotClaimed"
        | ChatAdmissionIntent.Rejection.AgentOwnerRootPromptNotClaimed _ -> "AgentOwnerRootPromptNotClaimed"
        | ChatAdmissionIntent.Rejection.PromptClaimSessionMismatch _ -> "PromptClaimSessionMismatch"
        | ChatAdmissionIntent.Rejection.PromptClaimOriginNotAdmissible _ -> "PromptClaimOriginNotAdmissible"
        | ChatAdmissionIntent.Rejection.UnknownOriginWhileActive -> "UnknownOriginWhileActive"

    let hostInternal (message: obj) : bool =
        ChatAdmissionIntent.isHostInternal (decodedMessage message)

    let resolve (message: obj) (durableSnapshot: obj) : obj =
        let decoded = decodedMessage message

        match ChatAdmissionIntent.resolve decoded (authoritySnapshot decoded durableSnapshot) with
        | ChatAdmissionIntent.Decision.NoManagedExecution ChatAdmissionIntent.NoManagedExecutionReason.UnmanagedMessage ->
            box
                {| ``case`` = "NoManagedExecution"
                   reason = "UnmanagedMessage" |}
        | ChatAdmissionIntent.Decision.NoManagedExecution(ChatAdmissionIntent.NoManagedExecutionReason.AlreadyAcceptedHostMessage continuation) ->
            box
                {| ``case`` = "NoManagedExecution"
                   reason = "AlreadyAcceptedHostMessage"
                   origin = originName (PromptAuthority.PromptOrigin.Continuation continuation) |}
        | ChatAdmissionIntent.Decision.ExternalRootIntent evidence ->
            let participant =
                evidence.IdentitySeed
                |> PromptAuthority.identitySeedParticipantIdentity
                |> ParticipantIdentity.selectedAgent

            box
                {| ``case`` = "ExternalRootIntent"
                   sessionId = SessionId.value evidence.Key.SessionId
                   physicalUserMessageId = PhysicalUserMessageId.value evidence.Key.PhysicalUserMessageId
                   explicitAgent = evidence.ExplicitAgent
                   origin = originName evidence.Origin
                   identitySeed = identitySeedName evidence.IdentitySeed
                   participant = participant |}
        | ChatAdmissionIntent.Decision.ActiveHumanContinuationIntent evidence ->
            box
                {| ``case`` = "ActiveHumanContinuationIntent"
                   sessionId = SessionId.value evidence.Key.SessionId
                   physicalUserMessageId = PhysicalUserMessageId.value evidence.Key.PhysicalUserMessageId
                   origin = originName evidence.Origin
                   participant = evidence.Authority.SelectedAgent |}
        | ChatAdmissionIntent.Decision.PendingPromptIntent evidence ->
            let participant =
                evidence.IdentitySeed
                |> PromptAuthority.identitySeedParticipantIdentity
                |> ParticipantIdentity.selectedAgent

            box
                {| ``case`` = "PendingPromptIntent"
                   sessionId = SessionId.value evidence.Key.SessionId
                   physicalUserMessageId = PhysicalUserMessageId.value evidence.Key.PhysicalUserMessageId
                   promptKey = PromptKey.value evidence.PromptKey
                   origin = originName evidence.Origin
                   identitySeed = identitySeedName evidence.IdentitySeed
                   participant = participant |}
        | ChatAdmissionIntent.Decision.HostInternal evidence ->
            box
                {| ``case`` = "HostInternal"
                   origin = originName evidence.Origin |}
        | ChatAdmissionIntent.Decision.Reject rejection ->
            box
                {| ``case`` = "Reject"
                   reason = rejectionName rejection |}
