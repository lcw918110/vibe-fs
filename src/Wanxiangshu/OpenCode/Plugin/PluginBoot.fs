namespace Wanxiangshu.OpenCode

#nowarn "3511"

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Process
open Wanxiangshu.Resources
open Wanxiangshu.Strength.OpenCode

module PluginBoot =

    /// Load-time capabilities captured from raw plugin input. This phase may read
    /// durable state but never repairs old work, mutates the workspace, or calls Host sessions.
    type Boot =
        { Input: obj
          PortOpt: IOpenCodePort option
          Journal: AgentJournal option
          Scope: PluginRuntimeScope
          StrengthScope: PluginStrengthScope
          Clock: IClockPort
          Timer: ITimerPort
          StrengthFailClosed: string -> unit
          WorkspaceDirectory: string option
          FamilyParent: SessionId -> SessionId option
          ProtocolArgumentVault: ProtocolArgumentVault.Vault }

    let create (input: obj) : Task<Boot> =
        task {
            // HOST-026: the global preference is the only language authority;
            // resolve it once at load so the first request already speaks it.
            Wanxiangshu.OpenCode.ProviderLanguageBinding.refreshGlobalLanguage ()

            // Fail-fast resource load before any consumer (StaticTools / BlogTool / EnforcerHost).
            RuntimeResources.install (RuntimeResourceAssembly.load ())

            // execution-model-routing-001: bootstrap/load the sole model scheduler during Load Phase.
            // This may create the missing user config atomically, but performs no Host call.
            do! ModelRouting.initialize ()

            // opencode hands a built-in tool its Effect argument schema and no
            // JSON schema (1.18.32 `src/tool/json-schema.ts`); the readonly
            // delegation protocol renders the provider-visible schema with the
            // same conversion, so resolve that renderer once here.
            do! ToolSchemaJson.initialize ()

            let portOpt = OpenCodePortAdapter.create input
            let workspaceDirectory = PluginHost.workspaceDirectory input

            let! journalResult = PluginHost.createJournal input

            let sharedRuntimeKey = workspaceDirectory |> Option.map RuntimePath.forWorkspace

            let journal =
                match journalResult with
                | Ok value -> value
                | Error err -> raise (InvalidOperationException err)

            let strengthScope =
                match SharedPredictorScope.tryAcquireForRuntime sharedRuntimeKey with
                | Some sharedScope -> sharedScope
                | None -> new PluginStrengthScope(None)

            let isSharedModelLease sessionId =
                strengthScope.StrengthRuntime.IsResidentSession sessionId
                || strengthScope.StrengthRuntime.TryFindResident(sessionId).IsSome

            let scope = new PluginRuntimeScope(journal, isSharedModelLease)

            scope.AttachSessionCleanup(fun sid -> strengthScope.ClearSession sid)
            scope.AttachScopeDispose(fun () -> SharedPredictorScope.release strengthScope)

            // host-boundary-032 / the protocol argument vault
            // records tool.execute.before originals so the provider transform
            // can restore them into persisted history. Process-local only;
            // dropped with the scope.
            let protocolArgumentVault = ProtocolArgumentVault.create ()

            scope.AttachScopeDispose(fun () -> ProtocolArgumentVault.clear protocolArgumentVault)

            let clock = NodeTiming.nodeClockPort ()
            let timer = NodeTiming.nodeTimerPort ()

            let strengthFailClosed (reason: string) : unit =
                strengthScope.TripStrengthFuse reason
                raise (InvalidOperationException reason)

            let familyParent (sessionId: SessionId) =
                match scope.Sessions.SessionParents.TryGetValue(SessionId.value sessionId) with
                | true, parentId -> Some(SessionId.create parentId)
                | false, _ -> None

            return
                { Input = input
                  PortOpt = portOpt
                  Journal = journal
                  Scope = scope
                  StrengthScope = strengthScope
                  Clock = clock
                  Timer = timer
                  StrengthFailClosed = strengthFailClosed
                  WorkspaceDirectory = workspaceDirectory
                  FamilyParent = familyParent
                  ProtocolArgumentVault = protocolArgumentVault }
        }
