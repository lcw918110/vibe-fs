namespace Wanxiangshu.Ablation

open System.Collections.Generic

/// Ablation decisions, made against a registry the caller supplies.
///
/// Every function here is pure: same registry in, same answer out. The registry is
/// resolved once by the caller — `Settings` for the Host edge, a test fixture for its
/// own scenario — and threaded down, so no decision function reads the environment
/// itself.
[<RequireQualifiedAccess>]
module AblationGate =

    /// The registry this process answers ablation questions against.
    ///
    /// A cell, not a memo: the Host boundary installs the resolved registry once, and
    /// everything downstream reads a value instead of deriving one. Because it is a
    /// plain reference the caller controls, a test installs its own registry without
    /// reaching into the environment — which is why there is no cache key to get
    /// wrong, and no way for two callers to disagree about which profile is active.
    // DSL-MUTABLE: resource — the one pointer to the active ablation registry.
    let mutable private registryCell: AblationRegistry option = None

    /// Install the registry every later `registry` call answers from.
    ///
    /// A caller that holds its own registry never needs this: it passes the registry
    /// to the decision function directly.
    let useRegistry (registry: AblationRegistry) = registryCell <- Some registry

    /// The registry decisions are made against when the caller supplies none.
    ///
    /// Falls back to the environment when nothing has been installed, so a Host
    /// boundary that never calls `useRegistry` still behaves as before. The fallback
    /// is deliberately not cached: an environment change is visible on the next call,
    /// which is what keeps this a value rather than a memo.
    let registry () : AblationRegistry =
        match registryCell with
        | Some installed -> installed
        | None ->
            AblationSettings.load ()
            |> Result.defaultWith (fun _ -> AblationResolution.denied ())

    /// Drop whatever registry is installed, returning to the environment fallback.
    let resetRegistry () = registryCell <- None

    /// The registry explicitly installed, without consulting the environment.
    ///
    /// `registry ()` answers a decision question and falls back; this answers a
    /// different one — "did anyone install anything" — and must not fall back.
    let installedRegistry () : AblationRegistry option = registryCell

    let deniedPath = "tool/registry/denied-ablation"

    /// A node is denied only when the registry ablates it; active and borrowed both
    /// stay reachable. Unmapped tags are denied by nobody.
    let private nodeDenied (registry: AblationRegistry) (node: AblationNodeId) =
        AblationRegistry.modeFor node registry = AblationMode.Ablated

    /// A tool is denied when its node is ablated.
    ///
    /// A tool owned by no node is denied by nobody: ablation governs what the manifest
    /// declares, not what it omits.
    let toolDenied (registry: AblationRegistry) (toolName: string) =
        let targetNode = AblationToolMap.tryNode toolName

        // Borrowed still runs: the surface it may borrow is decided by the caller's
        // manifest, and a borrowed tool is visible inside that surface. A tool owned
        // by no node is denied by nobody.
        match targetNode with
        | None -> false
        | Some node -> nodeDenied registry node

    /// A tool's schema is denied when the registry does not expose it at all.
    ///
    /// Schema visibility is stricter than execution: an ablated tool disappears from
    /// the provider's tool list, not merely fails when called.
    let toolSchemaDenied (registry: AblationRegistry) (toolName: string) =
        let targetNode = AblationToolMap.tryNode toolName

        match targetNode with
        | None -> false
        | Some node -> not (AblationRegistry.modeFor node registry |> AblationMode.allowsToolSchema)

    let filterToolPermissionMap (registry: AblationRegistry) (permissions: Map<string, bool>) =
        permissions
        |> Map.map (fun name allowed -> allowed && not (toolSchemaDenied registry name))

    let filterKnownToolNames (registry: AblationRegistry) (names: string list) =
        names |> List.filter (fun name -> not (toolSchemaDenied registry name))

    let deniedFactPath = "journal/denied-ablation"

    let factDenied (registry: AblationRegistry) (factTag: string) =
        match AblationFactMap.tryNode factTag with
        | None -> false
        | Some node -> nodeDenied registry node

    /// Which primary agents the registry exposes.
    ///
    /// `browser` is retired outright rather than ablated: no manifest node governs it,
    /// so the honest answer is a fixed false instead of a lookup that can never match.
    let primaryAgentAllowed (registry: AblationRegistry) (agentName: string) : bool =
        match agentName.ToLowerInvariant() with
        | "manager" -> AblationRegistry.isActive (AblationNodeId.create "work") registry
        | "orchestrator" -> AblationRegistry.isActive (AblationNodeId.create "change-integration") registry
        | "inquiry" -> AblationRegistry.isActive (AblationNodeId.create "epistemic-reasoning") registry
        | "browser" -> false
        | _ -> true

    let fissionVisible (registry: AblationRegistry) : bool =
        AblationRegistry.isActive (AblationNodeId.create "intra-participant-parallelism") registry

    /// Mode of one node, for callers that need the raw value rather than a yes/no.
    let modeFor (registry: AblationRegistry) (nodeId: string) : AblationMode =
        AblationRegistry.modeFor (AblationNodeId.create nodeId) registry
