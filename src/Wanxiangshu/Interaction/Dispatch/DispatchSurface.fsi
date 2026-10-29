namespace Wanxiangshu.Interaction.Dispatch

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.OpenCode
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Persistence.Journal

/// Dispatch-owned JavaScript boundary. Host ports stay opaque and durable
/// JournalHandle capabilities never cross as Fable records; only transport
/// constructors, send observations, and claim counts are plain values.
[<RequireQualifiedAccess>]
module DispatchSurface =
    val internal sessionPort: port: obj -> ISessionHostPort
    val internal rootWorkspaceReader: directory: obj -> IRootWorkspaceReader

    /// JS-safe controlled Host child listing for adapter proofs. The F# Result
    /// and ChildSessionInfo representations stay on this registered surface.
    val acceptedChild: session: string -> title: string -> agent: string -> Result<ChildSessionInfo list, string>

    val admittedWithReceipt: value: string -> Outcome.SendOutcome

    val admittedWithPhysicalMessage: value: string -> Outcome.SendOutcome

    val retryable: reason: string -> Outcome.SendOutcome

    val acceptanceUnknown: reason: string -> Outcome.SendOutcome

    val fatal: reason: string -> Outcome.SendOutcome


    /// Seed the durable AgentOwnerRoot needed by a continuation owner. This is
    /// the same PromptFact writer used by production ingress; the returned value
    /// contains no AgentFact/union representation.
    val appendAuthorityRoot:
        handle: JournalHandle -> session: string -> identitySeed: obj -> agentOwnerLife: string -> Task<obj>

    val sendAgentOwnerRoot:
        port: obj -> handle: JournalHandle -> session: string -> text: string -> identitySeed: obj -> Task<obj>

    val sendAgentOwnerRootAwait:
        port: obj -> handle: JournalHandle -> session: string -> text: string -> identitySeed: obj -> Task<obj>

    val sendAgentOwnerRootWithAcceptance:
        port: obj ->
        handle: JournalHandle ->
        session: string ->
        text: string ->
        identitySeed: obj ->
        awaitMode: string ->
        onAccepted: (string -> unit) ->
        attachRegistration: (obj -> unit) ->
            Task<obj>

    val deliverDetachedVerdict: listener: obj -> kind: string -> reason: string -> Task<obj>

    val sendAgentOwnerRootWithAcceptanceRegistration:
        port: obj ->
        handle: JournalHandle ->
        session: string ->
        text: string ->
        identitySeed: obj ->
        awaitMode: string ->
        onAccepted: (string -> unit) ->
        attachRegistration: (obj -> unit) ->
            Task<obj>

    val awaitPhysicalConfirmation: promptKey: string -> timeoutMs: int -> Task<obj>

    /// Preserve one opaque typed callback across independent registrations.
    val preparePhysicalAcceptanceObserver: onAccepted: (string -> unit) -> obj

    /// Return the production owner's opaque registration resource unchanged.
    val registerPhysicalAcceptanceObserver: promptKey: string -> observer: obj -> obj

    val disposePhysicalAcceptanceObserver: registration: obj -> unit

    val sendManagedAssignment:
        port: obj ->
        handle: JournalHandle ->
        session: string ->
        text: string ->
        identitySeed: obj ->
        tools: (string * bool) array option ->
            Task<obj>

    val sendContinuation:
        port: obj ->
        handle: JournalHandle ->
        session: string ->
        text: string ->
        continuation: string ->
        profile: obj ->
        awaitMode: string ->
            Task<obj>

    /// Hold the production dispatcher after its real durable claim append.
    /// The callback changes scheduling only; all journal operations use the
    /// production adapter and all admission decisions stay in the dispatcher.
    val sendContinuationAfterClaim:
        port: obj ->
        handle: JournalHandle ->
        session: string ->
        text: string ->
        continuation: string ->
        profile: obj ->
        awaitMode: string ->
        afterClaim: (string -> Task) ->
            Task<obj>

    val sendContinuationDetachedObserved:
        port: obj ->
        handle: JournalHandle ->
        session: string ->
        prompt: string ->
        continuation: string ->
        onAccepted: (string -> unit) ->
        attachRegistration: (obj -> unit) ->
        onRefused: (string -> unit) ->
            Task<obj>

    val sendContinuationWithAcceptance:
        port: obj ->
        handle: JournalHandle ->
        session: string ->
        text: string ->
        continuation: string ->
        profile: obj ->
        awaitMode: string ->
        onAccepted: (string -> unit) ->
        attachRegistration: (obj -> unit) ->
            Task<obj>

    val sendContinuationWithAcceptanceRegistration:
        port: obj ->
        handle: JournalHandle ->
        session: string ->
        text: string ->
        continuation: string ->
        profile: obj ->
        awaitMode: string ->
        onAccepted: (string -> unit) ->
        attachRegistration: (obj -> unit) ->
            Task<obj>

    val sendGateNudgesConcurrently:
        port: obj ->
        handle: JournalHandle ->
        session: string ->
        text: string ->
        continuation: string ->
        gateKind: string ->
        terminalProviderRun: string ->
        profile: obj ->
            Task<obj>

    /// HOST-004 / dispatch-protocol-002: exercise the dispatch-owned final
    /// physical-send admission without exposing Quiescence internals to this
    /// package's JS tests. Crash-reconciliation proves when the admission turns
    /// stale; this surface proves that stale evidence closes the durable claim
    /// and never reaches the Host SendPrompt boundary.
    val sendIdleContinuation:
        port: obj ->
        handle: JournalHandle ->
        session: string ->
        text: string ->
        continuation: string ->
        profile: obj ->
        physicalAdmission: obj ->
            Task<obj>

    /// PROMPT-004/005: prove one dispatched AgentOwnerRoot at a physical message
    /// boundary. The Dispatcher writes PhysicalAccepted before registering the
    /// authority profile; only the normalized profile crosses this boundary.
    val acceptAgentOwnerRoot:
        handle: JournalHandle ->
        session: string ->
        promptKey: string ->
        physicalMessageId: string ->
        agentOwnerLife: obj ->
            Task<obj>

    /// Registered proof boundary for external ingress: the caller supplies the
    /// exact RootSelection seed, including the deliberate absence of a seed.
    val acceptHumanRootSelection:
        handle: JournalHandle -> session: string -> physicalMessageId: string -> identitySeed: obj -> Task<obj>

    val acceptManagedExternal:
        handle: JournalHandle -> session: string -> physicalMessageId: string -> agent: string -> Task<obj>

    val acceptManagedPromptClaim:
        handle: JournalHandle ->
        session: string ->
        physicalMessageId: string ->
        promptKey: string ->
        agent: string ->
            Task<obj>

    /// Freeze the real ingress decision before asynchronous Host preparation,
    /// then accept that same decision through the production dispatcher.
    val prepareManagedPromptAcceptance:
        handle: JournalHandle ->
        session: string ->
        physicalMessageId: string ->
        promptKey: string ->
        agent: string ->
            (unit -> Task<obj>)

    /// Hold the frozen decision after real physical acceptance is durable,
    /// before the dispatcher may establish managed execution acceptance.
    val prepareManagedPromptAcceptanceAfterPhysical:
        handle: JournalHandle ->
        session: string ->
        physicalMessageId: string ->
        promptKey: string ->
        agent: string ->
        afterPhysical: (string -> Task) ->
            (unit -> Task<obj>)

    /// PROMPT-004: accept the external HumanRoot through the same Dispatcher
    /// writer used by chat.message. The physical id is supplied by the caller as
    /// host-boundary evidence; this surface never invents an alias.
    val acceptHumanRoot:
        handle: JournalHandle -> session: string -> physicalMessageId: string -> agent: string -> Task<obj>

    val projectionObservation: handle: JournalHandle -> session: string -> obj
    val userQuestionObservation: handle: JournalHandle -> session: string -> obj array

    val foldRuntimeStartWatermark: events: obj array -> obj

    val pendingClaimCount: handle: JournalHandle -> session: string -> int
