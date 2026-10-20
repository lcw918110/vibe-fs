namespace Wanxiangshu.OpenCode.Host

open System
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal

module LoadRecoverySurface =
    type private State =
        // DSL-MUTABLE: resource — opaque fixture projection read by the actual durable resolver.
        { mutable Projection: ProjectionSet }

    let private stateOf (value: obj) = unbox<State> value

    let create () : obj = box { Projection = Fold.empty }

    let foldCanonical (state: obj) (factJson: string) : obj =
        let handle = stateOf state

        match FactCodec.deserializeFact factJson with
        | Error error -> box {| ok = false; error = error |}
        | Ok fact ->
            match Fold.foldFact handle.Projection fact with
            | Error error -> box {| ok = false; error = error.Reason |}
            | Ok projection ->
                handle.Projection <- projection
                box {| ok = true; error = "" |}

    let childSettlements (state: obj) : string array =
        ChildWorkRecovery.orphanedChildRuns (stateOf state).Projection.AgentProjections
        |> List.map (
            ChildWorkRecovery.settlementFact
            >> AgentFact.Execution
            >> Fact.Agent
            >> FactCodec.serializeFact
        )
        |> List.toArray

    let settleChildRuns (handle: JournalHandle) : Task<unit> =
        ChildWorkRecovery.settleOrphanedChildRuns handle.Journal

    let private handlesOf state parent =
        (stateOf state).Projection.AgentProjections.Sessions
        |> Map.tryFind (SessionId.create parent)
        |> Option.bind (fun session -> session.Handles)
        |> Option.defaultValue HandleProjection.empty

    let childView (state: obj) (parent: string) (child: string) : obj =
        let projections = (stateOf state).Projection.AgentProjections
        let handles = handlesOf state parent

        let activeRun =
            projections.Sessions
            |> Map.tryFind (SessionId.create child)
            |> Option.bind (fun session -> session.PromptAuthority)
            |> Option.bind (fun authority -> authority.ActiveLogicalRun)
            |> Option.map (fun profile -> LogicalRunId.value profile.LogicalRunId)
            |> Option.defaultValue ""

        let lifecycle =
            handles.Handles
            |> Map.toList
            |> List.tryPick (fun (_, record) ->
                if record.ChildSessionId = SessionId.create child then
                    Some record
                else
                    None)
            |> Option.map (fun record ->
                match record.Lifecycle with
                | HandleLifecycle.Active -> "Active"
                | HandleLifecycle.CompletedAwaitingJoin _ -> "CompletedAwaitingJoin"
                | HandleLifecycle.Retired -> "Retired"
                | HandleLifecycle.Abandoned _ -> "Abandoned")
            |> Option.defaultValue ""

        box
            {| activeRun = activeRun
               lifecycle = lifecycle
               joinable = HandleProjection.joinable handles |> List.length
               horizonVisible = HandleProjection.horizonVisible handles |> List.length |}

    let lookupChild (state: obj) (parent: string) (key: string) (byName: bool) : obj =
        let handles = handlesOf state parent

        (if byName then
             DurableChildLookup.byByname handles key
         else
             DurableChildLookup.byHandleId handles key)
        |> Option.map (fun (session, role, agent) ->
            box
                {| session = SessionId.value session
                   role = role.ToString()
                   agent = agent |})
        |> Option.defaultValue null

    let bindingEvidence (state: obj) (child: string) : obj =
        SessionBindingRecovery.evidenceFor (stateOf state).Projection.AgentProjections (SessionId.create child)
        |> Option.map (fun (parent, agent) -> box {| parent = parent; agent = agent |})
        |> Option.defaultValue null

    let installResolvers (state: obj) : unit =
        let projections () =
            (stateOf state).Projection.AgentProjections

        FissionRuntime.installDurableLaneEvidence (fun session ->
            SessionBindingRecovery.fissionLaneFor (projections ()) session)

    let clearResolvers () : unit =
        FissionRuntime.installDurableLaneEvidence (fun _ -> None)

    let lane (session: string) : obj =
        FissionRuntime.tryLane (SessionId.create session)
        |> Option.map (fun binding ->
            box
                {| group = binding.GroupId
                   owner = SessionId.value binding.OwnerSessionId
                   index = binding.LaneIndex
                   count = binding.LaneCount |})
        |> Option.defaultValue null

    let clearLane (session: string) : unit =
        FissionRuntime.unbindLane (SessionId.create session)

    let drainCompletions (state: obj) (parent: string) (maxCount: int) (completedAt: string) : Task<obj> =
        task {
            let handle = stateOf state
            let appended = ResizeArray<string>()

            let port: AgentJournalPort =
                { AppendExecutionFact =
                    fun _ fact ->
                        let wrapped = Fact.Agent(AgentFact.Execution fact)

                        match Fold.foldFact handle.Projection wrapped with
                        | Error error -> Task.FromResult(Error error.Reason)
                        | Ok projection ->
                            handle.Projection <- projection
                            appended.Add(FactCodec.serializeFact wrapped)
                            Task.FromResult(Ok())
                  HandleProjection = fun session -> handlesOf state (SessionId.value session)
                  ReadBlob = fun _ -> Task.FromResult(Error "fixture has no completion body")
                  WriteBlob = fun _ -> Task.FromResult(Error "fixture does not write blobs")
                  RefreshProjection = fun () -> Ok()
                  Sha256 = fun _ -> invalidOp "fixture does not hash blobs" }

            match!
                JoinDrain.drainFromJournalWhere
                    port
                    (SessionId.create parent)
                    maxCount
                    (DateTimeOffset.Parse completedAt)
                    (fun _ -> true)
            with
            | Error error ->
                return
                    box
                        {| ok = false
                           error = string error
                           completions = [||]
                           appended = appended.ToArray() |}
            | Ok completions ->
                return
                    box
                        {| ok = true
                           error = ""
                           completions =
                            completions
                            |> List.map (fun completion ->
                                box
                                    {| run = completion.RunId
                                       agent = completion.AgentName |})
                            |> List.toArray
                           appended = appended.ToArray() |}
        }
