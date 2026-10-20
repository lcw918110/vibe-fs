namespace Wanxiangshu.Interaction.Authority

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Persona

/// JS-native lifecycle surface for authority provenance and run state.
/// Typed profiles, claims, identities, and maps stay private to this boundary.
module RuntimeSurface =

    let private text (value: obj) =
        if isNull value then "" else string value

    let private arrayOf (value: obj) : obj array =
        if isNull value then [||] else unbox<obj array> value

    let private rootKindResult (value: obj) : Result<PromptAuthority.RootAuthorityKind, string> =
        match text value with
        | "HumanRoot" -> Ok PromptAuthority.RootAuthorityKind.HumanRoot
        | "AgentOwnerRoot" -> Ok PromptAuthority.RootAuthorityKind.AgentOwnerRoot
        | unknown -> Error(sprintf "unknown authority root kind: %s" unknown)

    let private rootKindOf (value: obj) =
        match rootKindResult value with
        | Ok kind -> kind
        | Error error -> invalidArg "authorityKind" error

    let private continuationKindResult (value: string) =
        match PromptAuthority.tryParseContinuationKind value with
        | Some kind -> Ok kind
        | None -> Error(sprintf "unknown ContinuationKind: %s" value)

    let private continuationKindOf (value: string) =
        match continuationKindResult value with
        | Ok kind -> kind
        | Error error -> invalidArg "kind" error

    let private roleResult (value: obj) : Result<Role, string> =
        match Roles.tryParseRole (text value) with
        | Some role -> Ok role
        | None -> Error(sprintf "unknown role: %s" (text value))

    let private originResult (value: obj) : Result<PersonaOrigin, string> =
        match text value with
        | "ResolvedAtRoot" -> Ok PersonaOrigin.ResolvedAtRoot
        | "InheritedFromOwner" -> Ok PersonaOrigin.InheritedFromOwner
        | unknown -> Error(sprintf "unknown participant identity origin: %s" unknown)

    let private identityRoleResult (value: obj) : Result<Role option, string> =
        if text value = "bookkeeper" then
            Ok None
        else
            roleResult value |> Result.map Some

    let private participantIdentityResult (value: obj) : Result<ParticipantIdentityEvidence, string> =
        let roleVal =
            if isNull value?role then
                value?canonicalRole
            else
                value?role

        let participantVal =
            if isNull value?participant then
                value?selectedAgent
            else
                value?participant

        match identityRoleResult roleVal, originResult value?origin with
        | Ok role, Ok origin ->
            { SelectedAgent = text participantVal
              Role = role
              Persona = text value?persona
              PersonaCatalogVersion = unbox<int> value?personaCatalogVersion
              Origin = origin }
            |> ParticipantIdentity.fromInput
            |> Result.mapError (fun error -> sprintf "invalid participant identity: %A" error)
        | Error error, _
        | _, Error error -> Error error

    let private identitySeedResult (value: obj) : Result<PromptAuthority.IdentitySeed, string> =
        participantIdentityResult value?participantIdentity
        |> Result.bind (fun identity ->
            let input = ParticipantIdentity.toInput identity

            let requiredOwnerId label ownerId =
                let value = text ownerId

                if System.String.IsNullOrWhiteSpace value then
                    Error(sprintf "invalid identity seed: %s is blank" label)
                else
                    Ok value

            let seedInput =
                match text value?kind with
                | "RootSelection" -> Ok(PromptAuthority.IdentitySeedInput.RootSelectionInput input)
                | "InheritedFromOwner" ->
                    match
                        requiredOwnerId "ownerSession" value?ownerSession,
                        requiredOwnerId "ownerLogicalRun" value?ownerLogicalRun,
                        requiredOwnerId "ownerAuthorityRoot" value?ownerAuthorityRoot
                    with
                    | Ok ownerSession, Ok ownerLogicalRun, Ok ownerAuthorityRoot ->
                        Ok(
                            PromptAuthority.IdentitySeedInput.InheritedFromOwnerInput
                                { OwnerSessionId = SessionId.create ownerSession
                                  OwnerLogicalRunId = LogicalRunId.create ownerLogicalRun
                                  OwnerAuthorityRootUserMessageId =
                                    AuthorityRootUserMessageId.create ownerAuthorityRoot
                                  ParticipantIdentity = input }
                        )
                    | Error error, _, _
                    | _, Error error, _
                    | _, _, Error error -> Error error
                | unknown -> Error(sprintf "unknown identity seed kind: %s" unknown)

            seedInput
            |> Result.bind (
                PromptIdentitySeed.rehydrate
                >> Result.mapError (sprintf "invalid identity seed: %A")
            ))

    let private profileResult (value: obj) : Result<PromptAuthority.AuthorityExecutionProfile, string> =
        match rootKindResult value?authorityKind, identitySeedResult value?identitySeed with
        | Ok authorityKind, Ok identitySeed ->
            PromptAuthority.createAuthorityExecutionProfileFromSeed
                (SessionId.create (text value?session))
                (LogicalRunId.create (text value?logicalRun))
                (AuthorityRootUserMessageId.create (text value?authorityRoot))
                authorityKind
                identitySeed
        | Error error, _
        | _, Error error -> Error error

    let private profileOf (value: obj) : PromptAuthority.AuthorityExecutionProfile =
        match profileResult value with
        | Ok profile -> profile
        | Error error -> invalidArg "profile" error

    let private participantIdentityToJs (identity: ParticipantIdentityEvidence) : obj =
        box
            {| participant = ParticipantIdentity.selectedAgent identity
               role = ParticipantIdentity.roleLabel identity
               persona = ParticipantIdentity.persona identity
               personaCatalogVersion = ParticipantIdentity.personaCatalogVersion identity
               origin =
                match ParticipantIdentity.origin identity with
                | PersonaOrigin.ResolvedAtRoot -> "ResolvedAtRoot"
                | PersonaOrigin.InheritedFromOwner -> "InheritedFromOwner" |}

    let private identitySeedToJs (seed: PromptAuthority.IdentitySeed) : obj =
        let participantIdentity =
            PromptAuthority.identitySeedParticipantIdentity seed |> participantIdentityToJs

        match PromptAuthority.identitySeedOwner seed with
        | None ->
            box
                {| kind = "RootSelection"
                   ownerSession = null
                   ownerLogicalRun = null
                   ownerAuthorityRoot = null
                   participantIdentity = participantIdentity |}
        | Some(ownerSession, ownerLogicalRun, ownerAuthorityRoot) ->
            box
                {| kind = "InheritedFromOwner"
                   ownerSession = SessionId.value ownerSession
                   ownerLogicalRun = LogicalRunId.value ownerLogicalRun
                   ownerAuthorityRoot = AuthorityRootUserMessageId.value ownerAuthorityRoot
                   participantIdentity = participantIdentity |}

    let private profileToJs (profile: PromptAuthority.AuthorityExecutionProfile) : obj =
        box
            {| session = SessionId.value profile.SessionId
               logicalRun = LogicalRunId.value profile.LogicalRunId
               authorityRoot = AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId
               authorityKind =
                match profile.AuthorityKind with
                | PromptAuthority.RootAuthorityKind.HumanRoot -> "HumanRoot"
                | PromptAuthority.RootAuthorityKind.AgentOwnerRoot -> "AgentOwnerRoot"
               identitySeed = identitySeedToJs profile.IdentitySeed
               participantIdentity = participantIdentityToJs profile.ParticipantIdentity |}

    let private optionalString (value: obj) : string option =
        if isNull value then None else Some(text value)

    let private originName (origin: PromptAuthority.PromptOrigin) : string =
        match origin with
        | PromptAuthority.PromptOrigin.AuthorityRoot _ -> "AuthorityRoot"
        | PromptAuthority.PromptOrigin.Continuation _ -> "Continuation"
        | PromptAuthority.PromptOrigin.HostInternal -> "HostInternal"
        | PromptAuthority.PromptOrigin.UnknownOrigin -> "UnknownOrigin"

    let private originOf (kind: string) (label: string) : PromptAuthority.PromptOrigin =
        match kind with
        | "Continuation" -> PromptAuthority.PromptOrigin.Continuation(continuationKindOf label)
        | "AuthorityRoot" -> PromptAuthority.PromptOrigin.AuthorityRoot(rootKindOf (box label))
        | "HostInternal" -> PromptAuthority.PromptOrigin.HostInternal
        | _ -> PromptAuthority.PromptOrigin.UnknownOrigin

    let private claimToJs (claim: PromptAuthority.PromptClaim) : obj =
        box
            {| promptKey = PromptKey.value claim.PromptKey
               session = SessionId.value claim.SessionId
               origin = originName claim.Origin
               originLabel = PromptAuthority.originLabel claim.Origin
               logicalRun = claim.LogicalRunId |> Option.map LogicalRunId.value |> Option.defaultValue null
               authorityRoot =
                claim.AuthorityRootUserMessageId
                |> Option.map AuthorityRootUserMessageId.value
                |> Option.defaultValue null
               identitySeed = identitySeedToJs claim.IdentitySeed
               payloadDigest = claim.PayloadDigest
               receipt = claim.Receipt |> Option.map TransportReceipt.value |> Option.defaultValue null
               claimedAtRuntimeStartCount = claim.ClaimedAtRuntimeStartCount |}

    let private claimOf (value: obj) : PromptAuthority.PromptClaim =
        let kind = text value?origin
        let label = text value?originLabel

        let identitySeed =
            match identitySeedResult value?identitySeed with
            | Ok seed -> seed
            | Error error -> invalidArg "claim.identitySeed" error

        { PromptKey = PromptKey.create (text value?promptKey)
          SessionId = SessionId.create (text value?session)
          Origin = originOf kind label
          LogicalRunId = optionalString value?logicalRun |> Option.map LogicalRunId.create
          AuthorityRootUserMessageId =
            optionalString value?authorityRoot
            |> Option.map AuthorityRootUserMessageId.create
          IdentitySeed = identitySeed
          PayloadDigest = text value?payloadDigest
          Receipt = optionalString value?receipt |> Option.map TransportReceipt.create
          ClaimedAtRuntimeStartCount = 0 }

    let private acceptedDispatchToJs (dispatch: PromptAuthority.AcceptedDispatch) : obj =
        box
            {| promptKey = PromptKey.value dispatch.PromptKey
               session = SessionId.value dispatch.SessionId
               origin = originName dispatch.Origin
               originLabel = PromptAuthority.originLabel dispatch.Origin
               identitySeed = identitySeedToJs dispatch.IdentitySeed
               payloadDigest = dispatch.PayloadDigest
               physical = PhysicalUserMessageId.value dispatch.PhysicalUserMessageId |}

    let private acceptedDispatchOf (value: obj) : PromptAuthority.AcceptedDispatch =
        let identitySeed =
            match identitySeedResult value?identitySeed with
            | Ok seed -> seed
            | Error error -> invalidArg "dispatch.identitySeed" error

        { PromptKey = PromptKey.create (text value?promptKey)
          SessionId = SessionId.create (text value?session)
          Origin = originOf (text value?origin) (text value?originLabel)
          IdentitySeed = identitySeed
          PayloadDigest = text value?payloadDigest
          PhysicalUserMessageId = PhysicalUserMessageId.create (text value?physical) }

    let private profileOption (value: obj) =
        if isNull value then None else Some(profileOf value)

    let private projectionValidation (value: obj) : Result<unit, string> =
        if isNull value then
            Ok()
        else
            let validateProfile value =
                if isNull value then
                    Ok()
                else
                    profileResult value |> Result.map (fun _ -> ())

            match validateProfile value?lastAuthorityProfile, validateProfile value?activeLogicalRun with
            | Ok(), Ok() -> Ok()
            | Error error, _
            | _, Error error -> Error error

    let internal projectionOf (value: obj) : PromptAuthority.PromptAuthorityProjection =
        if isNull value then
            PromptAuthority.empty
        else
            let pending =
                arrayOf value?pendingClaims
                |> Array.fold
                    (fun current claim ->
                        let typed = claimOf claim
                        Map.add typed.PromptKey typed current)
                    Map.empty

            let accepted =
                arrayOf value?acceptedContinuations
                |> Array.fold
                    (fun current item ->
                        Map.add
                            (PhysicalUserMessageId.create (text item?physical))
                            (continuationKindOf (text item?kind))
                            current)
                    Map.empty

            let acceptedDispatches =
                arrayOf value?acceptedDispatches
                |> Array.fold
                    (fun current item ->
                        let dispatch = acceptedDispatchOf item

                        Map.add
                            (PromptAuthority.acceptedDispatchKey dispatch.SessionId dispatch.PayloadDigest)
                            dispatch
                            current)
                    Map.empty

            let physicalLandings =
                Array.append (arrayOf value?acceptedDispatches) (arrayOf value?physicalLandings)
                |> Array.fold
                    (fun current item ->
                        let dispatch = acceptedDispatchOf item
                        Map.add dispatch.PhysicalUserMessageId dispatch current)
                    Map.empty

            let sequences =
                arrayOf value?claimSequences
                |> Array.fold
                    (fun current item ->
                        let counter: PromptAuthority.ClaimSequenceCounter =
                            { SessionId = SessionId.create (text item?session)
                              LogicalRunId =
                                if isNull item?logicalRun then
                                    None
                                else
                                    Some(LogicalRunId.create (text item?logicalRun))
                              Count = int (text item?count) }

                        Map.add (text item?scope) counter current)
                    Map.empty

            { LastAuthorityProfile = profileOption value?lastAuthorityProfile
              ActiveLogicalRun = profileOption value?activeLogicalRun
              PendingClaims = pending
              AcceptedDispatches = acceptedDispatches
              PhysicalLandings = physicalLandings
              AcceptedContinuationIds = accepted
              ClaimSequences = sequences
              UserQuestions = Map.empty }

    let internal projectionToJs (projection: PromptAuthority.PromptAuthorityProjection) : obj =
        box
            {| lastAuthorityProfile =
                projection.LastAuthorityProfile
                |> Option.map profileToJs
                |> Option.defaultValue null
               activeLogicalRun =
                projection.ActiveLogicalRun
                |> Option.map profileToJs
                |> Option.defaultValue null
               pendingClaims =
                projection.PendingClaims
                |> Map.toList
                |> List.map (snd >> claimToJs)
                |> List.toArray
               acceptedDispatches =
                projection.AcceptedDispatches
                |> Map.toList
                |> List.map (snd >> acceptedDispatchToJs)
                |> List.toArray
               physicalLandings =
                projection.PhysicalLandings
                |> Map.toList
                |> List.map (snd >> acceptedDispatchToJs)
                |> List.toArray
               acceptedContinuations =
                projection.AcceptedContinuationIds
                |> Map.toList
                |> List.map (fun (physical, kind) ->
                    box
                        {| physical = PhysicalUserMessageId.value physical
                           kind = PromptAuthority.originLabel (PromptAuthority.PromptOrigin.Continuation kind) |})
                |> List.toArray
               claimSequences =
                projection.ClaimSequences
                |> Map.toList
                |> List.map (fun (scope, counter) ->
                    box
                        {| scope = scope
                           session = SessionId.value counter.SessionId
                           logicalRun =
                            counter.LogicalRunId
                            |> Option.map LogicalRunId.value
                            |> Option.defaultValue null
                           count = counter.Count |})
                |> List.toArray |}

    let private identitySeedValidationErrorToJs error : obj =
        match error with
        | PromptAuthority.IdentitySeedValidationError.ExpectedInheritedFromOwner ->
            box
                {| kind = "ExpectedInheritedFromOwner"
                   expected = "InheritedFromOwner"
                   actual = "RootSelection" |}
        | PromptAuthority.IdentitySeedValidationError.OwnerAuthorityNotActive sessionId ->
            box
                {| kind = "OwnerAuthorityNotActive"
                   expected = SessionId.value sessionId
                   actual = "" |}
        | PromptAuthority.IdentitySeedValidationError.OwnerSessionIdMismatch(expected, actual) ->
            box
                {| kind = "OwnerSessionIdMismatch"
                   expected = SessionId.value expected
                   actual = SessionId.value actual |}
        | PromptAuthority.IdentitySeedValidationError.OwnerLogicalRunIdMismatch(expected, actual) ->
            box
                {| kind = "OwnerLogicalRunIdMismatch"
                   expected = LogicalRunId.value expected
                   actual = LogicalRunId.value actual |}
        | PromptAuthority.IdentitySeedValidationError.OwnerAuthorityRootUserMessageIdMismatch(expected, actual) ->
            box
                {| kind = "OwnerAuthorityRootUserMessageIdMismatch"
                   expected = AuthorityRootUserMessageId.value expected
                   actual = AuthorityRootUserMessageId.value actual |}
        | PromptAuthority.IdentitySeedValidationError.InvalidInheritedParticipantIdentity error ->
            box
                {| kind = "InvalidInheritedParticipantIdentity"
                   expected = "owner-bound participant identity"
                   actual = sprintf "%A" error |}

    let empty: obj = projectionToJs PromptAuthority.empty

    let issueInheritedIdentitySeed (childName: string) (ownerProfile: obj) : obj =
        match profileResult ownerProfile with
        | Error error ->
            box
                {| ok = false
                   value = null
                   error = error |}
        | Ok owner ->
            match PromptAuthority.issueInheritedIdentitySeed childName owner with
            | Ok seed ->
                box
                    {| ok = true
                       value = identitySeedToJs seed
                       error = "" |}
            | Error error ->
                box
                    {| ok = false
                       value = null
                       error = sprintf "invalid participant identity: %A" error |}

    let validateInheritedIdentitySeedAgainstActiveOwner (ownerProfile: obj) (seedValue: obj) : obj =
        match identitySeedResult seedValue with
        | Error error ->
            box
                {| ok = false
                   value = null
                   error =
                    box
                        {| kind = "Malformed"
                           expected = ""
                           actual = error |} |}
        | Ok seed ->
            let ownerResult =
                if isNull ownerProfile then
                    Ok None
                else
                    profileResult ownerProfile |> Result.map Some

            match ownerResult with
            | Error error ->
                box
                    {| ok = false
                       value = null
                       error =
                        box
                            {| kind = "Malformed"
                               expected = ""
                               actual = error |} |}
            | Ok owner ->
                match PromptAuthority.validateInheritedIdentitySeedAgainstActiveOwner owner seed with
                | Ok identity ->
                    box
                        {| ok = true
                           value = participantIdentityToJs identity
                           error = null |}
                | Error error ->
                    box
                        {| ok = false
                           value = null
                           error = identitySeedValidationErrorToJs error |}

    let validateInheritedIdentitySeed (ownerProfile: obj) (seedValue: obj) : obj =
        validateInheritedIdentitySeedAgainstActiveOwner ownerProfile seedValue

    let serializeIdentitySeed (seedValue: obj) : obj =
        match identitySeedResult seedValue with
        | Ok seed ->
            box
                {| ok = true
                   value = JS.JSON.stringify (identitySeedToJs seed)
                   error = "" |}
        | Error error ->
            box
                {| ok = false
                   value = null
                   error = error |}

    let rehydrateIdentitySeed (serialized: string) : obj =
        try
            let value: obj = JS.JSON.parse serialized

            match identitySeedResult value with
            | Ok seed ->
                box
                    {| ok = true
                       value = identitySeedToJs seed
                       error = "" |}
            | Error error ->
                box
                    {| ok = false
                       value = null
                       error = error |}
        with error ->
            box
                {| ok = false
                   value = null
                   error = error.Message |}

    let recoverActiveIdentity (projection: obj) : obj =
        match projectionValidation projection with
        | Error error ->
            box
                {| ok = false
                   value = null
                   error = error |}
        | Ok() ->
            match (projectionOf projection).ActiveLogicalRun with
            | None ->
                box
                    {| ok = false
                       value = null
                       error = "MissingActiveAuthority" |}
            | Some profile ->
                box
                    {| ok = true
                       value =
                        box
                            {| participantIdentity = participantIdentityToJs profile.ParticipantIdentity
                               identitySeed = identitySeedToJs profile.IdentitySeed |}
                       error = "" |}

    let projectClaimIdentitySeed (claim: obj) : obj =
        claimOf claim |> fun typed -> identitySeedToJs typed.IdentitySeed

    let promotePhysical (physical: string) : string =
        PhysicalUserMessageId.promoteToAuthorityRoot (PhysicalUserMessageId.create physical)
        |> AuthorityRootUserMessageId.value

    let transportReceiptShape (receipt: string) : bool =
        TransportReceipt.isAdmissionShaped (TransportReceipt.create receipt)

    let createAuthorityRoot
        (hash: string -> string)
        (runtime: string)
        (session: string)
        (kind: string)
        (physical: string)
        (seedValue: obj)
        : obj =
        match rootKindResult (box kind) with
        | Error error ->
            box
                {| ok = false
                   value = null
                   error = error |}
        | Ok authorityKind ->
            match
                identitySeedResult seedValue
                |> Result.bind (fun identitySeed ->
                    PromptAuthorityRun.createAuthorityRoot
                        hash
                        (RuntimeId.create runtime)
                        (SessionId.create session)
                        authorityKind
                        (PhysicalUserMessageId.create physical)
                        identitySeed)
            with
            | Ok profile ->
                box
                    {| ok = true
                       value = profileToJs profile
                       error = "" |}
            | Error error ->
                box
                    {| ok = false
                       value = null
                       error = error |}

    let parseAgentName (agent: string) : obj =
        match ParticipantIdentity.resolveAtRoot agent with
        | Ok identity ->
            match ParticipantIdentity.role identity with
            | Some role ->
                box
                    {| ok = true
                       value =
                        box
                            {| participant = ParticipantIdentity.selectedAgent identity
                               role = Roles.roleLabel role |}
                       error = null |}
            | None ->
                box
                    {| ok = false
                       value = null
                       error =
                        box
                            {| kind = "UnknownManagedAgent"
                               message = sprintf "Unknown managed agent '%s'." agent |} |}
        | Error rejection ->
            let kind, message =
                match rejection with
                | ParticipantIdentityError.LegacyParticipantName name ->
                    "LegacyAgentName", ManagedAgentCatalog.formatLegacyNameNotSupported name
                | ParticipantIdentityError.UnknownParticipantName name ->
                    "UnknownManagedAgent", sprintf "Unknown managed agent '%s'." name
                | ParticipantIdentityError.MalformedParticipantName name ->
                    "Malformed", sprintf "Malformed managed agent name '%s'." name
                | _ -> "Malformed", sprintf "Malformed managed agent name '%s'." agent

            box
                {| ok = false
                   value = null
                   error = box {| kind = kind; message = message |} |}

    let registerAuthority (profile: obj) (projection: obj) : obj =
        match profileResult profile, projectionValidation projection with
        | Ok profile, Ok() ->
            match PromptAuthorityRun.registerAuthority profile (projectionOf projection) with
            | Ok registered -> projectionToJs registered
            | Error(PromptAuthorityRun.ActiveRunIdentityConflict(active, requested)) ->
                box
                    {| ok = false
                       error =
                        box
                            {| kind = "ActiveRunIdentityConflict"
                               active = profileToJs active
                               requested = profileToJs requested |} |}
        | Error error, _
        | _, Error error -> box {| ok = false; error = error |}

    let claimContinuation
        (promptKey: string)
        (session: string)
        (kind: string)
        (profile: obj)
        (payloadDigest: string)
        : obj =
        match continuationKindResult kind, profileResult profile with
        | Ok continuationKind, Ok profile ->
            PromptAuthorityRun.claimContinuation
                (PromptKey.create promptKey)
                (SessionId.create session)
                continuationKind
                profile
                payloadDigest
            |> claimToJs
        | Error error, _
        | _, Error error -> box {| ok = false; error = error |}

    let claimAgentOwnerRoot (promptKey: string) (session: string) (payloadDigest: string) (seedValue: obj) : obj =
        match
            identitySeedResult seedValue
            |> Result.bind (fun identitySeed ->
                PromptAuthorityRun.claimAgentOwnerRoot
                    (PromptKey.create promptKey)
                    (SessionId.create session)
                    payloadDigest
                    identitySeed)
        with
        | Ok claim ->
            box
                {| ok = true
                   value = claimToJs claim
                   error = "" |}
        | Error error ->
            box
                {| ok = false
                   value = null
                   error = error |}

    let registerClaim (claim: obj) (projection: obj) : obj =
        match projectionValidation projection with
        | Error error -> box {| ok = false; error = error |}
        | Ok() ->
            PromptAuthorityRun.registerClaim (claimOf claim) (projectionOf projection)
            |> projectionToJs

    let acceptClaim (promptKey: string) (physical: string) (projection: obj) : obj =
        match projectionValidation projection with
        | Error error -> box {| ok = false; error = error |}
        | Ok() ->
            PromptAuthorityRun.acceptClaim
                (PromptKey.create promptKey)
                (PhysicalUserMessageId.create physical)
                (projectionOf projection)
            |> projectionToJs

    let abandonClaim (promptKey: string) (projection: obj) : obj =
        match projectionValidation projection with
        | Error error -> box {| ok = false; error = error |}
        | Ok() ->
            PromptAuthorityRun.abandonClaim (PromptKey.create promptKey) (projectionOf projection)
            |> projectionToJs

    let nextClaimSequence (scope: string) (projection: obj) : int =
        match projectionValidation projection with
        | Error _ -> 0
        | Ok() -> PromptAuthority.nextClaimSequence scope (projectionOf projection)

    let submitClaim (promptKey: string) (receipt: string) (projection: obj) : obj =
        match projectionValidation projection with
        | Error error -> box {| ok = false; error = error |}
        | Ok() ->
            PromptAuthorityRun.submitClaim
                (PromptKey.create promptKey)
                (TransportReceipt.create receipt)
                (projectionOf projection)
            |> projectionToJs

    let claimScopeDigest (session: string) (logicalRun: obj) (origin: obj) (payloadDigest: string) : string =
        PromptAuthority.claimScopeDigest
            (SessionId.create session)
            (optionalString logicalRun |> Option.map LogicalRunId.create)
            (originOf (text origin?kind) (text origin?label))
            payloadDigest

    let derivePromptKey
        (hash: string -> string)
        (session: string)
        (logicalRun: obj)
        (authorityRoot: obj)
        (origin: obj)
        (payloadDigest: string)
        (claimSequence: int)
        : string =
        PromptAuthority.derivePromptKey
            hash
            (SessionId.create session)
            (optionalString logicalRun |> Option.map LogicalRunId.create)
            (optionalString authorityRoot |> Option.map AuthorityRootUserMessageId.create)
            (originOf (text origin?kind) (text origin?label))
            payloadDigest
            claimSequence
        |> PromptKey.value

    let closeAuthority (logicalRun: string) (authorityRoot: string) (projection: obj) : obj =
        match projectionValidation projection with
        | Error error ->
            box
                {| ok = false
                   value = null
                   error = error |}
        | Ok() ->
            match
                PromptAuthorityRun.closeAuthority
                    (LogicalRunId.create logicalRun)
                    (AuthorityRootUserMessageId.create authorityRoot)
                    (projectionOf projection)
            with
            | Ok closed ->
                box
                    {| ok = true
                       value = projectionToJs closed
                       error = "" |}
            | Error error ->
                box
                    {| ok = false
                       value = null
                       error = error |}

    let closeCompletedAgentOwnerChildWork (logicalRun: string) (authorityRoot: string) (projection: obj) : obj =
        match projectionValidation projection with
        | Error error -> box {| ok = false; error = error |}
        | Ok() ->
            let typed = projectionOf projection

            match
                PromptAuthorityRun.closeCompletedAgentOwnerChildWork
                    (LogicalRunId.create logicalRun)
                    (AuthorityRootUserMessageId.create authorityRoot)
                    typed
            with
            | Ok closed -> projectionToJs closed
            | Error error -> box {| ok = false; error = error |}

    let resolveKnownOrigin (physical: string) (promptKey: string) (hostCompaction: bool) (projection: obj) : string =
        match projectionValidation projection with
        | Error _ -> "UnknownOrigin"
        | Ok() ->
            PromptAuthorityRun.resolveKnownOrigin
                (PhysicalUserMessageId.create physical)
                (if System.String.IsNullOrWhiteSpace promptKey then
                     None
                 else
                     Some(PromptKey.create promptKey))
                hostCompaction
                (projectionOf projection)
            |> originName

    let stableLogicalRunId (hash: string -> string) (runtime: string) (session: string) (physical: string) : string =
        PromptAuthority.stableLogicalRunId
            hash
            (RuntimeId.create runtime)
            (SessionId.create session)
            (PhysicalUserMessageId.promoteToAuthorityRoot (PhysicalUserMessageId.create physical))
        |> LogicalRunId.value

    let originForContinuation (kind: string) : obj =
        let value = PromptAuthority.PromptOrigin.Continuation(continuationKindOf kind)

        box
            {| kind = originName value
               label = PromptAuthority.originLabel value |}

    let tryParseContinuationKind (kind: string) : obj =
        PromptAuthority.tryParseContinuationKind kind
        |> Option.map (fun value ->
            box {| kind = PromptAuthority.originLabel (PromptAuthority.PromptOrigin.Continuation value) |})
        |> Option.defaultValue null

    let repairPayloadDigest (request: string) (terminal: string) (kind: string) : string =
        PromptAuthority.repairPayloadDigest (RepairRequestId.create request) (ProviderRunIdentity.create terminal) kind

    let repairAlreadyClaimed
        (session: string)
        (logicalRun: string)
        (request: string)
        (terminal: string)
        (kind: string)
        (projection: obj)
        : bool =
        PromptAuthority.repairAlreadyClaimed
            (SessionId.create session)
            (LogicalRunId.create logicalRun)
            (RepairRequestId.create request)
            (ProviderRunIdentity.create terminal)
            kind
            (projectionOf projection)

    let gateNudgePayloadDigest (kind: string) (providerRun: string) : string =
        PromptAuthority.gateNudgePayloadDigest kind (ProviderRunIdentity.create providerRun)

    let gateNudgeAlreadyAdmitted
        (session: string)
        (logicalRun: string)
        (continuation: string)
        (kind: string)
        (providerRun: string)
        (projection: obj)
        : bool =
        PromptAuthority.gateNudgeAlreadyAdmitted
            (SessionId.create session)
            (LogicalRunId.create logicalRun)
            (continuationKindOf continuation)
            kind
            (ProviderRunIdentity.create providerRun)
            (projectionOf projection)

    let validateAuthorityRoot (ownerProfile: obj) : bool =
        if isNull ownerProfile then
            false
        else
            match profileResult ownerProfile with
            | Ok _ -> true
            | Error _ -> false
