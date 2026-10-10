namespace Wanxiangshu.Process

open System
open System.Threading
open System.Threading.Tasks

/// JS-native owner surface for the output-budget gate. Cancellation tokens and
/// process outcomes remain opaque; tests observe only permit state and whether
/// a bounded large estimate completed.
module LargeGateSurface =

    type private TokenHandle(source: CancellationTokenSource) =
        member _.Token = source.Token
        member _.Cancel() = source.Cancel()
        member _.IsCancellationRequested = source.IsCancellationRequested

    let createToken (cancelled: bool) : obj =
        let source = new CancellationTokenSource()

        if cancelled then
            source.Cancel()

        TokenHandle(source) :> obj

    let cancelToken (token: obj) : unit = (token :?> TokenHandle).Cancel()

    let isCancellationRequested (token: obj) : bool =
        (token :?> TokenHandle).IsCancellationRequested

    let acquire (token: obj) : Task =
        LargeGate.acquire ((token :?> TokenHandle).Token)

    let release () = LargeGate.release ()

    let getCount () = LargeGate.getCount ()

    /// Execute one in-memory large estimate through ProcessRunner. The callback
    /// runs at launcher entry, after ProcessRunner has acquired LargeGate.
    let runLargeEstimate (observe: unit -> unit) : Task<bool> =
        let launcher (_command: Command) (_token: CancellationToken) =
            task {
                observe ()
                return 0, [||], [||]
            }

        let command =
            { FileName = "sh"
              Arguments = [ "-c"; "echo hi" ]
              WorkingDirectory = None
              Environment = None
              Stdin = None
              PtyOptions = None }

        let estimate =
            { EstimatedRuntime = RuntimeSeconds 10.0
              EstimatedOutput = OutputBytes 1024L
              EstimatedMemory = EstimatedMemory.Large }

        let context =
            { WorkingDirectory = None
              HardLimit = ProcessEstimate.DefaultHardLimit }

        task {
            let! result = ProcessRunner.runWithLauncher launcher command estimate context CancellationToken.None
            return Result.isOk result
        }
