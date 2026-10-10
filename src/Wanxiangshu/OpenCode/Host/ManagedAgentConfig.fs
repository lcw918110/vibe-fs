namespace Wanxiangshu.OpenCode

open System
open Fable.Core
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Enforcer
open Wanxiangshu.Enforcer.Cycle
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Mission.WorkRecord
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Resources
open Wanxiangshu.Foundation
open Wanxiangshu.Ablation

/// Managed agent Host-config gate. This owns catalog projection and Wanxiangshu-owned
/// non-model fields. Physical model routing is exclusively owned by ModelRouting.
/// Missing catalog names are created on the live Host config; opencode.json is not
/// the inventory, and Host agent.model is never routing truth.
module ManagedAgentConfig =

    type ManagedAgentBinding = { Agent: ManagedAgent }

    type ManagedAgentInventory =
        { Bindings: Map<string, ManagedAgentBinding> }

    type ConfigGateError =
        | MissingHostConfig
        | LegacyAgentPresent of string
        | InvalidManagedAgent of string * detail: string

    let private agentEntry (agents: obj) (name: string) : obj option =
        if isNull agents then
            None
        else
            let value = agents?(name)
            if isNull value then None else Some value

    let private formatError (err: ConfigGateError) : string =
        match err with
        | MissingHostConfig -> "Managed agent projection requires a Host config object."
        | LegacyAgentPresent name -> ManagedAgentCatalog.formatLegacyNameInConfig name
        | InvalidManagedAgent(name, detail) -> sprintf "Invalid managed agent '%s': %s" name detail

    let private rejectLegacy (agents: obj) : Result<unit, ConfigGateError> =
        match
            ManagedAgentCatalog.legacyAgentNames
            |> Seq.tryFind (fun name -> agentEntry agents name |> Option.isSome)
        with
        | Some legacy -> Error(LegacyAgentPresent legacy)
        | None -> Ok()

    let private catalogBinding (name: string) : Result<(string * ManagedAgentBinding) option, ConfigGateError> =
        if ManagedAgentCatalog.isBookkeeperName name then
            Ok None
        else
            ManagedAgent.tryParse name
            |> Result.requireSome (InvalidManagedAgent(name, "failed to parse required name"))
            |> Result.map (fun managed -> Some(name, { Agent = managed }))

    let private catalogBindings () : Result<Map<string, ManagedAgentBinding>, ConfigGateError> =
        ManagedAgent.requiredNames
        |> List.traverseResultM catalogBinding
        |> Result.map (List.choose id >> Map.ofList)

    let private requireHostConfig (config: obj) : Result<unit, ConfigGateError> =
        if isNull config then Error MissingHostConfig else Ok()

    let validate (config: obj) : Result<ManagedAgentInventory, string> =
        result {
            do! requireHostConfig config
            do! rejectLegacy config?agent
            let! bindings = catalogBindings ()
            return { Bindings = bindings }
        }
        |> Result.mapError formatError

    let private assignOwnedFields (entry: obj) (owned: obj) : unit =
        entry?mode <- owned?mode
        entry?permission <- owned?permission

        if not (isNull owned?hidden) then
            entry?hidden <- owned?hidden

        if not (isNull owned?prompt) then
            entry?prompt <- owned?prompt

        if not (isNull owned?temperature) then
            entry?temperature <- owned?temperature

        if not (isNull owned?options) then
            entry?options <- owned?options
        elif isNull entry?options then
            entry?options <- createObj [ "temperature", box 1.0 ]
        else
            entry?options?temperature <- 1.0

    /// Only active roles receive a Host agent entry; retired identities are
    /// never projected into a live provider configuration.
    let private ownedConfigForRole (role: Role) : obj option =
        // provider-language-008: the Host config prompt is LLM-facing Class A
        // text. Project the language the sessions actually run in; a zh-CN
        // preference must never seed an English prompt into the config.
        let language = ProviderLanguageBinding.readGlobalPreference ()
        let prompts = RuntimeResources.promptsFor language

        match role with
        | Role.Manager -> Some(StaticTools.managerAgentConfig (Some prompts.ManagerSystemPrompt))
        | Role.Orchestrator -> Some(StaticTools.orchestratorAgentConfig (Some prompts.OrchestratorSystemPrompt))
        | Role.Engineer -> Some(StaticTools.engineerAgentConfig (Some prompts.EngineerSystemPrompt))
        | Role.DevOps -> Some(StaticTools.devopsAgentConfig (Some prompts.DevopsSystemPrompt))
        | Role.Blogger -> Some(StaticTools.bloggerAgentConfig prompts.BloggerSystemPrompt)
        | Role.Plan -> Some(StaticTools.planAgentConfig None)
        | Role.Coder
        | Role.Inspector
        | Role.Browser
        | Role.Inquiry
        | Role.Distiller -> None

    let private dynamicConfigForName (inventory: ManagedAgentInventory) (name: string) : obj option =
        match Map.tryFind name inventory.Bindings with
        | Some binding -> ownedConfigForRole binding.Agent.Role
        | None ->
            ManagedAgent.tryParse name
            |> Option.bind (fun managed -> ownedConfigForRole managed.Role)

    let private ownedConfigForName (inventory: ManagedAgentInventory) (name: string) : obj option =
        if ManagedAgentCatalog.isBookkeeperName name then
            let language = ProviderLanguageBinding.readGlobalPreference ()
            Some(StaticTools.bookkeeperAgentConfig (PromptResources.loadBookkeeperSystemFor language))
        else
            dynamicConfigForName inventory name

    let private ensureAgents (config: obj) : obj =
        if isNull config?agent then
            let created: obj = createEmpty
            config?agent <- created
            created
        else
            config?agent

    let private ensureAgentEntry (agents: obj) (name: string) : obj =
        match agentEntry agents name with
        | Some entry -> entry
        | None ->
            let created: obj = createEmpty
            agents?(name) <- created
            created

    let private applyNamedOwnedFields (agents: obj) (inventory: ManagedAgentInventory) (name: string) : unit =
        if not (AblationGate.primaryAgentAllowed (AblationGate.registry ()) name) then
            let entry = ensureAgentEntry agents name
            entry?hidden <- true
        elif Option.isSome (ownedConfigForName inventory name) then
            let owned = Option.get (ownedConfigForName inventory name)
            assignOwnedFields (ensureAgentEntry agents name) owned

    let private ensureExperimental (config: obj) : obj =
        if isNull config?experimental then
            let created: obj = createEmpty
            config?experimental <- created
            created
        else
            config?experimental

    let private disableHostChatRetry (config: obj) : unit =
        (ensureExperimental config)?chatMaxRetries <- 0

    let private applyAgentsOwnedFields (config: obj) (agents: obj) (inventory: ManagedAgentInventory) : unit =
        for name in ManagedAgent.requiredNames do
            applyNamedOwnedFields agents inventory name

        config?compaction <- createObj [ "auto" ==> false ]
        disableHostChatRetry config

    /// Project Wanxiangshu-owned non-model fields onto Host agent entries.
    /// Creates missing catalog names on the live config object, never writes/overwrites model.
    /// AGENT-007's first layer is fail-closed: a validation failure elsewhere in the
    /// config must not silently drop every permission write.
    let applyOwnedFields (config: obj) (inventory: ManagedAgentInventory) : unit =
        if isNull config then
            ()
        else
            applyAgentsOwnedFields config (ensureAgents config) inventory

    let configureFromHostConfig (config: obj) : Result<ManagedAgentInventory, string> =
        match validate config with
        | Error err ->
            // AGENT-007 fail-closed: a catalog validation failure must not silently
            // drop every permission/mode/prompt write. Project the catalog anyway.
            applyOwnedFields config { Bindings = catalogBindings () |> Result.defaultValue Map.empty }
            Error err
        | Ok inventory ->
            applyOwnedFields config inventory
            Ok inventory
