namespace Wanxiangshu.Process

open System.Collections.Generic
open System.Threading.Tasks

type PtySupervisor =
    { Gate: obj
      mutable SpawnFn: obj option
      mutable LoadTask: Task<unit> option
      Sessions: Dictionary<PtyId, PtySession> }

module PtySupervisor =
    [<Literal>]
    val PtyReadFirstByteMs: int = 250

    val create: unit -> PtySupervisor
    val add: supervisor: PtySupervisor -> id: PtyId -> session: PtySession -> unit
    val tryGet: supervisor: PtySupervisor -> id: PtyId -> PtySession option
    val get: supervisor: PtySupervisor -> id: PtyId -> PtySession
    val remove: supervisor: PtySupervisor -> id: PtyId -> unit
    val list: supervisor: PtySupervisor -> PtyId list
    val signalName: signal: PtySignal -> string
    val ensureSpawn: supervisor: PtySupervisor -> Task<unit>
    val spawn: supervisor: PtySupervisor -> command: string -> cwd: string -> Task<obj>

    val failPending:
        entries: (PtyCommand * TaskCompletionSource<Result<unit, string>> option) list -> reason: string -> unit

    val takePending:
        supervisor: PtySupervisor -> id: PtyId -> (PtyCommand * TaskCompletionSource<Result<unit, string>> option) list

    val drop:
        supervisor: PtySupervisor -> id: PtyId -> (PtyCommand * TaskCompletionSource<Result<unit, string>> option) list

    val applyLive:
        supervisor: PtySupervisor -> port: PtyPort -> id: PtyId -> command: PtyCommand -> Task<Result<unit, string>>

    val attach:
        supervisor: PtySupervisor ->
        port: PtyPort ->
        id: PtyId ->
        term: obj ->
        exitTcs: TaskCompletionSource<unit> ->
            unit

    val cancelAll: supervisor: PtySupervisor -> unit
