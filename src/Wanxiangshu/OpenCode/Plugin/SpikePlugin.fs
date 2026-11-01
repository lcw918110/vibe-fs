namespace Wanxiangshu.OpenCode

#nowarn "3511"

open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Composition

/// Composition root (Wave 3): SpikePlugin only assembles the wiring modules.
/// Every concrete step — resource install, journal, scope, host ports, session
/// runtimes, transforms, hooks — lives in its own module, and
/// PluginBoot keeps the global initialization order authoritative.
module SpikePlugin =

    type internal Runtime =
        { Hooks: obj
          Scope: PluginRuntimeScope }

    let private unpackActivation
        (result: Result<Wanxiangshu.Host.Ports.ProductHooks, Wanxiangshu.Host.Ports.HostError>)
        : Wanxiangshu.Host.Ports.ProductHooks =
        match result with
        | Ok hooks -> hooks
        | Error error -> failwithf "ProductHostPlugin.Activate failed during plugin startup: %A" error

    /// The host's config object, when the plugin input carries one. The host
    /// normally delivers it later through the `config` hook; the holder is
    /// seeded with it so the product never reads a stale snapshot.
    let private initialConfigOf (input: obj) : obj =
        if isNull input then null
        elif isNull input?config then null
        else input?config

    let internal createRuntime (input: obj) : Task<Runtime> =
        task {
            try
                let! boot = PluginBoot.create input
                // Turn routing is injected here so the Host-side observer never
                // needs a static reference to the relay turn-workflow module
                // (host -> relay stays one-way: plugin -> workflow -> host types).
                let observeTurnWorkflow =
                    fun sessionPort eventPort rootWorkspace (context: Turn.ReconciledTurnContext) ->
                        Turn.TurnWorkflow.observe
                            sessionPort
                            rootWorkspace
                            eventPort
                            boot.Journal
                            boot.Scope.SyncDelegateRuntime
                            boot.Scope.Sessions.NudgeSent
                            boot.Scope.Sessions.JoinGuardNudges
                            (fun s -> boot.Scope.HasLivePty s)
                            boot.Scope.Sessions.Quiescence
                            context


                let! host = PluginHostWiring.create observeTurnWorkflow boot
                PluginSessionWiring.attach boot host
                PluginRecoveryWiring.attach boot
                let liveConfig = LiveHostConfig(initialConfigOf boot.Input)
                let! ports = OpenCodeHostPorts.create boot host liveConfig
                let productPlugin: Wanxiangshu.Host.Ports.IHostPlugin = Plugin.ProductHostPlugin()
                let! activationResult = productPlugin.Activate ports
                let productHooks = unpackActivation activationResult
                let transform = PluginTransformComposition.create boot host
                let! hooks = PluginHooks.create boot host transform productHooks liveConfig
                return { Hooks = hooks; Scope = boot.Scope }
            with ex ->
                // A partially initialized Wanxiangshu instance is not a degraded
                // mode. OpenCode may otherwise keep running after a plugin-load
                // rejection with only half the runtime owners installed.
                Wanxiangshu.Foundation.Diagnostic.fatal "plugin-initialization-failed" [ "result", ex.Message ]
                return raise ex
        }

    let initSpikePlugin (input: obj) : Task<obj> =
        task {
            let! runtime = createRuntime input
            return runtime.Hooks
        }
