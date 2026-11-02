namespace Wanxiangshu.Execution.Delegation

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority

/// EXEC-009: a handle's durable lifecycle has four states, and they must be
/// distinguishable.
///
/// The previous model held two maps (linked / unlinked), which cannot express
/// completed-awaiting-join. EXEC-005 requires `list` to show that state, so a
/// finished-but-unjoined child was reported as running. Abandoned is a durable
/// terminal that is never joinable and never reverts.
/// DSL-state-combination: domain — optional blob reference and digest are the
/// two evidence facets of one durable completion; they never select execution.
type HandleCompletion =
    {
        Kind: HandleCompletionKind
        /// Durable join payload. `None` for Cancelled and for 0.5.1 lines that
        /// predate the blob fields.
        CompletionRef: BlobRef option
        CompletionDigest: BlobDigest option
    }

type HandleLifecycle =
    /// Linked and not yet completed. `list` shows running or busy.
    | Active
    /// Completion is durable; nobody has consumed it yet. `list` shows
    /// CompletedAwaitingJoin, and `join` may still return it from the blob.
    | CompletedAwaitingJoin of HandleCompletion
    /// EXEC-009: durable abandon. Not joinable. Permanent until process-level
    /// bookkeeping ends; never reverts to Active or CompletedAwaitingJoin.
    | Abandoned of HandleAbandonReason
    /// EXEC-009 tombstone. Permanent. A retired id answers RetiredHandle forever
    /// and must never degrade into "treat the input as an agent name and fork
    /// again".
    | Retired

type HandleRecord =
    {
        Handle: HandleId
        /// The Host session this handle drives. EXEC-009: recovery must rebind the
        /// same handle id to the same session, and only the Host can issue that id.
        ChildSessionId: SessionId
        TargetAgent: string
        /// Provider-facing stable name for this logical person. TargetAgent remains
        /// the Host machine binding used for restart and session execution.
        Byname: string
        CanonicalRole: Role
        /// Who owns this handle (HandleOwnership). HostOwnedHidden handles
        /// are excluded from every parent-visible surface and from parent
        /// recovery (GLORY-002 / SURFACE-006).
        Ownership: HandleOwnership
        Lifecycle: HandleLifecycle
        /// EXEC-018: handle create order (HandleLinked fold sequence). Additive;
        /// derived from link order, not a fact-schema field. Stable sort key #2.
        CreationOrder: int
        /// Last completion cell ever written. Survives Retired so clean-break
        /// migration can re-read the blob for LegacyFalseAbort without history scan.
        LastCompletion: HandleCompletion option
        Work: HandleWorkId option
    }

type HandleWork =
    { Work: HandleWorkId
      LogicalRunId: LogicalRunId
      Lifecycle: HandleLifecycle
      LastCompletion: HandleCompletion option
      ConsumptionId: string option }

type AdmittedWork = private AdmittedWork of HandleWorkId * LogicalRunId

module AdmittedWork =
    let id (AdmittedWork(work, _)) = work
    let logicalRunId (AdmittedWork(_, run)) = run

module private HandleValues =
    let handle = function
        | HandleId.Agent id -> createObj [ "kind" ==> 0; "id" ==> AgentHandleId.value id ]
        | HandleId.Pty id -> createObj [ "kind" ==> 1; "id" ==> PtyHandleId.value id ]
        | HandleId.ManagerJob id -> createObj [ "kind" ==> 2; "id" ==> ManagerJobId.value id ]
    let handleOf (raw: obj) =
        let id: string = raw?id
        match unbox<int> raw?kind with
        | 0 -> HandleId.Agent(AgentHandleId.create id)
        | 1 -> HandleId.Pty(PtyHandleId.create id)
        | _ -> HandleId.ManagerJob(ManagerJobId.create id)
    let optional mapping value = value |> Option.map mapping |> Option.map box |> Option.defaultValue null
    let option mapping (value: obj) = if isNull value then None else Some(mapping (unbox<string> value))
    let work (value: HandleWorkId) = createObj [ "handle" ==> handle value.Handle; "child" ==> SessionId.value value.ChildSessionId; "root" ==> AuthorityRootUserMessageId.value value.AuthorityRoot ]
    let workOf (raw: obj) : HandleWorkId = { Handle = handleOf raw?handle; ChildSessionId = SessionId.create raw?child; AuthorityRoot = AuthorityRootUserMessageId.create raw?root }
    let completion (value: HandleCompletion) = createObj [ "kind" ==> string value.Kind; "reference" ==> optional BlobRef.value value.CompletionRef; "digest" ==> optional BlobDigest.value value.CompletionDigest ]
    let completionOf (raw: obj) =
        { Kind = match unbox<string> raw?kind with | "Terminal" -> HandleCompletionKind.Terminal | "SendFailure" -> HandleCompletionKind.SendFailure | _ -> HandleCompletionKind.Cancelled
          CompletionRef = option BlobRef.create raw?reference
          CompletionDigest = option BlobDigest.create raw?digest }
    let reasonOf = function
        | "ParentCancelled" -> HandleAbandonReason.ParentCancelled
        | "DeadlineExceeded" -> HandleAbandonReason.DeadlineExceeded
        | "HostSessionGone" -> HandleAbandonReason.HostSessionGone
        | _ -> HandleAbandonReason.Voided
    let lifecycle (raw: obj) =
        match unbox<string> raw?phase with
        | "Active" -> Active
        | "CompletedAwaitingJoin" -> CompletedAwaitingJoin(completionOf raw?completion)
        | "Abandoned" -> Abandoned(reasonOf raw?reason)
        | _ -> Retired
    let cellOption (raw: obj) = if isNull raw then None else Some(completionOf raw)
    let record (raw: obj) : HandleRecord =
        { Handle = handleOf raw?handle
          ChildSessionId = SessionId.create raw?child
          TargetAgent = raw?target
          Byname = raw?byname
          CanonicalRole = Roles.tryParseRole raw?role |> Option.get
          Ownership = if unbox<bool> raw?parentVisible then HandleOwnership.DurableParentHandle else HandleOwnership.HostOwnedHidden
          Lifecycle = lifecycle raw
          CreationOrder = raw?creationOrder
          LastCompletion = cellOption raw?lastCompletion
          Work = if isNull raw?work then None else Some(workOf raw?work) }
    let workRecord (raw: obj) : HandleWork =
        { Work = workOf raw?work
          LogicalRunId = LogicalRunId.create raw?logicalRun
          Lifecycle = lifecycle raw
          LastCompletion = cellOption raw?lastCompletion
          ConsumptionId = option id raw?consumptionId }
    let records (raw: obj) = (unbox<obj array> raw) |> Array.map record |> Array.toList

/// Typed read views are made on demand; the immutable library history is the only owner.
[<Sealed>]
type AgentLinkageProjection internal (history: obj) =
    member internal _.Native = history
    member _.Handles = (unbox<obj array> (history?bindings())) |> Array.map (fun raw -> let record = HandleValues.record raw in record.Handle, record) |> Map.ofArray
    member _.Works = (unbox<obj array> (history?works())) |> Array.map (fun raw -> let record = HandleValues.workRecord raw in record.Work, record) |> Map.ofArray
    member _.LegacyWorkHandles = (unbox<obj array> (history?legacyHandles())) |> Array.map HandleValues.handleOf |> Set.ofArray
    member _.NextCreationOrder: int = history?nextCreationOrder

type HandleTransitionRejection =
    | UnknownHandle
    | HandleIdentityConflict
    | HandleIsRetired
    | AlreadyCompleted
    | AlreadyAbandoned
    | NotCompleted
    | WorkNotAdmitted
    | WorkStillActive
    | ConsumptionMismatch
    | LegacyWorkAmbiguous

module HandleProjection =
    [<Import("HandleHistory", "wanxiangli/handle-lifecycle")>]
    let private historyClass: obj = jsNative
    let empty = AgentLinkageProjection(createNew historyClass ())
    let native (current: AgentLinkageProjection) = current.Native
    let filterBindings (accept: HandleRecord -> bool) (current: AgentLinkageProjection) = AgentLinkageProjection(current.Native?filterBindings(fun raw -> accept (HandleValues.record raw)))
    let private rejection = function
        | "UnknownHandle" -> UnknownHandle
        | "HandleIdentityConflict" -> HandleIdentityConflict
        | "HandleIsRetired" -> HandleIsRetired
        | "AlreadyCompleted" -> AlreadyCompleted
        | "AlreadyAbandoned" -> AlreadyAbandoned
        | "NotCompleted" -> NotCompleted
        | "WorkNotAdmitted" -> WorkNotAdmitted
        | "WorkStillActive" -> WorkStillActive
        | "ConsumptionMismatch" -> ConsumptionMismatch
        | "LegacyWorkAmbiguous" -> LegacyWorkAmbiguous
        | other -> invalidOp ("Unknown handle rejection: " + other)
    let private result (raw: obj) =
        if unbox<bool> raw?ok then Ok(AgentLinkageProjection raw?value)
        else Error(rejection raw?error)
    let linkNamed handle childSessionId (targetAgent: string) (byname: string) role ownership (current: AgentLinkageProjection) =
        current.Native?link(createObj [ "handle" ==> HandleValues.handle handle; "child" ==> SessionId.value childSessionId; "target" ==> targetAgent; "byname" ==> byname; "role" ==> Roles.roleLabel role; "parentVisible" ==> (ownership = HandleOwnership.DurableParentHandle) ]) |> result
    let link handle childSessionId targetAgent role ownership current = linkNamed handle childSessionId targetAgent targetAgent role ownership current
    let private transition event handle fields (current: AgentLinkageProjection) =
        current.Native?transition(createObj ([ "event" ==> event; "handle" ==> HandleValues.handle handle ] @ fields)) |> result
    let complete handle completion current = transition "complete" handle [ "completion" ==> HandleValues.completion completion ] current
    let abandon handle (reason: HandleAbandonReason) current = transition "abandon" handle [ "reason" ==> string reason ] current
    let retire handle current = transition "retire" handle [] current
    let rejectFalseCompletion handle expectedRef expectedDigest (current: AgentLinkageProjection) =
        let binding: obj = current.Native?binding(HandleValues.handle handle)
        let kind: obj = if isNull binding || isNull binding?completion then box "Terminal" else binding?completion?kind
        transition "rejectFalseCompletion" handle [ "completion" ==> createObj [ "kind" ==> kind; "reference" ==> BlobRef.value expectedRef; "digest" ==> BlobDigest.value expectedDigest ] ] current
    let admitWork parentId handle (authority: PromptAuthority.PromptAuthorityProjection) (current: AgentLinkageProjection) =
        let profile, landing =
            match authority.ActiveLogicalRun with
            | None -> null, null
            | Some profile ->
                let physical = PhysicalUserMessageId.create (AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId)
                let owner = PromptAuthority.identitySeedOwner profile.IdentitySeed |> Option.map (fun (owner, _, _) -> SessionId.value owner) |> Option.toObj
                let identity value: string = emitJsExpr value "JSON.stringify($0)"
                let raw = createObj [ "child" ==> SessionId.value profile.SessionId; "role" ==> Roles.roleLabel profile.CanonicalRole; "target" ==> profile.SelectedAgent; "root" ==> AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId; "logicalRun" ==> LogicalRunId.value profile.LogicalRunId; "identity" ==> identity profile.IdentitySeed; "owner" ==> owner ]
                let landing = Map.tryFind physical authority.PhysicalLandings |> Option.map (fun accepted -> createObj [ "child" ==> SessionId.value accepted.SessionId; "identity" ==> identity accepted.IdentitySeed; "ownerRoot" ==> (accepted.Origin = PromptAuthority.PromptOrigin.AuthorityRoot PromptAuthority.RootAuthorityKind.AgentOwnerRoot) ]) |> Option.toObj
                raw, landing
        current.Native?admit(createObj [ "parent" ==> SessionId.value parentId; "handle" ==> HandleValues.handle handle; "profile" ==> profile; "landing" ==> landing ]) |> result
    let tryWork work (current: AgentLinkageProjection) =
        let raw: obj = current.Native?work(HandleValues.work work)
        if isNull raw then None else Some(HandleValues.workRecord raw)
    let tryAdmittedWork work (current: AgentLinkageProjection) =
        let raw: obj = current.Native?admitted(HandleValues.work work)
        if unbox<bool> raw?ok then Ok(AdmittedWork(work, LogicalRunId.create raw?value?logicalRun)) else Error(rejection raw?error)
    let private transitionWork event work fields (current: AgentLinkageProjection) = current.Native?transitionWork(createObj ([ "event" ==> event; "work" ==> HandleValues.work work ] @ fields)) |> result
    let completeWork work completion current = transitionWork "complete" work [ "completion" ==> HandleValues.completion completion ] current
    let abandonWork work (reason: HandleAbandonReason) current = transitionWork "abandon" work [ "reason" ==> string reason ] current
    let voidWork work current = transitionWork "void" work [] current
    let consumeWork work (consumptionId: string) expected current = transitionWork "consume" work [ "consumptionId" ==> consumptionId; "completion" ==> HandleValues.completion expected ] current
    let workBindings (current: AgentLinkageProjection) = HandleValues.records (current.Native?workBindings())
    let private recordOption (raw: obj) = if isNull raw then None else Some(HandleValues.record raw)
    let tryBinding handle (current: AgentLinkageProjection) = current.Native?binding(HandleValues.handle handle) |> recordOption
    let tryFind handle (current: AgentLinkageProjection) = current.Native?summary(HandleValues.handle handle) |> recordOption
    let isRetired handle current = tryBinding handle current |> Option.exists (fun record -> record.Lifecycle = Retired)
    let isAbandoned handle current = tryBinding handle current |> Option.exists (fun record -> match record.Lifecycle with | Abandoned _ -> true | _ -> false)
    let private summary current (binding: HandleRecord) = tryFind binding.Handle current |> Option.defaultValue binding
    let private recordsWhere predicate (current: AgentLinkageProjection) = HandleValues.records (current.Native?summaries()) |> List.filter predicate
    let private parentVisible (record: HandleRecord) = record.Ownership = HandleOwnership.DurableParentHandle
    let tryFindByByname (byname: string) (current: AgentLinkageProjection) = current.Native?byname(byname, true) |> recordOption
    let tryFindBindingByByname (byname: string) (current: AgentLinkageProjection) = current.Native?byname(byname, false) |> recordOption
    let auditListable (current: AgentLinkageProjection) = current.Handles |> Map.toList |> List.map snd |> List.filter (fun record -> parentVisible record && match record.Lifecycle with | Active | CompletedAwaitingJoin _ -> true | _ -> false)
    let auditActiveHandles (current: AgentLinkageProjection) = current.Handles |> Map.toList |> List.map snd |> List.filter (fun record -> parentVisible record && record.Lifecycle = Active)
    let listable current = recordsWhere (fun record -> record.Work.IsSome && parentVisible record && match record.Lifecycle with | Active | CompletedAwaitingJoin _ -> true | _ -> false) current

    let private isIdleFixedDevOpsBinding (record: HandleRecord) =
        record.Work.IsNone
        && record.CanonicalRole = Role.DevOps
        && record.Byname = "devops"
        && record.TargetAgent = "devops"
        && record.Lifecycle = Active

    let private isHistoricalVoidedBinding (record: HandleRecord) =
        record.Work.IsNone
        && match record.Lifecycle with
           | Abandoned HandleAbandonReason.Voided -> true
           | _ -> false

    let private horizonRecord current binding =
        let record = summary current binding

        if isIdleFixedDevOpsBinding binding && record.Lifecycle = Retired then
            binding
        else
            record

    /// participant-horizon-011: parent-visible roster is not the same question
    /// as "does this handle still block finality?". An Abandoned child still has
    /// one undelivered consequence for its parent, so it remains visible until
    /// Join consumes that consequence and writes Retired.
    let horizonVisible (current: AgentLinkageProjection) =
        current.Handles
        |> Map.toList
        |> List.map (snd >> horizonRecord current)
        |> List.filter (fun record ->
            (record.Work.IsSome
             || isIdleFixedDevOpsBinding record
             || isHistoricalVoidedBinding record)
            && parentVisible record
            && (match record.Lifecycle with
                | Retired -> false
                | Active
                | CompletedAwaitingJoin _
                | Abandoned _ -> true))

    /// EXEC-004: what `join` may consume as completion cells. Abandoned is not
    /// a completion cell — see `reportableAbandoned` for EXEC-009 batch items.
    let private deliveryRecords (current: AgentLinkageProjection) =
        (current.Handles |> Map.toList |> List.map snd) @ workBindings current

    let joinable (current: AgentLinkageProjection) =
        deliveryRecords current
        |> List.filter (fun record ->
            parentVisible record
            && (match record.Lifecycle with
                | CompletedAwaitingJoin _ -> true
                | Active
                | Abandoned _
                | Retired -> false))

    /// EXEC-009: Abandoned handles that join must include in the next `[[result]]`
    /// batch (explicit status, not Completed). After report, consume retires them
    /// so they appear at most once.
    let reportableAbandoned (current: AgentLinkageProjection) =
        deliveryRecords current
        |> List.filter (fun record ->
            parentVisible record
            && (match record.Lifecycle with
                | Abandoned _ -> true
                | Active
                | CompletedAwaitingJoin _
                | Retired -> false))

    /// EXEC-009: parent abort cancels every owned resource individually, so the
    /// caller needs the actual handles rather than a count. Host-owned hidden
    /// handles are not the parent's resources to cancel.
    let activeHandles (current: AgentLinkageProjection) =
        current
        |> recordsWhere (fun record ->
            record.Work.IsSome
            && parentVisible record
            && (match record.Lifecycle with
                | Active -> true
                | CompletedAwaitingJoin _
                | Abandoned _
                | Retired -> false))

    /// The handle driving a child session, retired ones included.
    ///
    /// Retired records are deliberately visible: EXEC-009 makes the tombstone
    /// permanent, and a caller asking "is this session one of my children" must get
    /// yes for a child that already finished. Filtering here would make a retired
    /// child look like one that never existed.
    let tryFindByChildSession (childSessionId: SessionId) (current: AgentLinkageProjection) =
        current.Handles
        |> Map.tryPick (fun _ record ->
            if record.ChildSessionId = childSessionId then
                Some(summary current record)
            else
                None)

    /// Every child session this parent has ever linked.
    ///
    /// Replaces the old `LinkedChildren` map. That map was keyed by child and held
    /// only live entries, so restart recovery and the retired-handle check needed
    /// two different structures; one list of records answers both.
    let linkedChildren (current: AgentLinkageProjection) =
        current.Handles |> Map.toList |> List.map (snd >> summary current)

    let handleValue (value: HandleId) : obj = HandleValues.handle value
    let handleOf (value: obj) : HandleId = HandleValues.handleOf value
    let workValue (value: HandleWorkId) : obj = HandleValues.work value
    let workOf (value: obj) : HandleWorkId = HandleValues.workOf value
    let completionOf (value: obj) : HandleCompletion = HandleValues.completionOf value
    let reasonOf (value: string) : HandleAbandonReason = HandleValues.reasonOf value
    let recordOf (value: obj) : HandleRecord = HandleValues.record value
    let rejectionOf (value: string) : HandleTransitionRejection = rejection value
    let recordValue (record: HandleRecord) =
        let phase, reason, cell =
            match record.Lifecycle with
            | Active -> "Active", null, null
            | CompletedAwaitingJoin cell -> "CompletedAwaitingJoin", null, HandleValues.completion cell
            | Abandoned reason -> "Abandoned", box (string reason), null
            | Retired -> "Retired", null, null
        createObj [ "handle" ==> HandleValues.handle record.Handle; "child" ==> SessionId.value record.ChildSessionId; "target" ==> record.TargetAgent; "byname" ==> record.Byname; "role" ==> Roles.roleLabel record.CanonicalRole; "parentVisible" ==> (record.Ownership = HandleOwnership.DurableParentHandle); "creationOrder" ==> record.CreationOrder; "phase" ==> phase; "reason" ==> reason; "completion" ==> cell; "lastCompletion" ==> HandleValues.optional HandleValues.completion record.LastCompletion; "work" ==> HandleValues.optional HandleValues.work record.Work ]
