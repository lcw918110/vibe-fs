namespace Wanxiangshu.Execution.Delegation

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

type HandleWorkRecord =
    { Work: HandleWorkId
      LogicalRunId: LogicalRunId
      Lifecycle: HandleLifecycle
      LastCompletion: HandleCompletion option
      ConsumptionId: string option }

type AdmittedWork = private AdmittedWork of HandleWorkId * LogicalRunId

module AdmittedWork =
    let id (AdmittedWork(work, _)) = work
    let logicalRunId (AdmittedWork(_, run)) = run

/// Durable handle linkage for one parent session.
///
/// PERSIST-008: one map, keyed lookup, no history scan. Retired entries stay in
/// the map — that IS the tombstone. Removing them would make a retired id
/// indistinguishable from one that never existed, which is the exact confusion
/// EXEC-009 forbids. Abandoned entries stay until join reports them once, then
/// retire (single-report tombstone, same spirit as completion consume).
type AgentLinkageProjection =
    {
        Handles: Map<HandleId, HandleRecord>
        /// Monotonic counter: next CreationOrder assigned on HandleLinked.
        NextCreationOrder: int
        Works: Map<HandleWorkId, HandleWorkRecord>
        LegacyWorkHandles: Set<HandleId>
    }

/// Why a lifecycle transition was refused.
type HandleTransitionRejection =
    | UnknownHandle
    | HandleIdentityConflict
    /// EXEC-009: retired is terminal. Re-linking a retired id is the failure
    /// mode the tombstone exists to prevent.
    | HandleIsRetired
    /// EXEC-004: the completion cell is single-assignment; the first winner is
    /// the only winner.
    | AlreadyCompleted
    /// EXEC-009: abandon is single-assignment; a second abandon is refused.
    | AlreadyAbandoned
    /// `join` consumes a completion, and an active handle has none.
    | NotCompleted
    | WorkNotAdmitted
    | WorkStillActive
    | ConsumptionMismatch
    | LegacyWorkAmbiguous

module HandleProjection =

    let empty =
        { Handles = Map.empty
          NextCreationOrder = 0
          Works = Map.empty
          LegacyWorkHandles = Set.empty }

    let private sameBinding
        (childSessionId: SessionId)
        (targetAgent: string)
        (byname: string)
        (role: Role)
        (ownership: HandleOwnership)
        (existing: HandleRecord)
        =
        existing.ChildSessionId = childSessionId
        && existing.TargetAgent = targetAgent
        && existing.Byname = byname
        && existing.CanonicalRole = role
        && existing.Ownership = ownership

    let private lifecycleAfterSameBinding
        (handle: HandleId)
        (existing: HandleRecord)
        (current: AgentLinkageProjection)
        : Result<AgentLinkageProjection, HandleTransitionRejection> =
        match existing.Lifecycle with
        | Retired ->
            Ok
                { current with
                    Handles = Map.add handle { existing with Lifecycle = Active } current.Handles }
        | Active
        | CompletedAwaitingJoin _ -> Ok current
        | Abandoned _ -> Error AlreadyAbandoned

    let private reopenOrKeepExisting
        (childSessionId: SessionId)
        (targetAgent: string)
        (byname: string)
        (role: Role)
        (ownership: HandleOwnership)
        (existing: HandleRecord)
        (current: AgentLinkageProjection)
        (handle: HandleId)
        : Result<AgentLinkageProjection, HandleTransitionRejection> =
        if not (sameBinding childSessionId targetAgent byname role ownership existing) then
            Error HandleIdentityConflict
        else
            lifecycleAfterSameBinding handle existing current

    let private absorbRetiredReplay
        (childSessionId: SessionId)
        (targetAgent: string)
        (byname: string)
        (role: Role)
        (ownership: HandleOwnership)
        (existing: HandleRecord)
        (current: AgentLinkageProjection)
        : Result<AgentLinkageProjection, HandleTransitionRejection> =
        if not (sameBinding childSessionId targetAgent byname role ownership existing) then
            Error HandleIdentityConflict
        else
            Ok current

    /// Abandoned is a durable terminal: the child never returns along this
    /// handle, so a re-link is refused (msl-006/018). Retired is the
    /// consumed-completion tombstone: reuse of the same agent id reopens Labor
    /// on the same child session (msl-015; the tombstone is the retained
    /// LastCompletion, not a permanent ban on further Labor). Active and
    /// CompletedAwaitingJoin replay idempotently.
    let linkNamed
        (handle: HandleId)
        (childSessionId: SessionId)
        (targetAgent: string)
        (byname: string)
        (role: Role)
        (ownership: HandleOwnership)
        (current: AgentLinkageProjection)
        : Result<AgentLinkageProjection, HandleTransitionRejection> =
        match Map.tryFind handle current.Handles with
        | None ->
            let order = current.NextCreationOrder

            Ok
                { Handles =
                    Map.add
                        handle
                        { Handle = handle
                          ChildSessionId = childSessionId
                          TargetAgent = targetAgent
                          Byname = byname
                          CanonicalRole = role
                          Ownership = ownership
                          Lifecycle = Active
                          CreationOrder = order
                          LastCompletion = None
                          Work = None }
                        current.Handles
                  NextCreationOrder = order + 1
                  Works = current.Works
                  LegacyWorkHandles = current.LegacyWorkHandles }
        | Some { Lifecycle = Abandoned _ } -> Error AlreadyAbandoned
        | Some existing -> reopenOrKeepExisting childSessionId targetAgent byname role ownership existing current handle

    /// Journal replay path (ExecutionFactFold): replaying a historical
    /// HandleLinked fact must not change already-folded state — a Retired
    /// binding absorbs the replay idempotently and stays Retired (msl-006:
    /// replay cannot revive). The explicit surface link command keeps the
    /// msl-009 reopen semantics in linkNamed.
    let replayLink
        (handle: HandleId)
        (childSessionId: SessionId)
        (targetAgent: string)
        (byname: string)
        (role: Role)
        (ownership: HandleOwnership)
        (current: AgentLinkageProjection)
        : Result<AgentLinkageProjection, HandleTransitionRejection> =
        match Map.tryFind handle current.Handles with
        | Some({ Lifecycle = Retired } as existing) ->
            absorbRetiredReplay childSessionId targetAgent byname role ownership existing current
        | _ -> linkNamed handle childSessionId targetAgent byname role ownership current

    /// Compatibility for internal callers that do not need a separate
    /// presentation identity. Provider-facing fork/commission use linkNamed.
    let link
        (handle: HandleId)
        (childSessionId: SessionId)
        (targetAgent: string)
        (role: Role)
        (ownership: HandleOwnership)
        (current: AgentLinkageProjection)
        : Result<AgentLinkageProjection, HandleTransitionRejection> =
        linkNamed handle childSessionId targetAgent targetAgent role ownership current

    /// EXEC-004: terminal, send-failure and cancel race for one cell. Whoever
    /// arrives first wins; later arrivals are refused, not overwritten.
    let complete
        (handle: HandleId)
        (completion: HandleCompletion)
        (current: AgentLinkageProjection)
        : Result<AgentLinkageProjection, HandleTransitionRejection> =
        match Map.tryFind handle current.Handles with
        | None -> Error UnknownHandle
        | Some { Lifecycle = Retired } -> Error HandleIsRetired
        | Some { Lifecycle = Abandoned _ } -> Error AlreadyAbandoned
        | Some { Lifecycle = CompletedAwaitingJoin _ } -> Error AlreadyCompleted
        | Some record ->
            Ok
                { current with
                    Handles =
                        Map.add
                            handle
                            { record with
                                Lifecycle = CompletedAwaitingJoin completion
                                LastCompletion = Some completion }
                            current.Handles
                    LegacyWorkHandles = Set.add handle current.LegacyWorkHandles }

    /// EXEC-009: durable abandon. Active or CompletedAwaitingJoin → Abandoned.
    /// First winner wins; later abandons are refused.
    let abandon
        (handle: HandleId)
        (reason: HandleAbandonReason)
        (current: AgentLinkageProjection)
        : Result<AgentLinkageProjection, HandleTransitionRejection> =
        match Map.tryFind handle current.Handles with
        | None -> Error UnknownHandle
        | Some { Lifecycle = Retired } -> Error HandleIsRetired
        | Some { Lifecycle = Abandoned _ } -> Error AlreadyAbandoned
        | Some record ->
            Ok
                { current with
                    Handles =
                        Map.add
                            handle
                            { record with
                                Lifecycle = Abandoned reason }
                            current.Handles }

    /// EXEC-004/EXEC-009: join consumed a reportable terminal (CompletedAwaitingJoin
    /// or Abandoned) and wrote the tombstone. Active has no payload to consume.
    /// Abandoned → Retired is the single-report path (EXEC-009 batch item once).
    let retire
        (handle: HandleId)
        (current: AgentLinkageProjection)
        : Result<AgentLinkageProjection, HandleTransitionRejection> =
        match Map.tryFind handle current.Handles with
        | None -> Error UnknownHandle
        | Some { Lifecycle = Retired } -> Error HandleIsRetired
        | Some { Lifecycle = Active } -> Error NotCompleted
        | Some record ->
            Ok
                { current with
                    Handles = Map.add handle { record with Lifecycle = Retired } current.Handles }

    /// Clean-break: reject a false completion cell only when lifecycle is
    /// CompletedAwaitingJoin and ref/digest match exactly → Active. Mismatch
    /// refuses so a true terminal cannot be revoked by a bad compensation fact.
    /// Already Active = prior reject succeeded (idempotent Ok).
    let private verifyFalseCompletionCell
        (handle: HandleId)
        (expectedRef: BlobRef)
        (expectedDigest: BlobDigest)
        (current: AgentLinkageProjection)
        (record: HandleRecord)
        (cell: HandleCompletion)
        =
        match cell.CompletionRef, cell.CompletionDigest with
        | Some blobRef, Some digest when blobRef = expectedRef && digest = expectedDigest ->
            Ok
                { current with
                    Handles = Map.add handle { record with Lifecycle = Active } current.Handles }
        | _ -> Error AlreadyCompleted

    let rejectFalseCompletion
        (handle: HandleId)
        (expectedRef: BlobRef)
        (expectedDigest: BlobDigest)
        (current: AgentLinkageProjection)
        : Result<AgentLinkageProjection, HandleTransitionRejection> =
        match Map.tryFind handle current.Handles with
        | None -> Error UnknownHandle
        | Some { Lifecycle = Retired } -> Error HandleIsRetired
        | Some { Lifecycle = Abandoned _ } -> Error AlreadyAbandoned
        | Some { Lifecycle = Active } -> Ok current
        | Some({ Lifecycle = CompletedAwaitingJoin cell } as record) ->
            verifyFalseCompletionCell handle expectedRef expectedDigest current record cell

    let private admitValidatedWork
        handle
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (binding: HandleRecord)
        (current: AgentLinkageProjection)
        =
        let work =
            { Handle = handle
              ChildSessionId = profile.SessionId
              AuthorityRoot = profile.AuthorityRootUserMessageId }

        match Map.tryFind work current.Works, binding.Lifecycle with
        | _, Abandoned _ -> Error AlreadyAbandoned
        | Some existing, _ when existing.LogicalRunId = profile.LogicalRunId -> Ok current
        | Some _, _ -> Error HandleIdentityConflict
        | None, _ when
            current.Works
            |> Map.exists (fun key value -> key.Handle = handle && value.Lifecycle = Active)
            ->
            Error WorkStillActive
        | None, _ ->
            let admitted =
                { Work = work
                  LogicalRunId = profile.LogicalRunId
                  Lifecycle = Active
                  LastCompletion = None
                  ConsumptionId = None }

            Ok
                { current with
                    Works = Map.add work admitted current.Works }

    let private decideAdmitWork
        parentId
        handle
        (authority: PromptAuthority.PromptAuthorityProjection)
        (binding: HandleRecord)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (current: AgentLinkageProjection)
        =
        let physical =
            PhysicalUserMessageId.create (AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId)

        let exactLanding =
            Map.tryFind physical authority.PhysicalLandings
            |> Option.exists (fun accepted ->
                accepted.SessionId = binding.ChildSessionId
                && accepted.IdentitySeed = profile.IdentitySeed
                && accepted.Origin = PromptAuthority.PromptOrigin.AuthorityRoot
                    PromptAuthority.RootAuthorityKind.AgentOwnerRoot)

        let exactOwner =
            PromptAuthority.identitySeedOwner profile.IdentitySeed
            |> Option.exists (fun (owner, _, _) -> owner = parentId)

        if not exactLanding || not exactOwner then
            Error WorkNotAdmitted
        elif
            profile.SessionId <> binding.ChildSessionId
            || profile.CanonicalRole <> binding.CanonicalRole
            || profile.SelectedAgent <> binding.TargetAgent
        then
            Error HandleIdentityConflict
        elif Set.contains handle current.LegacyWorkHandles then
            Error LegacyWorkAmbiguous
        else
            admitValidatedWork handle profile binding current

    // A binding never becomes a new work. Only validated canonical admission
    // adds a Root-keyed entry; completion/consume never alter another entry.
    let admitWork
        parentId
        handle
        (authority: PromptAuthority.PromptAuthorityProjection)
        (current: AgentLinkageProjection)
        =
        match Map.tryFind handle current.Handles, authority.ActiveLogicalRun with
        | None, _ -> Error UnknownHandle
        | _, None -> Error WorkNotAdmitted
        | Some binding, Some profile -> decideAdmitWork parentId handle authority binding profile current

    let tryWork work (current: AgentLinkageProjection) = Map.tryFind work current.Works

    let tryAdmittedWork work current =
        match tryWork work current with
        | _ when Set.contains work.Handle current.LegacyWorkHandles -> Error LegacyWorkAmbiguous
        | Some { Lifecycle = Active
                 LogicalRunId = logicalRunId } -> Ok(AdmittedWork(work, logicalRunId))
        | Some _ -> Error HandleIsRetired
        | None -> Error WorkNotAdmitted

    let private updateWork work update current =
        match tryWork work current with
        | None -> Error WorkNotAdmitted
        | Some _ when Set.contains work.Handle current.LegacyWorkHandles -> Error LegacyWorkAmbiguous
        | Some existing ->
            update existing
            |> Result.map (fun value ->
                { current with
                    Works = Map.add work value current.Works })

    let completeWork work completion current =
        updateWork
            work
            (fun record ->
                match record.Lifecycle with
                | Active when
                    completion.Kind <> HandleCompletionKind.Cancelled
                    && completion.CompletionRef.IsSome
                    && completion.CompletionDigest.IsSome
                    ->
                    Ok
                        { record with
                            Lifecycle = CompletedAwaitingJoin completion
                            LastCompletion = Some completion }
                | Active -> Error NotCompleted
                | CompletedAwaitingJoin _ -> Error AlreadyCompleted
                | Abandoned _ -> Error AlreadyAbandoned
                | Retired -> Error HandleIsRetired)
            current

    let abandonWork work reason current =
        updateWork
            work
            (fun record ->
                match record.Lifecycle with
                | Active ->
                    Ok
                        { record with
                            Lifecycle = Abandoned reason }
                | CompletedAwaitingJoin _ -> Error AlreadyCompleted
                | Abandoned _ -> Error AlreadyAbandoned
                | Retired -> Error HandleIsRetired)
            current

    let voidWork work current =
        updateWork
            work
            (fun record ->
                match record.Lifecycle with
                | Active -> Ok { record with Lifecycle = Retired }
                | CompletedAwaitingJoin _ -> Error AlreadyCompleted
                | Abandoned _ -> Error AlreadyAbandoned
                | Retired -> Error HandleIsRetired)
            current

    let consumeWork work consumptionId expected current =
        updateWork
            work
            (fun record ->
                match record.Lifecycle with
                | _ when System.String.IsNullOrWhiteSpace consumptionId -> Error ConsumptionMismatch
                | CompletedAwaitingJoin cell when cell = expected ->
                    Ok
                        { record with
                            Lifecycle = Retired
                            ConsumptionId = Some consumptionId }
                | Abandoned _ when
                    expected.Kind = HandleCompletionKind.Cancelled
                    && expected.CompletionRef.IsNone
                    && expected.CompletionDigest.IsNone
                    ->
                    Ok
                        { record with
                            Lifecycle = Retired
                            ConsumptionId = Some consumptionId }
                | CompletedAwaitingJoin _ -> Error ConsumptionMismatch
                | Retired -> Error HandleIsRetired
                | Active -> Error NotCompleted
                | Abandoned _ -> Error ConsumptionMismatch)
            current

    let private workView (binding: HandleRecord) (work: HandleWorkRecord) =
        { binding with
            Work = Some work.Work
            Lifecycle = work.Lifecycle
            LastCompletion = work.LastCompletion }

    let workRecords (current: AgentLinkageProjection) =
        current.Works
        |> Map.toList
        |> List.choose (fun (_, work) ->
            Map.tryFind work.Work.Handle current.Handles
            |> Option.map (fun binding -> workView binding work))

    let private summary (current: AgentLinkageProjection) (binding: HandleRecord) =
        let works =
            if Set.contains binding.Handle current.LegacyWorkHandles then
                []
            else
                workRecords current
                |> List.filter (fun record -> record.Handle = binding.Handle)

        match works |> List.tryFind (fun record -> record.Lifecycle = Active) with
        | Some active -> active
        | None ->
            works
            |> List.tryFind (fun record -> record.Lifecycle <> Retired)
            |> Option.orElseWith (fun () -> List.tryHead works)
            |> Option.defaultValue binding

    let tryBinding handle (current: AgentLinkageProjection) = Map.tryFind handle current.Handles

    let tryFind (handle: HandleId) (current: AgentLinkageProjection) =
        tryBinding handle current |> Option.map (summary current)


    /// EXEC-009: the question `fork` must ask before treating an id as anything.
    let isRetired (handle: HandleId) (current: AgentLinkageProjection) =
        match Map.tryFind handle current.Handles with
        | Some { Lifecycle = Retired } -> true
        | _ -> false

    /// EXEC-009: abandoned is durable terminal and not joinable.
    let isAbandoned (handle: HandleId) (current: AgentLinkageProjection) =
        match Map.tryFind handle current.Handles with
        | Some { Lifecycle = Abandoned _ } -> true
        | _ -> false

    let private recordsWhere predicate (current: AgentLinkageProjection) =
        current.Handles
        |> Map.toList
        |> List.map (snd >> summary current)
        |> List.filter predicate

    /// GLORY-002 / SURFACE-006: a HostOwnedHidden handle (such as an internal Host workflow)
    /// is invisible to its nominal parent. Every parent-visible
    /// surface — list, join, background guard, cancellation — filters it out.
    /// The record itself stays durable for audit and for the Host-owned
    /// workflow's own recovery.
    let private parentVisible (record: HandleRecord) =
        match record.Ownership with
        | HandleOwnership.DurableParentHandle -> true
        | HandleOwnership.HostOwnedHidden -> false

    /// EXEC-002: provider continuation lookup is by stable Byname, never by
    /// AgentHandleId. Retired records remain searchable so a name cannot be
    /// silently recycled for a different logical person later in the same life.
    let tryFindByByname (byname: string) (current: AgentLinkageProjection) =
        if System.String.IsNullOrWhiteSpace byname then
            None
        else
            let wanted = byname.Trim()

            current.Handles
            |> Map.tryPick (fun _ binding ->
                let record = summary current binding

                if
                    parentVisible record
                    && System.String.Equals(record.Byname, wanted, System.StringComparison.OrdinalIgnoreCase)
                then
                    Some record
                else
                    None)

    /// The raw binding record for a byname — no summary projection. The
    /// summary folds work-unit state into the view, so a Retired work unit
    /// masquerades as a Retired handle there; callers that must decide on the
    /// binding's own lifecycle (managed-session-lifecycle-024: the fixed DevOps
    /// binding stays Active across work-unit terminals) read the binding here.
    let tryFindBindingByByname (byname: string) (current: AgentLinkageProjection) =
        if System.String.IsNullOrWhiteSpace byname then
            None
        else
            let wanted = byname.Trim()

            current.Handles
            |> Map.tryPick (fun _ binding ->
                if
                    parentVisible binding
                    && System.String.Equals(binding.Byname, wanted, System.StringComparison.OrdinalIgnoreCase)
                then
                    Some binding
                else
                    None)

    /// Historical projection observations are not executable work admission.
    let auditListable (current: AgentLinkageProjection) =
        current.Handles
        |> Map.toList
        |> List.map snd
        |> List.filter (fun record ->
            parentVisible record
            && (match record.Lifecycle with
                | Active
                | CompletedAwaitingJoin _ -> true
                | _ -> false))

    let auditActiveHandles (current: AgentLinkageProjection) =
        current.Handles
        |> Map.toList
        |> List.map snd
        |> List.filter (fun record -> parentVisible record && record.Lifecycle = Active)

    /// EXEC-005: `list` shows running, busy and completed-awaiting-join, never
    /// retired or abandoned.
    let listable (current: AgentLinkageProjection) =
        current
        |> recordsWhere (fun record ->
            record.Work.IsSome
            && parentVisible record
            && (match record.Lifecycle with
                | Retired
                | Abandoned _ -> false
                | Active
                | CompletedAwaitingJoin _ -> true))

    /// participant-horizon-011: a durably linked visible child stays on the
    /// roster until Join consumes its final consequence and writes Retired —
    /// Abandoned included. Visibility follows the HandleLinked binding, not
    /// work admission: a handle without admitted work is still a durable child
    /// whose consequence the parent has not yet received.
    let horizonVisible (current: AgentLinkageProjection) =
        current
        |> recordsWhere (fun record ->
            parentVisible record
            && (match record.Lifecycle with
                | Retired -> false
                | Active
                | CompletedAwaitingJoin _
                | Abandoned _ -> true))

    /// EXEC-004: what `join` may consume as completion cells. Abandoned is not
    /// a completion cell — see `reportableAbandoned` for EXEC-009 batch items.
    let private deliveryRecords (current: AgentLinkageProjection) =
        (current.Handles |> Map.toList |> List.map snd) @ workRecords current

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

    /// Fork-child main is sealed for Blogger once joinable, abandoned, or retired.
    /// Human root (no handle) is never sealed by this rule.
    let lifecycleSealsBlogger (lifecycle: HandleLifecycle) : bool =
        match lifecycle with
        | CompletedAwaitingJoin _
        | Abandoned _
        | Retired -> true
        | Active -> false

    let recordSealsBlogger (record: HandleRecord) : bool = lifecycleSealsBlogger record.Lifecycle

    /// Every child session this parent has ever linked.
    ///
    /// Replaces the old `LinkedChildren` map. That map was keyed by child and held
    /// only live entries, so restart recovery and the retired-handle check needed
    /// two different structures; one list of records answers both.
    let linkedChildren (current: AgentLinkageProjection) =
        current.Handles |> Map.toList |> List.map (snd >> summary current)
