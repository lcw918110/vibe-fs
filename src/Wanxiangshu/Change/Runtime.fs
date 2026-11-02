namespace Wanxiangshu.Change

open Fable.Core
open Fable.Core.JsInterop
open System.IO
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git
open Wanxiangshu.Persistence.Journal

module internal ManagerWorktreePolicy =
    let private shortBranch (reference: string) =
        let prefix = "refs/heads/"

        if reference.StartsWith(prefix, System.StringComparison.Ordinal) then
            reference.Substring prefix.Length
        else
            reference

    let managerBranchOf (jobId: string) = "manager/" + jobId

    let staleWorktrees (entries: (string * string option) list) (owned: string list) =
        let owners = Set.ofList owned

        entries
        |> List.choose (fun (path, branch) ->
            match branch with
            | Some reference ->
                let name = shortBranch reference

                if
                    name.StartsWith("manager/", System.StringComparison.Ordinal)
                    && not (Set.contains name owners)
                then
                    Some path
                else
                    None
            | None -> None)

    let staleBranches (branches: string list) (owned: string list) =
        let owners = Set.ofList owned

        branches
        |> List.filter (fun reference -> not (Set.contains (shortBranch reference) owners))

type Orchestrator
    (
        observer: IWaitObserver,
        git: GitPort,
        relay: RelayPort,
        repoPath: string,
        targetRef: TargetRef,
        ?journal: OrchestratorJournalPort,
        ?lockRepoPath: string
    ) =
    let mailbox = VerdictMailbox(observer)

    let gatePath =
        IntegrationGate.lockPath (defaultArg lockRepoPath repoPath) (TargetRef.value targetRef)

    let append stream fact =
        task {
            match journal with
            | None -> return Ok()
            | Some port ->
                let! result = port.AppendFact stream fact
                return result |> Result.map ignore
        }

    let snapshot () =
        journal
        |> Option.map (fun port -> port.Snapshot())
        |> Option.defaultValue Fold.empty

    let acquireGate () =
        task {
            let! gate = IntegrationGate.acquire gatePath
            return { Release = fun () -> gate.Release() }
        }

    let deps: OrchestratorProgramDeps =
        { Git = git
          Relay = relay
          AppendFact = append
          Snapshot = snapshot
          AcquirePublishGate = acquireGate }

    let resource (worktree: WorktreeResource) =
        createObj
            [ "identity" ==> WorktreeIdentity.value worktree.Identity
              "path" ==> WorktreePath.value worktree.Path
              "release" ==> (fun () -> worktree.Release() |> ChangeInterop.mapTask id)
              "markDurable" ==> (fun () -> worktree.MarkDurable()) ]

    let worktrees =
        createObj
            [ "create"
              ==> (fun identity path target ->
                  git.Worktrees.Create(
                      WorktreeIdentity.create identity,
                      WorktreePath.create path,
                      TargetRef.create target
                  )
                  |> ChangeInterop.mapTask resource)
              "adopt"
              ==> (fun identity path ->
                  git.Worktrees.Adopt(WorktreeIdentity.create identity, WorktreePath.create path)
                  |> resource)
              "list"
              ==> (fun () ->
                  git.Worktrees.List()
                  |> ChangeInterop.mapTask (fun entries ->
                      entries
                      |> List.map (fun (path, identity) ->
                          createObj
                              [ "path" ==> WorktreePath.value path
                                "identity" ==> (identity |> Option.map WorktreeIdentity.value |> Option.toObj) ])
                      |> List.toArray)) ]

    let effects =
        createObj
            [ "publication" ==> ChangeInterop.collaborators deps
              "worktrees" ==> worktrees
              "repoPath" ==> repoPath
              "durable" ==> journal.IsSome
              "isDirty"
              ==> (fun path -> git.IsDirty(WorktreePath.create path) |> ChangeInterop.mapTask List.toArray)
              "createSession"
              ==> (fun (request: obj) ->
                  relay.CreateManagerSession
                      { JobId = ManagerJobId.create request?id
                        ManagerAgent = request?agent
                        Worktree = WorktreePath.create request?path
                        RootRequest = request?prompt
                        ExpectedToolCalls =
                          if isNull request?expectedToolCalls then
                              None
                          else
                              Some(unbox<int> request?expectedToolCalls) }
                  |> ChangeInterop.mapTask SessionId.value)
              "activate"
              ==> (fun id -> relay.ActivateManager(ManagerJobId.create id) |> ChangeInterop.mapTask ignore)
              "startJob" ==> (fun () -> mailbox.StartJob())
              "publish" ==> (fun raw -> mailbox.Publish(ChangeInterop.verdict raw))
              "formatError" ==> (fun (error: exn) -> sprintf "%A" error) ]

    let native = createNew (import "ChangeRuntime" "wanxiangli/change") effects

    member _.ForkManager
        (
            jobId: ManagerJobId,
            managerAgent: string,
            prompt: string,
            ?worktreePath: WorktreePath,
            ?byname: string,
            ?expectedToolCalls: int
        ) =
        let byname =
            match byname with
            | Some name when not (System.String.IsNullOrWhiteSpace name) -> name.Trim()
            | _ -> managerAgent

        let id = ManagerJobId.value jobId

        let path =
            worktreePath
            |> Option.map WorktreePath.value
            |> Option.defaultWith (fun () -> Path.Combine(Path.GetTempPath(), sprintf "wanxiangshu-%s" id))

        let request =
            createObj
                [ "id" ==> id
                  "agent" ==> managerAgent
                  "byname" ==> byname
                  "prompt" ==> prompt
                  "path" ==> path
                  "identity" ==> ManagerWorktreePolicy.managerBranchOf id
                  "target" ==> TargetRef.value targetRef
                  "expectedToolCalls"
                  ==> (expectedToolCalls |> Option.map box |> Option.defaultValue null) ]

        task {
            let! result = unbox<Task<obj>> (native?start (request))

            if unbox<bool> result?ok then
                return
                    Ok
                        { JobId = jobId
                          WorktreePath = WorktreePath.create result?value?path }
            else
                return Error(ChangeInterop.verdict result?error)
        }

    member _.RecoverManagerJob(record: ManagerJobProjection) : unit =
        native?recover (OrchestratorProjection.toJob record)

    member _.JoinPublishedBatch(maxCount: int, interrupt: Task<JoinInterruptReason>) =
        mailbox.JoinAvailable(maxCount, interrupt)
