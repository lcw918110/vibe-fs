namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt

/// JS-native execution-gate surface. ToolRegistry remains the owner of the
/// role predicate; callers provide labels and receive only a boolean decision.
module ToolRegistrySurface =

    /// Unknown role and unknown tool both fail closed.
    let rolePredicate (toolName: string) (roleLabel: string) : bool =
        match Roles.tryParseRole roleLabel with
        | None -> false
        | Some role -> ToolRegistry.rolePredicate toolName "surface-session" role

    /// capability-enforcement-006: which authority the execute gate resolves for a tool. `office`
    /// needs the session's established public Role; `private-attachment` is an
    /// internal leaf admitted by owner-held evidence and never holds an office.
    let deliverDescription () : string =
        Wanxiangshu.Mission.Work.OpenCode.DeliverTool.description ()

    let admissionAuthority (toolName: string) : string =
        match ToolRegistry.tryAdmission toolName with
        | Some(ToolAdmission.OfficeRole _) -> "office"
        | Some(ToolAdmission.PrivateAttachment _) -> "private-attachment"
        | None -> "unknown"

    /// capability-enforcement-006: the internal-leaf decision for a session holding no public
    /// office profile. Office tools always answer false here.
    let privateAttachmentAdmits (toolName: string) (sessionId: string) : bool =
        ToolRegistry.privateAttachmentAdmits toolName sessionId

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

    let runJsPlan
        (gitCommonDir: string)
        (root: string)
        (workId: string)
        (program: string)
        : System.Threading.Tasks.Task<string> =
        let prose =
            Wanxiangshu.Repository.Programming.Js.OpenCode.JsDescriptionAssets.load (
                ProviderLanguageBinding.readGlobalPreference ()
            )

        ToolRegistry.runPlanProgram prose gitCommonDir root workId program

    let jsPlanDescription (language: string) : string =
        let parsed = ProviderLanguage.parse language

        let prose =
            Wanxiangshu.Repository.Programming.Js.OpenCode.JsDescriptionAssets.load parsed

        Wanxiangshu.Repository.Programming.Js.JsCanonicalDescription.renderPlan
            prose
            (ProviderProse.render parsed "tool/js-plan" Map.empty)

    let permissionOfToolName (role: Role) (name: string) : ToolPermission option =
        StaticTools.permissionOfToolName role name
