namespace Wanxiangshu.OpenCode

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Sphinx.V2.Composition

module WorkspaceEventStore =

    let private hostProgram: IntegrationRule list =
        CanonicalIntegrator.baseRules
        @ Wanxiangshu.Sphinx.V2.Composition.Bind.rules
        @ Wanxiangshu.Repository.Knowledge.Casebook.CasebookIntegrationRules.rules
        @ Wanxiangshu.Repository.Programming.Js.JsTransactionIntegrationRules.rules
        @ Wanxiangshu.Repository.Programming.Js.JsLifecycleIntegrationRules.rules

    let programWithoutRegistration (ruleName: string) : IntegrationRule list =
        hostProgram |> List.filter (fun rule -> rule.Name <> ruleName)

    [<Import("sharedEventStorePool", "wanxiangli/work")>]
    type private INativeEventStorePool =
        abstract Acquire: commonDir: string * createStore: (string -> obj) -> obj
        abstract TryCurrent: commonDir: string -> obj option
        abstract Release: commonDir: string -> bool

    [<Import("sharedEventStorePool", "wanxiangli/work")>]
    let private sharedPool: INativeEventStorePool = jsNative

    let private deferredStore (commonDir: string) : IEventStore =
        let active =
            lazy
                (let writerId = Guid.NewGuid().ToString("N")

                 let integrator =
                     CanonicalIntegrator.createWithRules hostProgram AuthoritativeEventTypes.isKnown

                 EventStore.createLocal commonDir writerId integrator)

        { new IEventStore with
            member _.Append(events) = active.Value.Append events
            member _.WritePayload(content) = active.Value.WritePayload content
            member _.ReadPayload(payloadRef) = active.Value.ReadPayload payloadRef
            member _.TryCurrent(key) = active.Value.TryCurrent key
            member _.TryEvent(eventId) = active.Value.TryEvent eventId
            member _.TryHeads(streamId) = active.Value.TryHeads streamId
            member _.TryHead(streamId) = active.Value.TryHead streamId
            member _.AllHeads() = active.Value.AllHeads()
            member _.ReloadLocal() = active.Value.ReloadLocal() }

    let private rememberCommonDir (store: IEventStore) (commonDir: string) =
        emitJsStatement (box store, commonDir) "$0.gitCommonDir = $1"

    let acquire (commonDir: string) : IEventStore =
        match String.IsNullOrWhiteSpace commonDir with
        | true -> failwith "WorkspaceEventStore.acquire: commonDir is empty"
        | false ->
            let storeObj = sharedPool.Acquire(commonDir, fun dir -> box (deferredStore dir))
            let store = unbox<IEventStore> storeObj
            rememberCommonDir store commonDir
            store

    let tryCurrent (commonDir: string) : IEventStore option =
        sharedPool.TryCurrent(commonDir)
        |> Option.map unbox<IEventStore>

    let release (commonDir: string) =
        sharedPool.Release(commonDir) |> ignore

    let bootPort (commonDir: string) : IJournalEventStoreBoot =
        let store = acquire commonDir

        { new IJournalEventStoreBoot with
            member _.ResumeOrCreate(runtimeId, processId, startedAt) =
                EventStoreJournalWriter.resumeOrCreate (runtimeId, processId, startedAt, store) }
