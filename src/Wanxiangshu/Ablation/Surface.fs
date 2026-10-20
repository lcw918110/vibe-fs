namespace Wanxiangshu.Ablation

open Fable.Core.JsInterop

/// JS-native semantic surface for ablation registry tests and gates.
///
/// This is the boundary where the Host and its tests reach the ablation layer, so it
/// is also the one place that resolves a registry from the environment. It carries no
/// decisions of its own: every answer comes from `AblationGate` against a registry.
module AblationSurface =

    let private registryToJs (registry: AblationRegistry) : obj =
        let pairs =
            registry.Modes
            |> Map.toList
            |> List.map (fun (AblationNodeId id, mode) -> (id, AblationMode.wire mode))
            |> List.toArray

        let modesObj = emitJsExpr pairs "Object.fromEntries($0)"

        box
            {| ok = true
               profile = registry.Audit.Profile |> Option.toObj
               manifestVersion = registry.Audit.ManifestVersion
               manifestFingerprint = registry.Audit.ManifestFingerprint
               nodeCount = registry.Audit.NodeCount
               modes = modesObj |}

    let private errorToJs (error: AblationLoadError) : obj =
        let kind, detail =
            match error with
            | MissingManifest path -> "MissingManifest", path
            | InvalidManifest reason -> "InvalidManifest", reason
            | UnknownProfile name -> "UnknownProfile", name
            | InvalidMode(node, raw) -> "InvalidMode", $"{node}={raw}"
            | DagViolation reason -> "DagViolation", reason

        box
            {| ok = false
               kind = kind
               error = detail |}

    /// Load from the environment, install the result, and report it as JSON.
    ///
    /// Installing is what makes later `allowsTool` answers agree with this load: the
    /// alternative — re-reading on every call — is what let two callers disagree
    /// about which profile was active.
    let load () =
        match AblationSettings.load () with
        | Ok registry ->
            AblationGate.useRegistry registry
            registryToJs registry
        | Error(InvalidMode(node, raw)) -> invalidOp $"InvalidMode: {node}={raw}"
        | Error error -> errorToJs error

    /// Drop the installed registry, returning to the environment fallback.
    let resetRegistry () : unit = AblationGate.resetRegistry ()

    /// Install a registry directly, bypassing the environment read.
    ///
    /// This is the seam a test uses to ask about a profile it constructed itself,
    /// which is why no test needs to mutate `resources/ablation/*.json` on disk.
    let useRegistry (registry: obj) : unit =
        AblationGate.useRegistry (unbox<AblationRegistry> registry)

    /// Load a registry from a manifest document the caller supplies.
    ///
    /// Same validation, same result shape as `load`, minus the file read. A caller
    /// uses this to prove a rejection — a missing primary node, a cycle — without
    /// rewriting the shared manifest and racing whoever else is reading it.
    let loadFromNodes (document: obj) =
        match AblationManifest.decodeFromJs document with
        | Error error -> errorToJs error
        | Ok parsed ->
            match AblationManifest.registryOf parsed None Map.empty with
            | Error error -> errorToJs error
            | Ok registry -> registryToJs registry

    /// Build a registry from a manifest document expressed as plain JSON.
    ///
    /// The document is decoded with the same decoder the file reader uses, so a caller
    /// cannot accidentally construct a registry the loader would have rejected. The
    /// result is the registry itself, so a caller that only wants to decide does not
    /// have to install it.
    let registryFromNodes (document: obj) : AblationRegistry option =
        match AblationManifest.decodeFromJs document with
        | Ok parsed ->
            AblationManifest.registryOf parsed None Map.empty
            |> Result.map Some
            |> Result.defaultWith (fun _ -> None)
        | Error _ -> None

    /// The registry currently installed, as JSON.
    let registry () = registryToJs (AblationGate.registry ())

    /// The registry explicitly installed by `useRegistry`, or null when none is.
    ///
    /// Distinct from `registry ()`, which falls back to the environment: a caller that
    /// wants to know whether anything was installed — a test proving that a rejected
    /// document left nothing behind — must not be told about the fallback.
    let installed () : obj =
        match AblationGate.installedRegistry () with
        | Some installed -> registryToJs installed
        | None -> null

    let modeFor (nodeId: string) : string =
        AblationGate.modeFor (AblationGate.registry ()) nodeId |> AblationMode.wire

    let allowsTool (toolName: string) : bool =
        not (AblationGate.toolDenied (AblationGate.registry ()) toolName)

    let allowsToolSchema (toolName: string) : bool =
        not (AblationGate.toolSchemaDenied (AblationGate.registry ()) toolName)

    let allowsPrimaryAgent (agentName: string) : bool =
        AblationGate.primaryAgentAllowed (AblationGate.registry ()) agentName

    let fissionVisible () : bool =
        AblationGate.fissionVisible (AblationGate.registry ())

    let toolMapEntry (toolName: string) =
        match AblationToolMap.tryNode toolName with
        | None -> null
        | Some(AblationNodeId id) -> id

    let allowsFact (factTag: string) : bool =
        not (AblationGate.factDenied (AblationGate.registry ()) factTag)

    let factMapEntry (factTag: string) =
        match AblationFactMap.tryNode factTag with
        | None -> null
        | Some(AblationNodeId id) -> id

    let manifestNodeIds () : string array =
        match AblationManifest.loadNodes () with
        | Ok document -> document.Nodes |> List.map (fun node -> node.Id) |> List.toArray
        | Error _ -> [||]

    let profileIds () : string array =
        match AblationManifest.loadProfiles () with
        | Ok document -> document.Profiles |> Map.keys |> Seq.toArray
        | Error _ -> [||]
