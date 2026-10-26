namespace Wanxiangshu.Persistence.Journal

open System
open System.Threading.Tasks
open Fable.Core
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable

module SharedAgentJournal =

    [<Import("sharedAsyncJournalPool", "wanxiangli/work")>]
    type private INativeAsyncJournalPool =
        abstract AcquireOrStart: directory: string * startReady: (unit -> obj) -> obj * bool
        abstract SettleSuccess: directory: string * readyTask: obj * instance: obj -> unit
        abstract SettleFailure: directory: string * readyTask: obj -> unit
        abstract ReleaseTarget: target: obj -> obj option

    [<Import("sharedAsyncJournalPool", "wanxiangli/work")>]
    let private sharedPool: INativeAsyncJournalPool = jsNative

    module private Helpers =
        let settleOutcome
            (pool: INativeAsyncJournalPool)
            (directory: string)
            (readyObj: obj)
            (outcome: Result<AgentJournal, FoldRejection>)
            =
            match outcome with
            | Ok journal ->
                pool.SettleSuccess(directory, readyObj, box journal)
                Ok journal
            | Error err ->
                pool.SettleFailure(directory, readyObj)
                Error err

        let handleReleaseTarget (instOpt: obj option) : Task =
            match instOpt with
            | Some inst ->
                let target = unbox<AgentJournal> inst
                task { do! (target :> IAsyncDisposable).DisposeAsync() } :> Task
            | None ->
                Task.FromResult(()) :> Task

    let acquire
        (directory: string)
        (processId: int)
        (startedAt: DateTimeOffset)
        (openJournal: RuntimeId -> int -> DateTimeOffset -> Task<Result<AgentJournal, FoldRejection>>)
        : Task<Result<AgentJournal, FoldRejection>> =
        let openThunk () =
            let runtimeId = RuntimeId.create (Guid.NewGuid().ToString("N").Substring(0, 12))
            box (openJournal runtimeId processId startedAt)

        let readyObj, _isNew = sharedPool.AcquireOrStart(directory, openThunk)
        let ready = unbox<Task<Result<AgentJournal, FoldRejection>>> readyObj

        task {
            let! outcome = ready
            return Helpers.settleOutcome sharedPool directory readyObj outcome
        }

    let releaseAsync (journal: AgentJournal option) : Task =
        match journal with
        | Some target ->
            let released = sharedPool.ReleaseTarget(box target)
            Helpers.handleReleaseTarget released
        | None ->
            Task.FromResult(()) :> Task

    let release (journal: AgentJournal option) = releaseAsync journal |> ignore
