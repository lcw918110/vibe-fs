namespace Wanxiangshu.Interaction.Dispatch

open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
type PromptPhysicalOutcome =
    | Accepted of PhysicalUserMessageId
    | Rejected of string

/// It carries no business stage: callers register one callback before transport,
/// and the sole PhysicalAccepted writer completes it exactly once.
module PromptPhysicalAcceptance =

    [<Emit("""(() => {
      var timer = null;
      var p = new Promise(function(resolve) {
        timer = setTimeout(function() { resolve({ok:false}); }, $1);
        if (timer && typeof timer.unref === 'function') timer.unref();
      });
      return Promise.race([
        $0.then(
          function(v) { if (timer) clearTimeout(timer); return {ok:true, v:v}; },
          function(e) { if (timer) clearTimeout(timer); throw e; }
        ),
        p
      ]);
    })()""")>]
    let private raceTimeout (task: Task<'T>) (ms: int) : Task<obj> = jsNative

    let private gate = obj ()

    let private callbacks: Dictionary<string, PhysicalUserMessageId -> unit> =
        Dictionary<string, PhysicalUserMessageId -> unit>()

    let private waiters: Dictionary<string, TaskCompletionSource<PromptPhysicalOutcome>> =
        Dictionary<string, TaskCompletionSource<PromptPhysicalOutcome>>()

    let private trySetResult (tcs: TaskCompletionSource<'T>) (value: 'T) =
        try
            tcs.SetResult value
            true
        with _ ->
            false

    let register (promptKey: PromptKey) (callback: PhysicalUserMessageId -> unit) =
        lock gate (fun () -> callbacks.[PromptKey.value promptKey] <- callback)

    let cancel (promptKey: PromptKey) =
        lock gate (fun () ->
            callbacks.Remove(PromptKey.value promptKey) |> ignore

            let waiterOpt =
                match waiters.TryGetValue(PromptKey.value promptKey) with
                | true, tcs ->
                    waiters.Remove(PromptKey.value promptKey) |> ignore
                    Some tcs
                | false, _ -> None

            waiterOpt
            |> Option.iter (fun tcs -> trySetResult tcs (PromptPhysicalOutcome.Rejected "Cancelled") |> ignore))

    let accepted (promptKey: PromptKey) (physicalUserMessageId: PhysicalUserMessageId) =
        let callback, waiter =
            lock gate (fun () ->
                let cb =
                    match callbacks.TryGetValue(PromptKey.value promptKey) with
                    | true, pending ->
                        callbacks.Remove(PromptKey.value promptKey) |> ignore
                        Some pending
                    | false, _ -> None

                let w =
                    match waiters.TryGetValue(PromptKey.value promptKey) with
                    | true, tcs ->
                        waiters.Remove(PromptKey.value promptKey) |> ignore
                        Some tcs
                    | false, _ -> None

                cb, w)

        callback |> Option.iter (fun notify -> notify physicalUserMessageId)

        waiter
        |> Option.iter (fun tcs ->
            trySetResult tcs (PromptPhysicalOutcome.Accepted physicalUserMessageId)
            |> ignore)

    let rejected (promptKey: PromptKey) (reason: string) =
        let waiter =
            lock gate (fun () ->
                callbacks.Remove(PromptKey.value promptKey) |> ignore

                match waiters.TryGetValue(PromptKey.value promptKey) with
                | true, tcs ->
                    waiters.Remove(PromptKey.value promptKey) |> ignore
                    Some tcs
                | false, _ -> None)

        waiter
        |> Option.iter (fun tcs -> trySetResult tcs (PromptPhysicalOutcome.Rejected reason) |> ignore)

    let awaitConfirmation (promptKey: PromptKey) (timeoutMs: int option) : Task<PromptPhysicalOutcome option> =
        let (tcs: TaskCompletionSource<PromptPhysicalOutcome>) =
            lock gate (fun () ->
                match waiters.TryGetValue(PromptKey.value promptKey) with
                | true, existing -> existing
                | false, _ ->
                    let created =
                        TaskCompletionSource<PromptPhysicalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)

                    waiters.[PromptKey.value promptKey] <- created
                    created)

        let ms =
            match timeoutMs with
            | Some m -> m
            | None ->
                match System.Environment.GetEnvironmentVariable "WANXIANGSHU_ADMISSION_TIMEOUT_MS" with
                | null
                | "" -> 10000
                | value ->
                    match System.Int32.TryParse value with
                    | true, parsed -> parsed
                    | false, _ -> 10000

        task {
            let! (res: obj) = raceTimeout tcs.Task ms

            if unbox<bool> (res?ok) then
                return Some(unbox<PromptPhysicalOutcome> (res?v))
            else
                cancel promptKey
                return None
        }
