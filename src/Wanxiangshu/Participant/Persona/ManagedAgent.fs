namespace Wanxiangshu.Participant.ManagedAgents

open System
open Wanxiangshu.Foundation
open Wanxiangshu.Participant.Persona

/// Managed Agent identity: canonical Role.
/// Identity tables live in `ManagedAgentCatalog` (AGENT-001…004).
[<RequireQualifiedAccess>]
type AgentVisibility =
    | Public
    | Internal

type ManagedAgent =
    { Name: string
      Role: Role
      Visibility: AgentVisibility }

[<RequireQualifiedAccess>]
type ManagedAgentParseError =
    | UnknownManagedAgent of string
    | LegacyAgentName of string
    | Malformed of string

module ManagedAgent =

    let roleName = Roles.roleLabel
    let nameOf = ManagedAgentCatalog.nameOf

    let visibilityOf (role: Role) : AgentVisibility =
        if Roles.isInternal role then
            AgentVisibility.Internal
        else
            AgentVisibility.Public

    let make (role: Role) : ManagedAgent =
        { Name = nameOf role
          Role = role
          Visibility = visibilityOf role }

    let allPublicRoles = ManagedAgentCatalog.allPublicRoles
    let allInternalRoles = ManagedAgentCatalog.allInternalRoles
    let allRoles = ManagedAgentCatalog.allRoles
    let requiredNames = ManagedAgentCatalog.requiredNames
    let managerForkableNames = ManagedAgentCatalog.managerForkableNames
    let orchestratorForkableNames = ManagedAgentCatalog.orchestratorForkableNames

    let private mapIdentityError value error =
        match error with
        | ParticipantIdentityError.LegacyParticipantName name -> ManagedAgentParseError.LegacyAgentName name
        | ParticipantIdentityError.UnknownParticipantName name -> ManagedAgentParseError.UnknownManagedAgent name
        | ParticipantIdentityError.BlankParticipantName -> ManagedAgentParseError.Malformed value
        | ParticipantIdentityError.MalformedParticipantName name -> ManagedAgentParseError.Malformed name
        | ParticipantIdentityError.UnsupportedPersonaCatalogVersion _
        | ParticipantIdentityError.RoleMismatch _
        | ParticipantIdentityError.BlankPersona
        | ParticipantIdentityError.PersonaMismatch _
        | ParticipantIdentityError.OriginMismatch _
        | ParticipantIdentityError.OwnerRequired
        | ParticipantIdentityError.OwnerPersonaMismatch _
        | ParticipantIdentityError.OwnerCatalogVersionMismatch _
        | ParticipantIdentityError.LegacyRoleMismatch _
        | ParticipantIdentityError.UnsupportedLegacyAuthorityKind _
        | ParticipantIdentityError.UnprovableLegacyAuthorityIdentity _ -> ManagedAgentParseError.Malformed value

    let private classifyPublicRole value =
        function
        | Some role -> Ok role
        | None -> Error(ManagedAgentParseError.UnknownManagedAgent value)

    let private admitPublicRole value identity =
        ParticipantIdentity.role identity
        |> classifyPublicRole value
        |> Result.map (fun role ->
            { Name = ParticipantIdentity.selectedAgent identity
              Role = role
              Visibility = visibilityOf role })

    let parse (value: string) : Result<ManagedAgent, ManagedAgentParseError> =
        ParticipantIdentity.resolveAtRoot value
        |> Result.mapError (mapIdentityError value)
        |> Result.bind (admitPublicRole value)

    let tryParse (value: string) : ManagedAgent option = parse value |> Result.toOption

    let peer (agent: ManagedAgent) : ManagedAgent = agent

    let isInternal (agent: ManagedAgent) =
        agent.Visibility = AgentVisibility.Internal

    let isManagedName (value: string) : bool =
        if String.IsNullOrWhiteSpace value then
            false
        else
            let trimmed = value.Trim()
            tryParse trimmed |> Option.isSome || ManagedAgentCatalog.isBookkeeperName trimmed

    let isFunctionalName (value: string) : bool =
        if isNull value then
            false
        else
            let normalized = value.Trim().ToLowerInvariant()
            normalized = "title"
            || normalized = "compaction"
            || normalized = "summary"
            || normalized = "explore"
            || normalized = "generate"

    let private unknownAgentSuggestion (name: string) =
        if name.IndexOf("review", StringComparison.OrdinalIgnoreCase) >= 0 then
            " Use 'manager'."
        elif name.IndexOf("manager", StringComparison.OrdinalIgnoreCase) >= 0 then
            " Use 'manager'."
        elif name.IndexOf("engineer", StringComparison.OrdinalIgnoreCase) >= 0 then
            " Use 'engineer'."
        else
            " Use a valid canonical managed agent name."
