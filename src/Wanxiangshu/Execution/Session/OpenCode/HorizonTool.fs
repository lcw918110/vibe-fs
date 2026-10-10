namespace Wanxiangshu.Execution.Session.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal

/// horizon() — natural-language roster of who remains at the caller's horizon.
module HorizonTool =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        let Description = "tool/horizon/description"

        [<Literal>]
        let Returned = "tool/horizon/returned"

        [<Literal>]
        let StillAway = "tool/horizon/still-away"

        [<Literal>]
        let DidNotReturn = "tool/horizon/did-not-return"

        [<Literal>]
        let RemainsOpen = "tool/horizon/remains-open"

        [<Literal>]
        let Someone = "tool/horizon/someone"

        [<Literal>]
        let TerminalLabel = "tool/horizon/terminal-label"

        [<Literal>]
        let EmptyRoster = "tool/horizon/empty-roster"

        [<Literal>]
        let UnavailableFromContext = "tool/horizon/unavailable-from-context"

        [<Literal>]
        let CannotBeSeen = "tool/horizon/cannot-be-seen"

        [<Literal>]
        let LatestWork = "tool/horizon/latest-work"

        [<Literal>]
        let NoWorkYet = "tool/horizon/no-work-yet"

        [<Literal>]
        let LatestWorkUnavailable = "tool/horizon/latest-work-unavailable"

    type HorizonRuntimeContext =
        { RuntimeFor: HostToolContext -> Result<HostForkRuntime, string>
          LogicalOwnerFor: SessionId -> SessionId
          Journal: AgentJournal option
          EnsureRoadDevOpsBound: SessionId -> Task<unit> }

    let private lang (ctx: HostToolContext) =
        ProviderLanguageBinding.forSessionText ctx.SessionId

    let private labeled language path label =
        ProviderProse.render language path (Map [ "label", label ])

    let private labelForHandle language (handle: HandleRecord) =
        if not (String.IsNullOrWhiteSpace handle.Byname) then
            handle.Byname.Trim()
        elif String.IsNullOrWhiteSpace handle.TargetAgent then
            ProviderProse.render language Path.Someone Map.empty
        else
            ManagedAgent.tryParse handle.TargetAgent
            |> Option.map (fun managed -> managed.Name)
            |> Option.defaultValue (handle.TargetAgent.Trim())

    let private activeHandleLine language label runtimeRecord =
        match runtimeRecord with
        | Some record when record.CompletionCellSettled -> labeled language Path.Returned label
        | _ -> labeled language Path.StillAway label

    let private lineForHandle language (handle: HandleRecord) runtimeRecord : string =
        let label = labelForHandle language handle

        match handle.Lifecycle with
        | HandleLifecycle.CompletedAwaitingJoin _ -> labeled language Path.Returned label
        | HandleLifecycle.Active -> activeHandleLine language label runtimeRecord
        | HandleLifecycle.Abandoned _
        | HandleLifecycle.Retired -> labeled language Path.DidNotReturn label

    let private workRecordText language (journal: AgentJournal) (label: string) frame : Task<string> =
        task {
            match! journal.Writer.BlobWriter.Read frame.TextRef with
            | Ok text when HostDigest.sha256Hex text = BlobDigest.value frame.Digest ->
                return ProviderProse.render language Path.LatestWork (Map [ "label", label; "record", text ])
            | _ -> return labeled language Path.LatestWorkUnavailable label
        }

    let private workRecordForHandle
        language
        (journal: AgentJournal)
        (snapshot: ProjectionSet)
        (handle: HandleRecord)
        : Task<string> =
        let label = labelForHandle language handle

        let latestFrame =
            AgentProjection.tryFind handle.ChildSessionId snapshot.AgentProjections
            |> Option.bind (fun session -> session.Blog)
            |> Option.bind (BlogProjection.frames >> List.tryLast)

        match latestFrame with
        | None -> Task.FromResult(labeled language Path.NoWorkYet label)
        | Some frame -> workRecordText language journal label frame

    let private lineForPty language (record: PtyRecord) : string =
        let label =
            if String.IsNullOrWhiteSpace record.Command then
                ProviderProse.render language Path.TerminalLabel Map.empty
            else
                record.Command.Trim()

        labeled language Path.RemainsOpen label

    let private unavailable language path =
        ToolHostCodec.tomlObjectWithInstructions [ ProviderProse.render language path Map.empty ] []

    let private appendHandleLines language journal snapshot runtimeByAgentId (agentLines: ResizeArray<string>) handle =
        task {
            match HandleId.tryAgent handle.Handle with
            | None -> ()
            | Some handleId ->
                agentLines.Add(
                    lineForHandle language handle (Map.tryFind (AgentHandleId.value handleId) runtimeByAgentId)
                )

                let! workRecord = workRecordForHandle language journal snapshot handle
                agentLines.Add workRecord
        }

    let private rosterInstructions language (lines: string list) =
        if List.isEmpty lines then
            ProviderProse.instructionLines language Path.EmptyRoster Map.empty
        else
            lines

    let private appendHandleLinesForRoster language journal snapshot runtimeByAgentId runtime agentLines handles =
        task {
            let eligibleHandles =
                HandleProjection.horizonVisible handles
                |> List.filter (HostForkJoin.currentProcessHandle runtime)

            for handle in eligibleHandles do
                do! appendHandleLines language journal snapshot runtimeByAgentId agentLines handle
        }

    let private executeWithJournal language (runtimeCtx: HorizonRuntimeContext) context (journal: AgentJournal) =
        task {
            let parentSessionId =
                SessionId.create context.SessionId |> runtimeCtx.LogicalOwnerFor

            do! runtimeCtx.EnsureRoadDevOpsBound parentSessionId

            match runtimeCtx.RuntimeFor context with
            | Error _ -> return unavailable language Path.CannotBeSeen
            | Ok runtime ->
                let agents, ptys = runtime.List()
                let snapshot = AgentJournal.snapshot journal

                let durableHandles =
                    AgentProjection.tryFind parentSessionId snapshot.AgentProjections
                    |> Option.bind (fun session -> session.Handles)
                    |> Option.defaultValue HandleProjection.empty

                let runtimeByAgentId =
                    agents |> List.map (fun record -> record.AgentId, record) |> Map.ofList

                // DSL-MUTABLE: algorithm-scratch — agent line accumulator
                let agentLines = ResizeArray<string>()

                do!
                    appendHandleLinesForRoster
                        language
                        journal
                        snapshot
                        runtimeByAgentId
                        runtime
                        agentLines
                        durableHandles

                let agentLines = agentLines |> Seq.toList

                let ptyLines =
                    ptys
                    |> List.sortBy (fun record -> record.PtyId)
                    |> List.map (lineForPty language)

                let lines = List.append agentLines ptyLines
                return ToolHostCodec.tomlObjectWithInstructions (rosterInstructions language lines) []
        }

    let private execute (runtimeCtx: HorizonRuntimeContext) (_args: HostToolArguments) context =
        task {
            let language = lang context

            match runtimeCtx.Journal with
            | None -> return unavailable language Path.UnavailableFromContext
            | Some journal -> return! executeWithJournal language runtimeCtx context journal
        }

    let admission: ToolAdmission =
        ToolAdmission.OfficeRole(fun _ r -> OfficeCapability.isAllowed r ToolPermission.Horizon)

    let spec (runtimeCtx: HorizonRuntimeContext) =
        { Name = "horizon"
          Description =
            ProviderProse.render (ProviderLanguageBinding.readGlobalPreference ()) Path.Description Map.empty
          Arguments = []
          Admission = admission
          Execute = execute runtimeCtx }
