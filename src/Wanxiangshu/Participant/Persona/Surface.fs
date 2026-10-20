namespace Wanxiangshu.Participant.Persona

open System
open Wanxiangshu.Foundation

/// JS-native identity and persona contract owned by Participant/Persona.
///
/// Roles, managed names, legacy rejection, and persona labels cross as
/// plain strings/booleans/arrays. F# Role values never cross the
/// boundary, so callers cannot depend on their emitted representation.
module PersonaSurface =

    let private roleOf (label: string) : Role option =
        if isNull label then None else Roles.tryParseRole label

    let private historicalRoleOf (label: string) : Role option =
        if isNull label then
            None
        else
            Roles.tryParseHistoricalRole label

    let allRoleLabels: string array =
        ManagedAgentCatalog.allRoles |> List.map Roles.roleLabel |> List.toArray

    let allPublicRoleLabels: string array =
        ManagedAgentCatalog.allPublicRoles |> List.map Roles.roleLabel |> List.toArray

    let allInternalRoleLabels: string array =
        ManagedAgentCatalog.allInternalRoles |> List.map Roles.roleLabel |> List.toArray

    let requiredNames: string array = ManagedAgentCatalog.requiredNames |> List.toArray

    let legacyNames: string array = ManagedAgentCatalog.legacyAgentNames |> Set.toArray

    let currentCatalogVersion: string = "1"

    let nameOf (tierLabel: string) (roleLabel: string) : string =
        let targetRole =
            if isNull roleLabel || roleLabel = "" then
                tierLabel
            else
                roleLabel

        match roleOf targetRole with
        | Some role -> ManagedAgentCatalog.nameOf role
        | None -> ""

    let isManagedName (name: string) : bool =
        if isNull name then
            false
        elif ManagedAgentCatalog.isBookkeeperName name then
            true
        elif ManagedAgentCatalog.isLegacyAgentName (name.ToLowerInvariant()) then
            false
        else
            (Roles.tryParseRole (name.ToLowerInvariant())).IsSome

    let isLegacyName (name: string) : bool =
        not (isNull name)
        && ManagedAgentCatalog.isLegacyAgentName (name.ToLowerInvariant())

    let roleName (roleLabel: string) : string =
        historicalRoleOf roleLabel
        |> Option.map Roles.roleLabel
        |> Option.defaultValue ""

    let persona (roleLabel: string) (_tierLabel: string) : string =
        match historicalRoleOf roleLabel with
        | Some role -> PersonaCatalog.persona role |> Persona.render
        | None -> ""

    let bookkeeperPersona (_tierLabel: string) : string =
        PersonaCatalog.bookkeeperPersona () |> Persona.render

    let formatLegacyNameNotSupported (name: string) : string =
        ManagedAgentCatalog.formatLegacyNameNotSupported name

    let formatLegacyNameInConfig (name: string) : string =
        ManagedAgentCatalog.formatLegacyNameInConfig name

    let private originLabel origin =
        match origin with
        | PersonaOrigin.ResolvedAtRoot -> "ResolvedAtRoot"
        | PersonaOrigin.InheritedFromOwner -> "InheritedFromOwner"

    let private errorLabel error =
        match error with
        | ParticipantIdentityError.BlankParticipantName -> "BlankParticipantName"
        | ParticipantIdentityError.LegacyParticipantName _ -> "LegacyParticipantName"
        | ParticipantIdentityError.MalformedParticipantName _ -> "MalformedParticipantName"
        | ParticipantIdentityError.UnknownParticipantName _ -> "UnknownParticipantName"
        | ParticipantIdentityError.UnsupportedPersonaCatalogVersion _ -> "UnsupportedPersonaCatalogVersion"
        | ParticipantIdentityError.RoleMismatch _ -> "RoleMismatch"
        | ParticipantIdentityError.BlankPersona -> "BlankPersona"
        | ParticipantIdentityError.PersonaMismatch _ -> "PersonaMismatch"
        | ParticipantIdentityError.OriginMismatch _ -> "OriginMismatch"
        | ParticipantIdentityError.OwnerRequired -> "OwnerRequired"
        | ParticipantIdentityError.OwnerPersonaMismatch _ -> "OwnerPersonaMismatch"
        | ParticipantIdentityError.OwnerCatalogVersionMismatch _ -> "OwnerCatalogVersionMismatch"
        | ParticipantIdentityError.LegacyRoleMismatch _ -> "LegacyRoleMismatch"
        | ParticipantIdentityError.UnsupportedLegacyAuthorityKind _ -> "UnsupportedLegacyAuthorityKind"
        | ParticipantIdentityError.UnprovableLegacyAuthorityIdentity _ -> "UnprovableLegacyAuthorityIdentity"

    let private identityToJs evidence : obj =
        box
            {| name = ParticipantIdentity.selectedAgent evidence
               role = ParticipantIdentity.roleLabel evidence
               initialTier = "deep"
               peer = ParticipantIdentity.selectedAgent evidence
               persona = ParticipantIdentity.persona evidence
               catalogVersion = ParticipantIdentity.personaCatalogVersion evidence
               origin = ParticipantIdentity.origin evidence |> originLabel |}

    let private identityResult result : obj =
        match result with
        | Ok evidence ->
            box
                {| ok = true
                   identity = identityToJs evidence
                   error = null |}
        | Error error ->
            box
                {| ok = false
                   identity = null
                   error = errorLabel error |}

    let private boundaryFailure error : obj =
        box
            {| ok = false
               identity = null
               error = error |}

    let resolveParticipantIdentityAtRoot (canonicalManagedName: string) : obj =
        ParticipantIdentity.resolveAtRoot canonicalManagedName |> identityResult

    let inheritParticipantIdentityFromOwner (canonicalManagedName: string) (ownerCanonicalManagedName: string) : obj =
        ParticipantIdentity.resolveAtRoot ownerCanonicalManagedName
        |> Result.bind (ParticipantIdentity.inheritFromOwner canonicalManagedName)
        |> identityResult

    let rehydrateParticipantIdentity
        (ownerCanonicalManagedName: string)
        (canonicalManagedName: string)
        (roleLabel: string)
        (_initialTierLabel: string)
        (_peerCanonicalManagedName: string)
        (personaName: string)
        (catalogVersion: int)
        (origin: string)
        : obj =
        let parsedRole =
            if roleLabel = "bookkeeper" then
                Some None
            else
                historicalRoleOf roleLabel |> Option.map Some

        let parsedOrigin =
            match origin with
            | "ResolvedAtRoot" -> Some PersonaOrigin.ResolvedAtRoot
            | "InheritedFromOwner" -> Some PersonaOrigin.InheritedFromOwner
            | _ -> None

        match parsedRole, parsedOrigin with
        | None, _ -> boundaryFailure "InvalidRoleLabel"
        | _, None -> boundaryFailure "InvalidOriginLabel"
        | Some role, Some personaOrigin ->
            let input =
                { SelectedAgent = canonicalManagedName
                  Role = role
                  Persona = personaName
                  PersonaCatalogVersion = catalogVersion
                  Origin = personaOrigin }

            if String.IsNullOrWhiteSpace ownerCanonicalManagedName then
                ParticipantIdentity.rehydrate None input |> identityResult
            else
                ParticipantIdentity.resolveAtRoot ownerCanonicalManagedName
                |> Result.bind (fun owner -> ParticipantIdentity.rehydrate (Some owner) input)
                |> identityResult
