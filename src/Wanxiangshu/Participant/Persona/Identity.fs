namespace Wanxiangshu.Participant.Persona

open System
open Wanxiangshu.Foundation

[<RequireQualifiedAccess>]
type PersonaOrigin =
    | ResolvedAtRoot
    | InheritedFromOwner

[<RequireQualifiedAccess>]
type ParticipantIdentityError =
    | BlankParticipantName
    | LegacyParticipantName of string
    | MalformedParticipantName of string
    | UnknownParticipantName of string
    | UnsupportedPersonaCatalogVersion of int
    | RoleMismatch of Expected: Role option * Actual: Role option
    | BlankPersona
    | PersonaMismatch of Expected: string * Actual: string
    | OriginMismatch of Expected: PersonaOrigin * Actual: PersonaOrigin
    | OwnerRequired
    | OwnerPersonaMismatch of Expected: string * Actual: string
    | OwnerCatalogVersionMismatch of Expected: int * Actual: int
    | LegacyRoleMismatch of Expected: string * Actual: string
    | UnsupportedLegacyAuthorityKind of string
    | UnprovableLegacyAuthorityIdentity of string

type ParticipantIdentityInput =
    { SelectedAgent: string
      Role: Role option
      Persona: string
      PersonaCatalogVersion: int
      Origin: PersonaOrigin }

type LegacyAuthorityRootIdentityV1Input =
    { AuthorityKind: string
      SelectedAgent: string
      CanonicalRole: string }

type private PersonaName = PersonaName of string

type private PersonaCatalogVersion = PersonaCatalogVersion of int

type private ParticipantKind =
    | ManagedRole of Role
    | Bookkeeper

type ParticipantIdentity =
    private
        { SelectedAgent: PersonaName
          Kind: ParticipantKind
          Persona: string
          PersonaCatalogVersion: PersonaCatalogVersion
          Origin: PersonaOrigin }

type ParticipantIdentityEvidence =
    private
        { Identity: ParticipantIdentity }

[<RequireQualifiedAccess>]
module ParticipantIdentity =

    let private currentVersion = PersonaCatalogVersion 1

    let private versionNumber (PersonaCatalogVersion version) = version

    let private nameValue (PersonaName name) = name

    let private roleOfKind kind =
        match kind with
        | ManagedRole role -> Some role
        | Bookkeeper -> None

    let private roleLabelOfKind kind =
        match kind with
        | ManagedRole role -> Roles.roleLabel role
        | Bookkeeper -> "bookkeeper"

    let private personaV1 kind =
        match kind with
        | ManagedRole role -> PersonaCatalog.personaV1 role
        | Bookkeeper -> PersonaCatalog.bookkeeperPersonaV1 ()

    let private parseKnownCanonicalName
        (name: string)
        : Result<PersonaName * ParticipantKind, ParticipantIdentityError> =
        match name.ToLowerInvariant() with
        | "bookkeeper" -> Ok(PersonaName "bookkeeper", Bookkeeper)
        | roleName when (Roles.tryParseRole roleName).IsSome ->
            let role = (Roles.tryParseRole roleName).Value
            Ok(PersonaName(Roles.roleLabel role), ManagedRole role)
        | _ -> Error(ParticipantIdentityError.UnknownParticipantName name)

    let private parseCanonicalName (name: string) =
        if String.IsNullOrWhiteSpace name then
            Error ParticipantIdentityError.BlankParticipantName
        elif ManagedAgentCatalog.isLegacyAgentName (name.ToLowerInvariant()) then
            Error(ParticipantIdentityError.LegacyParticipantName name)
        elif name <> name.ToLowerInvariant() || name.Contains("-") then
            Error(ParticipantIdentityError.MalformedParticipantName name)
        else
            parseKnownCanonicalName name

    let private create
        (selectedAgent: PersonaName)
        (kind: ParticipantKind)
        (persona: string)
        (version: PersonaCatalogVersion)
        (origin: PersonaOrigin)
        : ParticipantIdentityEvidence =
        { SelectedAgent = selectedAgent
          Kind = kind
          Persona = persona
          PersonaCatalogVersion = version
          Origin = origin }
        |> fun identity -> { Identity = identity }

    let private identity (evidence: ParticipantIdentityEvidence) = evidence.Identity

    let selectedAgent (evidence: ParticipantIdentityEvidence) : string =
        (identity evidence).SelectedAgent |> nameValue

    let role (evidence: ParticipantIdentityEvidence) : Role option = (identity evidence).Kind |> roleOfKind

    let roleLabel (evidence: ParticipantIdentityEvidence) : string =
        (identity evidence).Kind |> roleLabelOfKind

    let persona (evidence: ParticipantIdentityEvidence) : string = (identity evidence).Persona

    let personaCatalogVersion (evidence: ParticipantIdentityEvidence) : int =
        (identity evidence).PersonaCatalogVersion |> versionNumber

    let origin (evidence: ParticipantIdentityEvidence) : PersonaOrigin = (identity evidence).Origin

    let toInput (evidence: ParticipantIdentityEvidence) : ParticipantIdentityInput =
        { SelectedAgent = selectedAgent evidence
          Role = role evidence
          Persona = persona evidence
          PersonaCatalogVersion = personaCatalogVersion evidence
          Origin = origin evidence }

    let resolveAtRoot (canonicalManagedName: string) : Result<ParticipantIdentityEvidence, ParticipantIdentityError> =
        parseCanonicalName canonicalManagedName
        |> Result.map (fun (name, kind) ->
            create name kind (personaV1 kind) currentVersion PersonaOrigin.ResolvedAtRoot)

    let inheritFromOwner
        (canonicalManagedName: string)
        (owner: ParticipantIdentityEvidence)
        : Result<ParticipantIdentityEvidence, ParticipantIdentityError> =
        parseCanonicalName canonicalManagedName
        |> Result.map (fun (name, kind) ->
            create name kind (persona owner) (identity owner).PersonaCatalogVersion PersonaOrigin.InheritedFromOwner)

    let private validateRehydrationShape
        (input: ParticipantIdentityInput)
        ((name, kind): PersonaName * ParticipantKind)
        : Result<PersonaName * ParticipantKind, ParticipantIdentityError> =
        let expectedRole = roleOfKind kind
        let expectedVersion = versionNumber currentVersion

        if input.PersonaCatalogVersion <> expectedVersion then
            Error(ParticipantIdentityError.UnsupportedPersonaCatalogVersion input.PersonaCatalogVersion)
        elif input.Role <> expectedRole then
            Error(ParticipantIdentityError.RoleMismatch(expectedRole, input.Role))
        elif String.IsNullOrWhiteSpace input.Persona then
            Error ParticipantIdentityError.BlankPersona
        else
            Ok(name, kind)

    let private rehydrateRoot
        (input: ParticipantIdentityInput)
        ((name, kind): PersonaName * ParticipantKind)
        : Result<ParticipantIdentityEvidence, ParticipantIdentityError> =
        let expectedPersona = personaV1 kind

        if input.Persona <> expectedPersona then
            Error(ParticipantIdentityError.PersonaMismatch(expectedPersona, input.Persona))
        else
            Ok(create name kind input.Persona currentVersion PersonaOrigin.ResolvedAtRoot)

    let private rehydrateInherited
        (owner: ParticipantIdentityEvidence)
        (input: ParticipantIdentityInput)
        ((name, kind): PersonaName * ParticipantKind)
        : Result<ParticipantIdentityEvidence, ParticipantIdentityError> =
        let expectedOwnerPersona = persona owner
        let expectedOwnerVersion = personaCatalogVersion owner

        if input.Persona <> expectedOwnerPersona then
            Error(ParticipantIdentityError.OwnerPersonaMismatch(expectedOwnerPersona, input.Persona))
        elif input.PersonaCatalogVersion <> expectedOwnerVersion then
            Error(
                ParticipantIdentityError.OwnerCatalogVersionMismatch(expectedOwnerVersion, input.PersonaCatalogVersion)
            )
        else
            Ok(create name kind input.Persona (identity owner).PersonaCatalogVersion PersonaOrigin.InheritedFromOwner)

    let private rehydrateByOrigin
        (ownerOption: ParticipantIdentityEvidence option)
        (input: ParticipantIdentityInput)
        (parsed: PersonaName * ParticipantKind)
        : Result<ParticipantIdentityEvidence, ParticipantIdentityError> =
        match ownerOption, input.Origin with
        | None, PersonaOrigin.InheritedFromOwner -> Error ParticipantIdentityError.OwnerRequired
        | Some _, PersonaOrigin.ResolvedAtRoot ->
            Error(
                ParticipantIdentityError.OriginMismatch(PersonaOrigin.InheritedFromOwner, PersonaOrigin.ResolvedAtRoot)
            )
        | None, PersonaOrigin.ResolvedAtRoot -> rehydrateRoot input parsed
        | Some owner, PersonaOrigin.InheritedFromOwner -> rehydrateInherited owner input parsed

    let private parseCandidate (input: ParticipantIdentityInput) =
        match parseCanonicalName input.SelectedAgent with
        | Ok p -> Ok p
        | Error(ParticipantIdentityError.LegacyParticipantName name) when
            input.Origin = PersonaOrigin.InheritedFromOwner
            ->
            Roles.tryParseHistoricalRole name
            |> Option.map (fun role -> Ok(PersonaName name, ManagedRole role))
            |> Option.defaultValue (Error(ParticipantIdentityError.LegacyParticipantName name))
        | Error err -> Error err

    let rehydrate
        (ownerOption: ParticipantIdentityEvidence option)
        (input: ParticipantIdentityInput)
        : Result<ParticipantIdentityEvidence, ParticipantIdentityError> =
        parseCandidate input
        |> Result.bind (validateRehydrationShape input)
        |> Result.bind (rehydrateByOrigin ownerOption input)

    let fromInput (input: ParticipantIdentityInput) : Result<ParticipantIdentityEvidence, ParticipantIdentityError> =
        parseCandidate input
        |> Result.bind (validateRehydrationShape input)
        |> Result.bind (fun ((name, kind) as parsed) ->
            match input.Origin with
            | PersonaOrigin.ResolvedAtRoot -> rehydrateRoot input parsed
            | PersonaOrigin.InheritedFromOwner ->
                Ok(create name kind input.Persona currentVersion PersonaOrigin.InheritedFromOwner))

    let legacyAgentOwnerRootUnprovableMessage =
        "legacy AuthorityRootAccepted v1 AgentOwnerRoot cannot prove participant identity"

    let upgradeLegacyV1Root
        (input: LegacyAuthorityRootIdentityV1Input)
        : Result<ParticipantIdentityEvidence, ParticipantIdentityError> =
        match input.AuthorityKind with
        | "AgentOwnerRoot" ->
            Error(ParticipantIdentityError.UnprovableLegacyAuthorityIdentity legacyAgentOwnerRootUnprovableMessage)
        | "HumanRoot" ->
            parseCanonicalName input.SelectedAgent
            |> Result.map (fun (name, kind) ->
                create name kind (personaV1 kind) (PersonaCatalogVersion 1) PersonaOrigin.ResolvedAtRoot)
        | authorityKind -> Error(ParticipantIdentityError.UnsupportedLegacyAuthorityKind authorityKind)
