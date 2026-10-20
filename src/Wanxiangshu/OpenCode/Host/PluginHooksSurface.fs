namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Persistence.EventStore

module PluginHooksSurface =

    let policyAwareHook operation (adaptedHook: obj) : obj =
        PluginHostInterop.policyAwareHook operation adaptedHook

    /// Classify a thrown JS value through the real failure membrane and expose
    /// the normalized outcome fields (failure kind, lifecycle, settlement
    /// evidence, whether hook args proved an owned execution).
    let normalizeHookFailureOutcome (args: obj) (context: obj) (error: obj) : obj =
        let outcome = PluginHostInterop.normalizeHookFailure args context error

        box
            {| failure = sprintf "%A" outcome.Failure
               lifecycle = sprintf "%A" outcome.Lifecycle
               settlement = sprintf "%A" outcome.Settlement
               hasExecutionKey = outcome.ExecutionKey.IsSome |}

    let decorateReviewToolDefinition (toolID: string) (definition: obj) : unit =
        ManagerReviewContract.decorateDefinition (box {| toolID = toolID |}) definition

    let hookFailurePolicy failure settlement : string =
        let typedFailure =
            match failure with
            | "LocalInvariant" -> ExecutionFailure.LocalInvariant
            | "ProtocolRejection" -> ExecutionFailure.ProtocolRejection
            | "UserCancelled" -> ExecutionFailure.UserCancelled
            | "Superseded" -> ExecutionFailure.Superseded
            | "CapacityQueueFull" -> ExecutionFailure.CapacityQueueFull
            | "AcceptanceUnknown" -> ExecutionFailure.AcceptanceUnknown
            | "PersistenceNotCommitted" -> ExecutionFailure.PersistenceFailure PersistenceCommitment.NotCommitted
            | "PersistenceCommitted" -> ExecutionFailure.PersistenceFailure PersistenceCommitment.Committed
            | "PersistenceUnknown" -> ExecutionFailure.PersistenceFailure PersistenceCommitment.Unknown
            | other -> invalidArg "failure" $"unknown hook proof failure '{other}'"

        let settlementEvidence =
            match settlement with
            | "NoOwnedExecution" -> PluginHostInterop.HookSettlementEvidence.NoOwnedExecution
            | "ExactSettlementComplete" -> PluginHostInterop.HookSettlementEvidence.ExactSettlementComplete
            | "DurableOutcomeUnknown" -> PluginHostInterop.HookSettlementEvidence.DurableOutcomeUnknown
            | "SettlementIncomplete" -> PluginHostInterop.HookSettlementEvidence.SettlementIncomplete
            | other -> invalidArg "settlement" $"unknown hook proof settlement '{other}'"

        let lifecycle =
            match settlementEvidence with
            | PluginHostInterop.HookSettlementEvidence.NoOwnedExecution -> DurableExecutionLifecycle.NoAcceptedFact
            | PluginHostInterop.HookSettlementEvidence.ExactSettlementComplete -> DurableExecutionLifecycle.Terminal
            | PluginHostInterop.HookSettlementEvidence.DurableOutcomeUnknown
            | PluginHostInterop.HookSettlementEvidence.SettlementIncomplete ->
                DurableExecutionLifecycle.AcceptedBeforeProvider

        let outcome: PluginHostInterop.HookFailureOutcome =
            { Failure = typedFailure
              Lifecycle = lifecycle
              ExecutionKey = None
              Settlement = settlementEvidence }

        match PluginHostInterop.interpretHookFailure outcome with
        | PluginHostInterop.HookFailurePolicy.RethrowUnchanged -> "RethrowUnchanged"
        | PluginHostInterop.HookFailurePolicy.FatalAfterSettlement -> "FatalAfterSettlement"
        | PluginHostInterop.HookFailurePolicy.RejectFatalBeforeSettlement -> "RejectFatalBeforeSettlement"
