namespace Wanxiangshu.OpenCode.Host

open Fable.Core
open Fable.Core.JsInterop

open System.Threading.Tasks
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Persistence.Journal

/// crash-reconciliation-017/018/020: a fresh process cannot execute the child
/// work the previous process left behind, so that obligation is settled by a
/// void: the work becomes Abandoned(Voided) — reported once by join, then
/// retired. Fission groups are settled as failed.
module ChildWorkRecovery =

    [<Import("fissionRuntime", "wanxiangli/fission-group")>]
    let private fissionRuntime: obj = jsNative

    /// Reason recorded when a fresh process settles a Fission group the previous
    /// runtime left Open.
    [<Literal>]
    let private OrphanedFissionReason = "process_interrupted"

    /// A child work run the fresh process can no longer execute, and the parent
    /// handle that must carry its settlement.
    type OrphanedChildRun = ChildWorkSettlement.OrphanedChildRun

    /// The child work runs a fresh process can no longer execute, with the parent
    /// handle that has to carry their settlement.
    let orphanedChildRuns (projections: AgentProjectionSet) : OrphanedChildRun list =
        ChildWorkSettlement.orphanedChildRuns projections

    /// The exact settlement fact for one orphaned child run: a scoped (admitted)
    /// child work is voided as ChildWorkVoided; an unscoped historical child run
    /// is voided as ChildRunVoided.
    let settlementFact (orphaned: OrphanedChildRun) : ExecutionFactCases =
        ChildWorkSettlement.settlementFact orphaned

    /// Open Fission groups left behind by a previous runtime.
    let private orphanedFissionGroups (projections: AgentProjectionSet) : FissionGroupProjection list =
        projections.Fission.ActiveByOwner
        |> Map.toList
        |> List.choose (fun (_, groupId) -> Map.tryFind groupId projections.Fission.Groups)
        |> List.filter (fun group ->
            match group.Terminal with
            | FissionGroupTerminal.Open -> true
            | _ -> false)

    /// Exact FissionFailed fact closing an orphaned open Fission group at load.
    let private fissionSettlementFact (group: FissionGroupProjection) : FissionFactCases =
        FissionFactCases.FissionFailed
            {| GroupId = group.GroupId
               OwnerSessionId = group.OwnerSessionId
               Reason = OrphanedFissionReason |}

    let settleOrphanedChildRuns (journal: AgentJournal) : Task<unit> =
        task {
            let projections = (AgentJournal.snapshot journal).AgentProjections

            do! ChildWorkSettlement.settleOrphanedChildRuns journal

            for group in orphanedFissionGroups projections do
                match!
                    AgentJournal.appendAgent
                        (StreamId.Session group.OwnerSessionId)
                        None
                        (AgentFact.Fission(fissionSettlementFact group))
                        journal
                with
                | Ok _ -> fissionRuntime?clearOwner (SessionId.value group.OwnerSessionId) |> ignore
                | Error failure -> raise (JournalAppendException failure)

            return ()
        }
