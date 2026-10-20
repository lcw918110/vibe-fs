namespace Wanxiangshu.Change

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Process
open Wanxiangshu.Git
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Mission.Work

/// JS-native boundary for change integration. Job projections and git/worktree
/// resources stay behind opaque handles; answers and failures are plain objects.
module ChangeSurface =

    type private ProjectionHandle(projection: OrchestratorProjection) =
        member _.Projection = projection

    type private GitHandle(port: GitPort, repo: string, runner: Command -> Task<int * string * string>) =
        member _.Port = port
        member _.repo = repo
        member _.Runner = runner

    type private WorktreeHandle(resource: WorktreeResource) =
        member _.Resource = resource

    type private GateHandle(gate: IntegrationGate) =
        member _.Gate = gate

    [<Emit("$0==null")>]
    let private isNullish (value: obj) : bool = jsNative

    [<Emit("Promise.resolve($0)")>]
    let private asPromise (value: obj) : JS.Promise<obj> = jsNative

    [<Emit("$0($1)")>]
    let private apply1 (fn: obj) (arg: obj) : obj = jsNative

    /// Production cancellation control outcome. Fable erases
    /// `new OperationCanceledException` to `System.Exception`, which the
    /// production `OrchestratorProgram.run` catch (`:? OperationCanceledException`)
    /// would no longer recognize — so cancellation doubles throw this exact
    /// runtime class (the same module instance the production catch matches).
    /// Coupled to the pinned Fable 5.13.0 toolchain (see .config/dotnet-tools.json):
    /// a Fable bump renames the fable-library path below.
    /// Fable resolves this path against the source file and re-bases it against
    /// the emitted file, so it is written source-relative to land on
    /// `dist/fable_modules/...` at runtime (same module instance Program.js uses).
    [<Import("OperationCanceledException", "../../../dist/fable_modules/fable-library-js.5.13.0/AsyncBuilder.js")>]
    let private operationCanceledCtor (message: string) : obj = jsNative

    [<Emit("throw new $0($1)")>]
    let private throwProductionCancellation (ctor: obj) (message: string) : 'T = jsNative

    let private property (value: obj) (name: string) : obj = emitJsExpr (value, name) "$0[$1]"

    [<Emit("undefined")>]
    let private jsUndefined: obj = jsNative

    let private optionObj (value: 'T option) : obj =
        match value with
        | Some value -> box value
        | None -> jsUndefined

    let private stringOf (value: obj) =
        if isNullish value then "" else string value

    let private field (value: obj) (names: string list) : obj =
        names
        |> List.tryPick (fun name ->
            let result = property value name
            if isNullish result then None else Some result)
        |> Option.defaultValue null

    let private stringField value names = stringOf (field value names)

    let private stringArray (value: obj) : string array =
        if isNullish value then
            [||]
        else
            unbox<obj array> value |> Array.map stringOf

    let private jobId value = ManagerJobId.create (stringOf value)
    let private sessionId value = SessionId.create (stringOf value)
    let private commit value = CommitHash.create (stringOf value)

    let private authorityRevision value =
        AuthorityRevision.create (stringOf value)

    let private worktreeIdentityValue value =
        WorktreeIdentity.create (stringOf value)

    let private worktreePathValue value = WorktreePath.create (stringOf value)
    let private target value = TargetRef.create (stringOf value)

    let private tryField (value: obj) (names: string list) : obj option =
        if isNullish value then
            None
        else
            names
            |> List.tryPick (fun name ->
                let result = property value name
                if isNullish result then None else Some result)

    let private decodePublishClaimed (payload: obj) =
        let targetRefStr =
            tryField payload [ "targetRef"; "TargetRef" ] |> Option.map stringOf

        let rebasedCommitStr =
            tryField payload [ "rebasedCommit"; "RebasedCommit" ] |> Option.map stringOf

        let expectedHeadStr =
            tryField payload [ "expectedHead"; "ExpectedHead" ] |> Option.map stringOf

        let authorityStr =
            tryField payload [ "authorityRevision"; "AuthorityRevision" ]
            |> Option.map stringOf

        match targetRefStr, rebasedCommitStr, expectedHeadStr, authorityStr with
        | Some tr, Some rc, Some eh, Some ar when
            not (String.IsNullOrWhiteSpace tr)
            && not (String.IsNullOrWhiteSpace rc)
            && not (String.IsNullOrWhiteSpace eh)
            && not (String.IsNullOrWhiteSpace ar)
            ->
            Ok
                {| TargetRef = target tr
                   RebasedCommit = commit rc
                   ExpectedHead = commit eh
                   AuthorityRevision = authorityRevision ar |}
        | _ -> Error "Incomplete PublishClaimed payload: missing required publication evidence"

    let private factKindAndPayload (value: obj) =
        stringField value [ "kind"; "case"; "name" ], field value [ "payload"; "value"; "data" ]

    let fact (kind: string) (payload: obj) : obj =
        box {| kind = kind; payload = payload |}

    let private recordFactValue
        (projection: OrchestratorProjection)
        (managerJobId: ManagerJobId)
        (value: obj)
        : OrchestratorProjection =
        let kind, payload = factKindAndPayload value

        match kind with
        | "CandidateReady" ->
            OrchestratorProjection.recordCandidateReady
                managerJobId
                {| CandidateCommit = commit (field payload [ "candidateCommit"; "CandidateCommit" ]) |}
                projection
        | "ConflictDetected" ->
            OrchestratorProjection.recordConflictDetected
                managerJobId
                {| CandidateCommit = commit (field payload [ "candidateCommit"; "CandidateCommit" ])
                   TargetHeadSnapshot = commit (field payload [ "targetHeadSnapshot"; "TargetHeadSnapshot" ])
                   ConflictFiles = stringArray (field payload [ "conflictFiles"; "ConflictFiles" ]) |> Array.toList
                   DiagnosticsDigest = stringField payload [ "diagnosticsDigest"; "DiagnosticsDigest" ] |}
                projection
        | "RebasedCandidateReady" ->
            OrchestratorProjection.recordRebasedCandidateReady
                managerJobId
                {| RebasedCommit = commit (field payload [ "rebasedCommit"; "RebasedCommit" ])
                   TargetHeadSnapshot = commit (field payload [ "targetHeadSnapshot"; "TargetHeadSnapshot" ]) |}
                projection
        | "PublishClaimed" ->
            match decodePublishClaimed payload with
            | Ok claim -> OrchestratorProjection.recordPublishClaimed managerJobId claim projection
            | Error err -> invalidArg "fact" err
        | "Published" ->
            OrchestratorProjection.recordTerminal
                managerJobId
                (TerminalOutcome.Published
                    {| CandidateCommit = commit (field payload [ "candidateCommit"; "CandidateCommit" ])
                       ResultingTargetHead = commit (field payload [ "resultingTargetHead"; "ResultingTargetHead" ]) |})
                projection
        | "JobFailed" ->
            OrchestratorProjection.recordTerminal
                managerJobId
                (TerminalOutcome.Failed(stringField payload [ "reason"; "Reason" ]))
                projection
        | "JobAbandoned" -> OrchestratorProjection.recordTerminal managerJobId TerminalOutcome.Abandoned projection
        | unknown -> invalidArg "fact" ("unknown ManagerJob fact: " + unknown)

    let private jobObject (job: ManagerJobProjection) : obj =
        let facts =
            [ if job.CandidateReady.IsSome then
                  yield "CandidateReady"

              if job.ConflictDetected.IsSome then
                  yield "ConflictDetected"

              if job.RebasedCandidateReady.IsSome then
                  yield "RebasedCandidateReady"

              if job.PublishClaimed.IsSome then
                  yield "PublishClaimed"

              match job.Terminal with
              | Some(TerminalOutcome.Published _) -> yield "Published"
              | Some(TerminalOutcome.Failed _) -> yield "JobFailed"
              | Some TerminalOutcome.Abandoned -> yield "JobAbandoned"
              | None -> () ]
            |> List.toArray

        box
            {| jobId = ManagerJobId.value job.ManagerJobId
               managerSessionId = SessionId.value job.ManagerSessionId
               managerAgent = job.ManagerAgent
               byname = job.Byname
               worktreeIdentity = WorktreeIdentity.value job.WorktreeIdentity
               worktreePath = WorktreePath.value job.WorktreePath
               targetRef = TargetRef.value job.TargetRef
               targetBranchFrozen = job.TargetBranchFrozen
               facts = facts |}

    let private createPayload (value: obj) =
        {| ManagerJobId = jobId (field value [ "jobId"; "ManagerJobId" ])
           ManagerSessionId = sessionId (field value [ "managerSessionId"; "ManagerSessionId" ])
           ManagerAgent = stringField value [ "managerAgent"; "ManagerAgent" ]
           Byname = stringField value [ "byname"; "Byname" ]
           WorktreeIdentity = worktreeIdentityValue (field value [ "worktreeIdentity"; "WorktreeIdentity" ])
           WorktreePath = worktreePathValue (field value [ "worktreePath"; "WorktreePath" ])
           TargetRef = target (field value [ "targetRef"; "TargetRef" ])
           TargetBranchFrozen = stringField value [ "targetBranchFrozen"; "TargetBranchFrozen" ] |}

    let empty () : obj =
        ProjectionHandle OrchestratorProjection.empty :> obj

    let recordFact (projection: obj) (job: string) (value: obj) : obj =
        let current = (projection :?> ProjectionHandle).Projection
        ProjectionHandle(recordFactValue current (jobId job) value) :> obj

    let private getProjection (state: obj) : OrchestratorProjection option =
        if isNullish state then
            None
        elif state :? ProjectionHandle then
            Some (state :?> ProjectionHandle).Projection
        else
            let v = property state "projection"

            if not (isNullish v) && v :? OrchestratorProjection then
                Some(v :?> OrchestratorProjection)
            else
                let wrapped = property state "value"

                if not (isNullish wrapped) && wrapped :? ProjectionHandle then
                    Some (wrapped :?> ProjectionHandle).Projection
                else
                    None

    let activeJobs (projection: obj) : obj array =
        match getProjection projection with
        | Some current -> OrchestratorProjection.activeJobs current |> List.map jobObject |> List.toArray
        | None -> [||]

    let find (projection: obj) (job: string) : obj =
        let projHandle =
            if not (isNullish projection) && stringField projection [ "ok" ] = "true" then
                field projection [ "value" ]
            else
                projection

        match getProjection projHandle with
        | Some current ->
            match OrchestratorProjection.tryFind (jobId job) current with
            | Some value -> jobObject value
            | None -> null
        | None -> null

    let createJob (projection: obj) (payload: obj) : obj =
        let proj =
            getProjection projection |> Option.defaultValue OrchestratorProjection.empty

        let next = OrchestratorProjection.createJob (createPayload payload) proj
        ProjectionHandle next :> obj

    let job (state: obj) (jobId: string) : obj = find state jobId

    let jobForSession (state: obj) (sessionId: string) : obj =
        match getProjection state with
        | Some current ->
            let sid = SessionId.create (stringOf sessionId)

            match OrchestratorProjection.tryFindByManagerSession sid current with
            | Some value -> jobObject value
            | None -> null
        | None -> null

    let isTerminal (state: obj) (jobId: string) : bool =
        match getProjection state with
        | Some current ->
            match OrchestratorProjection.tryFind (ManagerJobId.create (stringOf jobId)) current with
            | Some j -> j.Terminal.IsSome
            | None -> false
        | None -> false

    let isOutstanding (state: obj) (jobId: string) : bool =
        match getProjection state with
        | Some current ->
            match OrchestratorProjection.tryFind (ManagerJobId.create (stringOf jobId)) current with
            | Some j -> j.Terminal.IsNone
            | None -> false
        | None -> false

    /// ORCH-007 domain classification for a rebased candidate. Returns a
    /// physical-world classification, not a program counter.
    let classifyRebasedCandidate (head: obj) (rebasedCommit: string) (targetHeadSnapshot: string) : obj =
        let currentHead = if isNullish head then None else Some(commit head)

        let reality =
            OrchestratorProjection.classifyRebasedCandidate
                currentHead
                (commit rebasedCommit)
                (commit targetHeadSnapshot)

        match reality with
        | RebasedCandidateReality.HeadUnreadable -> box {| kind = "HeadUnreadable" |}
        | RebasedCandidateReality.PublishReady -> box {| kind = "PublishReady" |}
        | RebasedCandidateReality.NeedsRebase -> box {| kind = "NeedsRebase" |}

    /// ORCH-007 domain classification for a publish claim. Three branches in
    /// fixed order: already-published first, then unchanged target, then
    /// everything else.
    let classifyPublishClaim (head: obj) (rebasedCommit: string) (expectedHead: string) : obj =
        let currentHead = if isNullish head then None else Some(commit head)

        let reality =
            OrchestratorProjection.classifyPublishClaim currentHead (commit rebasedCommit) (commit expectedHead)

        match reality with
        | PublishClaimReality.HeadUnreadable -> box {| kind = "HeadUnreadable" |}
        | PublishClaimReality.AlreadyFastForwarded -> box {| kind = "AlreadyFastForwarded" |}
        | PublishClaimReality.PublishReady -> box {| kind = "PublishReady" |}
        | PublishClaimReality.ClaimExpired -> box {| kind = "ClaimExpired" |}

    let requestWorktree (projection: obj) (identity: string) (path: string) (job: string) : obj =
        let current = (projection :?> ProjectionHandle).Projection

        ProjectionHandle(
            OrchestratorProjection.requestWorktree
                (worktreeIdentityValue identity)
                (worktreePathValue path)
                (jobId job)
                current
        )
        :> obj

    let acceptWorktree (projection: obj) (identity: string) (path: string) (job: string) : obj =
        let current = (projection :?> ProjectionHandle).Projection

        ProjectionHandle(
            OrchestratorProjection.acceptWorktree
                (worktreeIdentityValue identity)
                (worktreePathValue path)
                (jobId job)
                current
        )
        :> obj

    let worktreeEffect (projection: obj) (identity: string) : obj =
        let current = (projection :?> ProjectionHandle).Projection

        match OrchestratorProjection.tryWorktreeEffect (worktreeIdentityValue identity) current with
        | None -> null
        | Some status ->
            match status with
            | WorktreeEffectStatus.Requested _ -> box "Requested"
            | WorktreeEffectStatus.Created _ -> box "Created"

    /// JS-native semantic surface for the pure Requested/Created reconciliation law.
    let worktreeReconciliationDecision (job: string) (identity: string) (path: string) (evidence: obj) : obj =
        let evidenceKind = stringField evidence [ "kind" ]
        let recordedJob = jobId (field evidence [ "jobId" ])
        let recordedPath = worktreePathValue (field evidence [ "path" ])

        let entries () =
            field evidence [ "entries" ]
            |> unbox<obj array>
            |> Array.map (fun entry ->
                let identityValue = field entry [ "identity" ]

                worktreePathValue (field entry [ "path" ]),
                if isNullish identityValue then
                    None
                else
                    Some(worktreeIdentityValue identityValue))
            |> Array.toList

        let observation =
            match evidenceKind with
            | "NoDurableEffect" -> WorktreeReconciliationObservation.NoDurableEffect
            | "CreatedReceipt" -> WorktreeReconciliationObservation.CreatedReceipt(recordedJob, recordedPath)
            | "RequestedConflict" -> WorktreeReconciliationObservation.RequestedConflict(recordedJob, recordedPath)
            | "RequestedEntries" ->
                WorktreeReconciliationObservation.RequestedAmbiguity(recordedJob, recordedPath, Ok(entries ()))
            | "RequestedQueryFailure" ->
                WorktreeReconciliationObservation.RequestedAmbiguity(
                    recordedJob,
                    recordedPath,
                    Error(stringField evidence [ "error" ])
                )
            | unknown -> invalidArg "evidence" ("unknown worktree reconciliation evidence: " + unknown)

        let decision =
            OrchestratorProjection.decideWorktreeReconciliation
                (ManagerJobId.create job)
                (WorktreeIdentity.create identity)
                (WorktreePath.create path)
                observation

        match decision with
        | WorktreeReconciliationDecision.RequestThenCreate -> box {| kind = "RequestThenCreate" |}
        | WorktreeReconciliationDecision.CreateAfterProvenMissing -> box {| kind = "CreateAfterProvenMissing" |}
        | WorktreeReconciliationDecision.AdoptThenRecordCreated -> box {| kind = "AdoptThenRecordCreated" |}
        | WorktreeReconciliationDecision.AdoptCreated -> box {| kind = "AdoptCreated" |}
        | WorktreeReconciliationDecision.Reject failure ->
            let reason =
                match failure with
                | WorktreeReconciliationFailure.DurableOwnershipConflict -> "DurableOwnershipConflict"
                | WorktreeReconciliationFailure.WorktreeQueryFailed _ -> "WorktreeQueryFailed"
                | WorktreeReconciliationFailure.PhysicalIdentityPathConflict -> "PhysicalIdentityPathConflict"

            box {| kind = "Reject"; reason = reason |}

    let private foldPublishClaimed (projection: OrchestratorProjection) (managerJobId: ManagerJobId) (payload: obj) =
        match OrchestratorProjection.tryFind managerJobId projection with
        | None -> Error "publish claimed for unknown job"
        | Some job ->
            match job.RebasedCandidateReady with
            | None -> Error "publish claimed for a job with no rebased candidate (ORCH-004)"
            | Some rebased ->
                match decodePublishClaimed payload with
                | Error err -> Error err
                | Ok claim ->
                    if claim.RebasedCommit <> rebased.RebasedCommit then
                        Error "publish claimed commit does not match admitted rebased commit"
                    else
                        // Latest-event view identical to Fold: a retried publish
                        // with fresh evidence supersedes the earlier claim.
                        Ok(OrchestratorProjection.recordPublishClaimed managerJobId claim projection)

    let private applyEvent (projection: OrchestratorProjection) (event: obj) : Result<OrchestratorProjection, string> =
        let kind = stringField event [ "kind"; "case"; "type" ]

        let payload =
            let p = field event [ "payload"; "value"; "data" ]
            if isNullish p then event else p

        match kind with
        | "ManagerJobCreated" -> Ok(OrchestratorProjection.createJob (createPayload payload) projection)
        | "PublishClaimed" -> foldPublishClaimed projection (jobId (field payload [ "jobId"; "ManagerJobId" ])) payload
        | "CandidateReady"
        | "ConflictDetected" ->
            Ok(recordFactValue projection (jobId (field payload [ "jobId"; "ManagerJobId" ])) (fact kind payload))
        | "RebasedCandidateReady" ->
            let targetHead =
                let t = field payload [ "targetHead"; "targetHeadSnapshot"; "TargetHeadSnapshot" ]

                if isNullish t then
                    field payload [ "rebasedCommit"; "RebasedCommit" ]
                else
                    t

            let normalizedPayload =
                box
                    {| rebasedCommit = field payload [ "rebasedCommit"; "RebasedCommit" ]
                       targetHeadSnapshot = targetHead |}

            Ok(
                recordFactValue
                    projection
                    (jobId (field payload [ "jobId"; "ManagerJobId" ]))
                    (fact kind normalizedPayload)
            )
        | "Published"
        | "JobFailed"
        | "JobAbandoned" ->
            Ok(recordFactValue projection (jobId (field payload [ "jobId"; "ManagerJobId" ])) (fact kind payload))
        | "WorktreeCreateRequested" ->
            Ok(
                OrchestratorProjection.requestWorktree
                    (worktreeIdentityValue (field payload [ "worktreeIdentity"; "WorktreeIdentity" ]))
                    (worktreePathValue (field payload [ "worktreePath"; "WorktreePath" ]))
                    (jobId (field payload [ "jobId"; "ManagerJobId" ]))
                    projection
            )
        | "WorktreeCreated" ->
            Ok(
                OrchestratorProjection.acceptWorktree
                    (worktreeIdentityValue (field payload [ "worktreeIdentity"; "WorktreeIdentity" ]))
                    (worktreePathValue (field payload [ "worktreePath"; "WorktreePath" ]))
                    (jobId (field payload [ "jobId"; "ManagerJobId" ]))
                    projection
            )
        | unknown -> Error("unknown orchestrator event: " + unknown)

    let fold (events: obj array) : obj =
        // DSL-MUTABLE: algorithm-scratch — fold accumulator
        let mutable projection = OrchestratorProjection.empty
        // DSL-MUTABLE: algorithm-scratch — first fold failure
        let mutable failure: string option = None

        for event in events do
            match failure with
            | Some _ -> ()
            | None ->
                match applyEvent projection event with
                | Ok next -> projection <- next
                | Error reason -> failure <- Some reason

        match failure with
        | Some reason -> box {| ok = false; error = reason |}
        | None ->
            box
                {| ok = true
                   value = ProjectionHandle projection |}

    let unwrapFold (result: obj) : obj =
        if stringField result [ "ok" ] = "false" then
            null
        else
            field result [ "value" ]

    [<RequireQualifiedAccess>]
    type private ProgramScenarioSignal =
        | QualityCandidate of snapshot: string
        | Retired
        | Exceptional of reason: string

    [<RequireQualifiedAccess>]
    type private ProgramScenarioRebase =
        | Ok of resultingHead: string
        | Error of reason: string

    [<RequireQualifiedAccess>]
    type private ProgramScenarioFf =
        | Ok of landedHead: string
        | TargetMoved

    type private ProgramScenario =
        { InitialHead: string
          InitialTarget: string
          InitialRebasedTarget: string option
          Signals: ProgramScenarioSignal list
          Snapshots: string list
          TargetReads: string list
          RebaseResults: ProgramScenarioRebase list
          ConflictReads: string list list
          FfResults: ProgramScenarioFf list
          SeededCandidateReady: (string * string * string) option
          SeededRebasedReady: (string * string * string) option
          SeededPublishClaimed: (string * string * string * string * string * string) option
          SeededPublished: (string * string) option
          WorktreeReads: string list
          FailPublishedAppend: bool
          GateCancelled: bool
          CleanupFails: bool }

    let private programScenario name =
        match name with
        | "fresh" ->
            { InitialHead = "candidate-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals =
                [ ProgramScenarioSignal.QualityCandidate "snapshot-1"
                  ProgramScenarioSignal.QualityCandidate "snapshot-rebased-1" ]
              Snapshots =
                [ "snapshot-1"
                  "snapshot-rebased-1"
                  "snapshot-rebased-1"
                  "snapshot-rebased-1"
                  "snapshot-rebased-1" ]
              TargetReads = [ "target-1"; "target-1"; "target-1" ]
              RebaseResults = [ ProgramScenarioRebase.Ok "rebased-1" ]
              ConflictReads = [ []; []; [] ]
              FfResults = [ ProgramScenarioFf.Ok "rebased-1" ]
              SeededCandidateReady = None
              SeededRebasedReady = None
              SeededPublishClaimed = None
              SeededPublished = None
              WorktreeReads = [ "rebased-1"; "rebased-1" ]
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "rebase-conflict" ->
            { InitialHead = "candidate-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals =
                [ ProgramScenarioSignal.QualityCandidate "snapshot-1"
                  ProgramScenarioSignal.Exceptional "scenario-complete" ]
              Snapshots = [ "snapshot-1"; "snapshot-conflict" ]
              TargetReads = [ "target-1" ]
              RebaseResults = [ ProgramScenarioRebase.Error "rebase conflict" ]
              ConflictReads = [ []; [ "conflict.fs" ] ]
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = None
              SeededPublishClaimed = None
              SeededPublished = None
              WorktreeReads = []
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "target-moved" ->
            { InitialHead = "rebased-1"
              InitialTarget = "target-2"
              InitialRebasedTarget = Some "target-1"
              Signals =
                [ ProgramScenarioSignal.QualityCandidate "snapshot-rebased-1"
                  ProgramScenarioSignal.Exceptional "scenario-complete" ]
              Snapshots = [ "snapshot-rebased-1"; "snapshot-rebased-2" ]
              TargetReads = [ "target-2" ]
              RebaseResults = [ ProgramScenarioRebase.Ok "rebased-2" ]
              ConflictReads = [ [] ]
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = None
              SeededPublishClaimed = None
              SeededPublished = None
              WorktreeReads = []
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "cas-miss" ->
            { InitialHead = "rebased-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = Some "target-1"
              Signals =
                [ ProgramScenarioSignal.QualityCandidate "snapshot-rebased-1"
                  ProgramScenarioSignal.Exceptional "scenario-complete" ]
              Snapshots =
                [ "snapshot-rebased-1"
                  "snapshot-rebased-1"
                  "snapshot-rebased-1"
                  "snapshot-rebased-2" ]
              TargetReads = [ "target-1"; "target-1"; "target-2" ]
              RebaseResults = [ ProgramScenarioRebase.Ok "rebased-2" ]
              ConflictReads = [ []; [] ]
              FfResults = [ ProgramScenarioFf.TargetMoved ]
              SeededCandidateReady = None
              SeededRebasedReady = None
              SeededPublishClaimed = None
              SeededPublished = None
              WorktreeReads = [ "rebased-1"; "rebased-2" ]
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "rebase-reuse-old-cert" ->
            { InitialHead = "candidate-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals =
                [ ProgramScenarioSignal.QualityCandidate "snapshot-1"
                  ProgramScenarioSignal.QualityCandidate "snapshot-1" ]
              Snapshots = [ "snapshot-1"; "snapshot-rebased-1"; "snapshot-1" ]
              TargetReads = [ "target-1"; "target-1" ]
              RebaseResults = [ ProgramScenarioRebase.Ok "rebased-1" ]
              ConflictReads = [ []; [] ]
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = None
              SeededPublishClaimed = None
              SeededPublished = None
              WorktreeReads = [ "rebased-1" ]
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "stale-certificate" ->
            { InitialHead = "candidate-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals =
                [ ProgramScenarioSignal.QualityCandidate "snapshot-certified"
                  ProgramScenarioSignal.Exceptional "scenario-complete" ]
              Snapshots = [ "snapshot-current" ]
              TargetReads = []
              RebaseResults = []
              ConflictReads = []
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = None
              SeededPublishClaimed = None
              SeededPublished = None
              WorktreeReads = []
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "artifact-conflict" ->
            { InitialHead = "candidate-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals =
                [ ProgramScenarioSignal.QualityCandidate "snapshot-1"
                  ProgramScenarioSignal.Exceptional "scenario-complete" ]
              Snapshots = [ "snapshot-1" ]
              TargetReads = [ "target-1" ]
              RebaseResults = []
              ConflictReads = [ [ "conflict.fs" ] ]
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = None
              SeededPublishClaimed = None
              SeededPublished = None
              WorktreeReads = []
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "retired" ->
            { InitialHead = "candidate-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals =
                [ ProgramScenarioSignal.Retired
                  ProgramScenarioSignal.Exceptional "scenario-complete" ]
              Snapshots = []
              TargetReads = []
              RebaseResults = []
              ConflictReads = []
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = None
              SeededPublishClaimed = None
              SeededPublished = None
              WorktreeReads = []
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "cleanup-failed" ->
            { InitialHead = "candidate-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals =
                [ ProgramScenarioSignal.QualityCandidate "snapshot-1"
                  ProgramScenarioSignal.QualityCandidate "snapshot-rebased-1" ]
              Snapshots =
                [ "snapshot-1"
                  "snapshot-rebased-1"
                  "snapshot-rebased-1"
                  "snapshot-rebased-1"
                  "snapshot-rebased-1" ]
              TargetReads = [ "target-1"; "target-1"; "target-1" ]
              RebaseResults = [ ProgramScenarioRebase.Ok "rebased-1" ]
              ConflictReads = [ []; []; [] ]
              FfResults = [ ProgramScenarioFf.Ok "rebased-1" ]
              SeededCandidateReady = None
              SeededRebasedReady = None
              SeededPublishClaimed = None
              SeededPublished = None
              WorktreeReads = [ "rebased-1"; "rebased-1" ]
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = true }
        | "cancelled-program" ->
            { InitialHead = "candidate-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals = []
              Snapshots = []
              TargetReads = []
              RebaseResults = []
              ConflictReads = []
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = None
              SeededPublishClaimed = None
              SeededPublished = None
              WorktreeReads = []
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "reentry-valid" ->
            { InitialHead = "rebased-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals = []
              Snapshots = [ "snapshot-rebased-1"; "snapshot-rebased-1" ]
              TargetReads = [ "target-1" ]
              RebaseResults = []
              ConflictReads = [ [] ]
              FfResults = [ ProgramScenarioFf.Ok "rebased-1" ]
              SeededCandidateReady = None
              SeededRebasedReady = Some("rebased-1", "target-1", "snapshot-rebased-1")
              SeededPublishClaimed =
                Some("refs/heads/main", "rebased-1", "target-1", "snapshot-rebased-1", "certificate-1", "authority-1")
              SeededPublished = None
              WorktreeReads = [ "rebased-1"; "rebased-1" ]
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "reentry-wrong-snapshot" ->
            { InitialHead = "rebased-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals = [ ProgramScenarioSignal.Exceptional "scenario-complete" ]
              Snapshots = [ "snapshot-1" ]
              TargetReads = []
              RebaseResults = []
              ConflictReads = []
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = Some("rebased-1", "target-1", "snapshot-rebased-1")
              SeededPublishClaimed =
                Some("refs/heads/main", "rebased-1", "target-1", "snapshot-rebased-1", "certificate-1", "authority-1")
              SeededPublished = None
              WorktreeReads = [ "rebased-1" ]
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "reentry-missing-rebased" ->
            { InitialHead = "rebased-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals = []
              Snapshots = [ "snapshot-rebased-1" ]
              TargetReads = []
              RebaseResults = []
              ConflictReads = []
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = None
              SeededPublishClaimed =
                Some("refs/heads/main", "rebased-1", "target-1", "snapshot-rebased-1", "certificate-1", "authority-1")
              SeededPublished = None
              WorktreeReads = [ "rebased-1" ]
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "reentry-claim-snapshot-mismatch" ->
            { InitialHead = "rebased-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals = [ ProgramScenarioSignal.Exceptional "scenario-complete" ]
              Snapshots = [ "snapshot-rebased-1" ]
              TargetReads = []
              RebaseResults = []
              ConflictReads = []
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = Some("rebased-1", "target-1", "snapshot-rebased-1")
              SeededPublishClaimed =
                Some("refs/heads/main", "rebased-1", "target-1", "snapshot-rebased-2", "certificate-1", "authority-1")
              SeededPublished = None
              WorktreeReads = [ "rebased-1" ]
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "reentry-claim-target-mismatch" ->
            { InitialHead = "rebased-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals = []
              Snapshots = [ "snapshot-rebased-1" ]
              TargetReads = []
              RebaseResults = []
              ConflictReads = []
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = Some("rebased-1", "target-1", "snapshot-rebased-1")
              SeededPublishClaimed =
                Some("refs/heads/other", "rebased-1", "target-1", "snapshot-rebased-1", "certificate-1", "authority-1")
              SeededPublished = None
              WorktreeReads = [ "rebased-1" ]
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "reentry-candidate-moved" ->
            { InitialHead = "rebased-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals = []
              Snapshots = [ "snapshot-rebased-1"; "snapshot-rebased-1" ]
              TargetReads = [ "target-1" ]
              RebaseResults = []
              ConflictReads = [ [] ]
              FfResults = [ ProgramScenarioFf.Ok "rebased-1" ]
              SeededCandidateReady = None
              SeededRebasedReady = Some("rebased-1", "target-1", "snapshot-rebased-1")
              SeededPublishClaimed =
                Some("refs/heads/main", "rebased-1", "target-1", "snapshot-rebased-1", "certificate-1", "authority-1")
              SeededPublished = None
              WorktreeReads = [ "rebased-1"; "rebased-2" ]
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "reentry-landed-mismatch" ->
            { InitialHead = "rebased-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals = []
              Snapshots = [ "snapshot-rebased-1"; "snapshot-rebased-1" ]
              TargetReads = [ "target-1" ]
              RebaseResults = []
              ConflictReads = [ [] ]
              FfResults = [ ProgramScenarioFf.Ok "other-landed" ]
              SeededCandidateReady = None
              SeededRebasedReady = Some("rebased-1", "target-1", "snapshot-rebased-1")
              SeededPublishClaimed =
                Some("refs/heads/main", "rebased-1", "target-1", "snapshot-rebased-1", "certificate-1", "authority-1")
              SeededPublished = None
              WorktreeReads = [ "rebased-1"; "rebased-1" ]
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "reentry-gate-cancelled" ->
            { InitialHead = "rebased-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals = []
              Snapshots = [ "snapshot-rebased-1" ]
              TargetReads = []
              RebaseResults = []
              ConflictReads = []
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = Some("rebased-1", "target-1", "snapshot-rebased-1")
              SeededPublishClaimed =
                Some("refs/heads/main", "rebased-1", "target-1", "snapshot-rebased-1", "certificate-1", "authority-1")
              SeededPublished = None
              WorktreeReads = [ "rebased-1" ]
              FailPublishedAppend = false
              GateCancelled = true
              CleanupFails = false }
        | "reentry-published-append-failed" ->
            { InitialHead = "rebased-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals = []
              Snapshots = [ "snapshot-rebased-1"; "snapshot-rebased-1" ]
              TargetReads = [ "target-1" ]
              RebaseResults = []
              ConflictReads = [ [] ]
              FfResults = [ ProgramScenarioFf.Ok "rebased-1" ]
              SeededCandidateReady = None
              SeededRebasedReady = Some("rebased-1", "target-1", "snapshot-rebased-1")
              SeededPublishClaimed =
                Some("refs/heads/main", "rebased-1", "target-1", "snapshot-rebased-1", "certificate-1", "authority-1")
              SeededPublished = None
              WorktreeReads = [ "rebased-1"; "rebased-1" ]
              FailPublishedAppend = true
              GateCancelled = false
              CleanupFails = false }
        | "reentry-cleanup-failed" ->
            { InitialHead = "rebased-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals = []
              Snapshots = [ "snapshot-rebased-1"; "snapshot-rebased-1" ]
              TargetReads = [ "target-1" ]
              RebaseResults = []
              ConflictReads = [ [] ]
              FfResults = [ ProgramScenarioFf.Ok "rebased-1" ]
              SeededCandidateReady = None
              SeededRebasedReady = Some("rebased-1", "target-1", "snapshot-rebased-1")
              SeededPublishClaimed =
                Some("refs/heads/main", "rebased-1", "target-1", "snapshot-rebased-1", "certificate-1", "authority-1")
              SeededPublished = None
              WorktreeReads = [ "rebased-1"; "rebased-1" ]
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = true }
        | "reentry-published-unsettled" ->
            { InitialHead = "rebased-1"
              InitialTarget = "rebased-1"
              InitialRebasedTarget = None
              Signals = []
              Snapshots = []
              TargetReads = []
              RebaseResults = []
              ConflictReads = []
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = Some("rebased-1", "target-1", "snapshot-rebased-1")
              SeededPublishClaimed =
                Some("refs/heads/main", "rebased-1", "target-1", "snapshot-rebased-1", "certificate-1", "authority-1")
              SeededPublished = Some("rebased-1", "rebased-1")
              WorktreeReads = []
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | "reentry-stale-claim-superseded" ->
            // Crash-window regression (R12): durable state holds the CAS-missed
            // R1 claim beside the superseding R2 rebased record. Reentry must treat
            // the stale claim as expired and re-enter the manager loop — never FF
            // on the old pin and never fail closed without consuming signals.
            { InitialHead = "rebased-1"
              InitialTarget = "target-1"
              InitialRebasedTarget = None
              Signals =
                [ ProgramScenarioSignal.Retired
                  ProgramScenarioSignal.Exceptional "scenario-complete" ]
              Snapshots = []
              TargetReads = []
              RebaseResults = []
              ConflictReads = []
              FfResults = []
              SeededCandidateReady = None
              SeededRebasedReady = Some("rebased-2", "target-2", "snapshot-rebased-2")
              SeededPublishClaimed =
                Some("refs/heads/main", "rebased-1", "target-1", "snapshot-rebased-1", "certificate-1", "authority-1")
              SeededPublished = None
              WorktreeReads = []
              FailPublishedAppend = false
              GateCancelled = false
              CleanupFails = false }
        | unknown -> invalidArg "scenario" ("unknown Change program scenario: " + unknown)

    let private programFactName fact =
        match fact with
        | AgentFact.Orchestrator value ->
            match value with
            | OrchestratorFactCases.ManagerJobCreated _ -> "ManagerJobCreated"
            | OrchestratorFactCases.CandidateReady _ -> "CandidateReady"
            | OrchestratorFactCases.ConflictDetected _ -> "ConflictDetected"
            | OrchestratorFactCases.RebasedCandidateReady _ -> "RebasedCandidateReady"
            | OrchestratorFactCases.PublishClaimed _ -> "PublishClaimed"
            | OrchestratorFactCases.Published _ -> "Published"
            | OrchestratorFactCases.JobFailed _ -> "JobFailed"
            | OrchestratorFactCases.JobAbandoned _ -> "JobAbandoned"
            | OrchestratorFactCases.WorktreeCreateRequested _ -> "WorktreeCreateRequested"
            | OrchestratorFactCases.WorktreeCreated _ -> "WorktreeCreated"
        | _ -> "Other"

    let private scenarioBinding tag =
        { PhysicalUserMessageId = "physical-" + tag
          ProviderRunId = "provider-" + tag
          ToolCallId = "tool-" + tag
          NarrativeDigest = "narrative-" + tag
          PayloadDigest = "payload-" + tag
          RootRequestDigest = "root-" + tag
          RequirementSetDigest = "requirements-" + tag
          EvidenceFrontierDigest = "evidence-" + tag }

    let private scenarioSignal index signal =
        ignore index

        match signal with
        | ProgramScenarioSignal.QualityCandidate _ -> ManagerLoopSignal.Candidate
        | ProgramScenarioSignal.Retired -> ManagerLoopSignal.Continue
        | ProgramScenarioSignal.Exceptional reason -> ManagerLoopSignal.ExceptionalTerminal reason

    let private verdictObject verdict =
        match verdict with
        | OrchestratorVerdict.Published(_, head) ->
            box
                {| kind = "Published"
                   detail = CommitHash.value head |}
        | OrchestratorVerdict.PublishedPendingCleanup(_, head, cleanupError) ->
            box
                {| kind = "PublishedPendingCleanup"
                   detail = sprintf "Published %s; cleanup pending: %s" (CommitHash.value head) cleanupError |}
        | OrchestratorVerdict.Cancelled _ ->
            box
                {| kind = "Cancelled"
                   detail = "cancelled" |}
        | OrchestratorVerdict.RejectedDirty reason ->
            box
                {| kind = "RejectedDirty"
                   detail = reason |}
        | OrchestratorVerdict.IntegrationFailed(_, detail) ->
            box
                {| kind = "IntegrationFailed"
                   detail = detail |}
        | OrchestratorVerdict.Empty -> box {| kind = "Empty"; detail = "" |}

    /// Executes the real OrchestratorProgram against deterministic in-memory
    /// ports. The surface exposes domain effects, not old review stages, so
    /// integration requirements can prove invalidation/loop-continuation/Git/CAS order.
    /// Signal input for program observations. Queued replays a named scenario;
    /// Burst feeds a countdown of Continue signals then one terminal without
    /// materializing a signal queue, keeping long manager-loop proofs bounded.
    [<RequireQualifiedAccess>]
    type private ManagerLoopSignalInput =
        | Queued of Queue<ProgramScenarioSignal>
        | Burst of remaining: int ref * terminalSent: bool ref * terminalReason: string

    /// Scalar effect counters shared by every program observation. Burst runs
    /// return only these scalars; per-step strings stay behind collectStepStrings.
    type private ProgramRunCounters =
        { SignalCount: int
          ContinuationCount: int
          FactCount: int
          GitCallCount: int
          GateAcquireCount: int
          GateReleaseCount: int
          GateHeld: bool }

    /// Shared physical-port doubles around the real OrchestratorProgram.
    /// Full observations keep per-step strings; burst runs count only.
    let private executeProgram
        (scenario: ProgramScenario)
        (scenarioName: string)
        (signalInput: ManagerLoopSignalInput)
        (collectStepStrings: bool)
        : Task<obj * ProgramRunCounters> =
        task {
            let jobId = ManagerJobId.create "surface-job"
            let sessionId = SessionId.create "surface-manager-session"
            let worktreePath = WorktreePath.create "/tmp/wanxiangshu-change-surface"
            let worktreeIdentity = WorktreeIdentity.create "manager/surface-job"
            let targetRef = TargetRef.create "refs/heads/main"
            let snapshots = Queue<string>(scenario.Snapshots)
            let targetReads = Queue<string>(scenario.TargetReads)
            let rebaseResults = Queue<ProgramScenarioRebase>(scenario.RebaseResults)
            let conflictReads = Queue<string list>(scenario.ConflictReads)
            let ffResults = Queue<ProgramScenarioFf>(scenario.FfResults)
            let worktreeReads = Queue<string>(scenario.WorktreeReads)
            let facts = ResizeArray<string>()
            let invalidations = ResizeArray<string>()
            let continuations = ResizeArray<string>()
            let timeline = ResizeArray<string>()
            let rebaseGateHeld = ResizeArray<bool>()
            let ffGateHeld = ResizeArray<bool>()
            let ffExpectedHeads = ResizeArray<string>()
            let ffPinnedCandidates = ResizeArray<string>()
            // DSL-MUTABLE: resource — simulated worktree ref states
            let worktreeHead = ref scenario.InitialHead
            let targetHead = ref scenario.InitialTarget
            let gateHeld = ref false
            // DSL-MUTABLE: resource — simulated gate counters
            let gateAcquireCount = ref 0
            let gateReleaseCount = ref 0
            let signalIndex = ref 0
            // DSL-MUTABLE: resource — simulated loop counters
            let signalCount = ref 0
            let continuationCount = ref 0
            let factCount = ref 0
            // DSL-MUTABLE: resource — simulated git counter
            let gitCallCount = ref 0
            let burstIncumbency = IncumbencyId.create "burst-loop"

            let nextSignal () =
                match signalInput with
                | ManagerLoopSignalInput.Burst(remaining, terminalSent, terminalReason) ->
                    if remaining.Value > 0 then
                        remaining.Value <- remaining.Value - 1
                        signalCount.Value <- signalCount.Value + 1
                        Ok ManagerLoopSignal.Continue
                    elif not terminalSent.Value then
                        terminalSent.Value <- true
                        signalCount.Value <- signalCount.Value + 1
                        Ok(ManagerLoopSignal.ExceptionalTerminal terminalReason)
                    else
                        Error "burst signal queue exhausted"
                | ManagerLoopSignalInput.Queued queue ->
                    if queue.Count = 0 then
                        Error "scenario signal queue exhausted"
                    else
                        signalIndex.Value <- signalIndex.Value + 1
                        signalCount.Value <- signalCount.Value + 1
                        let value = scenarioSignal signalIndex.Value (queue.Dequeue())

                        let label =
                            match value with
                            | ManagerLoopSignal.Candidate -> "Candidate"
                            | ManagerLoopSignal.Continue -> "Continue"
                            | ManagerLoopSignal.ExceptionalTerminal _ -> "ExceptionalTerminal"

                        if collectStepStrings then
                            timeline.Add("await:" + label)

                        Ok value

            let git: GitPort =
                { IsDirty = fun _ -> Task.FromResult(Ok false)
                  CreateWorktree = fun _ _ -> Task.FromResult(Ok worktreeIdentity)
                  FreezeTargetBranch = fun () -> Task.FromResult(Ok targetRef)
                  Rebase =
                    fun _ _ ->
                        task {
                            gitCallCount.Value <- gitCallCount.Value + 1
                            rebaseGateHeld.Add gateHeld.Value
                            timeline.Add "git:rebase"

                            if rebaseResults.Count = 0 then
                                return Error "scenario rebase queue exhausted"
                            else
                                match rebaseResults.Dequeue() with
                                | ProgramScenarioRebase.Ok head ->
                                    worktreeHead.Value <- head
                                    return Ok()
                                | ProgramScenarioRebase.Error reason -> return Error reason
                        }
                  FfMerge =
                    fun _ _ expected pinned ->
                        task {
                            gitCallCount.Value <- gitCallCount.Value + 1
                            ffGateHeld.Add gateHeld.Value
                            ffExpectedHeads.Add(CommitHash.value expected)
                            ffPinnedCandidates.Add(CommitHash.value pinned)
                            timeline.Add("git:ff:" + CommitHash.value pinned)

                            if ffResults.Count = 0 then
                                return Error "scenario ff queue exhausted"
                            else
                                match ffResults.Dequeue() with
                                | ProgramScenarioFf.Ok head ->
                                    targetHead.Value <- head
                                    return Ok(CommitHash.create head)
                                | ProgramScenarioFf.TargetMoved ->
                                    return Error OrchestratorConstants.targetRefMovedError
                        }
                  ConflictedFiles =
                    fun _ ->
                        if conflictReads.Count = 0 then
                            Task.FromResult(Error "scenario conflict queue exhausted")
                        else
                            Task.FromResult(Ok(conflictReads.Dequeue()))
                  RemoveWorktree =
                    fun _ ->
                        if scenario.CleanupFails then
                            Task.FromResult(Error "disk detached")
                        else
                            Task.FromResult(Ok())
                  HasRebaseHead = fun _ -> Task.FromResult false
                  ListWorktrees = fun () -> Task.FromResult(Ok [])
                  ListManagerBranches = fun () -> Task.FromResult(Ok [])
                  DeleteBranch = fun _ -> Task.FromResult(Ok())
                  ReadHead =
                    fun _ ->
                        let value =
                            if worktreeReads.Count = 0 then
                                worktreeHead.Value
                            else
                                worktreeReads.Dequeue()

                        timeline.Add("git:read-head:" + value)
                        Task.FromResult(Ok(CommitHash.create value))
                  GetTargetHead =
                    fun _ ->
                        let value =
                            if targetReads.Count = 0 then
                                targetHead.Value
                            else
                                targetReads.Dequeue()

                        targetHead.Value <- value
                        Task.FromResult(Ok(CommitHash.create value)) }

            let worktree = WorktreeResource.Adopt(git, worktreeIdentity, worktreePath)

            let job =
                { JobId = jobId
                  ManagerSessionId = sessionId
                  ManagerAgent = "manager"
                  TargetRef = targetRef
                  Worktree = worktree }

            let created =
                OrchestratorProjection.createJob
                    {| ManagerJobId = jobId
                       ManagerSessionId = sessionId
                       ManagerAgent = "manager"
                       Byname = "surface-road"
                       WorktreeIdentity = worktreeIdentity
                       WorktreePath = worktreePath
                       TargetRef = targetRef
                       TargetBranchFrozen = TargetRef.value targetRef |}
                    Wanxiangshu.Composition.Durable.Fold.empty.AgentProjections.Orchestrator

            let initialOrchestrator =
                let withLegacyRebased current =
                    match scenario.InitialRebasedTarget with
                    | None -> current
                    | Some targetSnapshot ->
                        OrchestratorProjection.recordRebasedCandidateReady
                            jobId
                            {| RebasedCommit = CommitHash.create scenario.InitialHead
                               TargetHeadSnapshot = CommitHash.create targetSnapshot |}
                            current

                let withCandidate current =
                    match scenario.SeededCandidateReady with
                    | None -> current
                    | Some(candidate, _snapshot, _cert) ->
                        OrchestratorProjection.recordCandidateReady
                            jobId
                            {| CandidateCommit = CommitHash.create candidate |}
                            current

                let withRebased current =
                    match scenario.SeededRebasedReady with
                    | None -> current
                    | Some(rebased, targetSnap, _workspaceSnap) ->
                        OrchestratorProjection.recordRebasedCandidateReady
                            jobId
                            {| RebasedCommit = CommitHash.create rebased
                               TargetHeadSnapshot = CommitHash.create targetSnap |}
                            current

                let withClaim current =
                    match scenario.SeededPublishClaimed with
                    | None -> current
                    | Some(targetRef, rebased, expected, _workspaceSnap, _cert, authority) ->
                        OrchestratorProjection.recordPublishClaimed
                            jobId
                            {| TargetRef = TargetRef.create targetRef
                               RebasedCommit = CommitHash.create rebased
                               ExpectedHead = CommitHash.create expected
                               AuthorityRevision = AuthorityRevision.create authority |}
                            current

                let withPublished current =
                    match scenario.SeededPublished with
                    | None -> current
                    | Some(candidate, resulting) ->
                        OrchestratorProjection.recordTerminal
                            jobId
                            (TerminalOutcome.Published
                                {| CandidateCommit = CommitHash.create candidate
                                   ResultingTargetHead = CommitHash.create resulting |})
                            current

                // Seed order mirrors durable write order: candidate, rebased,
                // claim, then terminal. Physical doubles only replay queued
                // observations; they never read source files or copy the model.
                created
                |> withLegacyRebased
                |> withCandidate
                |> withRebased
                |> withClaim
                |> withPublished

            let initialProjection = Wanxiangshu.Composition.Durable.Fold.empty

            let projection =
                ref
                    { initialProjection with
                        AgentProjections =
                            { initialProjection.AgentProjections with
                                Orchestrator = initialOrchestrator } }

            let appendFact _ fact =
                task {
                    factCount.Value <- factCount.Value + 1
                    let name = programFactName fact

                    if scenario.FailPublishedAppend && name = "Published" then
                        timeline.Add "fact-failed:Published"
                        return Error "injected Published append failure"
                    else
                        facts.Add name
                        timeline.Add("fact:" + name)

                        match
                            Wanxiangshu.Composition.Durable.Fold.foldAgentFact projection.Value.AgentProjections fact
                        with
                        | Error rejection -> return Error(sprintf "%A" rejection)
                        | Ok agents ->
                            projection.Value <-
                                { projection.Value with
                                    AgentProjections = agents }

                            return Ok()
                }

            let relay: RelayPort =
                { CreateManagerSession = fun _ -> Task.FromResult(Ok sessionId)
                  ActivateManager = fun _ -> Task.FromResult(Ok())
                  AwaitLoopSignal =
                    fun _ ->
                        if scenarioName = "cancelled-program" then
                            throwProductionCancellation operationCanceledCtor "manager task cancelled"

                        Task.FromResult(nextSignal ())
                  ContinueLoop =
                    fun _ ->
                        continuationCount.Value <- continuationCount.Value + 1

                        if collectStepStrings then
                            let nextId =
                                IncumbencyId.create ("surface-loop-" + string (continuations.Count + 1))

                            continuations.Add(IncumbencyId.value nextId)
                            timeline.Add("continue:" + IncumbencyId.value nextId)
                            Task.FromResult(Ok nextId)
                        else
                            Task.FromResult(Ok burstIncumbency)
                  PrepareCandidate =
                    fun _ ->
                        if conflictReads.Count > 0 then
                            if conflictReads.Peek() <> [] then
                                Task.FromResult(Error "unmerged paths remain in worktree")
                            else
                                conflictReads.Dequeue() |> ignore
                                Task.FromResult(Ok(CommitHash.create worktreeHead.Value))
                        else
                            Task.FromResult(Ok(CommitHash.create worktreeHead.Value))
                  TerminateWorkResources =
                    fun _ ->
                        timeline.Add "relay:terminate"
                        Task.FromResult()
                  LifecycleReviewed = fun _ _ -> Task.FromResult(Some("Promoted", "authority-1")) }

            let acquireGate () =
                if scenario.GateCancelled then
                    timeline.Add "gate:cancelled"
                    throwProductionCancellation operationCanceledCtor "publish gate cancelled"

                gateAcquireCount.Value <- gateAcquireCount.Value + 1
                gateHeld.Value <- true
                timeline.Add "gate:acquire"

                Task.FromResult
                    { Release =
                        fun () ->
                            task {
                                gateHeld.Value <- false
                                gateReleaseCount.Value <- gateReleaseCount.Value + 1
                                timeline.Add "gate:release"
                            } }

            let deps =
                { Git = git
                  Relay = relay
                  AppendFact = appendFact
                  Snapshot = fun () -> projection.Value
                  AcquirePublishGate = acquireGate }

            let! verdict = OrchestratorProgram.run deps job

            let counters =
                { SignalCount = signalCount.Value
                  ContinuationCount = continuationCount.Value
                  FactCount = factCount.Value
                  GitCallCount = gitCallCount.Value
                  GateAcquireCount = gateAcquireCount.Value
                  GateReleaseCount = gateReleaseCount.Value
                  GateHeld = gateHeld.Value }

            let observation =
                if collectStepStrings then
                    box
                        {| verdict = verdictObject verdict
                           facts = facts.ToArray()
                           invalidations = invalidations.ToArray()
                           continuations = continuations.ToArray()
                           timeline = timeline.ToArray()
                           rebaseGateHeld = rebaseGateHeld.ToArray()
                           ffGateHeld = ffGateHeld.ToArray()
                           ffExpectedHeads = ffExpectedHeads.ToArray()
                           ffPinnedCandidates = ffPinnedCandidates.ToArray()
                           ffCalls = ffGateHeld.Count
                           signalCount = counters.SignalCount
                           continuationCount = counters.ContinuationCount
                           factCount = counters.FactCount
                           gitCallCount = counters.GitCallCount
                           gateAcquireCount = counters.GateAcquireCount
                           gateReleaseCount = counters.GateReleaseCount
                           gateHeldAfterRun = counters.GateHeld |}
                else
                    box
                        {| verdict = verdictObject verdict
                           continuationCount = counters.ContinuationCount
                           signalCount = counters.SignalCount
                           gateAcquireCount = counters.GateAcquireCount
                           gateReleaseCount = counters.GateReleaseCount
                           gateHeldAfterRun = counters.GateHeld
                           factCount = counters.FactCount
                           gitCallCount = counters.GitCallCount |}

            return (observation, counters)
        }

    /// Executes the real OrchestratorProgram against deterministic in-memory
    /// ports. The surface exposes domain effects, not old review stages, so
    /// integration requirements can prove invalidation/loop-continuation/Git/CAS order.
    let observeRelayProgram (scenarioName: string) : Task<obj> =
        task {
            let scenario = programScenario scenarioName

            let! observation, _ =
                executeProgram
                    scenario
                    scenarioName
                    (ManagerLoopSignalInput.Queued(Queue<ProgramScenarioSignal>(scenario.Signals)))
                    true

            return observation
        }

    /// Feeds count Continue signals then one ExceptionalTerminal through the
    /// real OrchestratorProgram. Countdown source plus scalar-only observation
    /// keeps 10,000-iteration proofs in bounded memory: no signal queue, no
    /// timeline, no per-step continuation strings.
    let observeManagerLoopBurst (count: int) : Task<obj> =
        task {
            if count < 0 then
                invalidArg "count" "burst count must be non-negative"

            let scenario: ProgramScenario =
                { InitialHead = "candidate-1"
                  InitialTarget = "target-1"
                  InitialRebasedTarget = None
                  Signals = []
                  Snapshots = []
                  TargetReads = []
                  RebaseResults = []
                  ConflictReads = []
                  FfResults = []
                  SeededCandidateReady = None
                  SeededRebasedReady = None
                  SeededPublishClaimed = None
                  SeededPublished = None
                  WorktreeReads = []
                  FailPublishedAppend = false
                  GateCancelled = false
                  CleanupFails = false }

            let! observation, _ =
                executeProgram
                    scenario
                    "manager-loop-burst"
                    (ManagerLoopSignalInput.Burst(ref count, ref false, "burst-complete"))
                    false

            return observation
        }

    let private commandObject (command: Command) : obj =
        box
            {| fileName = command.FileName
               args = command.Arguments |> List.toArray
               workingDirectory = optionObj command.WorkingDirectory |}

    let private invokeRunner (runner: obj) (command: Command) : Task<int * string * string> =
        task {
            let! raw = unbox<Task<obj>> (asPromise (apply1 runner (commandObject command)))
            let values = unbox<obj array> raw
            return int (string values.[0]), stringOf values.[1], stringOf values.[2]
        }

    let createGit (repo: string) (runner: obj) : obj =
        let r = invokeRunner runner
        let port = GitOperations.createWithRepo repo r
        GitHandle(port, repo, r) :> obj

    let private resultObject (result: Result<'T, string>) (valueOf: 'T -> obj) : obj =
        match result with
        | Ok value -> box {| ok = true; value = valueOf value |}
        | Error error -> box {| ok = false; error = error |}

    let gitIsDirty (git: obj) (path: string) : Task<obj> =
        task {
            let! result = (git :?> GitHandle).Port.IsDirty(WorktreePath.create path)
            return resultObject result box
        }

    let gitFreezeTargetBranch (git: obj) : Task<obj> =
        task {
            let! result = (git :?> GitHandle).Port.FreezeTargetBranch()
            return resultObject result (fun value -> box (TargetRef.value value))
        }

    let gitFreezeTargetBranchResult (git: obj) : Task<obj> =
        task {
            let! result = (git :?> GitHandle).Port.FreezeTargetBranch()
            return resultObject result (fun value -> box (TargetRef.value value))
        }

    let gitRebase (git: obj) (path: string) (targetRef: string) : Task<obj> =
        task {
            let! result = (git :?> GitHandle).Port.Rebase (WorktreePath.create path) (TargetRef.create targetRef)
            return resultObject result (fun _ -> null)
        }

    let gitRebaseContinue (git: obj) : Task<obj> =
        task {
            let handle = git :?> GitHandle
            let! res = GitOperations.continueRebase handle.Runner handle.repo
            return resultObject res (fun _ -> null)
        }

    let gitStageAll (git: obj) : Task<obj> =
        task {
            let handle = git :?> GitHandle
            let! res = GitOperations.stageAll handle.Runner handle.repo
            return resultObject res (fun _ -> null)
        }

    let gitCandidateCommit (git: obj) (msg: string) : Task<obj> =
        task {
            let handle = git :?> GitHandle
            let! res = GitOperations.candidateCommit handle.Runner handle.repo msg
            return resultObject res (fun _ -> null)
        }

    let gitFfMerge
        (git: obj)
        (path: string)
        (targetRef: string)
        (expectedHead: string)
        (pinnedCandidate: string)
        : Task<obj> =
        task {
            let! result =
                (git :?> GitHandle).Port.FfMerge
                    (WorktreePath.create path)
                    (TargetRef.create targetRef)
                    (CommitHash.create expectedHead)
                    (CommitHash.create pinnedCandidate)

            return resultObject result (fun value -> box (CommitHash.value value))
        }

    let gitConflictedFiles (git: obj) (path: string) : Task<obj> =
        task {
            let! result = (git :?> GitHandle).Port.ConflictedFiles(WorktreePath.create path)
            return resultObject result (fun values -> values |> List.toArray |> Array.map box |> box)
        }

    let gitConflictedFilesResult (git: obj) : Task<obj> =
        task {
            let handle = git :?> GitHandle
            let! result = handle.Port.ConflictedFiles(WorktreePath.create handle.repo)
            return resultObject result (fun values -> values |> List.toArray |> Array.map box |> box)
        }

    let gitHasRebaseHead (git: obj) (path: string) : Task<bool> =
        (git :?> GitHandle).Port.HasRebaseHead(WorktreePath.create path)

    let gitReadHead (git: obj) (path: string) : Task<obj> =
        task {
            let! result = (git :?> GitHandle).Port.ReadHead(WorktreePath.create path)
            return resultObject result (fun value -> box (CommitHash.value value))
        }

    let gitGetTargetHead (git: obj) (targetRef: string) : Task<obj> =
        task {
            let! result = (git :?> GitHandle).Port.GetTargetHead(TargetRef.create targetRef)
            return resultObject result (fun value -> box (CommitHash.value value))
        }

    let gitCreateWorktree (git: obj) (job: string) (path: string) : Task<obj> =
        task {
            let! result = (git :?> GitHandle).Port.CreateWorktree (ManagerJobId.create job) (WorktreePath.create path)
            return resultObject result (fun value -> box (WorktreeIdentity.value value))
        }

    let gitRemoveWorktree (git: obj) (path: string) : Task<obj> =
        task {
            let! result = (git :?> GitHandle).Port.RemoveWorktree(WorktreePath.create path)
            return resultObject result (fun _ -> null)
        }

    let gitDeleteBranch (git: obj) (identity: string) : Task<obj> =
        task {
            let! result = (git :?> GitHandle).Port.DeleteBranch(WorktreeIdentity.create identity)
            return resultObject result (fun _ -> null)
        }

    let worktreeIdentityOf (job: string) : string =
        WorktreeIdentity.value (WorktreeCommands.identityOf (ManagerJobId.create job))

    let gitListWorktrees (git: obj) : Task<obj> =
        task {
            let! result = (git :?> GitHandle).Port.ListWorktrees()

            return
                resultObject result (fun values ->
                    values
                    |> List.map (fun (path, identity) ->
                        box
                            {| path = WorktreePath.value path
                               identity =
                                match identity with
                                | Some id -> box (WorktreeIdentity.value id)
                                | None -> null |})
                    |> List.toArray
                    |> box)
        }

    let gitListManagerBranches (git: obj) : Task<obj> =
        task {
            let! result = (git :?> GitHandle).Port.ListManagerBranches()
            return resultObject result (fun values -> values |> List.map WorktreeIdentity.value |> List.toArray |> box)
        }

    let worktreeCreate (git: obj) (job: string) (path: string) : Task<obj> =
        task {
            let! result =
                WorktreeResource.Create((git :?> GitHandle).Port, ManagerJobId.create job, WorktreePath.create path)

            return resultObject result (fun resource -> WorktreeHandle resource :> obj)
        }

    let worktreeAdopt (git: obj) (identity: string) (path: string) : obj =
        WorktreeHandle(
            WorktreeResource.Adopt((git :?> GitHandle).Port, WorktreeIdentity.create identity, WorktreePath.create path)
        )
        :> obj

    let worktreePath (resource: obj) : string =
        WorktreePath.value ((resource :?> WorktreeHandle).Resource.Path)

    let worktreeIdentity (resource: obj) : string =
        WorktreeIdentity.value ((resource :?> WorktreeHandle).Resource.Identity)

    let worktreeMarkDurable (resource: obj) : unit =
        (resource :?> WorktreeHandle).Resource.MarkDurable()

    let worktreeRelease (resource: obj) : Task<obj> =
        task {
            let! result = (resource :?> WorktreeHandle).Resource.Release()
            return resultObject result (fun _ -> null)
        }

    let worktreeDispose (resource: obj) : Task<unit> =
        task { do! ((resource :?> WorktreeHandle).Resource :> IAsyncDisposable).DisposeAsync() }

    let lockPath (repo: string) (branch: string) : string = IntegrationGate.lockPath repo branch

    let acquireGate (path: string) : Task<obj> =
        task {
            let! gate = IntegrationGate.acquire path
            return GateHandle gate :> obj
        }

    let releaseGate (gate: obj) : Task<unit> = (gate :?> GateHandle).Gate.Release()

    let disposeGate (gate: obj) : Task<unit> =
        task { do! ((gate :?> GateHandle).Gate :> IAsyncDisposable).DisposeAsync() }

    type private VerdictMailboxHandle(mailbox: VerdictMailbox) =
        member _.Mailbox = mailbox

    type private VerdictInterruptHandle(tcs: TaskCompletionSource<JoinInterruptReason>) =
        member _.Source = tcs

    let private verdictInterruptOf (reason: string) : JoinInterruptReason =
        match reason with
        | "UserMessageArrived" -> JoinInterruptReason.UserMessageArrived
        | "DeadlineExpired" -> JoinInterruptReason.DeadlineExpired
        | "OperatorAbort" -> JoinInterruptReason.OperatorAbort
        | _ -> invalidArg "reason" "Unknown join interrupt reason"

    let private verdictOf (value: obj) : OrchestratorVerdict =
        let job = jobId (field value [ "jobId"; "ManagerJobId" ])
        let head = commit (field value [ "head"; "Head"; "candidateCommit" ])

        match stringField value [ "kind"; "case"; "name" ] with
        | "PublishedPendingCleanup" ->
            OrchestratorVerdict.PublishedPendingCleanup(
                job,
                head,
                stringField value [ "cleanupError"; "error"; "detail" ]
            )
        | "Cancelled" -> OrchestratorVerdict.Cancelled job
        | "RejectedDirty" -> OrchestratorVerdict.RejectedDirty(stringField value [ "reason"; "detail" ])
        | "IntegrationFailed" ->
            OrchestratorVerdict.IntegrationFailed(job, stringField value [ "errorDetails"; "detail"; "error" ])
        | "Empty" -> OrchestratorVerdict.Empty
        | "Published" -> OrchestratorVerdict.Published(job, head)
        | _ -> invalidArg "value" "Unknown orchestrator verdict"

    let private verdictInterruptName (reason: JoinInterruptReason) =
        match reason with
        | JoinInterruptReason.UserMessageArrived -> "UserMessageArrived"
        | JoinInterruptReason.DeadlineExpired -> "DeadlineExpired"
        | JoinInterruptReason.OperatorAbort -> "OperatorAbort"

    let private verdictJoinOutcomeObject (outcome: JoinWaitOutcome<OrchestratorVerdict>) : obj =
        match outcome with
        | ResultsAvailable batch ->
            box
                {| kind = "ResultsAvailable"
                   count = NonEmptyBatch.length batch
                   verdicts = NonEmptyBatch.toList batch |> List.map verdictObject |> List.toArray |}
        | Interrupted reason ->
            box
                {| kind = "Interrupted"
                   reason = verdictInterruptName reason |}

    let createVerdictMailbox () : obj =
        VerdictMailboxHandle(VerdictMailbox(CausalWaitRuntime().Observer)) :> obj

    let verdictMailboxStartJob (mailbox: obj) : unit =
        (mailbox :?> VerdictMailboxHandle).Mailbox.StartJob()

    let verdictMailboxPublish (mailbox: obj) (verdict: obj) : unit =
        (mailbox :?> VerdictMailboxHandle).Mailbox.Publish(verdictOf verdict)

    let verdictMailboxPendingCount (mailbox: obj) : int =
        (mailbox :?> VerdictMailboxHandle).Mailbox.PendingCount

    let createVerdictInterrupt () : obj =
        VerdictInterruptHandle(
            TaskCompletionSource<JoinInterruptReason>(TaskCreationOptions.RunContinuationsAsynchronously)
        )
        :> obj

    let fireVerdictInterrupt (handle: obj) (reason: string) : unit =
        AsyncSupport.trySetResult (handle :?> VerdictInterruptHandle).Source (verdictInterruptOf reason)
        |> ignore

    let verdictMailboxJoinAvailable (mailbox: obj) (maxCount: int) (interrupt: obj) : Task<obj> =
        task {
            let! outcome =
                (mailbox :?> VerdictMailboxHandle)
                    .Mailbox.JoinAvailable(maxCount, (interrupt :?> VerdictInterruptHandle).Source.Task)

            return verdictJoinOutcomeObject outcome
        }

    let verdictMaxBatch () : int = JoinBatch.MaxJoinBatch
