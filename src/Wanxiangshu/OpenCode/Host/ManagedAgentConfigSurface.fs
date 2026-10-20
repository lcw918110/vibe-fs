namespace Wanxiangshu.OpenCode

open Wanxiangshu.Participant.Persona
open Wanxiangshu.Resources

/// JS-native Host-config boundary for the managed-agent capability projection.
///
/// ManagedAgentConfig owns validation and writes; this surface only translates
/// its Result and catalog to plain data. Model fields remain in the caller's
/// config object and are never copied into the inventory.
module ManagedAgentConfigSurface =

    /// Host plugin boot normally performs this installation. The explicit
    /// boundary is also useful to a pure config-contract consumer that does
    /// not construct a plugin instance.
    let installDefaultResources () : unit =
        RuntimeResources.install (RuntimeResourceAssembly.load ())

    let private roleBindingNames () =
        ManagedAgent.requiredNames
        |> List.filter (fun name -> not (ManagedAgentCatalog.isBookkeeperName name))
        |> List.toArray

    let private report (outcome: Result<ManagedAgentConfig.ManagedAgentInventory, string>) : obj =
        match outcome with
        | Ok _ ->
            box
                {| ok = true
                   bindingNames = roleBindingNames () |}
        | Error error ->
            box
                {| ok = false
                   error = error
                   bindingNames = roleBindingNames () |}

    /// Validate without crossing the F# Result or Map representation.
    let validate (config: obj) : obj =
        ManagedAgentConfig.validate config |> report

    /// Validate, then project all Wanxiangshu-owned fields onto the live config.
    let configure (config: obj) : obj =
        ManagedAgentConfig.configureFromHostConfig config |> report

    /// Apply the same owned projection as the manager hook. Invalid legacy
    /// names remain a fatal boundary for this JSON consumer.
    let configureManager (config: obj) : obj =
        match ManagedAgentConfig.configureFromHostConfig config with
        | Ok _ ->
            box
                {| ok = true
                   bindingNames = roleBindingNames () |}
        | Error error -> failwith ("managed-agent-config-invalid: " + error)
