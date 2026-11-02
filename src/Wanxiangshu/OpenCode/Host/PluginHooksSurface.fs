namespace Wanxiangshu.OpenCode

open System
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
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Host
open Wanxiangshu.Host.Ports
open Wanxiangshu.Persistence.Journal.JournalOutcome

module PluginHooksSurface =

    let private snapshotMessage (value: obj) : SessionMessage =
        { Id = unbox value?id
          Role = "assistant"
          Agent = None
          Finish = None
          ErrorName = None
          Model = None
          ParentId = None
          CreatedAt = None
          Completed = unbox value?completed
          IsCompaction = unbox value?compaction
          PromptKey = None
          Parts = [||]
          PartIds = [||]
          ToolParts = [||] }

    let private productCompaction (product: obj) : ICompactionGate =
        { new ICompactionGate with
            member _.OnCompacting session =
                task {
                    let! (lines: string array) = product?OnCompacting (SessionId.value session)
                    return CompactionContribution.Context(Array.toList lines)
                }

            member _.AutoContinue session =
                task {
                    let! (allowed: bool) = product?AutoContinue (SessionId.value session)

                    return
                        if allowed then
                            ContinueVerdict.Allow
                        else
                            ContinueVerdict.Deny "fixture"
                } }

    let createCompaction (handle: JournalHandle option) : obj =
        let owner = OpenCodeCompaction(handle |> Option.map _.Journal)

        createObj
            [ "IsCompacting" ==> Func<string, bool>(owner.IsCompacting)
              "TemporaryOverflowed" ==> Func<string, bool>(owner.TemporaryOverflowed)
              "FinishTemporary" ==> Action<string>(owner.FinishTemporary)
              "ForgetSession" ==> Action<string>(owner.ForgetSession)
              "TryConsumeHistoryBypass"
              ==> Func<obj, obj, bool>(fun input output -> owner.TryConsumeHistoryBypass(input, output))
              "RecordSettingGap"
              ==> Action<obj>(fun gap ->
                  owner.RecordSettingGap(
                      Some
                          { Path = unbox<string array> gap?Path |> Array.toList
                            Required = unbox gap?Required
                            Clause = unbox gap?Clause
                            Reason = unbox gap?Reason }
                  ))
              "OnCompacting"
              ==> Func<obj, (string -> bool), obj, obj, obj, Task<unit>>(fun product temporary client input output ->
                  owner.OnCompacting(productCompaction product, temporary, client, input, output))
              "OnAutoContinue"
              ==> Func<obj, (string -> bool), obj, obj, Task<unit>>(fun product temporary input output ->
                  owner.OnAutoContinue(productCompaction product, temporary, input, output))
              "ObserveSnapshot"
              ==> Func<string, obj array, Task>(fun session messages ->
                  owner.ObserveSnapshot(
                      SessionId.create session,
                      messages |> Array.map snapshotMessage |> Array.toList
                  )) ]

    let refuseCompactionAppendOnce (handle: JournalHandle) : obj =
        let writer = box handle.Journal.Writer
        let original: obj = writer?Append
        let mutable attempts = 0

        let append (stream: obj) (run: obj) (fact: obj) : Task<obj> =
            attempts <- attempts + 1

            if attempts = 1 then
                Task.FromResult(
                    box (
                        CommitResult<obj>
                            .NotAttempted(
                                EventId.create "not-attempted",
                                JournalUnavailable.WriterRefused "fixture refusal"
                            )
                    )
                )
            else
                emitJsExpr (original, writer, [| stream; run; fact |]) "$0.apply($1,$2)"

        writer?Append <- Func<obj, obj, obj, Task<obj>>(append)

        createObj
            [ "attempts" ==> Func<int>(fun () -> attempts)
              "restore" ==> Action(fun () -> writer?Append <- original) ]

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
