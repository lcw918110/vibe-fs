namespace Wanxiangshu.Execution.Fission

open Fable.Core
open Fable.Core.JsInterop
open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Persistence.Journal.JournalOutcome
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.TerminalSignal
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Composition.Durable

module FissionHost =
    [<Import("fissionRuntime", "wanxiangli/fission-group")>]
    let private runtime: obj = jsNative
    [<Import("FissionLifecycle", "wanxiangli/fission-group")>]
    let private lifecycleClass: obj = jsNative
    let private lifecycle = createNew lifecycleClass ()
    [<Emit("$0.converge($1,$2)")>]
    let private convergeNative (_lifecycle: obj) (_owner: string) (_effects: obj) : Task<bool> = jsNative
    [<Emit("$0.observe($1,$2)")>]
    let private observeNative (_lifecycle: obj) (_observation: obj) (_effects: obj) : Task<bool> = jsNative
    let private get<'T> (value: obj) (name: string) : 'T = unbox value?(name)
    let private nativeResult result =
        match result with
        | Ok value -> createObj [ "ok" ==> true; "value" ==> value ]
        | Error error -> createObj [ "ok" ==> false; "error" ==> error ]
    let private nullable convert value = value |> Option.map (convert >> box) |> Option.defaultValue null

    let routeAttemptAborted (sessionId: SessionId) (onSilentReplacement: unit -> unit) (onOrdinaryAbort: unit -> unit) =
        if runtime?isSilentInterrupt (SessionId.value sessionId) then onSilentReplacement ()
        else onOrdinaryAbort ()

    let observePhysicalExecutionEnd
        (tryCurrentPhysical: SessionId -> PhysicalUserMessageId option)
        (durable: AgentJournal option)
        (kick: SessionId -> unit)
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        =
        let current = tryCurrentPhysical sessionId |> Option.exists ((=) physicalUserMessageId)
        let lane = durable |> Option.exists (fun journal ->
            FissionProjection.tryMembershipOfLane sessionId (AgentJournal.snapshot journal).AgentProjections.Fission
            |> Option.isSome)
        if current && lane then kick sessionId

    let private appendEvent (durable: AgentJournal) (event: obj) =
        let owner = SessionId.create (get event "owner")
        let group = get<string> event "groupId"
        let reference () = BlobRef.create (get event "reference")
        let digest () = BlobDigest.create (get event "digest")
        let session () = SessionId.create (get event "session")
        let run () = ProviderRunIdentity.create (get event "run")
        let fact, providerRun =
            match get<string> event "kind" with
            | "laneMaterialized" ->
                FissionFact.FissionLaneMaterialized
                    {| GroupId = group; OwnerSessionId = owner; LaneIndex = get event "laneIndex"
                       LaneSessionId = session (); ProviderRun = run (); ViewRef = reference (); ViewDigest = digest () |}, Some(run ())
            | "takeover" ->
                FissionFact.FissionTakeoverClaimed
                    {| GroupId = group; OwnerSessionId = owner; LaneIndex = get event "laneIndex"
                       LaneSessionId = session (); PromptKey = PromptKey.create (get event "prompt")
                       AggregateViewRef = reference (); AggregateViewDigest = digest () |}, None
            | "converged" ->
                FissionFact.FissionConverged
                    {| GroupId = group; OwnerSessionId = owner; TerminalLaneSessionId = session ()
                       TerminalProviderRun = run (); AggregateViewRef = reference (); AggregateViewDigest = digest () |}, Some(run ())
            | "failed" ->
                FissionFact.FissionFailed {| GroupId = group; OwnerSessionId = owner; Reason = get event "reason" |}, None
            | kind -> invalidOp ("Unknown Fission lifecycle event: " + kind)
        task {
            let! result = AgentJournal.appendAgent (StreamId.Session owner) providerRun fact durable
            return result |> Result.map ignore |> Result.mapError JournalAppendFailure.describe |> nativeResult
        }

    let private readVerified (durable: AgentJournal) reference digest =
        task {
            let! read = durable.Writer.BlobWriter.Read(BlobRef.create reference)
            return
                match read with
                | Ok text when HostDigest.sha256Hex text = digest -> nativeResult (Ok text)
                | Ok _ -> nativeResult (Error "Fission aggregate blob digest mismatch": Result<string, string>)
                | Error error -> nativeResult (Error error: Result<string, string>)
        }

    let private writeBlob (durable: AgentJournal) text =
        task {
            let! result = durable.WriteBlob text
            return result |> Result.map (fun blob -> createObj [ "reference" ==> BlobRef.value blob.BlobRef; "digest" ==> BlobDigest.value blob.BlobDigest ]) |> nativeResult
        }

    let private ownerAuthority durable owner =
        let projections = (AgentJournal.snapshot durable).AgentProjections
        PromptAuthorityProjectionQueries.activeProfile owner projections
        |> Option.orElseWith (fun () -> PromptAuthorityProjectionQueries.lastAuthorityProfile owner projections)

    let private agentIdOfExternal (externalId: string) =
        if externalId.StartsWith("agent:", StringComparison.Ordinal) then Some(externalId.Substring("agent:".Length))
        else None

    let private isRetired durable owner handle =
        match HandleProjection.tryFind handle (AgentJournal.handleProjection durable owner) with
        | Some { Lifecycle = HandleLifecycle.Retired } -> true
        | _ -> false

    let private isExternalRetired durable owner externalId =
        agentIdOfExternal externalId |> Option.exists (fun agent -> isRetired durable owner (HandleController.agentHandle agent))

    let private retireCompletion durable owner completion =
        task {
            match agentIdOfExternal completion with
            | None -> return true
            | Some agent ->
                let handle = HandleController.agentHandle agent
                match HandleProjection.tryFind handle (AgentJournal.handleProjection durable owner) with
                | Some { Lifecycle = HandleLifecycle.Retired } -> return true
                | Some { Lifecycle = HandleLifecycle.CompletedAwaitingJoin _ }
                | Some { Lifecycle = HandleLifecycle.Abandoned _ } ->
                    let port = AgentJournalPortAdapter.fromAgentJournal durable
                    let! consumed = HandleController.consume port owner handle
                    match consumed with
                    | Ok _ -> return true
                    | Error error ->
                        let retired = isRetired durable owner handle
                        if not retired then
                            Diagnostic.emit "fission-handle-consume-failed"
                                [ "session_id", SessionId.value owner; "result", sprintf "handle consume failed: %A" error ]
                        return retired
                | _ -> return false
        }

    let private takeoverPrompt aggregate =
        LlmFacing.renderInstructions
            [ "The Fission ring has collected every lane's delivered view and returned the complete handoff to this final present."
              "Continue as the same logical participant. Integrate the handoff below and now produce the ordinary final report to your commissioner."
              "Do not report lane mechanics, physical session ids, or the internal Fission topology."
              aggregate ]

    let private sendTakeover sessionPort durable directoryFor session authority aggregate =
        task {
            let dispatcher = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)
            let! sent = dispatcher.SendContinuation
                            (DispatchSessionPort.ofSessionPort sessionPort)
                            session (takeoverPrompt aggregate) PromptAuthority.ContinuationKind.FissionHandoff
                            authority (directoryFor session) PromptDispatcher.AwaitMode.Await None
            return sent |> Result.map PromptKey.value |> nativeResult
        }

    let private report kind (group: obj) (detail: obj) =
        let id = get<string> group "id"
        let owner = get<string> group "owner"
        let reason () = get<string> detail "error"
        let lane () = get<int> detail "lane"
        let session () = get<string> detail "session"
        let name, target, message =
            match kind with
            | "takeover-send" -> "fission-send-failed", session (), sprintf "fission takeover prompt send failed: group=%s lane=%d reason=%s" id (lane ()) (reason ())
            | "takeover-append" -> "fission-takeover-claimed-append-failed", session (), sprintf "fission takeover claim append failed: group=%s lane=%d reason=%s" id (lane ()) (reason ())
            | "aggregate-read" -> "fission-aggregate-view-read-failed", owner, sprintf "fission aggregate view read failed: group=%s reason=%s" id (reason ())
            | "aggregate-write" -> "fission-aggregate-write-failed", owner, sprintf "fission aggregate blob write failed: group=%s reason=%s" id (reason ())
            | "failure-append" -> "fission-failed-fact-append-failed", owner, sprintf "fission failed fact append failed: group=%s reason=%s" id (reason ())
            | "lane-abort" -> "fission-lane-abort-failed", session (), sprintf "fission lane abort failed: group=%s reason=%s" id (reason ())
            | "failure-unobserved" -> "fission-failed-terminal-unobserved", owner, sprintf "fission failed terminal was a duplicate or found no listener: group=%s" id
            | _ -> invalidOp ("Unknown Fission diagnostic: " + kind)
        Diagnostic.emit name [ "session_id", target; "result", message ]

    let private convergenceEffects (sessionPort: FissionSessionPort) (durable: AgentJournal) directoryFor =
        createObj
            [ "history" ==> (fun () -> FissionProjection.native (AgentJournal.snapshot durable).AgentProjections.Fission)
              "append" ==> (fun (fact: obj) -> appendEvent durable fact)
              "readBlob" ==> (fun (reference: string) (digest: string) -> readVerified durable reference digest)
              "writeBlob" ==> (fun (text: string) -> writeBlob durable text)
              "ownerAuthority" ==> (fun (owner: string) -> ownerAuthority durable (SessionId.create owner) |> nullable id)
              "retireCompletion" ==> (fun (owner: string) (completion: string) -> retireCompletion durable (SessionId.create owner) completion)
              "sendTakeover" ==> (fun (session: string) (authority: obj) (aggregate: string) -> sendTakeover sessionPort durable directoryFor (SessionId.create session) (unbox authority) aggregate)
              "report" ==> (fun (kind: string) (group: obj) (detail: obj) -> report kind group detail) ]

    let tryConverge (sessionPort: FissionSessionPort) (durable: AgentJournal) (directoryFor: SessionId -> string option) (owner: SessionId) =
        convergeNative lifecycle (SessionId.value owner) (convergenceEffects sessionPort durable directoryFor)

    let private ownerRunResult durable (group: FissionGroupProjection) (turn: ReconciledTurn) =
        match ownerAuthority durable group.OwnerSessionId with
        | None -> Error "Fission takeover completed without owner authority"
        | Some authority ->
            let result =
                { SessionId = group.OwnerSessionId
                  AuthorityRootUserMessageId = authority.AuthorityRootUserMessageId
                  ProviderRun = turn.ProviderRun
                  Role = authority.CanonicalRole
                  Directory = turn.Directory
                  TerminalText = CompletedTurnClassifier.partsSessionText turn.Parts
                  TurnFormalText = CompletedTurnClassifier.partsText turn.Parts }
            if result.IsValid then Ok(result, authority.CanonicalRole)
            else Error "Fission takeover completed with empty terminal output"

    let private publishOwnerTakeover eventPort durable (group: FissionGroupProjection) (turn: ReconciledTurn) (result: AgentRunResult) role =
        task {
            let ownerTurn = { turn with SessionId = group.OwnerSessionId; AuthorityRootUserMessageId = result.AuthorityRootUserMessageId; Role = Some role }
            let delivery =
                TaskDeliveryJournal.tryFindByFenceFields durable turn.SessionId turn.ProviderRun None
                    (Some turn.AuthorityRootUserMessageId) (Some turn.PhysicalUserMessageId) None None
                |> Option.isSome
            let! published = TerminalReporter.completeWithEvidence eventPort (Some(TerminalTracePort.forJournal durable)) ownerTurn delivery
            return
                match published with
                | XTraceTerminalCompletion.Published value when value.SessionId = result.SessionId && value.ProviderRun = result.ProviderRun -> Ok true
                | XTraceTerminalCompletion.Published _ -> Error "Fission takeover terminal reporter changed the physical owner or provider run"
                | XTraceTerminalCompletion.CaptureFailed error -> Error(sprintf "Fission takeover terminal trace capture failed: %A" error)
                | XTraceTerminalCompletion.RejectedMissingRole -> Error "Fission takeover terminal reporter rejected the owner role"
                | XTraceTerminalCompletion.RejectedEmptyOutput -> Error "Fission takeover completed with empty terminal output"
                | XTraceTerminalCompletion.RejectedUndelivered -> Error "Fission takeover completed without a delivery witness"
        }

    let private publishOwner eventPort durable rawGroup observation =
        let group = FissionProjection.fromGroup rawGroup
        let turn: ReconciledTurn = get observation "original"
        task {
            match ownerRunResult durable group turn with
            | Error error -> return nativeResult (Error error: Result<bool, string>)
            | Ok(result, role) ->
                let! published = publishOwnerTakeover eventPort durable group turn result role
                return nativeResult published
        }

    let private readDelivery durable (observation: obj) =
        let turn: ReconciledTurn = get observation "original"
        TaskDeliveryJournal.tryFindByFenceFields durable turn.SessionId turn.ProviderRun None
            (Some turn.AuthorityRootUserMessageId) (Some turn.PhysicalUserMessageId) None None
        |> Option.map (fun entry -> entry.View)
        |> Option.orElseWith (fun () ->
            Wanxiangshu.Repository.Programming.Js.JsDeliveredValues.tryPeekMaterialWith
                (SessionId.value turn.SessionId) (Some(CompletedTurnClassifier.partsSessionText turn.Parts)))
        |> nullable ReturnedMaterial.render

    let private reportNudge group lane (turn: ReconciledTurn) outstanding nudge =
        let id = get<string> group "id"
        match outstanding, nudge with
        | true, HostJoinGuard.JoinGuardNudgeOutcome.Failed reason ->
            Diagnostic.emit "fission-join-nudge-failed"
                [ "session_id", SessionId.value turn.SessionId; "result", sprintf "fission outstanding external nudge failed: %s" reason ]
        | true, HostJoinGuard.JoinGuardNudgeOutcome.NotSent ->
            Diagnostic.emit "fission-join-nudge-unsent"
                [ "session_id", SessionId.value turn.SessionId; "result", "fission outstanding external nudge unsent: not-sent" ]
        | true, HostJoinGuard.JoinGuardNudgeOutcome.AdmissionRejected failure ->
            Diagnostic.emit "fission-join-nudge-unsent"
                [ "session_id", SessionId.value turn.SessionId; "result", sprintf "fission outstanding external nudge admission rejected: %A" failure ]
        | false, HostJoinGuard.JoinGuardNudgeOutcome.NotSent ->
            Diagnostic.emit "fission-join-nudge-unsent"
                [ "session_id", SessionId.value turn.SessionId; "result", sprintf "fission undelivered lane nudge unsent: group=%s lane=%d reason=not-sent" id lane ]
        | false, HostJoinGuard.JoinGuardNudgeOutcome.AdmissionRejected failure ->
            Diagnostic.emit "fission-join-nudge-unsent"
                [ "session_id", SessionId.value turn.SessionId; "result", sprintf "fission undelivered lane nudge admission rejected: group=%s lane=%d reason=%A" id lane failure ]
        | _ -> ()

    let private nudge sessionPort rootWorkspace journal nudges (quiescence: ISessionQuiescenceGate) permit group lane observation outstanding =
        let turn: ReconciledTurn = get observation "original"
        task {
            let! result =
                HostJoinGuard.nudge sessionPort rootWorkspace (journal |> Option.map AgentJournalPortAdapter.forHostJoinGuard)
                    journal nudges
                    (fun () -> match permit with Some value -> quiescence.TryConsume value | None -> Error QuiescencePermitFailure.NoFreshIdle)
                    (fun () -> match permit with Some value -> quiescence.TryRelease value | None -> Error QuiescencePermitFailure.NoFreshIdle)
                    turn.SessionId turn.ProviderRun turn.Directory
            reportNudge group lane turn outstanding result
            return
                match result with
                | HostJoinGuard.JoinGuardNudgeOutcome.Failed reason -> createObj [ "kind" ==> "failed"; "error" ==> reason ]
                | _ -> createObj [ "kind" ==> "kept" ]
        }

    let private observation (turn: ReconciledTurn) canNudge =
        let outcome, reason =
            match turn.Outcome with
            | ReconcileProgram.TurnInProgress -> "OngoingExecution", ""
            | ReconcileProgram.TurnNeedsContinuation _ -> "NeedsContinuation", ""
            | ReconcileProgram.TurnFailed _ -> "ProviderFailed", ""
            | ReconcileProgram.TurnAborted reason -> "ExternalAbort", reason
            | ReconcileProgram.TurnCompleted -> "Completed", ""
        createObj [ "session" ==> SessionId.value turn.SessionId; "run" ==> ProviderRunIdentity.value turn.ProviderRun
                    "outcome" ==> outcome; "reason" ==> reason; "canNudge" ==> canNudge; "original" ==> turn ]

    let observeLaneTurn
        (sessionPort: FissionSessionPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: FissionEventPort)
        (journal: AgentJournal option)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit option)
        (turn: ReconciledTurn)
        : Task<bool> =
        match journal with
        | None -> task { return runtime?isSilentInterrupt (SessionId.value turn.SessionId) }
        | Some durable ->
            let directoryFor session = if session = turn.SessionId then turn.Directory else None
            let effects = convergenceEffects sessionPort durable directoryFor
            effects?abortSession <- (fun (session: string) -> task { let! result = sessionPort.AbortSession(SessionId.create session)
                                                                   return nativeResult result })
            effects?notifyFailed <- (fun (owner: string) (reason: string) -> eventPort.NotifyTerminal (SessionId.create owner) (TerminalOutcome.Failed(TerminalStop.session reason)))
            effects?publishOwner <- (fun (group: obj) (observation: obj) -> publishOwner eventPort durable group observation)
            effects?readDelivery <- (fun (observation: obj) -> readDelivery durable observation)
            effects?isExternalRetired <- (fun (owner: string) (external: string) -> isExternalRetired durable (SessionId.create owner) external)
            effects?nudge <- (fun (group: obj) (lane: int) (observation: obj) (outstanding: bool) -> nudge sessionPort rootWorkspace journal joinGuardNudges quiescence permit group lane observation outstanding)
            observeNative lifecycle (observation turn permit.IsSome) effects
