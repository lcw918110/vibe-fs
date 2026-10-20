namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Repository.Knowledge.Casebook

module HostSignalBootstrap =

    type internal ChatAdmissionHookFailure =
        | IntentRejected of ChatAdmissionIntent.Rejection
        | TransactionFailed of ChatAdmissionTransactionError
        | TransactionStopped of ChatAdmissionTransactionOutcome

    type internal ChatAdmissionHookException =
        inherit Exception
        new: failure: ChatAdmissionHookFailure * executionKey: ChatExecutionKey option -> ChatAdmissionHookException
        member Failure: ChatAdmissionHookFailure
        member ExecutionKey: ChatExecutionKey option

    /// What the composition root needs back from `wire`.
    ///
    /// Exactly the members `SpikePlugin` calls. Six more used to hang here —
    /// `Reconciler`, `SignalRouter`, `Subscription`, `UnregisterOwned`,
    /// `RegisterSource`, `BindUserMessage` — with no consumer anywhere: the
    /// subscription is already tracked by the scope inside `wire`, and the three
    /// functions are called internally by the binding helpers. Handing them out as
    /// well made the signal stack look like it had six more entry points than it does.
    type WiredSignals =
        { RegisterOwned: string -> unit
          CancelSignals: SessionId seq -> unit
          BindActiveRun: SessionId -> Role -> string option -> unit
          CurrentPhysicalUserMessage: string -> string option
          ConfirmProviderStarted: ExactProviderStartObservation -> Task
          ChatMessageHook: obj
          ObserveEvent: obj -> Task<unit>
          EnsureVisibleInputAdmission: obj -> Task<unit> }



    val wire:
        observeTurnWorkflow: (AbortCause -> ReconciledTurnContext -> Task) ->
        sessionPort: ISessionHostPort ->
        eventPort: IEventObservationPort ->
        snapshotOpt: ISessionSnapshotPort option ->
        journal: AgentJournal option ->
        scope: PluginRuntimeScope ->

        rootWorkspace: IRootWorkspaceReader ->
        input: obj ->
        tryConsumeHostInternalPrompt: (SessionId -> string option -> string option -> bool) ->
        observeHostInternalTerminal: (ExactProviderTerminalObservation -> unit) ->
        workspaceDirectory: string option ->
        tryFinalizeDraft: (string -> string -> Task<CaseFinalizeSettlement>) option ->
        cleanupDraft: (string -> unit) option ->
            Task<WiredSignals>
