namespace Wanxiangshu.Interaction.Dispatch

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.OpenCode
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch.OpenCode
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Persistence.Journal

/// Dispatch-owned JavaScript boundary. Host ports stay opaque and durable
/// JournalHandle capabilities never cross as Fable records; only transport
/// constructors, send observations, and claim counts are plain values.
[<RequireQualifiedAccess>]
module DispatchSurface =

    type private PlainSessionPort(raw: obj) =
        let typed = unbox<Wanxiangshu.OpenCode.ISessionHostPort> raw
        let sendPrompt = raw?``SendPrompt``
        // DSL-MUTABLE: resource — latest physical send observation for the JS result
        let mutable lastObservation: obj = null

        member _.LastObservation = lastObservation

        interface Wanxiangshu.OpenCode.ISessionHostPort with
            member _.SubscribeTerminal(sessionId, listener) =
                typed.SubscribeTerminal(sessionId, listener)

            member _.SubscribeFutureTerminal(sessionId, listener) =
                typed.SubscribeFutureTerminal(sessionId, listener)

            member _.SendPrompt(sessionId, text, options) =
                lastObservation <-
                    box
                        {| session = SessionId.value sessionId
                           text = text
                           agent = options.Agent |> Option.defaultValue null
                           model =
                            options.Model
                            |> Option.map (fun model ->
                                box
                                    {| providerID = model.providerID
                                       modelID = model.modelID
                                       variant = model.variant |> Option.defaultValue null |})
                            |> Option.defaultValue null
                           directory = options.Directory |> Option.defaultValue null
                           tools = options.Tools |> Option.map Map.toArray |> Option.toObj
                           metadata = options.Metadata |> Option.defaultValue null |}

                emitJsExpr (sendPrompt, SessionId.value sessionId, text, options) "$0($1,$2,$3)"

            member _.AbortSession(sessionId) = typed.AbortSession sessionId
            member _.InterruptAttempt(sessionId) = typed.InterruptAttempt sessionId
            member _.IsManagedChild(sessionId) = typed.IsManagedChild sessionId
            member _.AbortChildren(sessionId) = typed.AbortChildren sessionId

            member _.CreateSiblingSession(owner, parent, options) =
                typed.CreateSiblingSession(owner, parent, options)

            member _.TryGetParentSession(sessionId) = typed.TryGetParentSession sessionId

            member _.CreateChildSession(parent, options) =
                typed.CreateChildSession(parent, options)

            member _.ListChildren(parent) = typed.ListChildren parent
            member _.FamilyRootOf(sessionId) = typed.FamilyRootOf sessionId

        /// Dispatch view of the same physical host port: narrows the interface
        /// through a dedicated member so AttachMembers never collides.
        member this.DispatchPort: Wanxiangshu.Interaction.Dispatch.IDispatchSessionPort =
            { new Wanxiangshu.Interaction.Dispatch.IDispatchSessionPort with
                member _.SendPrompt(sessionId, text, options) =
                    (this :> Wanxiangshu.OpenCode.ISessionHostPort)
                        .SendPrompt(sessionId, text, options)

                member _.SubscribeTerminal(sessionId, listener) =
                    (this :> Wanxiangshu.OpenCode.ISessionHostPort)
                        .SubscribeTerminal(sessionId, listener)

                member _.SubscribeFutureTerminal(sessionId, listener) =
                    (this :> Wanxiangshu.OpenCode.ISessionHostPort)
                        .SubscribeFutureTerminal(sessionId, listener) }

    let internal sessionPort (port: obj) : Wanxiangshu.OpenCode.ISessionHostPort =
        PlainSessionPort(port) :> Wanxiangshu.OpenCode.ISessionHostPort

    let internal rootWorkspaceReader (directory: obj) : Wanxiangshu.OpenCode.IRootWorkspaceReader =
        { new Wanxiangshu.OpenCode.IRootWorkspaceReader with
            member _.TryRead() =
                if isNull directory then None else Some(string directory) }

    /// JS-safe controlled Host child listing for adapter proofs. The F# Result
    /// and OpenCodeChildInfo representations stay on this registered surface.
    let acceptedChild
        (session: string)
        (title: string)
        (agent: string)
        : Result<Wanxiangshu.OpenCode.OpenCodeChildInfo list, string> =
        Ok
            [ { SessionId = SessionId.create session
                ParentSessionId = None
                Agent = Some agent
                Title = Some title } ]

    let admittedWithReceipt (value: string) : Outcome.SendOutcome =
        Outcome.SendOutcome.AdmittedWithReceipt(TransportReceipt.create value)

    let admittedWithPhysicalMessage (value: string) : Outcome.SendOutcome =
        Outcome.SendOutcome.AdmittedWithPhysicalMessage(PhysicalUserMessageId.create value)

    let retryable (reason: string) : Outcome.SendOutcome = Outcome.SendOutcome.Retryable reason

    let acceptanceUnknown (reason: string) : Outcome.SendOutcome =
        Outcome.SendOutcome.AcceptanceUnknown reason

    let fatal (reason: string) : Outcome.SendOutcome = Outcome.SendOutcome.Fatal reason

    let decodePhysicalUserMessageId (input: obj) (output: obj) : obj =
        PromptIngressCodec.decodeWith input output
        |> fun decoded -> decoded.PhysicalUserMessageId
        |> Option.map PhysicalUserMessageId.value
        |> Option.map box
        |> Option.toObj

    let decodeIngress (input: obj) (output: obj) : obj =
        let decoded = PromptIngressCodec.decodeWith input output

        box
            {| sessionId = decoded.SessionId |> Option.map SessionId.value |> Option.toObj
               physicalUserMessageId =
                decoded.PhysicalUserMessageId
                |> Option.map PhysicalUserMessageId.value
                |> Option.toObj
               explicitAgent = decoded.ExplicitAgent |> Option.toObj
               promptKey = decoded.PromptKey |> Option.map PromptKey.value |> Option.toObj
               isHostCompaction = decoded.IsHostCompaction
               isHostSynthetic = decoded.IsHostSynthetic
               text = decoded.Text |> Option.toObj |}

    let private appendResult result =
        match result with
        | Ok _ -> box {| ok = true; error = null |}
        | Error failure ->
            box
                {| ok = false
                   error = JournalAppendFailure.describe failure |}

    let private text (value: obj) =
        if isNull value then "" else string value

    let private participantIdentityOf (value: obj) : Result<ParticipantIdentityEvidence, string> =
        let participant =
            let raw = text value?participant

            if String.IsNullOrWhiteSpace raw then
                text value?selectedAgent
            else
                raw

        let role =
            let roleRaw =
                let raw = text value?role

                if String.IsNullOrWhiteSpace raw then
                    text value?canonicalRole
                else
                    raw

            if roleRaw = "bookkeeper" then
                Some None
            else
                Roles.tryParseRole roleRaw |> Option.map Some

        let origin =
            match text value?origin with
            | "ResolvedAtRoot" -> Ok PersonaOrigin.ResolvedAtRoot
            | "InheritedFromOwner" -> Ok PersonaOrigin.InheritedFromOwner
            | unknown -> Error(sprintf "Unknown participant identity origin: %s" unknown)

        match role, origin with
        | None, _ -> Error(sprintf "Unknown role: %s" (text value?canonicalRole))
        | _, Error error -> Error error
        | Some role, Ok origin ->
            { SelectedAgent = participant
              Role = role
              Persona = text value?persona
              PersonaCatalogVersion = unbox<int> value?personaCatalogVersion
              Origin = origin }
            |> ParticipantIdentity.fromInput
            |> Result.mapError (fun error -> sprintf "Invalid participant identity: %A" error)

    let private identitySeedOf (value: obj) : Result<PromptAuthority.IdentitySeed, string> =
        match text value?kind with
        | "RootSelection" ->
            participantIdentityOf value?participantIdentity
            |> Result.map PromptAuthority.IdentitySeed.RootSelection
        | "InheritedFromOwner" ->
            participantIdentityOf value?participantIdentity
            |> Result.bind (fun identity ->
                PromptAuthority.IdentitySeedInput.InheritedFromOwnerInput
                    { OwnerSessionId = SessionId.create (text value?ownerSession)
                      OwnerLogicalRunId = LogicalRunId.create (text value?ownerLogicalRun)
                      OwnerAuthorityRootUserMessageId =
                        AuthorityRootUserMessageId.create (text value?ownerAuthorityRoot)
                      ParticipantIdentity = ParticipantIdentity.toInput identity }
                |> PromptIdentitySeed.rehydrate
                |> Result.mapError (fun error -> sprintf "Invalid identity seed: %A" error))
        | unknown -> Error(sprintf "Unknown identity seed kind: %s" unknown)

    /// Seed the durable AgentOwnerRoot needed by a continuation owner. This is
    /// the same PromptFact writer used by production ingress; the returned value
    /// contains no AgentFact/union representation.
    let appendAuthorityRoot (handle: JournalHandle) (session: string) (identitySeed: obj) : Task<obj> =
        task {
            match identitySeedOf identitySeed with
            | Error error -> return box {| ok = false; error = error |}
            | Ok(PromptAuthority.IdentitySeed.RootSelection _) ->
                return
                    box
                        {| ok = false
                           error = "AgentOwnerRoot requires an inherited owner identity seed" |}
            | Ok identitySeed ->
                let sessionId = SessionId.create session

                let payload: AuthorityRootAcceptedPayload =
                    { SchemaVersion = 2
                      SessionId = sessionId
                      LogicalRunId = LogicalRunId.create (sprintf "run-%s" session)
                      AuthorityRootUserMessageId = AuthorityRootUserMessageId.create (sprintf "root-%s" session)
                      AuthorityKind = "AgentOwnerRoot"
                      IdentitySeed = identitySeed }

                let fact = PromptFact.AuthorityRootAccepted payload

                let! result = AgentJournal.appendAgent (StreamId.Session sessionId) None fact handle.Journal
                return appendResult result
        }

    /// Real PROMPT-002 send through the production dispatcher. The transport
    /// port and await-mode selection are adapted only at this JS boundary;
    /// claim/persist/send semantics remain PromptDispatcher.Runtime.
    let private sendAgentOwnerRootWithMode
        (awaitMode: PromptDispatcher.AwaitMode)
        (port: obj)
        (handle: JournalHandle)
        (session: string)
        (text: string)
        (identitySeed: obj)
        : Task<obj> =
        task {
            match identitySeedOf identitySeed with
            | Error error ->
                return
                    box
                        {| ok = false
                           key = null
                           error = error
                           observation = null |}
            | Ok identitySeed ->
                let runtime =
                    PromptDispatcher.forPrompts (PromptJournalAdapter.create handle.Journal)

                let adapter = PlainSessionPort(port)

                let! result =
                    runtime.SendAgentOwnerRoot
                        adapter.DispatchPort
                        (SessionId.create session)
                        text
                        identitySeed
                        None
                        awaitMode
                        None

                return
                    match result with
                    | Ok key ->
                        box
                            {| ok = true
                               key = PromptKey.value key
                               error = null
                               observation = adapter.LastObservation |}
                    | Error error ->
                        box
                            {| ok = false
                               key = null
                               error = error
                               observation = adapter.LastObservation |}
        }

    let sendAgentOwnerRoot
        (port: obj)
        (handle: JournalHandle)
        (session: string)
        (text: string)
        (identitySeed: obj)
        : Task<obj> =
        sendAgentOwnerRootWithMode PromptDispatcher.AwaitMode.Detached port handle session text identitySeed

    let sendAgentOwnerRootAwait
        (port: obj)
        (handle: JournalHandle)
        (session: string)
        (text: string)
        (identitySeed: obj)
        : Task<obj> =
        sendAgentOwnerRootWithMode PromptDispatcher.AwaitMode.Await port handle session text identitySeed

    let sendManagedAssignment
        (port: obj)
        (handle: JournalHandle)
        (session: string)
        (text: string)
        (identitySeed: obj)
        (tools: (string * bool) array option)
        : Task<obj> =
        task {
            let runtime =
                PromptDispatcher.forPrompts (PromptJournalAdapter.create handle.Journal)

            let adapter = PlainSessionPort(port)

            let! result =
                runtime.SendManagedAssignment
                    adapter.DispatchPort
                    (SessionId.create session)
                    text
                    (fun () -> identitySeedOf identitySeed)
                    None
                    (tools |> Option.map Map.ofArray)

            return
                match result with
                | Ok key ->
                    box
                        {| ok = true
                           key = PromptKey.value key
                           error = null
                           observation = adapter.LastObservation |}
                | Error error ->
                    box
                        {| ok = false
                           key = null
                           error = error
                           observation = adapter.LastObservation |}
        }

    let private profileOf (value: obj) : Result<PromptAuthority.AuthorityExecutionProfile, string> =
        let authorityKind =
            match text value?authorityKind with
            | "AgentOwnerRoot" -> Ok PromptAuthority.RootAuthorityKind.AgentOwnerRoot
            | "HumanRoot" -> Ok PromptAuthority.RootAuthorityKind.HumanRoot
            | unknown -> Error(sprintf "Unknown authority root kind: %s" unknown)

        match authorityKind, identitySeedOf value?identitySeed with
        | Ok authorityKind, Ok identitySeed ->
            PromptAuthority.createAuthorityExecutionProfileFromSeed
                (SessionId.create (text value?session))
                (LogicalRunId.create (text value?logicalRun))
                (AuthorityRootUserMessageId.create (text value?authorityRoot))
                authorityKind
                identitySeed
        | Error error, _
        | _, Error error -> Error error

    let private awaitModeOf (value: string) =
        if isNull value then
            PromptDispatcher.AwaitMode.Await
        else
            match value with
            | "Detached" -> PromptDispatcher.AwaitMode.Detached
            | _ -> PromptDispatcher.AwaitMode.Await

    /// Continuation may only attach to the target's own active Logical Run
    /// (interaction-authority-017). This Surface always holds a JournalHandle, so
    /// the journal-less branch HostSessionNudge must handle cannot occur here;
    /// the two reachable rejections keep that path's wording verbatim.
    let private activeProfileAt (handle: JournalHandle) (sessionId: SessionId) : Result<unit, string> =
        let projections = (AgentJournal.snapshot handle.Journal).AgentProjections

        match Wanxiangshu.Execution.Fission.FissionProjection.tryActiveForOwner sessionId projections.Fission with
        | Some _ -> Error "Session is retired by Fission"
        | None ->
            // dispatch-protocol-009/011: a detached AgentOwnerRoot dispatch
            // returns right after its durable claim, so a continuation on the
            // same claim path may attach before the physical message lands.
            // interaction-authority-001 keeps the root itself gated on
            // physical acceptance, so here a pending AgentOwnerRoot claim
            // counts as the session's in-flight run — it is neither an
            // archived nor a closed profile (interaction-authority-017).
            let pendingRootClaim =
                PromptAuthorityProjectionQueries.projectionFor sessionId projections
                |> Option.exists (fun authority ->
                    authority.PendingClaims
                    |> Map.exists (fun _ (claim: PromptAuthority.PromptClaim) ->
                        claim.Origin = PromptAuthority.PromptOrigin.AuthorityRoot
                            PromptAuthority.RootAuthorityKind.AgentOwnerRoot))

            match PromptAuthorityProjectionQueries.activeProfile sessionId projections with
            | Some _ -> Ok()
            | None when pendingRootClaim -> Ok()
            | None -> Error "No active authority profile"

    let sendContinuation
        (port: obj)
        (handle: JournalHandle)
        (session: string)
        (text: string)
        (continuation: string)
        (profile: obj)
        (awaitMode: string)
        : Task<obj> =
        task {
            match PromptAuthority.tryParseContinuationKind continuation, profileOf profile with
            | Some kind, Ok authorityProfile ->
                // Active-run validation is read-only and happens before any runtime
                // is built, so a rejected target never reaches PromptDispatcher and
                // never records a durable claim.
                match activeProfileAt handle (SessionId.create session) with
                | Error error ->
                    return
                        box
                            {| ok = false
                               key = null
                               error = error
                               observation = null |}
                | Ok _ ->
                    let runtime =
                        PromptDispatcher.forPrompts (PromptJournalAdapter.create handle.Journal)

                    let adapter = PlainSessionPort(port)

                    let! result =
                        runtime.SendContinuation
                            adapter.DispatchPort
                            (SessionId.create session)
                            text
                            kind
                            authorityProfile
                            None
                            (awaitModeOf awaitMode)
                            None

                    return
                        match result with
                        | Ok key ->
                            box
                                {| ok = true
                                   key = PromptKey.value key
                                   error = null
                                   observation = adapter.LastObservation |}
                        | Error error ->
                            box
                                {| ok = false
                                   key = null
                                   error = error
                                   observation = adapter.LastObservation |}
            | None, _ ->
                return
                    box
                        {| ok = false
                           key = null
                           error = sprintf "Unknown continuation kind: %s" continuation
                           observation = null |}
            | _, Error error ->
                return
                    box
                        {| ok = false
                           key = null
                           error = error
                           observation = null |}
        }

    let sendGateNudgesConcurrently
        (port: obj)
        (handle: JournalHandle)
        (session: string)
        (text: string)
        (continuation: string)
        (gateKind: string)
        (terminalProviderRun: string)
        (profile: obj)
        : Task<obj> =
        task {
            match PromptAuthority.tryParseContinuationKind continuation, profileOf profile with
            | Some kind, Ok authorityProfile ->
                let runtime =
                    PromptDispatcher.forPrompts (PromptJournalAdapter.create handle.Journal)

                let adapter = PlainSessionPort(port)

                let send () =
                    runtime.SendGateNudge
                        adapter.DispatchPort
                        (SessionId.create session)
                        text
                        kind
                        gateKind
                        (ProviderRunIdentity.create terminalProviderRun)
                        authorityProfile
                        None
                        PromptDispatcher.AwaitMode.Await
                        None

                let first = send ()
                let second = send ()
                let! firstResult = first
                let! secondResult = second
                let results = [| firstResult; secondResult |]

                return
                    results
                    |> Array.map (function
                        | Ok key ->
                            box
                                {| ok = true
                                   key = PromptKey.value key
                                   error = null |}
                        | Error error ->
                            box
                                {| ok = false
                                   key = null
                                   error = error |})
                    |> box
            | None, _ ->
                return
                    box
                        [| box
                               {| ok = false
                                  key = null
                                  error = "Unknown continuation kind" |} |]
            | _, Error error ->
                return
                    box
                        [| box
                               {| ok = false
                                  key = null
                                  error = error |} |]
        }

    /// HOST-004 / dispatch-protocol-002: exercise the dispatch-owned final
    /// physical-send admission without exposing Quiescence internals to this
    /// package's JS tests. Crash-reconciliation proves when the admission turns
    /// stale; this surface proves that stale evidence closes the durable claim
    /// and never reaches the Host SendPrompt boundary.
    let sendIdleContinuation
        (port: obj)
        (handle: JournalHandle)
        (session: string)
        (text: string)
        (continuation: string)
        (profile: obj)
        (physicalAdmission: obj)
        : Task<obj> =
        task {
            match PromptAuthority.tryParseContinuationKind continuation, profileOf profile with
            | Some kind, Ok authorityProfile ->
                let runtime =
                    PromptDispatcher.forPrompts (PromptJournalAdapter.create handle.Journal)

                let adapter = PlainSessionPort(port)

                let! outcome =
                    runtime.SendIdleContinuation
                        adapter.DispatchPort
                        (SessionId.create session)
                        text
                        kind
                        authorityProfile
                        None
                        PromptDispatcher.AwaitMode.Await
                        None
                        (fun () ->
                            match physicalAdmission with
                            | :? bool as admitted when admitted -> Ok()
                            | :? string as failure ->
                                match failure with
                                | "WrongOwner" -> Error QuiescencePermitFailure.WrongOwner
                                | "NoFreshIdle" -> Error QuiescencePermitFailure.NoFreshIdle
                                | "AlreadyConsumed" -> Error QuiescencePermitFailure.AlreadyConsumed
                                | "Revoked" -> Error QuiescencePermitFailure.Revoked
                                | _ -> Error QuiescencePermitFailure.Superseded
                            | _ -> Error QuiescencePermitFailure.Superseded)

                return
                    match outcome with
                    | PromptDispatcher.SendAttemptOutcome.Sent key ->
                        box
                            {| outcome = "Sent"
                               key = PromptKey.value key
                               error = null
                               observation = adapter.LastObservation |}
                    | PromptDispatcher.SendAttemptOutcome.AdmissionRejected failure ->
                        box
                            {| outcome = "Superseded"
                               key = null
                               error = string failure
                               observation = adapter.LastObservation |}
                    | PromptDispatcher.SendAttemptOutcome.NotSent error ->
                        box
                            {| outcome = "NotSent"
                               key = null
                               error = error
                               observation = adapter.LastObservation |}
                    | PromptDispatcher.SendAttemptOutcome.Failed error ->
                        box
                            {| outcome = "Failed"
                               key = null
                               error = error
                               observation = adapter.LastObservation |}
            | None, _ ->
                return
                    box
                        {| outcome = "Failed"
                           key = null
                           error = sprintf "Unknown continuation kind: %s" continuation
                           observation = null |}
            | _, Error error ->
                return
                    box
                        {| outcome = "Failed"
                           key = null
                           error = error
                           observation = null |}
        }

    let private participantIdentityView (identity: ParticipantIdentityEvidence) : obj =
        box
            {| participant = ParticipantIdentity.selectedAgent identity
               role = ParticipantIdentity.roleLabel identity
               persona = ParticipantIdentity.persona identity
               personaCatalogVersion = ParticipantIdentity.personaCatalogVersion identity
               origin =
                match ParticipantIdentity.origin identity with
                | PersonaOrigin.ResolvedAtRoot -> "ResolvedAtRoot"
                | PersonaOrigin.InheritedFromOwner -> "InheritedFromOwner" |}

    let private identitySeedView (identitySeed: PromptAuthority.IdentitySeed) : obj =
        let identity = PromptAuthority.identitySeedParticipantIdentity identitySeed

        match PromptAuthority.identitySeedOwner identitySeed with
        | None ->
            box
                {| kind = "RootSelection"
                   ownerSession = null
                   ownerLogicalRun = null
                   ownerAuthorityRoot = null
                   participantIdentity = participantIdentityView identity |}
        | Some(ownerSession, ownerLogicalRun, ownerAuthorityRoot) ->
            box
                {| kind = "InheritedFromOwner"
                   ownerSession = SessionId.value ownerSession
                   ownerLogicalRun = LogicalRunId.value ownerLogicalRun
                   ownerAuthorityRoot = AuthorityRootUserMessageId.value ownerAuthorityRoot
                   participantIdentity = participantIdentityView identity |}

    let private profileView (profile: PromptAuthority.AuthorityExecutionProfile) : obj =
        box
            {| session = SessionId.value profile.SessionId
               logicalRun = LogicalRunId.value profile.LogicalRunId
               authorityRoot = AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId
               authorityKind =
                match profile.AuthorityKind with
                | PromptAuthority.RootAuthorityKind.AgentOwnerRoot -> "AgentOwnerRoot"
                | PromptAuthority.RootAuthorityKind.HumanRoot -> "HumanRoot"
               identitySeed = identitySeedView profile.IdentitySeed
               participantIdentity = participantIdentityView profile.ParticipantIdentity |}

    let private humanRootAcceptanceFailureView =
        function
        | PromptDispatcher.HumanRootAcceptanceFailure.IdentityRejected reason ->
            box
                {| kind = "IdentityRejected"
                   reason = reason |}
        | PromptDispatcher.HumanRootAcceptanceFailure.AuthorityRegistrationRejected(PromptDispatcher.AuthorityRegistrationFailure.PersistenceRejected reason) ->
            box
                {| kind = "AuthorityPersistenceRejected"
                   reason = reason |}
        | PromptDispatcher.HumanRootAcceptanceFailure.AuthorityRegistrationRejected(PromptDispatcher.AuthorityRegistrationFailure.RegistrationRejected(PromptAuthorityRun.ActiveRunIdentityConflict(active,
                                                                                                                                                                                                    requested))) ->
            box
                {| kind = "ActiveRunIdentityConflict"
                   active = profileView active
                   requested = profileView requested |}

    /// PROMPT-004/005: prove one dispatched AgentOwnerRoot at a physical message
    /// boundary. The Dispatcher writes PhysicalAccepted before registering the
    /// authority profile; only the normalized profile crosses this boundary.
    let acceptAgentOwnerRoot
        (handle: JournalHandle)
        (session: string)
        (promptKey: string)
        (physicalMessageId: string)
        : Task<obj> =
        task {
            let! result =
                (PromptDispatcher.forPrompts (PromptJournalAdapter.create handle.Journal))
                    .AcceptAgentOwnerRoot
                    (PromptKey.create promptKey)
                    (SessionId.create session)
                    (PhysicalUserMessageId.create physicalMessageId)

            return
                match result with
                | Ok profile ->
                    box
                        {| ok = true
                           profile = profileView profile
                           error = null |}
                | Error error ->
                    box
                        {| ok = false
                           profile = null
                           error = error |}
        }

    /// Registered proof boundary for external ingress: the caller supplies the
    /// exact RootSelection seed, including the deliberate absence of a seed.
    let acceptHumanRootSelection
        (handle: JournalHandle)
        (session: string)
        (physicalMessageId: string)
        (identitySeed: obj)
        : Task<obj> =
        task {
            let seedResult =
                if isNull identitySeed then
                    Ok None
                else
                    identitySeedOf identitySeed |> Result.map Some

            match seedResult with
            | Error error ->
                return
                    box
                        {| ok = false
                           profile = null
                           error = error |}
            | Ok identitySeed ->
                let! result =
                    (PromptDispatcher.forPrompts (PromptJournalAdapter.create handle.Journal))
                        .AcceptHumanRoot
                        (SessionId.create session)
                        (PhysicalUserMessageId.create physicalMessageId)
                        identitySeed

                return
                    match result with
                    | Ok profile ->
                        box
                            {| ok = true
                               profile = profileView profile
                               error = null |}
                    | Error error ->
                        box
                            {| ok = false
                               profile = null
                               error = humanRootAcceptanceFailureView error |}
        }

    let private managedAcceptanceView (result: Result<ManagedChatAcceptanceWitness, ManagedChatAcceptanceError>) : obj =
        match result with
        | Ok witness ->
            let evidence = ManagedChatAcceptanceWitness.evidence witness

            box
                {| ok = true
                   error = null
                   sessionId = SessionId.value evidence.SessionId
                   physicalUserMessageId = PhysicalUserMessageId.value evidence.PhysicalUserMessageId
                   origin = PromptAuthority.originLabel evidence.Origin
                   participant = AcceptedChatExecutionEvidence.participant evidence
                   role = Roles.roleLabel (AcceptedChatExecutionEvidence.canonicalRole evidence) |}
        | Error error ->
            let kind =
                match error with
                | ManagedChatAcceptanceError.IntentRejected _ -> "IntentRejected"
                | ManagedChatAcceptanceError.AuthorityRegistrationRejected _ -> "AuthorityRegistrationRejected"
                | ManagedChatAcceptanceError.AttemptEvidenceInvalid _ -> "AttemptEvidenceInvalid"
                | ManagedChatAcceptanceError.AttemptKeyMismatch _ -> "AttemptKeyMismatch"
                | ManagedChatAcceptanceError.EstablishedEvidenceConflict _ -> "EstablishedEvidenceConflict"
                | ManagedChatAcceptanceError.ProjectionMissingAfterCommit _ -> "ProjectionMissingAfterCommit"
                | ManagedChatAcceptanceError.ProjectionConflictAfterCommit _ -> "ProjectionConflictAfterCommit"
                | ManagedChatAcceptanceError.NotAttempted _ -> "NotAttempted"
                | ManagedChatAcceptanceError.CommitUnknown _ -> "CommitUnknown"
                | ManagedChatAcceptanceError.FactRejected _ -> "FactRejected"

            box
                {| ok = false
                   error = kind
                   sessionId = null
                   physicalUserMessageId = null
                   origin = null
                   participant = null
                   role = null |}

    let private acceptManagedDecision
        (handle: JournalHandle)
        (message: ChatAdmissionIntent.DecodedMessage)
        : Task<obj> =
        task {
            let decision = PromptIngress.resolveDecision (Some handle.Journal) message

            let! accepted =
                (PromptDispatcher.forPrompts (PromptJournalAdapter.create handle.Journal))
                    .AcceptManagedChatIntent
                    decision

            return managedAcceptanceView accepted
        }

    let acceptManagedExternal
        (handle: JournalHandle)
        (session: string)
        (physicalMessageId: string)
        (agent: string)
        : Task<obj> =
        acceptManagedDecision
            handle
            { SessionId = Some(SessionId.create session)
              PhysicalUserMessageId = Some(PhysicalUserMessageId.create physicalMessageId)
              InvalidIdentityCarrier = None
              ExplicitAgent = Some agent
              PromptKey = None
              IsHostCompaction = false
              IsHostSynthetic = false
              Text = None }

    let acceptManagedPromptClaim
        (handle: JournalHandle)
        (session: string)
        (physicalMessageId: string)
        (promptKey: string)
        (agent: string)
        : Task<obj> =
        acceptManagedDecision
            handle
            { SessionId = Some(SessionId.create session)
              PhysicalUserMessageId = Some(PhysicalUserMessageId.create physicalMessageId)
              InvalidIdentityCarrier = None
              ExplicitAgent = Some agent
              PromptKey = Some(PromptKey.create promptKey)
              IsHostCompaction = false
              IsHostSynthetic = false
              Text = None }

    /// PROMPT-004: accept the external HumanRoot through the same Dispatcher
    /// writer used by chat.message. The physical id is supplied by the caller as
    /// host-boundary evidence; this surface never invents an alias.
    let acceptHumanRoot
        (handle: JournalHandle)
        (session: string)
        (physicalMessageId: string)
        (agent: string)
        : Task<obj> =
        task {
            match ParticipantIdentity.resolveAtRoot agent with
            | Error error ->
                return
                    box
                        {| ok = false
                           profile = null
                           error = sprintf "Invalid root participant identity: %A" error |}
            | Ok identity ->
                let identitySeed = PromptAuthority.IdentitySeed.RootSelection identity

                let! result =
                    (PromptDispatcher.forPrompts (PromptJournalAdapter.create handle.Journal))
                        .AcceptHumanRoot
                        (SessionId.create session)
                        (PhysicalUserMessageId.create physicalMessageId)
                        (Some identitySeed)

                return
                    match result with
                    | Ok profile ->
                        box
                            {| ok = true
                               profile = profileView profile
                               error = null |}
                    | Error error ->
                        box
                            {| ok = false
                               profile = null
                               error = humanRootAcceptanceFailureView error |}
        }

    let private claimView (claim: PromptAuthority.PromptClaim) : obj =
        box
            {| promptKey = PromptKey.value claim.PromptKey
               session = SessionId.value claim.SessionId
               origin = PromptAuthority.originLabel claim.Origin
               logicalRun = claim.LogicalRunId |> Option.map LogicalRunId.value |> Option.defaultValue null
               authorityRoot =
                claim.AuthorityRootUserMessageId
                |> Option.map AuthorityRootUserMessageId.value
                |> Option.defaultValue null
               participant =
                ParticipantIdentity.selectedAgent (PromptAuthority.identitySeedParticipantIdentity claim.IdentitySeed)
               role = ParticipantIdentity.roleLabel (PromptAuthority.identitySeedParticipantIdentity claim.IdentitySeed)
               identitySeed = identitySeedView claim.IdentitySeed
               payloadDigest = claim.PayloadDigest
               receipt = claim.Receipt |> Option.map TransportReceipt.value |> Option.defaultValue null
               claimedAtRuntimeStartCount = claim.ClaimedAtRuntimeStartCount |}

    let projectionObservation (handle: JournalHandle) (session: string) : obj =
        let runtime =
            PromptDispatcher.forPrompts (PromptJournalAdapter.create handle.Journal)

        let projection = runtime.ProjectionFor(SessionId.create session)
        let snapshot = AgentJournal.snapshot handle.Journal

        box
            {| runtimeStartCount = snapshot.AgentProjections.RuntimeStartCount
               activeLogicalRun =
                projection.ActiveLogicalRun
                |> Option.map profileView
                |> Option.defaultValue null
               pendingClaims =
                projection.PendingClaims
                |> Map.toArray
                |> Array.map (fun (_, claim) -> claimView claim)
               claimSequences =
                projection.ClaimSequences
                |> Map.toArray
                |> Array.map (fun (scope, count) -> box {| scope = scope; count = count |}) |}

    let private watermarkText (value: obj) =
        if isNull value then "" else string value

    let private watermarkEnvelope (value: obj) : Envelope =
        let kind = watermarkText value?kind
        let sequence = Int64.Parse(watermarkText value?seq)
        let runtime = watermarkText value?runtime

        let observedAt =
            if isNull value?observedAt then
                DateTimeOffset.Parse("1970-01-01T00:00:00Z").AddTicks sequence
            else
                DateTimeOffset.Parse(watermarkText value?observedAt)

        let stream, fact =
            match kind with
            | "runtime-start" ->
                StreamId.Workspace,
                Fact.Runtime(
                    Fact.RuntimeFact.RuntimeStarted
                        {| RuntimeId = RuntimeId.create runtime
                           ProcessId = 0
                           StartedAt = observedAt |}
                )
            | "claim" ->
                let session = SessionId.create (watermarkText value?session)
                let logicalRun = watermarkText value?logicalRun
                let authorityRoot = watermarkText value?authorityRoot

                let identitySeed =
                    match identitySeedOf value?identitySeed with
                    | Ok identitySeed -> identitySeed
                    | Error error -> invalidArg "identitySeed" error

                StreamId.Session session,
                Fact.Agent(
                    AgentFact.Prompt(
                        PromptFactCases.PluginPromptClaimed
                            {| PromptKey = PromptKey.create (watermarkText value?promptKey)
                               SessionId = session
                               ContinuationKind = watermarkText value?continuationKind
                               LogicalRunId =
                                if System.String.IsNullOrWhiteSpace logicalRun then
                                    None
                                else
                                    Some(LogicalRunId.create logicalRun)
                               AuthorityRootUserMessageId =
                                if System.String.IsNullOrWhiteSpace authorityRoot then
                                    None
                                else
                                    Some(AuthorityRootUserMessageId.create authorityRoot)
                               IdentitySeed = identitySeed
                               PayloadDigest = watermarkText value?payloadDigest |}
                    )
                )
            | other -> invalidArg "kind" (sprintf "unknown watermark event kind: %s" other)

        { RuntimeId = RuntimeId.create runtime
          LocalSeq = LocalSeq.create sequence
          ObservedAt = observedAt
          EventId = EventId.create (sprintf "dispatch-watermark-%d" sequence)
          Stream = stream
          ProviderRun = None
          Fact = fact }

    let foldRuntimeStartWatermark (events: obj array) : obj =
        let rec loop current remaining =
            match remaining with
            | [] ->
                let claims =
                    current.AgentProjections.Sessions
                    |> Map.toArray
                    |> Array.collect (fun (sessionId, session) ->
                        match session.PromptAuthority with
                        | None -> [||]
                        | Some authority ->
                            authority.PendingClaims
                            |> Map.toArray
                            |> Array.map (fun (_, claim) ->
                                box
                                    {| session = SessionId.value sessionId
                                       promptKey = PromptKey.value claim.PromptKey
                                       claimedAtRuntimeStartCount = claim.ClaimedAtRuntimeStartCount |}))

                box
                    {| ok = true
                       value =
                        box
                            {| runtimeStartCount = current.AgentProjections.RuntimeStartCount
                               claims = claims |}
                       error = null |}
            | value :: tail ->
                match Fold.foldEnvelope current (watermarkEnvelope value) with
                | Ok updated -> loop updated tail
                | Error rejection ->
                    box
                        {| ok = false
                           value = null
                           error =
                            box
                                {| fact = rejection.Fact
                                   reason = rejection.Reason |} |}

        loop Fold.empty (events |> Array.toList)

    let pendingClaimCount (handle: JournalHandle) (session: string) : int =
        let projection =
            (PromptDispatcher.forPrompts (PromptJournalAdapter.create handle.Journal))
                .ProjectionFor(SessionId.create session)

        projection.PendingClaims |> Map.count
