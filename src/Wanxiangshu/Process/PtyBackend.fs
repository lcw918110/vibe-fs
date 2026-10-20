namespace Wanxiangshu.Process

open System
open System.Threading
open System.Threading.Tasks

/// Production PTY backend: drives bun-pty under the OpenCode/Bun host.
/// All per-PTY state lives in PtySession/PtySupervisor; this file keeps the
/// command pipeline and the port assembly.
module PtyBackend =

    open PtySession
    open PtySupervisor

    let private completeExit (exitTcs: TaskCompletionSource<unit>) : unit =
        try
            exitTcs.SetResult(())
        with _ ->
            ()

    let private failPendingWrites (super: PtySupervisor) (port: PtyPort) (id: PtyId) (msg: string) : unit =
        port.FailRead(id, msg)

        for (_, tcsOpt) in takePending super id do
            tcsOpt |> Option.iter (fun t -> t.SetResult(Error msg))

        drop super id |> ignore
        port.Complete(id, Error msg)

    let private spawnCommand
        (super: PtySupervisor)
        (port: PtyPort)
        (id: PtyId)
        (cmd: string)
        (cwd: string)
        : Task<Result<unit, string>> =
        task {
            let exitTcs = TaskCompletionSource<unit>()
            port.RegisterExitTask(id, exitTcs.Task)

            try
                do! ensureSpawn super
                let! term = spawn super cmd cwd
                attach super port id term exitTcs
                return Ok()
            with ex ->
                completeExit exitTcs
                let msg = sprintf "PTY spawn failed: %s" ex.Message
                failPendingWrites super port id msg
                return Error msg
        }

    let private handle
        (super: PtySupervisor)
        (port: PtyPort)
        (id: PtyId)
        (command: PtyCommand)
        : Task<Result<unit, string>> =
        task {
            match command with
            | PtyCommand.Spawn(cmd, cwd) -> return! spawnCommand super port id cmd cwd
            | other -> return! applyLive super port id other
        }

    /// Builds a PtyPort whose handler drives real bun-pty sessions. Each call
    /// yields a port with fully isolated backend state.
    let createPort () : PtyPort =
        let super = create ()
        // DSL-MUTABLE: resource — back-reference to the created port (cycle closure)
        let mutable portRef: PtyPort option = None

        let handler (id: PtyId) (command: PtyCommand) : Task<Result<unit, string>> =
            match portRef with
            | None -> Task.FromResult(Ok())
            | Some port -> handle super port id command

        let port = PtyPort(handler = handler)
        portRef <- Some port
        port
