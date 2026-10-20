namespace Wanxiangshu.OpenCode.Host

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Fission.OpenCode
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.OpenCode
open Wanxiangshu.Ablation

/// JS-native Host boundary surface for Fission turn absorption.
///
/// This module composes host session/event ports with ordinary-turn observation
/// and publishes only a JSON-shaped observation for the logical-owner law. It
/// keeps Host capabilities private; callers cannot obtain emitted turn values.
module FissionHostSurface =

    /// intra-participant-parallelism-013: expose the exact request-local
    /// provider tool projection without exposing Host session registries.
    let projectFissionToolVisibility (hasPhysicalParent: bool) (tools: obj) : obj =
        if not (AblationGate.fissionVisible (AblationGate.registry ())) then
            tools?fission <- box false
        elif FissionRequestProjection.apply hasPhysicalParent then
            tools?fission <- box false

        tools

    type private CallFlags() =
        // DSL-MUTABLE: single-flight — one-shot continuation sent latch.
        member val ContinuationSent = false with get, set
        // DSL-MUTABLE: single-flight — one-shot terminal notified latch.
        member val TerminalNotified = false with get, set

    type private DummySessionPort(flags: CallFlags) =
        interface ISessionHostPort with
            member _.SubscribeTerminal(_, _) =
                { new IDisposable with
                    member _.Dispose() = () }

            member _.SubscribeFutureTerminal(_, _) =
                { new IDisposable with
                    member _.Dispose() = () }

            member _.SendPrompt(_, _, _) =
                flags.ContinuationSent <- true
                Task.FromResult(SendOutcome.AdmittedWithReceipt(TransportReceipt.create "receipt"))

            member _.AbortSession _ = Task.FromResult(Ok())
            member _.InterruptAttempt _ = Task.FromResult(Ok())
            member _.IsManagedChild _ = true
            member _.AbortChildren _ = AsyncSupport.completedTask ()
            member _.CreateSiblingSession(_, _, _) = Task.FromResult(Error "unused")
            member _.TryGetParentSession _ = Task.FromResult(Ok None)
            member _.CreateChildSession(_, _) = Task.FromResult(Error "unused")
            member _.ListChildren _ = Task.FromResult(Ok [])
            member _.FamilyRootOf sessionId = sessionId

    type private DummyEventPort(flags: CallFlags) =
        interface IEventObservationPort with
            member _.SubscribeTerminalListener _ =
                { new IDisposable with
                    member _.Dispose() = () }

            member _.SubscribeFutureTerminalListener _ =
                { new IDisposable with
                    member _.Dispose() = () }

            member _.NotifyTerminal _ _ =
                flags.TerminalNotified <- true
                true

    let private dummyTurn (owner: SessionId) : ReconciledTurn =
        { SessionId = owner
          PhysicalUserMessageId = PhysicalUserMessageId.create "msg-1"
          AuthorityRootUserMessageId = AuthorityRootUserMessageId.create "msg-0"
          ProviderRun = ProviderRunIdentity.create "run-1"
          Role = None
          Directory = None
          Parts = [||]
          Finish = None
          ErrorName = None
          Model = None
          Outcome = ReconcileProgram.TurnInProgress
          Observation = None }

    let private terminalBridgeMessage
        (id: string)
        (role: string)
        (parentId: string option)
        (finish: string option)
        (completed: bool)
        (parts: MessagePart array)
        : SessionMessage =
        { Id = id
          Role = role
          Agent = None
          Finish = finish
          ErrorName = None
          Model = None
          ParentId = parentId
          CreatedAt = None
          Completed = completed
          IsCompaction = false
          PromptKey = None
          Parts = parts
          PartIds = Array.create parts.Length None
          ToolParts = [||] }

    /// Executable canary for the production Fission terminal bridge. OpenCode
    /// transports may deliver the exact final assistant message while dropping
    /// the later session.status/session.idle event. The bridge records that
    /// projection edge and opens a RetryWake snapshot occasion; the snapshot,
    /// not the edge, remains the authority for TurnCompleted.
    let missingIdleTerminalBridgeScenario () : Task<obj> =
        task {
            let sessionId = SessionId.create "fission-missing-idle-lane"
            let physical = PhysicalUserMessageId.create "fission-missing-idle-user"
            let store = TurnBinding.Store()
            store.BindUserMessage(sessionId, physical)

            // DSL-MUTABLE: algorithm-scratch — executable surface observation.
            let mutable snapshotReads = 0
            let observed = TaskCompletionSource<ReconciledTurnContext>()

            let snapshot =
                { new ISessionSnapshotPort with
                    member _.GetMessages _ =
                        snapshotReads <- snapshotReads + 1

                        Task.FromResult(
                            Ok
                                [ terminalBridgeMessage
                                      (PhysicalUserMessageId.value physical)
                                      "user"
                                      None
                                      None
                                      false
                                      [||]
                                  terminalBridgeMessage
                                      "fission-missing-idle-run"
                                      "assistant"
                                      (Some(PhysicalUserMessageId.value physical))
                                      (Some "stop")
                                      true
                                      [| MessagePart.Text "lane terminal" |] ]
                        ) }

            let onTurn (context: ReconciledTurnContext) : Task =
                AsyncSupport.trySetResult observed context |> ignore
                Task.FromResult(()) :> Task

            let scheduler = Reconciler.Scheduler(snapshot, store, onTurn)

            // Production order: the exact physical terminal first advances the
            // projection edge epoch, then Fission opens one snapshot occasion.
            scheduler.NotifyProjectionChanged(sessionId, physical)
            scheduler.Kick(sessionId, ReconcileProgram.ReconcileWake.RetryWake)

            let! context = observed.Task
            do! scheduler.StopAndDrain()

            return
                box
                    {| snapshotReads = snapshotReads
                       physicalUserMessageId = PhysicalUserMessageId.value context.Turn.PhysicalUserMessageId
                       providerRun = ProviderRunIdentity.value context.Turn.ProviderRun
                       outcome =
                        match context.Turn.Outcome with
                        | ReconcileProgram.TurnCompleted -> "TurnCompleted"
                        | ReconcileProgram.TurnFailed _ -> "TurnFailed"
                        | ReconcileProgram.TurnAborted _ -> "TurnAborted"
                        | ReconcileProgram.TurnInProgress -> "TurnInProgress"
                        | ReconcileProgram.TurnNeedsContinuation _ -> "TurnNeedsContinuation" |}
        }

    /// Absorb a Fission-replaced owner turn through Host + ordinary-turn observe.
    /// Caller must have already `markSilentInterrupt`'d the owner.
    let observeReplacedOwner (ownerSessionId: string) : Task<obj> =
        task {
            let flags = CallFlags()
            let sessionPort = DummySessionPort flags :> ISessionHostPort
            let eventPort = DummyEventPort flags :> IEventObservationPort
            let owner = SessionId.create ownerSessionId
            let turn = dummyTurn owner
            let quiescence = SessionQuiescenceGate()

            let rootWorkspace =
                { new IRootWorkspaceReader with
                    member _.TryRead() = None }

            let! handled =
                FissionHost.observeLaneTurn
                    sessionPort
                    rootWorkspace
                    eventPort
                    None
                    (HashSet<string>())
                    quiescence
                    None
                    AbortCause.External
                    turn

            let context =
                { Turn = turn
                  Failure = None
                  Quiescence = None
                  Delivery = ReconciledTurnDelivery.Observation }

            do!
                OrdinaryTurnWorkflow.observe
                    sessionPort
                    rootWorkspace
                    eventPort
                    None
                    None
                    (HashSet<string>())
                    (fun _ -> false)
                    AbortCause.External
                    (SessionQuiescenceGate())
                    context
                    None

            let idleContext =
                { context with
                    Delivery = ReconciledTurnDelivery.IdleRevisit }

            do!
                OrdinaryTurnWorkflow.observeIdle
                    (SessionQuiescenceGate())
                    sessionPort
                    rootWorkspace
                    eventPort
                    None
                    None
                    idleContext
                    None

            return
                box
                    {| handled = handled
                       continuationSent = flags.ContinuationSent
                       terminalNotified = flags.TerminalNotified |}
        }
