namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.Journal

/// Process-local terminal fan-out keyed by the same workspace runtime path as
/// SharedAgentJournal. OpenCode loads the plugin once for the root workspace and
/// again for each manager worktree; AwaitAgent subscriptions live on the parent
/// instance while child terminal NotifyTerminal often fires on the worktree
/// instance. Without a shared bus, candidate confirmation can occur in the journal while
/// OrchestratorHost never observes the completion signal.
module SharedTerminalBus =

    /// DSL-state-combination: physical — shared terminal port refcount resource
    type private SharedPort =
        { Port: Events.HostEventPort
          mutable RefCount: int }

    let private gate = obj ()
    // DSL-MUTABLE: resource — shared terminal port refcount registry
    let private shared = Dictionary<string, SharedPort>()

    let acquire (directory: string) : Events.HostEventPort =
        lock gate (fun () ->
            match shared.TryGetValue directory with
            | true, entry ->
                entry.RefCount <- entry.RefCount + 1
                entry.Port
            | false, _ ->
                let port = Events.HostEventPort()
                shared.[directory] <- { Port = port; RefCount = 1 }
                port)

    let private updateRelease key (entry: SharedPort) =
        let remaining = entry.RefCount - 1

        if remaining <= 0 then
            shared.Remove key |> ignore
        else
            entry.RefCount <- remaining

    let private releaseTarget key target =
        lock gate (fun () ->
            match shared.TryGetValue key with
            | true, entry when obj.ReferenceEquals(entry.Port, target) -> updateRelease key entry
            | _ -> ())

    let release (directory: string option) (port: Events.HostEventPort option) =
        match directory, port with
        | Some key, Some target -> releaseTarget key target
        | _ -> ()

    let tryAcquireForWorkspace
        (workspace: string option)
        : System.Threading.Tasks.Task<(string * Events.HostEventPort) option> =
        task {
            match workspace with
            | None -> return None
            | Some path when String.IsNullOrWhiteSpace path -> return None
            | Some path ->
                let! key = RuntimePath.forWorkspace path
                return Some(key, acquire key)
        }
