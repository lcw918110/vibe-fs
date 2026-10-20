namespace Wanxiangshu.Foundation

/// JS-native semantic surface for participant identity. Labels stay
/// canonical wire strings. `manager` and `plan` are the existing Role
/// values, so a caller can pass them without inventing a label.
module RolesSurface =

    let manager: Role = Role.Manager
    let plan: Role = Role.Plan

    let private labelsOf (predicate: Role -> bool) : string array =
        Roles.all
        |> List.filter predicate
        |> List.map Roles.roleLabel
        |> List.sort
        |> List.toArray

    /// The ten canonical role labels, sorted.
    let allRoleLabels: string array = labelsOf (fun _ -> true)

    /// AGENT-008 public fork / horizon vocabulary (no Distiller).
    let allPublicRoleLabels: string array = labelsOf (Roles.isInternal >> not)

    /// Private runtimes: Distiller (map/reduce).
    let allInternalRoleLabels: string array = labelsOf Roles.isInternal
