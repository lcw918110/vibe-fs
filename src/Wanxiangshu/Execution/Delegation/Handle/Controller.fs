namespace Wanxiangshu.Execution.Delegation.Handle

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork.ChildRecovery
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

type HandleConsumeRejection =
    | AlreadyRetired
    | NotJoinable of HandleTransitionRejection
    | AppendFailed of string

module internal HandleDurable =
    [<Import("DurableHandles", "wanxiangli/handle-lifecycle")>]
    let private durableClass: obj = jsNative
    let result value = match value with | Ok value -> createObj [ "ok" ==> true; "value" ==> value ] | Error error -> createObj [ "ok" ==> false; "error" ==> error ]
    let unitResult (raw: obj) = if unbox<bool> raw?ok then Ok() else Error(unbox<string> raw?error)
    let private fact parentId (raw: obj) =
        let work () = HandleProjection.workOf raw?work
        let cell () = HandleProjection.completionOf raw?completion
        match unbox<string> raw?kind with
        | "link" ->
            let binding: obj = raw?binding
            ExecutionFactCases.HandleLinked {| ParentSessionId = parentId; Handle = HandleProjection.handleOf binding?handle; ChildSessionId = SessionId.create binding?child; TargetAgent = binding?target; Byname = binding?byname; CanonicalRole = Roles.tryParseRole binding?role |> Option.get; Ownership = if unbox<bool> binding?parentVisible then HandleOwnership.DurableParentHandle else HandleOwnership.HostOwnedHidden |}
        | "completeWork" ->
            let cell = cell ()
            ExecutionFactCases.HandleWorkCompleted {| ParentSessionId = parentId; Work = work (); Kind = cell.Kind; CompletionRef = cell.CompletionRef; CompletionDigest = cell.CompletionDigest |}
        | "consumeWork" ->
            let cell = cell ()
            ExecutionFactCases.HandleWorkConsumed {| ParentSessionId = parentId; Work = work (); ConsumptionId = raw?consumptionId; Kind = cell.Kind; CompletionRef = cell.CompletionRef; CompletionDigest = cell.CompletionDigest |}
        | "abandonWork" -> ExecutionFactCases.HandleWorkAbandoned {| ParentSessionId = parentId; Work = work (); Reason = HandleProjection.reasonOf raw?reason |}
        | "retire" -> ExecutionFactCases.HandleRetired {| ParentSessionId = parentId; Handle = HandleProjection.handleOf raw?handle |}
        | "rejectFalseCompletion" ->
            let cell = cell ()
            ExecutionFactCases.HandleFalseCompletionRejected {| ParentSessionId = parentId; Handle = HandleProjection.handleOf raw?handle; ExpectedCompletionRef = Option.get cell.CompletionRef; ExpectedCompletionDigest = Option.get cell.CompletionDigest; Reason = FalseCompletionReason.LegacyAbortWasObservation |}
        | other -> invalidOp ("Unknown durable handle event: " + other)
    let create (journal: AgentJournalPort) parentId : obj =
        let effects = createObj [
            "current" ==> (fun () -> journal.HandleProjection parentId |> HandleProjection.native)
            "refresh" ==> (fun () -> journal.RefreshProjection() |> result)
            "append" ==> (fun raw -> task { let! appended = journal.AppendExecutionFact parentId (fact parentId raw)
                                           return result appended })
            "writeBlob" ==> (fun body -> task { let! written = journal.WriteBlob body
                                                return written |> Result.map (fun (reference, digest) -> createObj [ "reference" ==> BlobRef.value reference; "digest" ==> BlobDigest.value digest ]) |> result })
            "readBlob" ==> (fun reference -> task { let! read = journal.ReadBlob (BlobRef.create reference)
                                                    return result read })
            "sha256" ==> (fun body -> journal.Sha256 body)
            "mintConsumptionId" ==> (fun () -> System.Guid.NewGuid().ToString("N")) ]
        createNew durableClass effects

module HandleController =
    let agentHandle agentId = HandleId.Agent(AgentHandleId.create agentId)
    let linkNamed journal parentId agentId childSessionId (targetAgent: string) (byname: string) role ownership =
        task {
            match journal with
            | None -> return Ok()
            | Some durable ->
                let controller = HandleDurable.create durable parentId
                let binding = createObj [ "handle" ==> HandleProjection.handleValue (agentHandle agentId); "child" ==> SessionId.value childSessionId; "target" ==> targetAgent; "byname" ==> byname; "role" ==> Roles.roleLabel role; "parentVisible" ==> (ownership = HandleOwnership.DurableParentHandle) ]
                let! outcome = unbox<Task<obj>> (controller?link(binding))
                return HandleDurable.unitResult outcome
        }
    let link journal parentId agentId childSessionId (targetAgent: string) role ownership = linkNamed journal parentId agentId childSessionId targetAgent targetAgent role ownership
    let recordWorkCompletion journal parentId admitted completion =
        task {
            match journal with
            | None -> return Error "scoped work completion requires its canonical journal"
            | Some durable ->
                let controller = HandleDurable.create durable parentId
                let request = createObj [ "work" ==> HandleProjection.workValue (AdmittedWork.id admitted); "logicalRun" ==> LogicalRunId.value (AdmittedWork.logicalRunId admitted); "handle" ==> HandleProjection.handleValue (JoinableCompletion.handle completion); "child" ==> SessionId.value (JoinableCompletion.childSession completion); "kind" ==> string (JoinableCompletion.kind completion); "body" ==> (JoinableCompletion.body completion |> Option.toObj) ]
                let! outcome = unbox<Task<obj>> (controller?complete(request))
                return HandleDurable.unitResult outcome
        }
    let recordCompletion journal parentId completion =
        let outcome =
            match journal with
            | None -> Ok()
            | Some durable ->
                match HandleProjection.tryBinding (JoinableCompletion.handle completion) (durable.HandleProjection parentId) with
                | Some { Lifecycle = CompletedAwaitingJoin _ } | Some { Lifecycle = Retired } -> Ok()
                | _ -> Error "legacy unscoped completion is read-only; exact admitted work is required"
        Task.FromResult outcome
    let recordAbandon journal parentId agentId (reason: HandleAbandonReason) (abandonedAt: System.DateTimeOffset) =
        task {
            match journal with
            | None -> return Ok()
            | Some durable ->
                let controller = HandleDurable.create durable parentId
                let! outcome = unbox<Task<obj>> (controller?abandonActive(HandleProjection.handleValue (agentHandle agentId), string reason))
                return HandleDurable.unitResult outcome
        }
    let retire (journal: AgentJournalPort option) (parentId: SessionId) (agentId: string) : Task<Result<unit, string>> = Task.FromResult(Error "unscoped retirement is read-only; consume an exact admitted work")
    let consume (journal: AgentJournalPort) parentId handle : Task<Result<HandleRecord, HandleConsumeRejection>> =
        let outcome =
            match HandleProjection.tryBinding handle (journal.HandleProjection parentId) with
            | None -> Error(NotJoinable UnknownHandle)
            | Some { Lifecycle = Retired } -> Error AlreadyRetired
            | Some { Lifecycle = Active } -> Error(NotJoinable NotCompleted)
            | Some _ -> Error(AppendFailed "unscoped completion is audit-only; exact settlement evidence is required")
        Task.FromResult outcome
    let consumeWork journal parentId record =
        task {
            let controller = HandleDurable.create journal parentId
            let! outcome = unbox<Task<obj>> (controller?consume(HandleProjection.recordValue record))
            if unbox<bool> outcome?ok then return Ok(HandleProjection.recordOf outcome?value)
            else
                match unbox<string> outcome?kind with
                | "AlreadyRetired" -> return Error AlreadyRetired
                | "NotJoinable" -> return Error(NotJoinable(HandleProjection.rejectionOf outcome?error))
                | _ -> return Error(AppendFailed(unbox outcome?error))
        }
    let abandonWork journal parentId work (reason: HandleAbandonReason) =
        task {
            let controller = HandleDurable.create journal parentId
            let! outcome = unbox<Task<obj>> (controller?abandon(HandleProjection.workValue work, string reason))
            return HandleDurable.unitResult outcome
        }
    let cancelChildren journal parentId agentIds (abandonedAt: System.DateTimeOffset) =
        task {
            match journal with
            | None -> return Ok()
            | Some durable ->
                let controller = HandleDurable.create durable parentId
                let handles = agentIds |> List.map (agentHandle >> HandleProjection.handleValue) |> List.toArray
                let! outcome = unbox<Task<obj>> (controller?cancel(handles, "ParentCancelled"))
                return HandleDurable.unitResult outcome
        }
