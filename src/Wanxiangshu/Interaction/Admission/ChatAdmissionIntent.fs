namespace Wanxiangshu.Interaction.Admission

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.ManagedAgents
open Wanxiangshu.Execution.Session.ChatExecution

[<RequireQualifiedAccess>]
module ChatAdmissionIntent =

    type DecodedMessage =
        { SessionId: SessionId option
          PhysicalUserMessageId: PhysicalUserMessageId option
          ExplicitAgent: string option
          PromptKey: PromptKey option
          IsHostCompaction: bool
          IsHostSynthetic: bool
          Text: string option }

    type DurableSnapshot =
        { Authority: PromptAuthority.PromptAuthorityProjection option
          AcceptedExecutionEvidence: AcceptedChatExecutionEvidence option }

    [<RequireQualifiedAccess>]
    type NoManagedExecutionReason =
        | UnmanagedMessage
        | AlreadyAcceptedHostMessage of PromptAuthority.PromptOrigin

    [<RequireQualifiedAccess>]
    /// DSL-class: Evidence
    type Rejection =
        | ManagedIntentMissingSessionId
        | ManagedIntentMissingPhysicalUserMessageId
        | InvalidExplicitAgent of string
        | AcceptedParticipantConflict of ChatExecutionKey * acceptedAgent: string * requestedAgent: string
        | PromptKeyNotClaimed of PromptKey
        | AgentOwnerRootPromptNotClaimed of PromptKey * PromptAuthority.IdentitySeed
        | PromptClaimSessionMismatch of expectedSessionId: SessionId * claimedSessionId: SessionId
        | PromptClaimOriginNotAdmissible of PromptKey * PromptAuthority.PromptOrigin
        | UnknownOriginWhileActive

    type ExternalRootEvidence =
        { Key: ChatExecutionKey
          ExplicitAgent: string
          Origin: PromptAuthority.PromptOrigin
          IdentitySeed: PromptAuthority.IdentitySeed }

    type PendingPromptEvidence =
        { Key: ChatExecutionKey
          PromptKey: PromptKey
          Claim: PromptAuthority.PromptClaim
          Origin: PromptAuthority.PromptOrigin
          IdentitySeed: PromptAuthority.IdentitySeed }

    type ActiveHumanContinuationEvidence =
        { Key: ChatExecutionKey
          Origin: PromptAuthority.PromptOrigin
          Authority: PromptAuthority.AuthorityExecutionProfile }

    type HostInternalEvidence =
        { SessionId: SessionId option
          PhysicalUserMessageId: PhysicalUserMessageId option
          Origin: PromptAuthority.PromptOrigin }

    [<RequireQualifiedAccess>]
    type Decision =
        | NoManagedExecution of NoManagedExecutionReason
        | ExternalRootIntent of ExternalRootEvidence
        | ActiveHumanContinuationIntent of ActiveHumanContinuationEvidence
        | PendingPromptIntent of PendingPromptEvidence
        | AcceptedInputIntent of AcceptedChatExecutionEvidence
        | HostInternal of HostInternalEvidence
        | Reject of Rejection

    [<RequireQualifiedAccess>]
    type ManagedIntent =
        | ExternalRoot of ExternalRootEvidence
        | ActiveHumanContinuation of ActiveHumanContinuationEvidence
        | PendingPrompt of PendingPromptEvidence
        | AcceptedInput of AcceptedChatExecutionEvidence

    let tryManaged (decision: Decision) : ManagedIntent option =
        match decision with
        | Decision.ExternalRootIntent evidence -> Some(ManagedIntent.ExternalRoot evidence)
        | Decision.ActiveHumanContinuationIntent evidence -> Some(ManagedIntent.ActiveHumanContinuation evidence)
        | Decision.PendingPromptIntent evidence -> Some(ManagedIntent.PendingPrompt evidence)
        | Decision.AcceptedInputIntent evidence -> Some(ManagedIntent.AcceptedInput evidence)
        | Decision.NoManagedExecution _
        | Decision.HostInternal _
        | Decision.Reject _ -> None

    let ofManaged (managed: ManagedIntent) : Decision =
        match managed with
        | ManagedIntent.ExternalRoot evidence -> Decision.ExternalRootIntent evidence
        | ManagedIntent.ActiveHumanContinuation evidence -> Decision.ActiveHumanContinuationIntent evidence
        | ManagedIntent.PendingPrompt evidence -> Decision.PendingPromptIntent evidence
        | ManagedIntent.AcceptedInput evidence -> Decision.AcceptedInputIntent evidence

    let managedKey (managed: ManagedIntent) : ChatExecutionKey =
        match managed with
        | ManagedIntent.ExternalRoot evidence -> evidence.Key
        | ManagedIntent.ActiveHumanContinuation evidence -> evidence.Key
        | ManagedIntent.PendingPrompt evidence -> evidence.Key
        | ManagedIntent.AcceptedInput evidence ->
            { SessionId = evidence.SessionId
              PhysicalUserMessageId = evidence.PhysicalUserMessageId }

    /// Host-internal (compaction / synthetic) classification, shared by
    /// the intent resolver and the read-side Host hooks.
    let isHostInternal (message: DecodedMessage) : bool =
        message.IsHostCompaction
        || message.IsHostSynthetic
        || (message.ExplicitAgent |> Option.exists ManagedAgent.isFunctionalName)

    let private tryRootIdentity (value: string) =
        ParticipantIdentity.resolveAtRoot value
        |> Result.toOption
        |> Option.filter (fun identity -> ParticipantIdentity.role identity |> Option.isSome)

    let private seedAgent (seed: PromptAuthority.IdentitySeed) =
        seed
        |> PromptAuthority.identitySeedParticipantIdentity
        |> ParticipantIdentity.selectedAgent

    /// Typed projection of `wanxiangli/authority` `admit` (CUT-09-a): the entrance
    /// order is decided by the library over plain observed facts; participant
    /// resolution, identity seeds and the typed Decision payloads stay here.
    module private AdmissionLaw =

        type Law =
            abstract admit: input: obj -> obj

        [<Import("Authority", "wanxiangli/authority")>]
        let private authorityClass: obj = jsNative

        [<Emit("new $0()")>]
        let private construct (cls: obj) : Law = jsNative

        let law: Law = construct authorityClass

        let kindLabel (kind: PromptAuthority.RootAuthorityKind) =
            match kind with
            | PromptAuthority.RootAuthorityKind.HumanRoot -> "HumanRoot"
            | PromptAuthority.RootAuthorityKind.AgentOwnerRoot -> "AgentOwnerRoot"

        let originView (origin: PromptAuthority.PromptOrigin) : obj =
            match origin with
            | PromptAuthority.PromptOrigin.AuthorityRoot kind ->
                createObj [ "kind" ==> "AuthorityRoot"; "root" ==> kindLabel kind ]
            | PromptAuthority.PromptOrigin.Continuation _ ->
                createObj
                    [ "kind" ==> "Continuation"
                      "continuation" ==> PromptAuthority.originLabel origin ]
            | PromptAuthority.PromptOrigin.HostInternal -> createObj [ "kind" ==> "HostInternal" ]
            | PromptAuthority.PromptOrigin.UnknownOrigin -> createObj [ "kind" ==> "UnknownOrigin" ]

    /// The physical-message evidence the library orders; looked up here so the
    /// typed payloads are at hand when the verdict comes back.
    type private Observed =
        { Active: PromptAuthority.AuthorityExecutionProfile option
          AcceptedContinuation: PromptAuthority.ContinuationKind option
          Landed: PromptAuthority.PromptOrigin option
          Pending: PromptAuthority.PromptClaim option
          RootIdentity: ParticipantIdentityEvidence option }

    let private observe (message: DecodedMessage) (snapshot: DurableSnapshot) : Observed =
        let projection = snapshot.Authority

        let atPhysical find =
            match projection, message.PhysicalUserMessageId with
            | Some projection, Some physical -> find projection physical
            | _ -> None

        { Active = projection |> Option.bind (fun projection -> projection.ActiveLogicalRun)
          AcceptedContinuation =
            atPhysical (fun projection physical -> Map.tryFind physical projection.AcceptedContinuationIds)
          Landed =
            atPhysical (fun projection physical ->
                Map.tryFind physical projection.PhysicalLandings
                |> Option.map (fun landing -> landing.Origin))
          Pending =
            match projection, message.PromptKey with
            | Some projection, Some promptKey -> Map.tryFind promptKey projection.PendingClaims
            | _ -> None
          RootIdentity = message.ExplicitAgent |> Option.bind tryRootIdentity }

    let private admissionInput (message: DecodedMessage) (snapshot: DurableSnapshot) (observed: Observed) : obj =
        let text value = value |> Option.toObj

        createObj
            [ "sessionId" ==> (message.SessionId |> Option.map SessionId.value |> text)
              "physicalId"
              ==> (message.PhysicalUserMessageId |> Option.map PhysicalUserMessageId.value |> text)
              "promptKey" ==> (message.PromptKey |> Option.map PromptKey.value |> text)
              "explicitAgent" ==> text message.ExplicitAgent
              "explicitRootAgent"
              ==> (observed.RootIdentity |> Option.map ParticipantIdentity.selectedAgent |> text)
              "hostInternal" ==> isHostInternal message
              "authority"
              ==> (snapshot.Authority
                   |> Option.map (fun _ ->
                       createObj
                           [ "active"
                             ==> (observed.Active
                                  |> Option.map (fun profile ->
                                      createObj
                                          [ "kind" ==> AdmissionLaw.kindLabel profile.AuthorityKind
                                            "selectedAgent" ==> profile.SelectedAgent ])
                                  |> Option.toObj) ])
                   |> Option.toObj)
              "acceptedContinuation"
              ==> (observed.AcceptedContinuation
                   |> Option.map (fun kind ->
                       PromptAuthority.originLabel (PromptAuthority.PromptOrigin.Continuation kind))
                   |> text)
              "landedOrigin"
              ==> (observed.Landed |> Option.map AdmissionLaw.originView |> Option.toObj)
              "pendingClaim"
              ==> (observed.Pending
                   |> Option.map (fun claim ->
                       createObj
                           [ "sessionId" ==> SessionId.value claim.SessionId
                             "origin" ==> AdmissionLaw.originView claim.Origin
                             "selectedAgent" ==> seedAgent claim.IdentitySeed ])
                   |> Option.toObj)
              "acceptedEvidence"
              ==> (snapshot.AcceptedExecutionEvidence
                   |> Option.map (fun accepted ->
                       createObj
                           [ "sessionId" ==> SessionId.value accepted.SessionId
                             "physicalId" ==> PhysicalUserMessageId.value accepted.PhysicalUserMessageId
                             "origin" ==> AdmissionLaw.originView accepted.Origin
                             "selectedAgent" ==> seedAgent accepted.IdentitySeed ])
                   |> Option.toObj) ]

    let private required (what: string) (value: 'T option) : 'T =
        match value with
        | Some value -> value
        | None -> failwithf "wanxiangli/authority admit returned %s without the evidence it needs" what

    let private hostInternal (message: DecodedMessage) : Decision =
        let evidence: HostInternalEvidence =
            { SessionId = message.SessionId
              PhysicalUserMessageId = message.PhysicalUserMessageId
              Origin = PromptAuthority.PromptOrigin.HostInternal }

        Decision.HostInternal evidence

    let private rejectionOf
        (message: DecodedMessage)
        (snapshot: DurableSnapshot)
        (observed: Observed)
        (verdict: obj)
        : Rejection =
        match unbox<string> verdict?reason with
        | "missing-session" -> Rejection.ManagedIntentMissingSessionId
        | "missing-physical" -> Rejection.ManagedIntentMissingPhysicalUserMessageId
        | "invalid-explicit-agent" ->
            Rejection.InvalidExplicitAgent(required "invalid-explicit-agent" message.ExplicitAgent)
        | "accepted-participant-conflict" ->
            let accepted =
                required "accepted-participant-conflict" snapshot.AcceptedExecutionEvidence

            Rejection.AcceptedParticipantConflict(
                { SessionId = accepted.SessionId
                  PhysicalUserMessageId = accepted.PhysicalUserMessageId },
                seedAgent accepted.IdentitySeed,
                required "accepted-participant-conflict" message.ExplicitAgent
            )
        | "prompt-key-not-claimed" -> Rejection.PromptKeyNotClaimed(required "prompt-key-not-claimed" message.PromptKey)
        | "agent-owner-root-prompt-not-claimed" ->
            Rejection.AgentOwnerRootPromptNotClaimed(
                required "agent-owner-root-prompt-not-claimed" message.PromptKey,
                (required "agent-owner-root-prompt-not-claimed" observed.Active).IdentitySeed
            )
        | "claim-session-mismatch" ->
            Rejection.PromptClaimSessionMismatch(
                required "claim-session-mismatch" message.SessionId,
                (required "claim-session-mismatch" observed.Pending).SessionId
            )
        | "claim-origin-not-admissible" ->
            let claim = required "claim-origin-not-admissible" observed.Pending
            Rejection.PromptClaimOriginNotAdmissible(claim.PromptKey, claim.Origin)
        | "unknown-origin-while-active" -> Rejection.UnknownOriginWhileActive
        | other -> failwithf "wanxiangli/authority admit returned an unknown rejection %s" other

    let private decide (message: DecodedMessage) (snapshot: DurableSnapshot) (observed: Observed) (verdict: obj) =
        let key () : ChatExecutionKey =
            { SessionId = required "a keyed verdict" message.SessionId
              PhysicalUserMessageId = required "a keyed verdict" message.PhysicalUserMessageId }

        let humanContinuation () =
            Decision.ActiveHumanContinuationIntent
                { Key = key ()
                  Origin = PromptAuthority.PromptOrigin.Continuation PromptAuthority.ContinuationKind.HumanMessage
                  Authority = required "active-human-continuation" observed.Active }

        match unbox<string> verdict?kind with
        | "unmanaged" -> Decision.NoManagedExecution NoManagedExecutionReason.UnmanagedMessage
        | "already-accepted" ->
            let origin =
                observed.AcceptedContinuation
                |> Option.map PromptAuthority.PromptOrigin.Continuation
                |> Option.defaultWith (fun () -> required "already-accepted" observed.Landed)

            Decision.NoManagedExecution(NoManagedExecutionReason.AlreadyAcceptedHostMessage origin)
        | "host-internal" -> hostInternal message
        | "accepted-root-replay" ->
            let accepted = required "accepted-root-replay" snapshot.AcceptedExecutionEvidence

            Decision.ExternalRootIntent
                { Key =
                    { SessionId = accepted.SessionId
                      PhysicalUserMessageId = accepted.PhysicalUserMessageId }
                  ExplicitAgent = seedAgent accepted.IdentitySeed
                  Origin = accepted.Origin
                  IdentitySeed = accepted.IdentitySeed }
        | "external-root" ->
            let identity = required "external-root" observed.RootIdentity

            Decision.ExternalRootIntent
                { Key = key ()
                  ExplicitAgent = ParticipantIdentity.selectedAgent identity
                  Origin = PromptAuthority.PromptOrigin.AuthorityRoot PromptAuthority.RootAuthorityKind.HumanRoot
                  IdentitySeed = PromptAuthority.IdentitySeed.RootSelection identity }
        | "active-human-continuation" -> humanContinuation ()
        | "pending-prompt" ->
            let claim = required "pending-prompt" observed.Pending

            Decision.PendingPromptIntent
                { Key = key ()
                  PromptKey = claim.PromptKey
                  Claim = claim
                  Origin = claim.Origin
                  IdentitySeed = claim.IdentitySeed }
        | "reject" -> Decision.Reject(rejectionOf message snapshot observed verdict)
        | other -> failwithf "wanxiangli/authority admit returned an unknown verdict %s" other

    let resolve (message: DecodedMessage) (snapshot: DurableSnapshot) : Decision =
        let observed = observe message snapshot

        AdmissionLaw.law.admit (admissionInput message snapshot observed)
        |> decide message snapshot observed
