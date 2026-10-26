namespace Wanxiangshu.Repository.Programming.Js

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Participant.Provider

/// JS-native generator boundary. Role and permission labels are vocabulary at
/// the JS edge; typedRole exposes the typed JsSurface to real F# composition
/// consumers, while typedFor stays inside the owner.
[<RequireQualifiedAccess>]
module JsGeneratorSurface =

    [<Emit("undefined")>]
    let private jsUndefined: obj = jsNative

    let private capabilityLabel capability =
        match capability with
        | JsCapability.Read -> "Read"
        | JsCapability.Write -> "Write"

        | JsCapability.Glob -> "Glob"
        | JsCapability.Grep -> "Grep"
        | JsCapability.Move -> "Move"
        | JsCapability.Remove -> "Remove"
        | JsCapability.Exec -> "Exec"
        | JsCapability.Fork -> "Fork"
        | JsCapability.Resume -> "Resume"
        | JsCapability.Commission -> "Commission"
        | JsCapability.DevOps -> "DevOps"
        | JsCapability.Fission -> "Fission"
        | JsCapability.Horizon -> "Horizon"
        | JsCapability.Ask -> "Ask"
        | JsCapability.Review -> "Review"
        | JsCapability.Park -> "Park"

    let private languageOf (value: string) = ProviderLanguage.parse value

    let private roleExists (role: string) =
        Roles.tryParseRole role |> Option.isSome

    let private permissionsOfLabels (labels: string array) =
        labels
        |> Array.choose (fun label -> OfficeCapability.permissionOfLabel label)
        |> Set.ofArray

    let private canonicalLabels (role: string) =
        match
            Roles.tryParseRole role
            |> Option.bind (fun r ->
                if Roles.all |> List.contains r then
                    Some r
                else
                    Some Role.Engineer)
        with
        | None -> [||]
        | Some parsed ->
            OfficeCapability.permissions parsed
            |> Set.toArray
            |> Array.map OfficeCapability.permissionLabel

    let internal typedFor (role: string) (labels: string array) (language: string) : JsSurface option =
        if not (roleExists role) then
            None
        else
            let prose = JsDescriptionAssets.load (languageOf language)
            JsToolGenerator.generate role (permissionsOfLabels labels) prose

    let typedRole (role: string) (language: string) : JsSurface option =
        typedFor role (canonicalLabels role) language

    let private fragmentToJs fragment =
        box
            {| capability = capabilityLabel fragment.Capability
               memberName = fragment.MemberName |}

    let private surfaceToJs (surface: JsSurface) : obj =
        box
            {| toolName = surface.ToolName
               roleName = surface.RoleName
               capabilities = surface.Capabilities |> Set.toArray |> Array.map capabilityLabel
               members = surface.Members |> List.toArray |> Array.map fragmentToJs
               description = surface.Description
               baseClassSource = surface.BaseClassSource
               examples = surface.Examples |> List.toArray |}

    /// Generate one JS SDK surface from plain role/permission labels.
    /// Unknown roles and capability-free roles return `null`.
    let generate (role: string) (permissionLabels: string array) (language: string) : obj =
        typedFor role permissionLabels language
        |> Option.map surfaceToJs
        |> Option.toObj

    /// Generate from the canonical role permission projection.
    let generateRole (role: string) (language: string) : obj =
        typedRole role language |> Option.map surfaceToJs |> Option.toObj

    let generateSurfaceForRole (role: string) (permissionLabels: string array) : obj =
        generate role permissionLabels "en"

    let isGeneratedToolName (role: string) (permissionLabels: string array) (toolName: string) : bool =
        if not (roleExists role) then
            false
        else
            JsToolGenerator.isGeneratedToolName role (permissionsOfLabels permissionLabels) toolName

    let memberBinding (role: string) (permissionLabels: string array) (memberName: string) : obj =
        if not (roleExists role) then
            jsUndefined
        else
            match JsToolGenerator.memberBinding role (permissionsOfLabels permissionLabels) memberName with
            | Some binding -> box binding
            | None -> jsUndefined

    let permissionLabels (role: string) : string array = canonicalLabels role
