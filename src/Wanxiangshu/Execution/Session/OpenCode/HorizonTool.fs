namespace Wanxiangshu.Execution.Session.OpenCode

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
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

        [<Literal>]
        let Calibration = "tool/horizon/calibration"

    type HorizonRuntimeContext =
        { RuntimeFor: HostToolContext -> Result<HostForkRuntime, string>
          LogicalOwnerFor: SessionId -> SessionId
          Journal: AgentJournal option
          EnsureWorkDevOpsBound: SessionId -> Task<unit> }

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

    /// The one lifecycle→roster-status mapping the prose line and the data
    /// envelope share, so the two horizons cannot disagree about a participant.
    [<RequireQualifiedAccess>]
    type private RosterStatus =
        | Returned
        | StillAway
        | DidNotReturn

    let private prosePath status =
        match status with
        | RosterStatus.Returned -> Path.Returned
        | RosterStatus.StillAway -> Path.StillAway
        | RosterStatus.DidNotReturn -> Path.DidNotReturn

    let private wireStatus status =
        match status with
        | RosterStatus.Returned -> "returned"
        | RosterStatus.StillAway -> "still-away"
        | RosterStatus.DidNotReturn -> "did-not-return"

    let private activeStatus (runtimeRecord: AgentRecord option) : RosterStatus =
        match runtimeRecord with
        | Some record when record.CompletionCellSettled -> RosterStatus.Returned
        | _ -> RosterStatus.StillAway

    let private statusForHandle (runtimeRecord: AgentRecord option) (handle: HandleRecord) =
        match handle.Lifecycle with
        | HandleLifecycle.CompletedAwaitingJoin _ -> RosterStatus.Returned
        | HandleLifecycle.Active -> activeStatus runtimeRecord
        | HandleLifecycle.Abandoned _
        | HandleLifecycle.Retired -> RosterStatus.DidNotReturn

    let private lineForHandle language (handle: HandleRecord) (runtimeRecord: AgentRecord option) : string =
        labeled language (prosePath (statusForHandle runtimeRecord handle)) (labelForHandle language handle)

    /// Latest delivered view for one participant. Horizon reads the delivered
    /// view as it stands and does not advance the returned cursor.
    let private deliveredView (handle: HandleRecord) : string option =
        Wanxiangshu.Repository.Programming.Js.JsDeliveredValues.tryPeekRendered (SessionId.value handle.ChildSessionId)

    let private latestViewForHandle
        language
        (_journal: AgentJournal)
        (_snapshot: ProjectionSet)
        (handle: HandleRecord)
        : Task<string> =
        let label = labelForHandle language handle

        match deliveredView handle with
        | None -> Task.FromResult(labeled language Path.NoWorkYet label)
        | Some text ->
            Task.FromResult(ProviderProse.render language Path.LatestWork (Map [ "label", label; "record", text ]))

    let private viewData
        (_journal: AgentJournal)
        (_snapshot: ProjectionSet)
        (handle: HandleRecord)
        : Task<string * string> =
        match deliveredView handle with
        | None -> Task.FromResult("none", "")
        | Some text -> Task.FromResult("latest", text)

    /// The terminal name a pty is known by, when the runtime snapshot carries
    /// one. The prose line names a generic terminal when it does not; the data
    /// envelope stays empty rather than inventing a name.
    let private tryTerminalLabel (record: PtyRecord) =
        if String.IsNullOrWhiteSpace record.Command then
            None
        else
            Some(record.Command.Trim())

    let private lineForPty language (record: PtyRecord) : string =
        let label =
            match tryTerminalLabel record with
            | Some label -> label
            | None -> ProviderProse.render language Path.TerminalLabel Map.empty

        labeled language Path.RemainsOpen label

    let private unavailable language path =
        ToolHostCodec.tomlObjectWithInstructions [ ProviderProse.render language path Map.empty ] []

    /// The four calibration numbers for one participant, when both the
    /// delegator's estimate and the participant's own start are known: turns
    /// spent and left, and the wall-clock spent beside the time that remains at
    /// the pace this participant has shown. Absent (never guessed) otherwise.
    let private calibrationNumbers
        (now: DateTimeOffset)
        (journal: AgentJournal)
        (handle: HandleRecord)
        : (int * int * float * float) option =
        let estimateState =
            AgentJournalPortAdapter.forDelegatedToolEstimate journal
            |> fun port -> DelegatedToolEstimateLedger.tryState port handle.ChildSessionId

        let startedAt =
            (AgentJournalPortAdapter.forSessionStartedAt journal).TryStartedAt handle.ChildSessionId

        match estimateState, startedAt with
        | Some state, Some bound ->
            let turnsUsed = DelegatedToolEstimateProjection.countedCallCount state
            let turnsLeft = DelegatedToolEstimateProjection.remaining state
            let timeUsedMs = (now - bound).TotalMilliseconds
            Some(turnsUsed, turnsLeft, timeUsedMs, timeUsedMs * float turnsLeft / float (max 1 turnsUsed))
        | _ -> None

    let private calibrationLine
        language
        (now: DateTimeOffset)
        (journal: AgentJournal)
        (handle: HandleRecord)
        : string option =
        calibrationNumbers now journal handle
        |> Option.map (fun (turnsUsed, turnsLeft, timeUsedMs, _) ->
            HorizonCalibration.render
                (ProviderLanguage.externalName language)
                (labelForHandle language handle)
                turnsUsed
                turnsLeft
                timeUsedMs)

    /// The handles a horizon presents, in order: horizon-visible, established by
    /// this process, and carrying the agent identity its caller knows it by.
    /// Pty handles carry none and are listed from the runtime's pty table.
    let private rosterEntries (runtime: HostForkRuntime) (handles: AgentLinkageProjection) =
        HandleProjection.horizonVisible handles
        |> List.filter (HostForkJoin.currentProcessHandle runtime)
        |> List.choose (fun handle ->
            HandleId.tryAgent handle.Handle
            |> Option.map (fun handleId -> AgentHandleId.value handleId, handle))

    let private appendHandleLines
        language
        journal
        snapshot
        (now: DateTimeOffset)
        runtimeByAgentId
        (agentLines: ResizeArray<string>)
        (agentId: string)
        (handle: HandleRecord)
        : Task<unit> =
        task {
            agentLines.Add(lineForHandle language handle (Map.tryFind agentId runtimeByAgentId))

            let! viewLine = latestViewForHandle language journal snapshot handle
            agentLines.Add viewLine

            calibrationLine language now journal handle |> Option.iter agentLines.Add
        }

    let private rosterInstructions language (lines: string list) =
        if List.isEmpty lines then
            ProviderProse.instructionLines language Path.EmptyRoster Map.empty
        else
            lines

    let private appendHandleLinesForRoster
        language
        journal
        snapshot
        runtimeByAgentId
        (runtime: HostForkRuntime)
        agentLines
        handles
        =
        let now = runtime.Now()

        task {
            for agentId, handle in rosterEntries runtime handles do
                do! appendHandleLines language journal snapshot now runtimeByAgentId agentLines agentId handle
        }

    let private executeWithJournal language (runtimeCtx: HorizonRuntimeContext) context (journal: AgentJournal) =
        task {
            let parentSessionId =
                SessionId.create context.SessionId |> runtimeCtx.LogicalOwnerFor

            do! runtimeCtx.EnsureWorkDevOpsBound parentSessionId

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

    let private unavailableReceipt () : obj =
        createObj [ "ok" ==> false; "code" ==> "horizon-unavailable" ]

    let private agentRow
        (language: ProviderLanguage)
        (journal: AgentJournal)
        (snapshot: ProjectionSet)
        (now: DateTimeOffset)
        (runtimeByAgentId: Map<string, AgentRecord>)
        (agentId: string)
        (handle: HandleRecord)
        : Task<obj> =
        task {
            let! workState, recordText = viewData journal snapshot handle

            let turnsUsed, turnsLeft, timeUsedMs, timeLeftMs =
                calibrationNumbers now journal handle |> Option.defaultValue (0, 0, 0.0, 0.0)

            return
                createObj
                    [ "label" ==> labelForHandle language handle
                      "status"
                      ==> wireStatus (statusForHandle (Map.tryFind agentId runtimeByAgentId) handle)
                      "target" ==> handle.TargetAgent
                      "workState" ==> workState
                      "record" ==> recordText
                      "turnsUsed" ==> turnsUsed
                      "turnsLeft" ==> turnsLeft
                      "timeUsedMs" ==> timeUsedMs
                      "timeLeftMs" ==> timeLeftMs ]
        }

    /// The same roster as data for `js.horizon()`: the participants this process
    /// still has at the caller's horizon, each with the status the prose line
    /// reports, its latest record body, and the four calibration numbers, plus
    /// the terminals still open. It states data only — no provider prose is
    /// rendered here. A horizon the caller cannot be shown is refused as data.
    let private rosterRows
        (language: ProviderLanguage)
        (journal: AgentJournal)
        (runtime: HostForkRuntime)
        (parentSessionId: SessionId)
        : Task<obj> =
        task {
            let agents, ptys = runtime.List()
            let snapshot = AgentJournal.snapshot journal

            let durableHandles =
                AgentProjection.tryFind parentSessionId snapshot.AgentProjections
                |> Option.bind (fun session -> session.Handles)
                |> Option.defaultValue HandleProjection.empty

            let runtimeByAgentId =
                agents |> List.map (fun record -> record.AgentId, record) |> Map.ofList

            let now = runtime.Now()

            // DSL-MUTABLE: algorithm-scratch — agent row accumulator
            let agentRows = ResizeArray<obj>()

            for agentId, handle in rosterEntries runtime durableHandles do
                let! row = agentRow language journal snapshot now runtimeByAgentId agentId handle
                agentRows.Add row

            let ptyRows =
                ptys
                |> List.sortBy (fun record -> record.PtyId)
                |> List.map (fun record ->
                    createObj [ "terminal" ==> (tryTerminalLabel record |> Option.defaultValue "") ])

            return
                createObj
                    [ "ok" ==> true
                      "agents" ==> (agentRows |> Seq.toArray)
                      "ptys" ==> (ptyRows |> List.toArray) ]
        }

    let private rosterWithJournal
        (language: ProviderLanguage)
        (runtimeCtx: HorizonRuntimeContext)
        (context: HostToolContext)
        (journal: AgentJournal)
        : Task<obj> =
        task {
            let parentSessionId =
                SessionId.create context.SessionId |> runtimeCtx.LogicalOwnerFor

            do! runtimeCtx.EnsureWorkDevOpsBound parentSessionId

            match runtimeCtx.RuntimeFor context with
            | Error _ -> return unavailableReceipt ()
            | Ok runtime -> return! rosterRows language journal runtime parentSessionId
        }

    let rosterReceipt (language: string) (runtimeCtx: HorizonRuntimeContext) (context: HostToolContext) : Task<obj> =
        task {
            let language = ProviderLanguage.parse language

            match runtimeCtx.Journal with
            | None -> return unavailableReceipt ()
            | Some journal -> return! rosterWithJournal language runtimeCtx context journal
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
