namespace Wanxiangshu.Participant.Persona

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation

/// JS-native office-capability owner. Role and permission labels are vocabulary
/// at this edge; callers never decode Role unions or F# collections.
[<RequireQualifiedAccess>]
module OfficeCapabilitySurface =

    [<Emit("$0 == null")>]
    let private isNullish (value: obj) : bool = jsNative

    /// office-capability-007 / ARCH-017: the canonical manager fork office consequence set.
    let managerForkableOffices () : string array =
        ManagedAgentCatalog.managerForkableRoles
        |> List.map Roles.roleLabel
        |> List.toArray

    /// Sorted permission labels for a role. Unknown role fails closed; historical
    /// roles hold no office in `wanxiangli/capability` and so return none.
    let permissions (roleLabel: string) : string array =
        match Roles.tryParseRole roleLabel with
        | None -> [||]
        | Some role ->
            OfficeCapability.permissions role
            |> Set.toList
            |> List.map OfficeCapability.permissionLabel
            |> List.sort
            |> List.toArray

    /// Unknown role or permission is denied.
    let isAllowed (roleLabel: string) (permissionLabel: string) : bool =
        match Roles.tryParseRole roleLabel, OfficeCapability.permissionOfLabel permissionLabel with
        | Some role, Some permission when Roles.all |> List.contains role -> OfficeCapability.isAllowed role permission
        | _ -> false

    /// The canonical permission labels, in catalog order, derived from the one
    /// label function. This list is the single authority: no consumer keeps
    /// its own copy, because a hand-copied table is a thing to rot. The order
    /// is the ToolPermission declaration order.
    let permissionLabels () : string array =
        [ ToolPermission.Fork
          ToolPermission.Resume
          ToolPermission.Join
          ToolPermission.Horizon
          ToolPermission.Fission
          ToolPermission.Read
          ToolPermission.Write
          ToolPermission.Edit
          ToolPermission.Glob
          ToolPermission.Grep
          ToolPermission.Move
          ToolPermission.Remove
          ToolPermission.Exec
          ToolPermission.Pty
          ToolPermission.ReviewAssessment
          ToolPermission.Fetch
          ToolPermission.Finality
          ToolPermission.BashHoneypot
          ToolPermission.JsPlan
          ToolPermission.Ask
          ToolPermission.Deliver
          ToolPermission.Review
          ToolPermission.DevOpsConsultation
          ToolPermission.ResetScope ]
        |> List.map OfficeCapability.permissionLabel
        |> List.toArray

    /// The canonical label for a permission label, or None when the label is
    /// not a permission at all. A caller can therefore tell "unknown label"
    /// apart from "known label that projects no member".
    let permissionOfLabel (label: string) : string option =
        match OfficeCapability.permissionOfLabel label with
        | Some permission -> Some(OfficeCapability.permissionLabel permission)
        | None -> None

    /// capability-enforcement-025: manager gate facts as a plain JS object. The
    /// caller declares the four facts and never sees the F# record.
    let managerFacts
        (hasActiveIncumbency: bool)
        (hasAssessment: bool)
        (isFinalIncumbent: bool)
        (cleanupBlockerDigest: string)
        : obj =
        box
            {| hasActiveIncumbency = hasActiveIncumbency
               hasAssessment = hasAssessment
               isFinalIncumbent = isFinalIncumbent
               cleanupBlockerDigest = cleanupBlockerDigest |}

    let private factsOfJs (facts: obj) : ManagerCapabilityFacts option =
        if isNullish facts then
            None
        else
            let digest: string = facts?cleanupBlockerDigest

            Some
                { HasActiveIncumbency = unbox<bool> (facts?hasActiveIncumbency)
                  HasAssessment = unbox<bool> (facts?hasAssessment)
                  IsFinalIncumbent = unbox<bool> (facts?isFinalIncumbent)
                  CleanupBlockerDigest = if isNullish digest then None else Some digest }

    /// capability-enforcement-025: the fact-driven manager gate. Missing facts and
    /// unknown permission labels both fail closed.
    let managerFactsAllowed (facts: obj) (permissionLabel: string) : bool =
        match factsOfJs facts, OfficeCapability.permissionOfLabel permissionLabel with
        | Some facts, Some permission -> OfficeCapability.isAllowedForManagerFacts facts permission
        | _ -> false
