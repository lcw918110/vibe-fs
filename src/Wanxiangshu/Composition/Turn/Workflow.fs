namespace Wanxiangshu.Composition.Turn

open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Mission.Manager
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Persistence.Journal

/// Sole Application entry for a reconciled turn observation (rabbit §6.5).
///
/// Host hands a stable observation here; this module owns bounded-context fan-out:
/// SyncDelegate-owned → Manager → Ordinary.
module TurnWorkflow =

    /// Route one stable observation to SyncDelegate-owned / Manager / Ordinary.
    let observe
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (syncDelegate: SyncDelegateRuntime option)
        (nudgeSent: HashSet<string>)
        (joinGuardNudges: HashSet<string>)
        (hasLivePty: string -> bool)
        (abortCause: AbortCause)
        (quiescence: ISessionQuiescenceGate)
        (context: ReconciledTurnContext)
        : Task =
        task {
            let turn = context.Turn

            // The observation port is bound once; each member re-reads the journal
            // at call time, so a later SyncDelegate write still reaches the readers.
            let observation = journal |> Option.map AgentJournalPortAdapter.forTurnObservation

            // SyncDelegate path stays first and exclusive when it claims the turn
            // (Inspector/Coder/Engineer/DevOps dedicated sessions). Do not break
            // this ownership.
            let! syncDelegateHandled =
                match syncDelegate with
                | Some runtime -> runtime.HandleTurn(turn, context.Failure, context.Quiescence)
                | None -> Task.FromResult false

            if syncDelegateHandled then
                return ()

            let acceptanceFor (current: ReconciledTurnContext) =
                syncDelegate
                |> Option.bind (fun runtime ->
                    runtime.BindContinuationAcceptance
                        { SessionId = current.Turn.SessionId
                          PhysicalUserMessageId = current.Turn.PhysicalUserMessageId
                          AuthorityRootUserMessageId = current.Turn.AuthorityRootUserMessageId
                          ProviderRun = current.Turn.ProviderRun })

            let observeIdleOrdinary current =
                OrdinaryTurnWorkflow.observeIdle
                    quiescence
                    sessionPort
                    rootWorkspace
                    eventPort
                    journal
                    observation
                    current
                    (acceptanceFor current)

            let observeIdleDelivery () : Task =
                task {
                    match turn.Role, turn.Observation, turn.Outcome with
                    | Some Role.Manager, None, ReconcileProgram.TurnCompleted ->
                        do! ManagerWorkflow.observeIdle quiescence sessionPort rootWorkspace journal context
                    | _ -> do! observeIdleOrdinary context
                }

            let observeOrdinary current =
                OrdinaryTurnWorkflow.observe
                    sessionPort
                    rootWorkspace
                    eventPort
                    journal
                    observation
                    joinGuardNudges
                    hasLivePty
                    abortCause
                    quiescence
                    current
                    (acceptanceFor current)

            let observeObservation () : Task =
                task {
                    // Linked-child prompt authority is Application ownership: establish it
                    // once from durable linkage before bounded-context workflows consume the fact.
                    let! _ =
                        ChildPromptAuthority.ensureForLinkedChild
                            (journal |> Option.map PromptJournalAdapter.create)
                            turn

                    match turn.Role, turn.Observation, turn.Outcome with
                    | Some Role.Manager, _, _ ->
                        do! ManagerWorkflow.observe quiescence sessionPort rootWorkspace journal observeOrdinary context
                    | _ -> do! observeOrdinary context
                }

            match context.Delivery with
            | ReconciledTurnDelivery.IdleRevisit -> return! observeIdleDelivery ()
            | ReconciledTurnDelivery.Observation -> return! observeObservation ()
        }
        :> Task
