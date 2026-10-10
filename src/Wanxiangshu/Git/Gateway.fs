namespace Wanxiangshu.Git

open System
open System.Threading
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Process

/// Hook-process Git transport helper. It is not a product-process remote API:
/// OpenCode/Wanxiangshu never owns user fetch/pull/push triggers.
type GitGatewayRunner = string list -> Task<int * string * string>

/// Work that runs while the cross-process store lock is held: local writer
/// reads, union validation, remote import and the final materialization.
/// Discovery, remote object reads and publication stay outside the lock.
type GitGatewayLocalStage = unit -> Task<Result<StoreSnapshot, ConvergeError>>

[<RequireQualifiedAccess>]
module GitGateway =
    let private trackingRef remote = StoreRef.remoteTracking remote

    let private transportError stdout stderr =
        let detail = if String.IsNullOrWhiteSpace stderr then stdout else stderr

        ConvergeError.Transport(
            if String.IsNullOrWhiteSpace detail then
                "git transport failed"
            else
                detail.Trim()
        )

    let private tryObjectIdFromLsRemoteLine (line: string) =
        let trimmed = line.Trim()
        let fields = trimmed.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

        if String.IsNullOrWhiteSpace trimmed || fields.Length < 1 then
            None
        else
            Some(GitObjectId.create fields.[0])

    let private parseLsRemote (stdout: string) : GitObjectId option =
        stdout.Split('\n') |> Array.toList |> List.tryPick tryObjectIdFromLsRemoteLine

    let private requireExit0 stdout stderr code =
        if code <> 0 then
            Error(transportError stdout stderr)
        else
            Ok()

    let private requireFetchedObjectId (actual: string) =
        if String.IsNullOrWhiteSpace actual then
            Error(ConvergeError.Transport "fetched Wanxiang store ref has no object id")
        else
            Ok(GitObjectId.create actual)

    let private fetchRemoteRoot (run: GitGatewayRunner) (remote: string) : Task<Result<GitObjectId, ConvergeError>> =
        taskResult {
            let tracking = trackingRef remote
            let refspec = sprintf "+%s:%s" StoreRef.canonical tracking
            let! code, stdout, stderr = run [ "fetch"; "--no-tags"; remote; refspec ] |> TaskResultCE.ofTask
            do! requireExit0 stdout stderr code
            let! verifyCode, verifyOut, verifyErr = run [ "rev-parse"; "--verify"; tracking ] |> TaskResultCE.ofTask
            do! requireExit0 verifyOut verifyErr verifyCode
            return! requireFetchedObjectId (verifyOut.Trim())
        }

    let private materializeRemotePresence run remote =
        taskResult {
            let! actual = fetchRemoteRoot run remote
            return Some { RootOid = RootOid.create actual }, Some actual
        }

    let private afterLsRemote run remote stdout =
        match parseLsRemote stdout with
        | None -> taskResult { return None, None }
        | Some _advertisedOid -> materializeRemotePresence run remote

    /// Discover the current remote store root and ensure its object graph exists
    /// locally. Absence is a valid empty remote.
    let private discoverRemote
        (run: GitGatewayRunner)
        (remote: string)
        : Task<Result<StoreSnapshot option * GitObjectId option, ConvergeError>> =
        taskResult {
            let! code, stdout, stderr = run [ "ls-remote"; "--refs"; remote; StoreRef.canonical ] |> TaskResultCE.ofTask

            if code <> 0 then
                return! Error(transportError stdout stderr)
            else
                return! afterLsRemote run remote stdout
        }

    let private pushSnapshot
        (run: GitGatewayRunner)
        (remote: string)
        (expectedRemote: GitObjectId option)
        (snapshot: StoreSnapshot)
        : Task<Result<unit, ConvergeError>> =
        task {
            let next = RootOid.value snapshot.RootOid

            let lease =
                match expectedRemote with
                | None -> "--force-with-lease=" + StoreRef.canonical + ":"
                | Some oid -> "--force-with-lease=" + StoreRef.canonical + ":" + GitObjectId.value oid

            let refspec = GitObjectId.value next + ":" + StoreRef.canonical

            // --no-verify avoids recursively entering pre-push; the hook process
            // also exports WANXIANG_GIT_SYNC_ACTIVE for every internal Git child.
            let! code, stdout, stderr = run [ "push"; "--no-verify"; lease; remote; refspec ]

            if code = 0 then
                return Ok()
            else
                return Error(transportError stdout stderr)
        }

    let private readTrackedRemote (raw: IGitRawStore) remote =
        task {
            let! expected = raw.ReadRef(trackingRef remote)

            return expected |> Option.map (fun oid -> { RootOid = RootOid.create oid }), expected
        }

    let private sameSnapshot (left: StoreSnapshot) (right: StoreSnapshot) =
        RootOid.value left.RootOid = RootOid.value right.RootOid

    let private publishIfNeeded run remote remoteKnownCurrent expectedRemote (merged: StoreSnapshot) =
        task {
            let next = RootOid.value merged.RootOid

            if remoteKnownCurrent && expectedRemote = Some next then
                return Ok()
            else
                return! pushSnapshot run remote expectedRemote merged
        }

    /// Full bidirectional convergence. `observedRemote` is only an optimization
    /// for reference-transaction: the algorithm after root discovery is identical
    /// to pre-push. Lease races refetch and repeat boundedly.
    ///
    /// `withLocalLock` covers exactly the local byte boundary. Remote discovery,
    /// remote object reads and publication run outside the lock; the budget
    /// bounds the whole pass so a hung transport cannot hold the store open.
    let converge
        (raw: IGitRawStore)
        (commonDir: string)
        (run: GitGatewayRunner)
        (maxRetries: int)
        (remote: string)
        (observedRemote: StoreSnapshot option)
        (withLocalLock: GitGatewayLocalStage -> Task<Result<StoreSnapshot, ConvergeError>>)
        (deadline: Deadline)
        (clock: unit -> DateTimeOffset)
        : Task<Result<StoreSnapshot, ConvergeError>> =
        let currentTimeMs () =
            (clock ()).ToUnixTimeMilliseconds() |> float

        let budgetExhausted () = Deadline.isExpired clock deadline

        let readRemoteAndMerge snapshot nowMs =
            task {
                let! remoteWriters = WriterStreamSync.readRemoteStreamsAt raw commonDir nowMs snapshot

                match remoteWriters with
                | Error error -> return Error error
                | Ok writers ->
                    return!
                        withLocalLock (fun () ->
                            WriterStreamSync.mergeRemoteStreamsUnderLock raw commonDir nowMs writers)
            }

        let syncWithRemoteSnapshot snapshot nowMs =
            task {
                match WriterStreamSync.tryCachedMergedAt commonDir nowMs snapshot with
                | Some cached -> return Ok cached
                | None -> return! readRemoteAndMerge snapshot nowMs
            }

        let syncUnderBoundary remoteSnapshot nowMs =
            task {
                match remoteSnapshot with
                | None ->
                    return! withLocalLock (fun () -> WriterStreamSync.syncWithoutRemoteUnderLock raw commonDir nowMs)
                | Some snapshot -> return! syncWithRemoteSnapshot snapshot nowMs
            }

        let rec loop
            (remoteSnapshot: StoreSnapshot option)
            (expectedRemote: GitObjectId option)
            (remoteKnownCurrent: bool)
            (retriesLeft: int)
            =
            task {
                if budgetExhausted () then
                    return Error ConvergeError.ConvergeBudgetExhausted
                else
                    return! convergePass remoteSnapshot expectedRemote remoteKnownCurrent retriesLeft
            }

        and convergePass
            (remoteSnapshot: StoreSnapshot option)
            (expectedRemote: GitObjectId option)
            (remoteKnownCurrent: bool)
            (retriesLeft: int)
            =
            task {
                let! merged = syncUnderBoundary remoteSnapshot (currentTimeMs ())

                match merged with
                | Error error -> return Error error
                | Ok mergedSnapshot ->
                    let! pushResult = publishIfNeeded run remote remoteKnownCurrent expectedRemote mergedSnapshot
                    return! resolvePushOutcome retriesLeft pushResult mergedSnapshot
            }

        and resolvePushOutcome
            (retriesLeft: int)
            (pushResult: Result<unit, ConvergeError>)
            (mergedSnapshot: StoreSnapshot)
            =
            task {
                match pushResult with
                | Ok() -> return Ok mergedSnapshot
                | Error _ when budgetExhausted () -> return Error ConvergeError.ConvergeBudgetExhausted
                | Error _ when retriesLeft > 0 -> return! rediscoverAndContinue retriesLeft
                | Error _ when maxRetries <= 0 -> return Error ConvergeError.ConvergeCasRejected
                | Error _ -> return Error ConvergeError.ConvergeRetryExhausted
            }

        and rediscoverAndContinue (retriesLeft: int) =
            task {
                let! discovered = discoverRemote run remote

                match discovered with
                | Error error -> return Error error
                | Ok(nextSnapshot, nextExpected) -> return! loop nextSnapshot nextExpected true (retriesLeft - 1)
            }

        let convergeWithoutObservedRemote () =
            task {
                let! tracked = readTrackedRemote raw remote
                let trackedSnapshot, expected = tracked

                match trackedSnapshot, WriterStreamSync.tryCachedLocalSnapshot commonDir with
                | Some trackedValue, Some cached when sameSnapshot trackedValue cached -> return Ok cached
                | _ -> return! loop trackedSnapshot expected false maxRetries
            }

        match observedRemote with
        | Some snapshot ->
            let expected = Some(RootOid.value snapshot.RootOid)
            loop (Some snapshot) expected true maxRetries
        | None -> convergeWithoutObservedRemote ()

    /// Per-command hook budget: discovery fails fast, fetch/push may take longer.
    /// These are the hook-process limits under the administrator ceiling.
    let private estimateFor (args: string list) =
        let seconds =
            match args with
            | "ls-remote" :: _ -> 30.0
            | _ -> 120.0

        { EstimatedRuntime = RuntimeSeconds seconds
          EstimatedOutput = OutputBytes 65536L
          EstimatedMemory = EstimatedMemory.Medium }

    /// Production runner for the hook-process Git transport path.
    ///
    /// Routes through `ProcessRunner.run` (the canonical Process module entry)
    /// instead of importing `node:child_process` directly, matching the
    /// Orchestrator-side Git path (`OrchestratorGit.run`).  `WorkingDirectory`
    /// replaces the previous `git -C <repoPath>` argv prefix — equivalent for
    /// git, which resolves `.git` relative to the working directory.
    let createDefaultRunner (repoPath: string) : GitGatewayRunner =
        fun args ->
            task {
                let cmd =
                    { FileName = GitSubject.Executable
                      Arguments = args
                      WorkingDirectory = Some repoPath
                      Environment = None
                      Stdin = None
                      PtyOptions = None }

                let ctx =
                    { WorkingDirectory = Some repoPath
                      HardLimit = ProcessEstimate.DefaultHardLimit }

                let! res = ProcessRunner.run cmd (estimateFor args) ctx CancellationToken.None

                match res with
                | Ok(ProcessOutcome.Completed(code, stdout, stderr, _)) -> return (code, stdout, stderr)
                | Ok(ProcessOutcome.Spooled(code, _, _, _)) -> return (code, "", "output spooled")
                | Error err -> return (1, "", sprintf "%A" err)
            }
