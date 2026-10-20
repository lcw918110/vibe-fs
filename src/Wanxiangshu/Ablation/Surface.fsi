namespace Wanxiangshu.Ablation

/// JS-native semantic surface for ablation registry tests and gates.
///
/// This is the boundary where the Host and its tests reach the ablation layer, so it
/// is also the one place that resolves a registry from the environment. It carries no
/// decisions of its own: every answer comes from `AblationGate` against a registry.
module AblationSurface =
    /// Load from the environment, install the result, and report it as JSON.
    val load: unit -> obj

    /// Drop the installed registry, returning to the environment fallback.
    val resetRegistry: unit -> unit

    /// Install a registry directly, bypassing the environment read.
    ///
    /// This is the seam a test uses to ask about a registry it constructed itself,
    /// which is why no test needs to mutate `resources/ablation/*.json` on disk.
    val useRegistry: registry: obj -> unit

    /// The registry currently installed, as JSON.
    val registry: unit -> obj

    /// The registry explicitly installed, or null when none is.
    val installed: unit -> obj

    /// Load a registry from a manifest document the caller supplies.
    ///
    /// Same validation, same result shape as `load`, minus the file read.
    val loadFromNodes: document: obj -> obj

    /// Build a registry from a manifest document expressed as plain JSON.
    val registryFromNodes: document: obj -> AblationRegistry option

    val modeFor: nodeId: string -> string
    val allowsTool: toolName: string -> bool
    val allowsToolSchema: toolName: string -> bool
    val allowsPrimaryAgent: agentName: string -> bool

    val fissionVisible: unit -> bool
    val toolMapEntry: toolName: string -> string
    val allowsFact: factTag: string -> bool
    val factMapEntry: factTag: string -> string
    val manifestNodeIds: unit -> string array
    val profileIds: unit -> string array
