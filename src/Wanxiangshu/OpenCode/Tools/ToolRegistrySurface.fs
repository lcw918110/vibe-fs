namespace Wanxiangshu.OpenCode

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Host
open Wanxiangshu.Host.Ports
open Wanxiangshu.Foundation
open Wanxiangshu.Execution.Tool
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Participant.Provider.Attempt

/// JS-native execution-gate surface. The product ToolAdmissionTable owns the
/// role predicate; callers provide labels and receive only a boolean decision.
module ToolRegistrySurface =

    /// Unknown role and unknown tool both fail closed.
    let planSourceWriteAllowed (waitingForUser: bool) =
        ToolRegistry.planSourceWriteAllowed waitingForUser

    let devopsDispatchAllowed (waitingForUser: bool) =
        ToolRegistry.devopsDispatchAllowed waitingForUser

    let rolePredicate (toolName: string) (roleLabel: string) : bool =
        match Roles.tryParseRole roleLabel with
        | None -> false
        | Some role -> ToolAdmissionTable.officeRolePredicate toolName (AgentRole(Roles.roleLabel role))

    /// capability-enforcement-006: which authority the execute gate resolves for a tool. `office`
    /// needs the session's established public Role; `private-attachment` is an
    /// internal leaf admitted by owner-held evidence and never holds an office.
    let deliverDescription () : string =
        Wanxiangshu.Mission.Work.OpenCode.DeliverTool.description ()

    let admissionAuthority (toolName: string) : string =
        match ToolAdmissionTable.tryAdmission toolName with
        | Some(ToolAdmission.OfficeRole _) -> "office"
        | Some(ToolAdmission.PrivateAttachment _) -> "private-attachment"
        | None -> "unknown"

    /// capability-enforcement-006: the internal-leaf decision for a session holding no public
    /// office profile. Office tools always answer false here.
    let privateAttachmentAdmits (toolName: string) (sessionId: string) : bool =
        ToolAdmissionTable.privateAttachmentAdmitsForSession toolName sessionId

    let private requestKindOf (label: string) : ProviderRequestKind option =
        match label.ToLowerInvariant() with
        | "work-main" -> Some ProviderRequestKind.WorkMain
        | "interaction-repair" -> Some ProviderRequestKind.InteractionRepair
        | _ -> None


    /// Provider tool names projected from the same capability set as the gate.
    let capabilityToolNames (roleLabel: string) (requestKindLabel: string) : string array =
        match Roles.tryParseRole roleLabel, requestKindOf requestKindLabel with
        | Some role, Some requestKind ->
            PromptAuthority.toolCapabilitiesFor role requestKind
            |> Set.toList
            |> List.collect StaticTools.toolNames
            |> List.sort
            |> List.toArray
        | _ -> [||]

    let jsPlanDescription (language: string) : string =
        Wanxiangshu.Repository.Programming.Js.JsGeneratorSurface.typedRole "plan" language
        |> Option.map (fun s -> s.Description)
        |> Option.defaultValue ""

    let permissionOfToolName (role: Role) (name: string) : ToolPermission option =
        StaticTools.permissionOfToolName role name
