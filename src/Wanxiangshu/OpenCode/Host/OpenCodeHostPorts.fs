namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Host.Ports
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.ManagedAgents

module OpenCodeHostPorts =

    type private OpenCodePromptPort(sessionHostPort: ISessionHostPort) =

        let deliveryVerdictOf (verdict: DetachedSendVerdict) : DeliveryVerdict =
            match verdict with
            | DetachedSendVerdict.OwnedSettled -> DeliveryVerdict.OwnedSettled
            | DetachedSendVerdict.Refused reason -> DeliveryVerdict.Refused reason
            | DetachedSendVerdict.OutcomeUnknown reason -> DeliveryVerdict.OutcomeUnknown reason

        let lateListenerOf (lateOpt: LateDeliveryListener option) : DetachedSendListener option =
            lateOpt
            |> Option.map (fun listener -> fun (verdict: DetachedSendVerdict) -> listener (deliveryVerdictOf verdict))

        let modelOf (routeOpt: ModelRoute option) : HostModel option =
            routeOpt
            |> Option.map (fun r ->
                { providerID = r.Provider
                  modelID = r.Model
                  variant = r.Variant })

        let metadataOf (correlationOpt: PromptKey option) : obj option =
            correlationOpt
            |> Option.map (fun key -> PromptMetadataCodec.create key "hni-prompt-port" None)

        interface IPromptPort with
            member this.Send
                (session: SessionId, request: PromptRequest, late: LateDeliveryListener option)
                : Task<SendOutcome> =
                let opts: SessionPromptOptions =
                    { Model = modelOf request.Route
                      Agent = request.Role |> Option.map (fun (AgentRole s) -> s)
                      Directory = request.Workspace |> Option.map (fun (WorkspacePath p) -> p)
                      Metadata = metadataOf request.Correlation
                      Delivery = request.Delivery
                      Tools = request.ToolPolicy
                      DetachedListener = lateListenerOf late }

                sessionHostPort.SendPrompt(session, request.Text, opts)

    type private DiagnosticSinkPort() =
        interface IDiagnosticSink with
            member this.Emit(operation: string, fields: (string * string) list) : unit =
                Diagnostic.emit operation fields

    type private UnavailableSessionPort() =
        interface ISessionPort with
            member this.Create(_parent: SessionId option, _spec: SessionSpec) : HostResult<SessionId> =
                Task.FromResult(Error(HostError.Transport "no OpenCode session transport on host input"))

            member this.Parent(_session: SessionId) : HostResult<SessionId option> =
                Task.FromResult(Error(HostError.Transport "no OpenCode session transport on host input"))

            member this.Children(_parent: SessionId) : HostResult<SessionInfo list> =
                Task.FromResult(Error(HostError.Transport "no OpenCode session transport on host input"))

            member this.Abort(_session: SessionId) : HostResult<unit> =
                Task.FromResult(Error(HostError.Transport "no OpenCode session transport on host input"))

            member this.Interrupt(_session: SessionId) : HostResult<unit> =
                Task.FromResult(Error(HostError.Transport "no OpenCode session transport on host input"))

            member this.Close(_session: SessionId) : HostResult<unit> =
                Task.FromResult(Error(HostError.Transport "no OpenCode session transport on host input"))

            member this.FindByTitle(_title: string) : HostResult<SessionId list> =
                Task.FromResult(Error(HostError.Transport "no OpenCode session transport on host input"))

    type private UnavailableTranscriptPort() =
        interface ITranscriptPort with
            member this.Messages(_session: SessionId) : HostResult<TranscriptMessage list> =
                Task.FromResult(Error(HostError.Transport "no OpenCode transcript snapshot port on host input"))

    let private resolveTranscriptPort (boot: PluginBoot.Boot) (host: PluginHostWiring.Host) : ITranscriptPort =
        host.SnapshotOpt
        |> Option.orElse boot.Transport.Snapshot
        |> Option.map (fun snapshotPort -> TranscriptPort.HostTranscriptPort(snapshotPort) :> ITranscriptPort)
        |> Option.defaultWith (fun () -> UnavailableTranscriptPort() :> ITranscriptPort)

    let private resolveSessionPort (boot: PluginBoot.Boot) : ISessionPort =
        boot.Transport.SessionCatalog
        |> Option.defaultWith (fun () -> UnavailableSessionPort() :> ISessionPort)

    type private UnsupportedModelCatalog() =
        interface IModelCatalog with
            member this.Available() : HostResult<ModelRoute list> =
                Task.FromResult(Error(HostError.Unsupported HostCapability.ModelCatalog))

            member this.Apply(_session: SessionId, _route: ModelRoute) : HostResult<unit> =
                Task.FromResult(Error(HostError.Unsupported HostCapability.ModelCatalog))

    let private sharedSignalSource = Events.HostSignalSource()

    /// Broadcast a neutral host signal to all active subscribers.
    let publishSignal (signal: HostSignal) : Task = sharedSignalSource.Publish signal

    type private UnsupportedToolRegistrar() =
        interface IToolRegistrar with
            member this.Register(_tools: ToolSpec list) : Result<unit, HostError> =
                Error(HostError.Unsupported HostCapability.ToolRegistration)

    type private OpenCodeAgentCatalog(input: obj) =
        interface IAgentCatalog with
            member this.Roles() : HostResult<AgentRoleSpec list> =
                task {
                    let configAgents =
                        if isNull input || isNull input?config || isNull input?config?agent then
                            null
                        else
                            input?config?agent

                    let specs =
                        ManagedAgent.requiredNames
                        |> List.map (fun name ->
                            let desc =
                                if
                                    not (isNull configAgents)
                                    && not (isNull configAgents?(name))
                                    && not (isNull configAgents?(name)?description)
                                then
                                    Some(string configAgents?(name)?description)
                                else
                                    None

                            { Role = AgentRole name
                              Description = desc
                              Route = None
                              ToolPolicy = None })

                    return Ok specs
                }

    let create (boot: PluginBoot.Boot) (host: PluginHostWiring.Host) (liveConfig: LiveHostConfig) : Task<HostPorts> =
        task {
            let projectRootStr = boot.WorkspaceDirectory |> Option.defaultValue "."

            // HNI P-10: the neutral `IHostPaths` resolved once at boot is the
            // single authority for the state root, the project root and the
            // project identity. Only a host that names no workspace at all
            // falls back to the runtime's own roots.
            let! pathsPort =
                task {
                    match boot.Paths with
                    | Some paths -> return paths
                    | None ->
                        // The state root is the runtime's own state directory
                        // (`.Herdchestra` under the primary worktree), not the
                        // project directory.
                        let! stateRootStr = Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot projectRootStr

                        return
                            PathsPort.HostPaths(
                                (fun () -> stateRootStr),
                                projectRootStr,
                                PathsPort.projectIdOf boot.Input projectRootStr
                            )
                            :> IHostPaths
                }

            let sessionPort = resolveSessionPort boot

            let transcriptPort = resolveTranscriptPort boot host

            let promptPort = OpenCodePromptPort(host.SessionPort) :> IPromptPort
            let configPort = liveConfig :> IHostConfigPort

            let diagnosticsPort =
                DiagnosticsPort.create boot.Input
                |> Option.defaultWith (fun () -> DiagnosticSinkPort() :> IDiagnosticSink)

            // HNI P-08: the neutral model catalog is wired on OpenCode. The
            // adapter enumerates the routes the host advertises; an input that
            // names no catalog source falls back to a declared refusal, never
            // to an empty catalog that would look like a host without models.
            let modelsOpt = ModelCatalogPort.create boot.WorkspaceDirectory boot.Input

            let modelsPort =
                modelsOpt
                |> Option.defaultWith (fun () -> UnsupportedModelCatalog() :> IModelCatalog)

            let signalsPort = sharedSignalSource :> ISignalSource
            let toolsPort = UnsupportedToolRegistrar() :> IToolRegistrar
            let agentsPort = OpenCodeAgentCatalog(boot.Input) :> IAgentCatalog

            let capabilities: HostCapabilities =
                { HostName = "OpenCode"
                  Supports =
                    set (
                        [ HostCapability.ChildSessions
                          HostCapability.SessionDiscovery
                          HostCapability.Interrupt
                          HostCapability.SignalSubscription
                          HostCapability.AgentCatalog
                          HostCapability.ToolDefinitionEdit ]
                        // The catalog capability is claimed only when the
                        // catalog is really wired; an unclaimed capability
                        // must answer `HostError.Unsupported`.
                        @ (if modelsOpt.IsSome then
                               [ HostCapability.ModelCatalog ]
                           else
                               [])
                    ) }

            return
                { Sessions = sessionPort
                  Prompts = promptPort
                  Transcript = transcriptPort
                  Models = modelsPort
                  Signals = signalsPort
                  Tools = toolsPort
                  Paths = pathsPort
                  Config = configPort
                  Diagnostics = diagnosticsPort
                  Agents = agentsPort
                  Capabilities = capabilities }
        }
