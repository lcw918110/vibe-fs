namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation

/// Workspace-host-owned shared journal surface.
/// Runtime paths and EventStore capabilities stay opaque; callers observe only
/// identity, keyed Current presence, and append outcomes.
[<RequireQualifiedAccess>]
module WorkspaceEventStoreSurface =

    let private errorText (error: FoldRejection) = $"{error.Fact}: {error.Reason}"

    let private acquireJournal
        (commonDirectory: string)
        (runtimeDirectory: string)
        (processId: int)
        (startedAt: string)
        : Task<obj> =
        task {
            let boot = Wanxiangshu.OpenCode.WorkspaceEventStore.bootPort commonDirectory

            let openJournal runtimeId processIdValue processStartedAt =
                task {
                    let! result = boot.ResumeOrCreate(runtimeId, processIdValue, processStartedAt)

                    match result with
                    | Ok(writer, _, projection) -> return AgentJournal.createFromProjection writer projection
                    | Error error -> return Error error
                }

            let! result =
                SharedAgentJournal.acquire runtimeDirectory processId (DateTimeOffset.Parse startedAt) openJournal

            match result with
            | Ok journal ->
                return
                    box
                        {| ok = true
                           journal = JournalHandle.CreateShared(journal, None) |}
            | Error error ->
                return
                    box
                        {| ok = false
                           error = errorText error |}
        }

    /// Acquire the process-local workspace journal for raw directories.
    let acquire (retiredDirectory: string) (commonDirectory: string) (processId: int) (startedAt: string) : Task<obj> =
        acquireJournal commonDirectory retiredDirectory processId startedAt

    /// Acquire the plugin's process-local workspace journal through the same
    /// runtime-path owner as the composition root. Returns a standard
    /// `JournalHandle` suited for `JournalSurface` operations.
    let acquireSharedForWorkspace (workspace: string) (processId: int) (startedAt: string) : Task<obj> =
        task {
            let! commonDir = RuntimePath.initializeStateRoot workspace
            let! runtimeDir = RuntimePath.forWorkspace workspace
            return! acquireJournal commonDir runtimeDir processId startedAt
        }

    /// Release a workspace journal handle.
    let release (handle: JournalHandle) : unit = handle.Dispose()

    /// Test whether two handles refer to the same journal instance.
    let same (left: JournalHandle) (right: JournalHandle) : bool =
        obj.ReferenceEquals(left.Journal, right.Journal)
