namespace Wanxiangshu.Context.Trace

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.Persistence.Journal

[<RequireQualifiedAccess>]
type XTraceCaptureIdentity =
    | NoDurableTrace
    | PositionalIdentity
    | StableHostIdentity

[<RequireQualifiedAccess>]
type XTraceCaptureError =
    | Refused of string
    | StorageAppendFailed of JournalAppendFailure
    | StorageFailed of string

type XTraceCaptureReceipt =
    { PreviousHead: XTraceCursor
      CurrentHead: XTraceCursor
      CapturedPartCount: int
      OpeningCaptured: bool
      TerminalCaptured: bool
      Identity: XTraceCaptureIdentity }

[<RequireQualifiedAccess>]
type XTraceStableCaptureEligibility =
    | Eligible of messageIds: string list
    | NoDurableTrace
    | LegacyPositionalTrace
    | MissingHostMessageIdentity
    | BlankHostMessageIdentity
    | DuplicateHostMessageIdentity

type XTraceMessageObservation =
    { Message: CapturedWire.CapturedWireMessage
      HostMessageId: string option
      Origin: PromptAuthority.PromptOrigin option }

type XTraceMessageCapture =
    { Receipt: XTraceCaptureReceipt
      Current: XTraceProjectionState option }

module XTraceCapture =
    val semanticPart: part: MessagePart -> SemanticPart option

    val captureOpeningWithReceipt:
        journal: AgentJournal option ->
        sessionId: SessionId ->
        assignmentText: string ->
        authoritativeRequirements: string list ->
            Task<Result<XTraceCaptureReceipt, XTraceCaptureError>>

    val captureTerminalTextWithReceipt:
        journal: AgentJournal option ->
        sessionId: SessionId ->
        text: string ->
        providerRun: ProviderRunIdentity ->
            Task<Result<XTraceCaptureReceipt, XTraceCaptureError>>

    val captureTerminalWithReceipt:
        journal: AgentJournal option -> turn: ReconciledTurn -> Task<Result<XTraceCaptureReceipt, XTraceCaptureError>>

    val captureLastWordsWithReceipt:
        journal: AgentJournal option ->
        sessionId: SessionId ->
        textRef: BlobRef ->
        textDigest: BlobDigest ->
        providerRun: ProviderRunIdentity ->
            Task<Result<XTraceCaptureReceipt, XTraceCaptureError>>

    val captureProjectionWithReceipt:
        journal: AgentJournal option ->
        sessionId: SessionId ->
        projection: ProviderSemanticProjection ->
            Task<Result<XTraceCaptureReceipt, XTraceCaptureError>>

    val captureObservedMessagesWithReceipt:
        journal: AgentJournal option ->
        sessionId: SessionId ->
        observations: XTraceMessageObservation list ->
            Task<Result<XTraceMessageCapture, XTraceCaptureError>>

    val captureMessageViewWithReceipt:
        journal: AgentJournal option ->
        sessionId: SessionId ->
        messageIds: string list option ->
        messages: CapturedWire.CapturedWireMessage list ->
            Task<Result<XTraceCaptureReceipt, XTraceCaptureError>>

    val captureSessionMessagesWithReceipt:
        journal: AgentJournal option ->
        sessionId: SessionId ->
        messages: SessionMessage list ->
            Task<Result<XTraceCaptureReceipt, XTraceCaptureError>>
