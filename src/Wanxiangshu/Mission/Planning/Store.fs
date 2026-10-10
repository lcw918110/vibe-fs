namespace Wanxiangshu.Mission.Planning

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

[<RequireQualifiedAccess>]
module PlanEventStore =

    let currentPlanState (store: IEventStore) : PlanState option =
        store.TryCurrent "Plan" |> Option.map unbox<PlanState>

    let workState (store: IEventStore) (workId: PlanWorkId) : PlanWorkState option =
        let resolve (obj: obj) =
            if isNull obj then
                None
            elif emitJsExpr obj "$0.fields !== undefined && Array.isArray($0.fields)" then
                PlanFold.workState (PlanWorkId.value workId) (unbox<PlanState> obj)
            else
                Some(unbox<PlanWorkState> obj)

        match store.TryCurrent "Plan" with
        | Some obj -> resolve obj
        | None -> store.TryCurrent(PlanWorkId.value workId) |> Option.bind resolve

    let view (store: IEventStore) (workId: PlanWorkId) : PlanWorkView option =
        workState store workId |> Option.map PlanFold.viewOf

    let private resolveWorkViews (obj: obj) : PlanWorkView list =
        if isNull obj then
            []
        elif emitJsExpr obj "$0.fields !== undefined && Array.isArray($0.fields)" then
            let works = emitJsExpr<Map<string, PlanWorkState>> obj "$0.fields[0]"
            works |> Map.toList |> List.map (fun (_, st) -> PlanFold.viewOf st)
        else
            [ PlanFold.viewOf (unbox<PlanWorkState> obj) ]

    let allWorkViews (store: IEventStore) : PlanWorkView list =
        match store.TryCurrent "Plan" with
        | Some obj -> resolveWorkViews obj
        | None -> []

    let workStateFromIntegrator (integrator: ICanonicalIntegrator) (workId: PlanWorkId) : PlanWorkState option =
        integrator.TryCurrent "Plan"
        |> Option.bind (fun obj ->
            if isNull obj then
                None
            elif emitJsExpr obj "$0.fields !== undefined && Array.isArray($0.fields)" then
                PlanFold.workState (PlanWorkId.value workId) (unbox<PlanState> obj)
            else
                Some(unbox<PlanWorkState> obj))

    let viewFromIntegrator (integrator: ICanonicalIntegrator) (workId: PlanWorkId) : PlanWorkView option =
        workStateFromIntegrator integrator workId |> Option.map PlanFold.viewOf

    let allWorkViewsFromIntegrator (integrator: ICanonicalIntegrator) : PlanWorkView list =
        match integrator.TryCurrent "Plan" with
        | Some obj -> resolveWorkViews obj
        | None -> []

    let private stateOfPackedFields (obj: obj) : PlanWorkState option =
        let works = emitJsExpr<Map<string, PlanWorkState>> obj "$0.fields[0]"
        works |> Map.toList |> List.tryHead |> Option.map snd

    let private stateOfCurrent (obj: obj) : PlanWorkState option =
        if isNull obj then
            None
        elif emitJsExpr obj "$0.fields !== undefined && Array.isArray($0.fields)" then
            stateOfPackedFields obj
        else
            Some(unbox<PlanWorkState> obj)

    let tryActiveWorkState (tryCurrent: string -> obj option) : PlanWorkState option =
        tryCurrent "Plan"
        |> Option.bind stateOfCurrent
        |> Option.filter (fun st -> st.Active.IsSome)

    let private executeAppend (store: IEventStore) (envelope: EventEnvelope) : Task<Result<EventId, string>> =
        task {
            match! store.Append [ envelope ] with
            | Ok _ -> return Ok envelope.EventId
            | Error appendError -> return Error(AppendError.describe appendError)
        }

    let append (store: IEventStore) (workId: PlanWorkId) (event: PlanEvent) : Task<Result<EventId, string>> =
        let currentWorkState =
            workState store workId |> Option.defaultValue PlanFold.initialWorkState

        match PlanFold.applyWorkEvent event currentWorkState with
        | Error ruleViolation ->
            Task.FromResult(Error(sprintf "Fold validation failed before append: %s" ruleViolation))
        | Ok _ ->
            let streamId = EventStreamId.create ("plan/" + PlanWorkId.value workId)
            let parents = store.TryHead streamId |> Option.toList
            let envelope = PlanEventCodec.toEnvelope workId event parents
            executeAppend store envelope
