namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Repository.Knowledge.Casebook

module PluginLifecycleSurface =

    let create (input: obj) : Task<obj> =
        task {
            let! runtime = SpikePlugin.createRuntime input
            return box runtime
        }

    let hooks (runtime: obj) : obj =
        (unbox<SpikePlugin.Runtime> runtime).Hooks

    let private syncDelegate (runtime: obj) =
        (unbox<SpikePlugin.Runtime> runtime).Scope.SyncDelegateRuntime
        |> Option.defaultWith (fun () -> invalidOp "plugin has no attached SyncDelegate runtime")

    let invokeEngineer (runtime: obj) (ownerSessionId: string) (charge: string) : Task<obj> =
        task {
            match! (syncDelegate runtime).Invoke(ownerSessionId, SyncDelegateRole.Engineer, charge) with
            | Ok view ->
                return
                    box
                        {| ok = true
                           view = view
                           reason = null |}
            | Error reason ->
                return
                    box
                        {| ok = false
                           view = null
                           reason = reason |}
        }

    let attachedEngineer (runtime: obj) (ownerSessionId: string) : string =
        (syncDelegate runtime)
            .TryFind(SessionId.create ownerSessionId, SyncDelegateRole.Engineer)
        |> Option.map SessionId.value
        |> Option.toObj

    let awaitAssignmentReady (runtime: obj) (delegateSessionId: string) : Task<bool> =
        (syncDelegate runtime).AwaitAssignmentReady(SessionId.create delegateSessionId)

    /// Consumes the original completed draft; this is not a read-only peek.
    let takeEngineerDraft (runtime: obj) (ownerSessionId: string) : obj =
        let delegateSessionId =
            (syncDelegate runtime)
                .TryFind(SessionId.create ownerSessionId, SyncDelegateRole.Engineer)
            |> Option.map SessionId.value
            |> Option.defaultWith (fun () -> invalidOp "owner has no attached Engineer")

        match CasebookDraftStore.tryTake delegateSessionId with
        | None -> null
        | Some draft ->
            box
                {| sessionId = delegateSessionId
                   turns =
                    draft.Turns
                    |> List.map (fun turn ->
                        box
                            {| question = turn.Q
                               answer = Option.toObj turn.A |})
                    |> List.toArray |}
