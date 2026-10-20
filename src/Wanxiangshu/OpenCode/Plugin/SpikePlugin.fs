namespace Wanxiangshu.OpenCode

#nowarn "3511"

open System.Threading.Tasks
open Wanxiangshu.Composition.Turn

/// Composition root (Wave 3): SpikePlugin only assembles the wiring modules.
/// Every concrete step — resource install, journal, scope, host ports, session
/// runtimes, transforms, hooks — lives in its own module, and
/// PluginBoot keeps the global initialization order authoritative.
module SpikePlugin =

    type internal Runtime =
        { Hooks: obj
          Scope: PluginRuntimeScope }

    let internal createRuntime (input: obj) : Task<Runtime> =
        task {
            try
                let! boot = PluginBoot.create input
                // Turn routing is injected here so the Host-side observer never
                // needs a static reference to the relay turn-workflow module
                // (host -> relay stays one-way: plugin -> workflow -> host types).
                let observeTurnWorkflow =
                    fun sessionPort eventPort rootWorkspace cause (context: ReconciledTurnContext) ->
                        TurnWorkflow.observe
                            sessionPort
                            rootWorkspace
                            eventPort
                            boot.Journal
                            boot.Scope.SyncDelegateRuntime
                            boot.Scope.Sessions.NudgeSent
                            boot.Scope.Sessions.JoinGuardNudges
                            (fun s -> boot.Scope.HasLivePty s)
                            cause
                            boot.Scope.Sessions.Quiescence
                            context

                let! host = PluginHostWiring.create observeTurnWorkflow boot
                PluginSessionWiring.attach boot host
                PluginRecoveryWiring.attach boot
                let transform = PluginTransforms.create boot host
                let! hooks = PluginHooks.create boot host transform
                return { Hooks = hooks; Scope = boot.Scope }
            with ex ->
                // A partially initialized Wanxiangshu instance is not a degraded
                // mode. OpenCode may otherwise keep running after a plugin-load
                // rejection with only half the runtime owners installed.
                Diagnostic.fatal "plugin-initialization-failed" [ "result", ex.Message ]
                return raise ex
        }

    let initSpikePlugin (input: obj) : Task<obj> =
        task {
            let! runtime = createRuntime input
            return runtime.Hooks
        }
