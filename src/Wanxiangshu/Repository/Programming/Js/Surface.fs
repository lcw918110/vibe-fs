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
        /// member name → runtime binding key; the runtime gate checks the
        /// invoked member against this exact map (JS-004).
        RuntimeBindings: Map<string, string>
    }

module JsToolGenerator =

    let membersFor (capabilities: Set<JsCapability>) : JsCapabilityFragment list =
        JsFragmentRegistry.all
        |> List.filter (fun fragment ->
            fragment.Capability <> JsCapability.Park
            && Set.contains fragment.Capability capabilities)

    let toolNameFor (roleName: string) : string =
        "todowrite-for-" + roleName.ToLowerInvariant()

    let private tryProject (roleName: string) (capabilities: Set<ToolPermission>) =
        let jsCapabilities = JsCapability.ofToolCapabilities capabilities

        if Set.isEmpty jsCapabilities then
            None
        else
            Some(toolNameFor roleName, jsCapabilities, membersFor jsCapabilities)

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
                  BaseClassSource = renderBaseClass prose jsCapabilities
                  Examples = renderExamples prose roleName jsCapabilities
                  RuntimeBindings =
                    members
                    |> List.map (fun fragment -> fragment.MemberName, fragment.RuntimeBindingKey)
                    |> Map.ofList }

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
            |> Option.map (fun fragment -> fragment.RuntimeBindingKey))

/// Transaction context supporting separation of ReadSnapshots and SubstantiveAccess (repository-programming-026 / T18 / T19).
type JsTransactionContext() =
    let readSnapshots = System.Collections.Generic.HashSet<string>()
    let explicitReads = System.Collections.Generic.HashSet<string>()
    let stagedWrites = System.Collections.Generic.Dictionary<string, string>()
    // DSL-MUTABLE: cancellation — transaction aborted flag
    let mutable isAborted = false
    // DSL-MUTABLE: single-flight — transaction committed flag
    let mutable isCommitted = false

    member _.RecordGrepScan(paths: string seq) : unit =
        for p in paths do
            readSnapshots.Add p |> ignore

    member _.recordGrepScan(paths: string array) : unit =
        for p in paths do
            readSnapshots.Add p |> ignore

    member _.RecordExplicitRead(path: string) : unit =
        readSnapshots.Add path |> ignore
        explicitReads.Add path |> ignore

    member _.recordExplicitRead(path: string) : unit =
        readSnapshots.Add path |> ignore
        explicitReads.Add path |> ignore

    member _.StageWrite(path: string, content: string) : unit = stagedWrites.[path] <- content

    member _.stageWrite(path: string, content: string) : unit = stagedWrites.[path] <- content

    member _.Abort() : unit = isAborted <- true
    member _.abort() : unit = isAborted <- true

    member _.Commit() : unit =
        if not isAborted then
            isCommitted <- true

    member _.commit() : unit =
        if not isAborted then
            isCommitted <- true

    member _.GetReadSnapshots() : string array = readSnapshots |> Seq.toArray
    member _.getReadSnapshots() : string array = readSnapshots |> Seq.toArray
    member _.GetSubstantiveAccess() : string array = explicitReads |> Seq.toArray
    member _.getSubstantiveAccess() : string array = explicitReads |> Seq.toArray

module JsSurfaceExports =
    let createTransactionContext () : JsTransactionContext = JsTransactionContext()
