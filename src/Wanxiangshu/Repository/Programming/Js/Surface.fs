namespace Wanxiangshu.Repository.Programming.Js

open Wanxiangshu.Foundation

/// The generated js-ROLE surface for one Attempt profile. Deterministic:
/// same capabilities → same bytes (JS-002; fast/deep identical).
type JsSurface =
    {
        ToolName: string
        RoleName: string
        Capabilities: Set<JsCapability>
        Members: JsCapabilityFragment list
        Description: string
        BaseClassSource: string
        /// GrandRewrite §6.10: exactly one responsibility-shaped Ultra Example.
        Examples: string list
    }

module JsToolGenerator =

    /// Rebuild host fragments from the library's member names (in the library's
    /// canonical order) so Description still receives typed `JsCapabilityFragment`
    /// values. The `reviewOnly.` prefix is the library's Manager projection.
    let private fragmentsOf (memberNames: string array) : JsCapabilityFragment list =
        let byName =
            JsFragmentRegistry.all
            |> List.map (fun fragment -> fragment.MemberName, fragment)
            |> Map.ofList

        memberNames
        |> Array.toList
        |> List.choose (fun name ->
            let bare =
                if name.StartsWith "reviewOnly." then
                    name.Substring("reviewOnly.".Length)
                else
                    name

            Map.tryFind bare byName
            |> Option.map (fun fragment -> { fragment with MemberName = name }))

    /// The role-independent member directory, read from the library law.
    let membersFor (capabilities: Set<JsCapability>) : JsCapabilityFragment list =
        fragmentsOf (JsCapability.memberNamesFor capabilities "")

    let toolNameFor (roleName: string) : string = JsCapability.toolNameFor roleName

    let private tryProject (roleName: string) (capabilities: Set<ToolPermission>) =
        match JsCapability.projectRole roleName capabilities with
        | None -> None
        | Some(toolName, jsCapabilities, memberNames) -> Some(toolName, jsCapabilities, fragmentsOf memberNames)

    let renderBaseClass (prose: JsCanonicalDescription.Prose) (capabilities: Set<JsCapability>) : string =
        JsCanonicalDescription.runtimeBaseClass prose capabilities

    let renderDescription
        (prose: JsCanonicalDescription.Prose)
        (roleName: string)
        (capabilities: Set<JsCapability>)
        : string =
        JsCanonicalDescription.render prose roleName (toolNameFor roleName) capabilities

    let renderExamples
        (prose: JsCanonicalDescription.Prose)
        (roleName: string)
        (capabilities: Set<JsCapability>)
        : string list =
        JsCanonicalDescription.ultraExample prose roleName capabilities
        |> Option.map (fun example -> [ example.Source ])
        |> Option.defaultValue []

    /// Deterministic projection: an Attempt profile with no filesystem
    /// capability gets no js-* surface at all (JS-001/004).
    let generate
        (roleName: string)
        (capabilities: Set<ToolPermission>)
        (prose: JsCanonicalDescription.Prose)
        : JsSurface option =
        match tryProject roleName capabilities with
        | None -> None
        | Some(toolName, jsCapabilities, members) ->
            Some
                { ToolName = toolName
                  RoleName = roleName
                  Capabilities = jsCapabilities
                  Members = members
                  Description = renderDescription prose roleName jsCapabilities
                  BaseClassSource = JsCanonicalDescription.runtimeBaseClassForRole roleName prose jsCapabilities
                  Examples = renderExamples prose roleName jsCapabilities }

    /// Generated-name gate: a js-* tool call is accepted iff its name is the
    /// surface this profile generates; any other name fails closed (JS-001).
    let isGeneratedToolName (roleName: string) (capabilities: Set<ToolPermission>) (toolName: string) : bool =
        tryProject roleName capabilities
        |> Option.map (fun (name, _, _) -> name = toolName)
        |> Option.defaultValue false

    /// Runtime member gate: a member invocation is accepted iff the member is
    /// present in this profile's surface; the returned binding key names the
    /// exact executor (JS-004 — forged calls have no binding).
    let memberBinding (roleName: string) (capabilities: Set<ToolPermission>) (memberName: string) : string option =
        tryProject roleName capabilities
        |> Option.bind (fun (_, _, members) ->
            members
            |> List.tryFind (fun fragment -> fragment.MemberName = memberName)
            |> Option.map (fun fragment -> fragment.MemberName))
