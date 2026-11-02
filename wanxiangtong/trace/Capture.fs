namespace Wanxiangshu.Context.Trace

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Session
open Wanxiangshu.Participant.Provider.Attempt.Fallback

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

/// COMPANION-003 / HOST-005 / COMPANION-012: the single semantic capture path.
///
/// Every prompt/assistant/reasoning/tool part enters the XTrace through exactly
/// one mapper, so no two independent parser/renderer pairs can disagree about a
/// segment. There is no separate terminal session: the terminal output is
/// captured as the XTrace's last segment (TerminalOutputCaptured), not as a
/// parallel session-wide text.
module XTraceCapture =

    /// The one `MessagePart → SemanticPart` mapper. `Activity` parts are
    /// transport bookkeeping and carry no semantics, so they are dropped here.
    let semanticPart (part: MessagePart) : SemanticPart option =
        match part with
        | MessagePart.Text text -> Some(SemanticText text)
        | MessagePart.Reasoning text -> Some(SemanticReasoning text)
        | MessagePart.ToolCall(_callId, name, args) -> Some(SemanticToolCall(name, args))
        | MessagePart.ToolResult(_callId, result) -> Some(SemanticToolResult result)
        | MessagePart.Activity _ -> None

    /// The `SemanticPart → (kind, toolName, body)` split that the durable fact
    /// needs: body goes to a blob, kind and tool name ride on the journal line.
    let private partShape (part: SemanticPart) : string * string option * string =
        match part with
        | SemanticText text -> "text", None, text
        | SemanticReasoning text -> "reasoning", None, text
        | SemanticToolCall(name, args) -> "tool_call", Some name, args
        | SemanticToolResult result -> "tool_result", None, result
        | SemanticMedia(mediaType, _digest) -> "media_omitted", None, (mediaType |> Option.defaultValue "")

    type private TraceSourcePart =
        { Part: SemanticPart
          HostPartId: HostMessagePartId option
          ToolCallId: ToolCallId option
          HostToolPartId: HostToolPartId option }

    type private TraceSourceMessage =
        { Role: string
          ProviderRun: ProviderRunIdentity option
          Parts: TraceSourcePart list }

    let private xTraceOf (journal: AgentJournal) (sessionId: SessionId) =
        AgentJournal.snapshot journal
        |> fun projection -> AgentProjection.tryFind sessionId projection.AgentProjections
        |> Option.bind (fun session -> session.XTrace)
        |> Option.defaultValue XTraceProjection.empty

    let private appendFact
        (journal: AgentJournal)
        (sessionId: SessionId)
        (run: ProviderRunIdentity option)
        (fact: AgentFact)
        : Task<unit> =
        task {
            match! AgentJournal.appendAgent (StreamId.Session sessionId) run fact journal with
            | Ok _ -> return ()
            | Error failure -> return raise (JournalAppendException failure)
        }

    [<AllowNullLiteral>]
    type private CaptureStore =
        abstract read: unit -> obj
        abstract writeBody: string -> Task<obj>
        abstract append: obj -> Task<unit>

    [<AllowNullLiteral>]
    type private CaptureOwner =
        abstract run<'T> : string * Func<Task<'T>> -> Task<'T>
        abstract receipt: WanxiangliTrace.History * WanxiangliTrace.History -> obj
        abstract captureOpening: CaptureStore * string * string array -> Task<unit>
        abstract captureTerminalText: CaptureStore * string * string * string -> Task<obj>
        abstract captureTerminalReference: CaptureStore * string * string * string -> Task<unit>
        abstract captureParts: CaptureStore * obj array * string -> Task<obj>
        abstract captureReference: CaptureStore * obj * string * string -> Task<unit>

    [<Import("TraceCapture", "wanxiangli/trace")>]
    let private captureClass: obj = jsNative

    let private captures = createNew captureClass () |> unbox<CaptureOwner>

    type private CaptureMetadata =
        { Provenance: string
          Turn: int
          PartIndex: int
          ProviderRun: ProviderRunIdentity option
          ToolCallId: ToolCallId option
          HostToolPartId: HostToolPartId option }

    [<Emit("JSON.stringify($0)")>]
    let private identityKey (_parts: obj array) : string = jsNative

    let private optionalKey value unwrap =
        value |> Option.map (unwrap >> box) |> Option.defaultValue null

    let private secondaryKey hostPart callId run =
        hostPart
        |> Option.map (fun host ->
            identityKey
                [| box (HostToolPartId.value host)
                   optionalKey callId ToolCallId.value
                   optionalKey run ProviderRunIdentity.value |])
        |> Option.map box
        |> Option.defaultValue null

    let private compatibilityKey run kind toolName callId hostPart digest =
        identityKey
            [| optionalKey run ProviderRunIdentity.value
               box kind
               optionalKey toolName id
               optionalKey callId ToolCallId.value
               optionalKey hostPart HostToolPartId.value
               box digest |]

    let private capturedIdentity (part: XTracePartRef) =
        createObj
            [ "key" ==> part.Provenance
              "secondaryKey"
              ==> secondaryKey part.HostToolPartId part.ToolCallId part.ProviderRun
              "compatibility"
              ==> compatibilityKey
                      part.ProviderRun
                      part.Kind
                      part.ToolName
                      part.ToolCallId
                      part.HostToolPartId
                      (BlobDigest.value part.TextDigest) ]

    let private captureStore (durable: AgentJournal) (sessionId: SessionId) =
        { new CaptureStore with
            member _.read() =
                let state = xTraceOf durable sessionId

                createObj
                    [ "history" ==> XTraceProjection.history state
                      "identities"
                      ==> (XTraceProjection.parts state |> List.map capturedIdentity |> List.toArray) ]

            member _.writeBody body =
                task {
                    let! result = durable.WriteBlob body

                    return
                        match result with
                        | Ok blob ->
                            createObj
                                [ "ok" ==> true
                                  "value"
                                  ==> createObj
                                          [ "reference" ==> BlobRef.value blob.BlobRef
                                            "digest" ==> BlobDigest.value blob.BlobDigest ] ]
                        | Error error -> createObj [ "ok" ==> false; "error" ==> error ]
                }

            member _.append event =
                match unbox<string> event?kind with
                | "opening" ->
                    CompanionFact.OpeningPromptCaptured
                        {| SessionId = sessionId
                           AssignmentText = unbox<string> event?assignment
                           AuthoritativeRequirements = unbox<string array> event?requirements |> Array.toList
                           ProviderRun = None |}
                    |> appendFact durable sessionId None
                | "terminal" ->
                    let run = ProviderRunIdentity.create (unbox<string> event?providerRun)

                    CompanionFact.TerminalOutputCaptured
                        {| SessionId = sessionId
                           TextRef = BlobRef.create (unbox<string> event?bodyRef)
                           TextDigest = BlobDigest.create (unbox<string> event?bodyDigest)
                           ProviderRun = run |}
                    |> appendFact durable sessionId (Some run)
                | "part" ->
                    let part: obj = event?part
                    let metadata = unbox<CaptureMetadata> part?metadata
                    let toolName: obj = part?toolName

                    CompanionFact.XTracePartAppended
                        {| SessionId = sessionId
                           CursorSequence = unbox<int64> event?sequence
                           Role = unbox<string> part?role
                           Turn = metadata.Turn
                           PartIndex = metadata.PartIndex
                           Kind = unbox<string> part?kind
                           ToolName =
                            if isNull toolName then
                                None
                            else
                                Some(unbox<string> toolName)
                           TextRef = BlobRef.create (unbox<string> event?bodyRef)
                           TextDigest = BlobDigest.create (unbox<string> event?bodyDigest)
                           Provenance = metadata.Provenance
                           ProviderRun = metadata.ProviderRun
                           ToolCallId = metadata.ToolCallId
                           HostToolPartId = metadata.HostToolPartId |}
                    |> appendFact durable sessionId metadata.ProviderRun
                | kind -> invalidOp ("wanxiangli/trace returned an unknown capture event: " + kind) }

    let private captureResult (pending: Task<obj>) : Task<Result<unit, string>> =
        task {
            let! result = pending

            return
                if unbox<bool> result?ok then
                    Ok()
                else
                    Error(unbox<string> result?error)
        }

    let private requireBlobWritten (context: string) (result: Result<'a, string>) : 'a =
        match result with
        | Ok blob -> blob
        | Error error -> raise (InvalidOperationException(sprintf "%s: %s" context error))

    /// Evidence → Decision: opening absent and assignment non-blank → capture.
    let private captureOpeningWhenAbsent
        (durable: AgentJournal)
        (sessionId: SessionId)
        (assignmentText: string)
        (authoritativeRequirements: string list)
        : Task<unit> =
        captures.captureOpening (captureStore durable sessionId, assignmentText, List.toArray authoritativeRequirements)

    /// COMPANION-003: capture the opening task verbatim. Idempotent — a session
    /// with an opening already captured is left alone (PERSIST-010), which makes
    /// a replayed chat.message harmless.
    let captureOpening
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (assignmentText: string)
        (authoritativeRequirements: string list)
        =
        task {
            match journal with
            | None -> return ()
            | Some durable -> do! captureOpeningWhenAbsent durable sessionId assignmentText authoritativeRequirements
        }

    /// Evidence → Decision: non-blank, non-replay terminal text → durable capture.
    let private captureTerminalTextWhenFresh
        (durable: AgentJournal)
        (sessionId: SessionId)
        (text: string)
        (providerRun: ProviderRunIdentity)
        : Task<unit> =
        task {
            let digest =
                if String.IsNullOrWhiteSpace text then
                    ""
                else
                    HostDigest.sha256Hex text

            let! result =
                captures.captureTerminalText (
                    captureStore durable sessionId,
                    ProviderRunIdentity.value providerRun,
                    text,
                    digest
                )
                |> captureResult

            return requireBlobWritten "XTrace terminal blob write failed" result
        }

    /// COMPANION-003 / EXEC-009: capture terminal text into XTrace.
    /// Same durability as `captureTerminal`; used when the caller already holds
    /// `AgentRunResult.TerminalText` (e.g. oneshot Completed before journal write).
    /// Idempotent replay (same text) is a no-op.
    let captureTerminalText
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (text: string)
        (providerRun: ProviderRunIdentity)
        =
        task {
            match journal with
            | None -> return ()
            | Some durable -> do! captureTerminalTextWhenFresh durable sessionId text providerRun
        }

    let private captureTerminalBlobWhenFresh
        (durable: AgentJournal)
        (sessionId: SessionId)
        (textRef: BlobRef)
        (textDigest: BlobDigest)
        (providerRun: ProviderRunIdentity)
        : Task<unit> =
        captures.captureTerminalReference (
            captureStore durable sessionId,
            ProviderRunIdentity.value providerRun,
            BlobRef.value textRef,
            BlobDigest.value textDigest
        )

    /// COMPANION-003 / EXEC-009: capture the terminal output verbatim as the
    /// XTrace's last segment. Idempotent replay (same text) is a no-op;
    /// a different text overwrites — subagent reuse produces a new terminal
    /// per work unit on the same child session.
    let captureTerminal (journal: AgentJournal option) (turn: ReconciledTurn) =
        // COMPANION-003: TerminalOutputRaw = formal text + host-visible
        // reasoning only. partsSessionText drops tool call/result so
        // the captured terminal segment stays free of raw tools and matches
        // AgentRunResult.TerminalText.
        let text = CompletedTurnClassifier.partsSessionText turn.Parts
        captureTerminalText journal turn.SessionId text turn.ProviderRun

    /// HOST-006: Host turn indices restart after reanchor; XTrace does not.
    /// Provenance must carry the reanchor generation or post-compaction turns
    /// collide with pre-compaction `turn:0/part:0` and never append — RecordCoverage
    /// then maps a Host cursor onto a dead numbering and stages Next≤Prev.
    let private captureGeneration (journal: AgentJournal) (sessionId: SessionId) : int =
        AgentJournal.snapshot journal
        |> fun projection -> AgentProjection.tryFind sessionId projection.AgentProjections
        |> Option.bind (fun session -> session.PrefixEpoch)
        |> Option.map (PrefixEpochProjection.reanchoredRuns >> Array.length)
        |> Option.defaultValue 0

    /// COMPANION-003 / COMPANION-007: synchronise the XTrace with the provider's
    /// semantic projection at the transform boundary.
    ///
    /// Idempotent by `(reanchorGen, turn, part)` provenance: the same Host view is
    /// re-observed every transform without duplicating the trace; a reanchor opens
    /// a new generation so renumbered Host turns append instead of colliding.
    ///
    /// Returns the updated XTrace state (or `None` without a journal), so the
    /// caller can refresh the in-memory Companion mirror — otherwise the chunker
    /// keeps mapping against the stale trace captured at construction and re-reads
    /// the projection head every round.
    let private semanticPartFromWire (part: WirePart) : SemanticPart =
        match part with
        | WireText text -> SemanticText text
        | WireReasoning text -> SemanticReasoning text
        | WireToolCall(_, name, args) -> SemanticToolCall(name, args)
        | WireToolResult(_, result) -> SemanticToolResult result
        | WireMedia(mediaType, digest) -> SemanticMedia(mediaType, digest)

    type private PartCaptureWork =
        { Message: TraceSourceMessage
          TurnIndex: int
          PartIndex: int
          Source: TraceSourcePart
          Provenance: string
          LegacyStableProvenance: string option }

    let private enumeratePositionalCaptureWork
        (generation: int)
        (messages: TraceSourceMessage list)
        : PartCaptureWork list =
        messages
        |> List.mapi (fun turnIndex message ->
            message.Parts
            |> List.mapi (fun partIndex source ->
                { Message = message
                  TurnIndex = turnIndex
                  PartIndex = partIndex
                  Source = source
                  Provenance = sprintf "g:%d/turn:%d/part:%d" generation turnIndex partIndex
                  LegacyStableProvenance = None }))
        |> List.concat

    let private enumerateStableCaptureWork
        (generation: int)
        (messageIds: string list)
        (messages: TraceSourceMessage list)
        : PartCaptureWork list =
        List.zip messageIds messages
        |> List.mapi (fun turnIndex (messageId, message) ->
            message.Parts
            |> List.mapi (fun partIndex source ->
                let provenance =
                    match source.HostPartId with
                    | Some partId ->
                        sprintf "g:%d/msg:%s/host-part:%s" generation messageId (HostMessagePartId.value partId)
                    | None -> sprintf "g:%d/msg:%s/part:%d" generation messageId partIndex

                { Message = message
                  TurnIndex = turnIndex
                  PartIndex = partIndex
                  Source = source
                  Provenance = provenance
                  LegacyStableProvenance = Some(sprintf "g:%d/msg:%s/part:%d" generation messageId partIndex) }))
        |> List.concat

    let private capturePart (work: PartCaptureWork) =
        let kind, toolName, body = partShape work.Source.Part

        let metadata: CaptureMetadata =
            { Provenance = work.Provenance
              Turn = work.TurnIndex
              PartIndex = work.PartIndex
              ProviderRun = work.Message.ProviderRun
              ToolCallId = work.Source.ToolCallId
              HostToolPartId = work.Source.HostToolPartId }

        createObj
            [ "key" ==> work.Provenance
              "exactKey" ==> Option.isSome work.Source.HostPartId
              "secondaryKey"
              ==> secondaryKey work.Source.HostToolPartId work.Source.ToolCallId work.Message.ProviderRun
              "legacyKey" ==> (work.LegacyStableProvenance |> Option.toObj)
              "compatibility"
              ==> compatibilityKey
                      work.Message.ProviderRun
                      kind
                      toolName
                      work.Source.ToolCallId
                      work.Source.HostToolPartId
                      (HostDigest.sha256Hex body)
              "role" ==> work.Message.Role
              "kind" ==> kind
              "toolName" ==> Option.toObj toolName
              "body" ==> body
              "metadata" ==> metadata ]

    let private captureSourcesWithJournal
        (durable: AgentJournal)
        (sessionId: SessionId)
        (messages: TraceSourceMessage list)
        : Task<XTraceProjectionState option> =
        task {
            let generation = captureGeneration durable sessionId

            let works =
                enumeratePositionalCaptureWork generation messages
                |> List.map capturePart
                |> List.toArray

            let! result =
                captures.captureParts (captureStore durable sessionId, works, "positional")
                |> captureResult

            requireBlobWritten "XTrace part blob write failed" result
            return Some(xTraceOf durable sessionId)
        }

    let private captureSources
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (messages: TraceSourceMessage list)
        : Task<XTraceProjectionState option> =
        task {
            match journal with
            | None -> return None
            | Some durable -> return! captureSourcesWithJournal durable sessionId messages
        }

    /// STRENGTH-008: whether this XTrace can accept a historical insertion
    /// without positional provenance drift. Empty traces are eligible; once a
    /// runtime has captured parts, every provenance must be Host-message based.
    /// Legacy `g:N/turn:M/part:P` traces remain readable but refuse stable
    /// historical insertion (XTraceStableCaptureEligibility.LegacyPositionalTrace).
    let supportsStableInsertion (journal: AgentJournal option) (sessionId: SessionId) : bool =
        match journal with
        | None -> false
        | Some durable ->
            let existing = xTraceOf durable sessionId

            XTraceProjection.parts existing
            |> List.forall (fun part -> part.Provenance.Contains("/msg:", StringComparison.Ordinal))

    let private classifyStableHostIds (hostMessageIds: string option list) =
        let ids = hostMessageIds |> List.choose id
        let missing = hostMessageIds |> List.exists Option.isNone
        let blank = ids |> List.exists String.IsNullOrWhiteSpace
        let duplicate = (ids |> Set.ofList |> Set.count) <> List.length ids

        match missing, blank, duplicate with
        | true, _, _ -> XTraceStableCaptureEligibility.MissingHostMessageIdentity
        | false, true, _ -> XTraceStableCaptureEligibility.BlankHostMessageIdentity
        | false, false, true -> XTraceStableCaptureEligibility.DuplicateHostMessageIdentity
        | false, false, false -> XTraceStableCaptureEligibility.Eligible ids

    let stableCaptureEligibility
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (hostMessageIds: string option list)
        : XTraceStableCaptureEligibility =
        match journal, supportsStableInsertion journal sessionId with
        | None, _ -> XTraceStableCaptureEligibility.NoDurableTrace
        | Some _, false -> XTraceStableCaptureEligibility.LegacyPositionalTrace
        | Some _, true -> classifyStableHostIds hostMessageIds

    let private stableCaptureRejection =
        function
        | XTraceStableCaptureEligibility.Eligible _ -> None
        | XTraceStableCaptureEligibility.NoDurableTrace -> Some "stable XTrace requires a durable journal"
        | XTraceStableCaptureEligibility.LegacyPositionalTrace ->
            Some "legacy positional XTrace cannot accept stable historical insertion"
        | XTraceStableCaptureEligibility.MissingHostMessageIdentity ->
            Some "stable XTrace requires a Host id for every semantic message"
        | XTraceStableCaptureEligibility.BlankHostMessageIdentity ->
            Some "stable XTrace requires a non-empty Host id for every semantic message"
        | XTraceStableCaptureEligibility.DuplicateHostMessageIdentity ->
            Some "stable XTrace requires unique Host message ids"

    let private requireStableEligibility =
        function
        | XTraceStableCaptureEligibility.Eligible _ -> Ok()
        | eligibility ->
            eligibility
            |> stableCaptureRejection
            |> Option.defaultValue "stable XTrace eligibility was refused"
            |> Error

    /// Evidence → Decision: stable insertion prerequisites (capability + id contract).
    let private validateStableCapturePrerequisites
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (messageIds: string list)
        (messages: TraceSourceMessage list)
        : Result<unit, string> =
        if List.length messageIds <> List.length messages then
            Error "stable XTrace message identity cardinality does not match semantic projection"
        else
            stableCaptureEligibility journal sessionId (messageIds |> List.map Some)
            |> requireStableEligibility

    let private captureSourcesStableWithJournal
        (durable: AgentJournal)
        (sessionId: SessionId)
        (messageIds: string list)
        (messages: TraceSourceMessage list)
        : Task<Result<XTraceProjectionState option, string>> =
        taskResult {
            do! validateStableCapturePrerequisites (Some durable) sessionId messageIds messages
            let generation = captureGeneration durable sessionId

            let works =
                enumerateStableCaptureWork generation messageIds messages
                |> List.map capturePart
                |> List.toArray

            let! result =
                captures.captureParts (captureStore durable sessionId, works, "stable")
                |> captureResult
                |> TaskResultCE.ofTask

            do!
                result
                |> Result.mapError (fun error -> "XTrace part blob write failed: " + error)

            return Some(xTraceOf durable sessionId)
        }

    let private captureSourcesStable
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (messageIds: string list)
        (messages: TraceSourceMessage list)
        : Task<Result<XTraceProjectionState option, string>> =
        task {
            match journal with
            | None -> return Ok None
            | Some durable -> return! captureSourcesStableWithJournal durable sessionId messageIds messages
        }

    /// Evidence → Decision: last-words turn/part from existing XTrace tip.
    let private lastWordsPlacement (existing: XTraceProjectionState) : int * int =
        match XTraceProjection.parts existing |> List.tryLast with
        | Some last -> last.Turn + 1, 0
        | None -> 0, 0

    let private captureLastWordsWhenAbsent
        (durable: AgentJournal)
        (sessionId: SessionId)
        (textRef: BlobRef)
        (textDigest: BlobDigest)
        (providerRun: ProviderRunIdentity)
        : Task<unit> =
        task {
            let existing = xTraceOf durable sessionId
            let generation = captureGeneration durable sessionId
            let provenance = sprintf "g:%d/last_words" generation
            let turn, partIndex = lastWordsPlacement existing

            let metadata: CaptureMetadata =
                { Provenance = provenance
                  Turn = turn
                  PartIndex = partIndex
                  ProviderRun = Some providerRun
                  ToolCallId = None
                  HostToolPartId = None }

            let part =
                createObj
                    [ "key" ==> provenance
                      "role" ==> "assistant"
                      "kind" ==> "text"
                      "toolName" ==> null
                      "metadata" ==> metadata ]

            do!
                captures.captureReference (
                    captureStore durable sessionId,
                    part,
                    BlobRef.value textRef,
                    BlobDigest.value textDigest
                )
        }

    /// Write last_words as a normal assistant text part so the session's own
    /// plain transcript contains them. Dedicated provenance avoids colliding
    /// with g:N/turn:M/part:P capture. Caller supplies an already-written blob.
    let captureLastWords
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (textRef: BlobRef)
        (textDigest: BlobDigest)
        (providerRun: ProviderRunIdentity)
        =
        task {
            match journal with
            | None -> return ()
            | Some durable -> do! captureLastWordsWhenAbsent durable sessionId textRef textDigest providerRun
        }

    let captureProjection
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (projection: ProviderSemanticProjection)
        : Task<XTraceProjectionState option> =
        projection.Messages
        |> List.map (fun message ->
            { Role = message.Role
              ProviderRun = None
              Parts =
                message.Parts
                |> List.map (fun part ->
                    { Part = part
                      HostPartId = None
                      ToolCallId = None
                      HostToolPartId = None }) })
        |> captureSources journal sessionId

    /// Capture an already-decoded transform view with the assistant message and
    /// Host ToolPart identities that localize a V1 tool invocation.
    let captureMessageView
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (messages: CapturedWire.CapturedWireMessage list)
        : Task<XTraceProjectionState option> =
        messages
        |> List.map (fun message ->
            { Role = message.Role
              ProviderRun = message.ProviderRun
              Parts =
                message.Parts
                |> List.map (fun captured ->
                    let toolCallId =
                        match captured.WirePart with
                        | WireToolCall(callId, _, _)
                        | WireToolResult(callId, _) -> Some callId
                        | _ -> None

                    { Part = semanticPartFromWire captured.WirePart
                      HostPartId = captured.HostPartId
                      ToolCallId = toolCallId
                      HostToolPartId = captured.HostToolPartId }) })
        |> captureSources journal sessionId

    let captureMessageViewStable
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (messageIds: string list)
        (messages: CapturedWire.CapturedWireMessage list)
        : Task<Result<XTraceProjectionState option, string>> =
        messages
        |> List.map (fun message ->
            { Role = message.Role
              ProviderRun = message.ProviderRun
              Parts =
                message.Parts
                |> List.map (fun captured ->
                    let toolCallId =
                        match captured.WirePart with
                        | WireToolCall(callId, _, _)
                        | WireToolResult(callId, _) -> Some callId
                        | _ -> None

                    { Part = semanticPartFromWire captured.WirePart
                      HostPartId = captured.HostPartId
                      ToolCallId = toolCallId
                      HostToolPartId = captured.HostToolPartId }) })
        |> captureSourcesStable journal sessionId messageIds

    let private toCapturedWire (message: SessionMessage) : CapturedWire.CapturedWireMessage =
        let hostToolByCall =
            message.ToolParts
            |> Array.fold (fun acc part -> Map.add (ToolCallId.value part.ToolCallId) part.HostToolPartId acc) Map.empty

        { Role = message.Role
          ProviderRun =
            if message.Role = "assistant" && not (String.IsNullOrWhiteSpace message.Id) then
                Some(ProviderRunIdentity.create message.Id)
            else
                None
          Parts =
            message.Parts
            |> Array.mapi (fun index part ->
                let hostPartId =
                    if index < message.PartIds.Length then
                        message.PartIds.[index]
                    else
                        None

                part, hostPartId)
            |> Array.toList
            |> List.choose (fun (part, hostPartId) ->
                match part with
                | MessagePart.Text text ->
                    Some
                        { WirePart = WireText text
                          HostPartId = hostPartId
                          HostToolPartId = None }
                | MessagePart.Reasoning text ->
                    Some
                        { WirePart = WireReasoning text
                          HostPartId = hostPartId
                          HostToolPartId = None }
                | MessagePart.ToolCall(callId, name, args) ->
                    let hostToolPartId = Map.tryFind callId hostToolByCall

                    Some
                        { WirePart = WireToolCall(ToolCallId.create callId, name, args)
                          HostPartId =
                            hostPartId
                            |> Option.orElseWith (fun () ->
                                hostToolPartId |> Option.map (HostToolPartId.value >> HostMessagePartId.create))
                          HostToolPartId = hostToolPartId }
                | MessagePart.ToolResult(callId, result) ->
                    let hostToolPartId = Map.tryFind callId hostToolByCall

                    Some
                        { WirePart = WireToolResult(ToolCallId.create callId, result)
                          HostPartId =
                            hostPartId
                            |> Option.orElseWith (fun () ->
                                hostToolPartId |> Option.map (HostToolPartId.value >> HostMessagePartId.create))
                          HostToolPartId = hostToolPartId }
                | MessagePart.Activity _ -> None) }

    /// Synchronise XTrace with the Host snapshot (after-hook / ensureReview).
    /// Idempotent with messages.transform via the same stable or positional provenance.
    let private captureStableSessionMessages journal sessionId ids captured =
        task {
            let! result = captureMessageViewStable journal sessionId ids captured
            return result |> Result.map ignore
        }

    let private capturePositionalSessionMessages journal sessionId captured =
        task {
            let! _ = captureMessageView journal sessionId captured
            return Ok()
        }

    let captureSessionMessages
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (messages: SessionMessage list)
        : Task<Result<unit, string>> =
        task {
            let captured = messages |> List.map toCapturedWire
            let observedIds = messages |> List.map (fun message -> Some message.Id)

            match stableCaptureEligibility journal sessionId observedIds with
            | XTraceStableCaptureEligibility.Eligible ids when List.length ids = List.length captured ->
                return! captureStableSessionMessages journal sessionId ids captured
            | _ -> return! capturePositionalSessionMessages journal sessionId captured
        }

    let private captureReceipt
        (identity: XTraceCaptureIdentity)
        (before: XTraceProjectionState)
        (after: XTraceProjectionState)
        : XTraceCaptureReceipt =
        let receipt =
            captures.receipt (XTraceProjection.history before, XTraceProjection.history after)

        { PreviousHead = XTraceCursor.create (unbox<int64> receipt?previousHead)
          CurrentHead = XTraceCursor.create (unbox<int64> receipt?currentHead)
          CapturedPartCount = unbox<int> receipt?capturedPartCount
          OpeningCaptured = unbox<bool> receipt?openingCaptured
          TerminalCaptured = unbox<bool> receipt?terminalCaptured
          Identity = identity }

    let private withoutJournalReceipt () =
        { PreviousHead = XTraceCursor.originCursor
          CurrentHead = XTraceCursor.originCursor
          CapturedPartCount = 0
          OpeningCaptured = false
          TerminalCaptured = false
          Identity = XTraceCaptureIdentity.NoDurableTrace }

    let private captureDurableUnitWithReceipt durable sessionId identity capture =
        task {
            let before = xTraceOf durable sessionId

            try
                do! capture ()
                let after = xTraceOf durable sessionId
                return Ok(captureReceipt identity before after)
            with
            | :? JournalAppendException as append -> return Error(XTraceCaptureError.StorageAppendFailed append.Failure)
            | ex -> return Error(XTraceCaptureError.StorageFailed ex.Message)
        }

    let private serializeCapture (sessionId: SessionId) (capture: unit -> Task<'T>) : Task<'T> =
        captures.run (SessionId.value sessionId, Func<Task<'T>>(capture))

    let private captureUnitWithReceipt
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (identity: XTraceCaptureIdentity)
        (capture: unit -> Task<unit>)
        : Task<Result<XTraceCaptureReceipt, XTraceCaptureError>> =
        task {
            match journal with
            | None -> return Ok(withoutJournalReceipt ())
            | Some durable ->
                return!
                    serializeCapture sessionId (fun () ->
                        captureDurableUnitWithReceipt durable sessionId identity capture)
        }

    /// Typed Opening capture. The receipt states whether the append changed the
    /// durable trace, so callers never infer capture from projection fields.
    let captureOpeningWithReceipt
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (assignmentText: string)
        (authoritativeRequirements: string list)
        =
        task {
            match journal with
            | None -> return Ok(withoutJournalReceipt ())
            | Some durable ->
                return!
                    serializeCapture sessionId (fun () ->
                        task {
                            match
                                XTraceProjection.applyOpening
                                    assignmentText
                                    authoritativeRequirements
                                    (xTraceOf durable sessionId)
                            with
                            | Error _ -> return Error(XTraceCaptureError.Refused "opening-already-captured")
                            | Ok _ ->
                                return!
                                    captureDurableUnitWithReceipt
                                        durable
                                        sessionId
                                        XTraceCaptureIdentity.PositionalIdentity
                                        (fun () ->
                                            captureOpening journal sessionId assignmentText authoritativeRequirements)
                        })
        }

    let captureTerminalTextWithReceipt
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (text: string)
        (providerRun: ProviderRunIdentity)
        =
        captureUnitWithReceipt journal sessionId XTraceCaptureIdentity.PositionalIdentity (fun () ->
            captureTerminalText journal sessionId text providerRun)

    let captureTerminalWithReceipt (journal: AgentJournal option) (turn: ReconciledTurn) =
        captureUnitWithReceipt journal turn.SessionId XTraceCaptureIdentity.PositionalIdentity (fun () ->
            captureTerminal journal turn)

    let captureLastWordsWithReceipt
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (textRef: BlobRef)
        (textDigest: BlobDigest)
        (providerRun: ProviderRunIdentity)
        =
        captureUnitWithReceipt journal sessionId XTraceCaptureIdentity.PositionalIdentity (fun () ->
            captureLastWords journal sessionId textRef textDigest providerRun)

    let captureProjectionWithReceipt
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (projection: ProviderSemanticProjection)
        : Task<Result<XTraceCaptureReceipt, XTraceCaptureError>> =
        captureUnitWithReceipt journal sessionId XTraceCaptureIdentity.PositionalIdentity (fun () ->
            task {
                let! _ = captureProjection journal sessionId projection
                return ()
            })

    let private capturedObservationMessage (observation: XTraceMessageObservation) =
        match observation.Origin with
        | Some(PromptAuthority.PromptOrigin.Continuation PromptAuthority.ContinuationKind.ProviderRetryAttempt) ->
            { observation.Message with Parts = [] }
        | _ -> observation.Message

    let private observedCaptureResult identity before current =
        let after = current |> Option.defaultValue before

        { Receipt = captureReceipt identity before after
          Current = current }

    let private captureStableObserved journal sessionId before stableIds captured =
        task {
            let! result = captureMessageViewStable journal sessionId stableIds captured

            return
                result
                |> Result.mapError XTraceCaptureError.Refused
                |> Result.map (observedCaptureResult XTraceCaptureIdentity.StableHostIdentity before)
        }

    let private capturePositionalObserved journal sessionId before captured =
        task {
            let! current = captureMessageView journal sessionId captured

            return
                current
                |> observedCaptureResult XTraceCaptureIdentity.PositionalIdentity before
                |> Ok
        }

    let private captureObservedByEligibility journal sessionId before eligibility captured =
        match eligibility with
        | XTraceStableCaptureEligibility.Eligible stableIds ->
            captureStableObserved journal sessionId before stableIds captured
        | _ -> capturePositionalObserved journal sessionId before captured

    let private protectObservedCapture capture =
        task {
            try
                return! capture ()
            with
            | :? JournalAppendException as append -> return Error(XTraceCaptureError.StorageAppendFailed append.Failure)
            | ex -> return Error(XTraceCaptureError.StorageFailed ex.Message)
        }

    let private captureObservedDurable journal durable sessionId observations =
        let before = xTraceOf durable sessionId
        let captured = observations |> List.map capturedObservationMessage

        let observedIds =
            observations |> List.map (fun observation -> observation.HostMessageId)

        let eligibility = stableCaptureEligibility journal sessionId observedIds

        protectObservedCapture (fun () -> captureObservedByEligibility journal sessionId before eligibility captured)

    /// Typed Host observation membrane. Retry-attempt continuation rows retain
    /// their physical position and identity but contribute no durable semantic
    /// parts. Stable eligibility and append mode are decided only here.
    let captureObservedMessagesWithReceipt
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (observations: XTraceMessageObservation list)
        : Task<Result<XTraceMessageCapture, XTraceCaptureError>> =
        task {
            match journal with
            | None ->
                return
                    Ok
                        { Receipt = withoutJournalReceipt ()
                          Current = None }
            | Some durable ->
                return!
                    serializeCapture sessionId (fun () -> captureObservedDurable journal durable sessionId observations)
        }

    /// Existing decoded-view entry derives from the typed observation membrane;
    /// it contains no filtering or stable-identity policy of its own.
    let captureMessageViewWithReceipt
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (messageIds: string list option)
        (messages: CapturedWire.CapturedWireMessage list)
        : Task<Result<XTraceCaptureReceipt, XTraceCaptureError>> =
        task {
            let observations =
                messages
                |> List.mapi (fun index message ->
                    { Message = message
                      HostMessageId = messageIds |> Option.bind (List.tryItem index)
                      Origin = None })

            let! captured = captureObservedMessagesWithReceipt journal sessionId observations
            return captured |> Result.map (fun result -> result.Receipt)
        }

    /// Trace-owned capture decision for a complete Host snapshot. Composition
    /// roots control ordering; this operation alone chooses stable vs positional
    /// identity and performs the durable append.
    let private captureIdentityForEligibility =
        function
        | XTraceStableCaptureEligibility.Eligible _ -> XTraceCaptureIdentity.StableHostIdentity
        | _ -> XTraceCaptureIdentity.PositionalIdentity

    let private captureSessionMessagesDurable journal durable sessionId messages =
        task {
            let before = xTraceOf durable sessionId

            let eligibility =
                messages
                |> List.map (fun message -> Some message.Id)
                |> stableCaptureEligibility journal sessionId

            let identity = captureIdentityForEligibility eligibility
            let! result = captureSessionMessages journal sessionId messages

            return
                result
                |> Result.mapError XTraceCaptureError.Refused
                |> Result.map (fun () -> captureReceipt identity before (xTraceOf durable sessionId))
        }

    let captureSessionMessagesWithReceipt
        (journal: AgentJournal option)
        (sessionId: SessionId)
        (messages: SessionMessage list)
        : Task<Result<XTraceCaptureReceipt, XTraceCaptureError>> =
        task {
            match journal with
            | None -> return Ok(withoutJournalReceipt ())
            | Some durable ->
                return!
                    serializeCapture sessionId (fun () ->
                        protectObservedCapture (fun () ->
                            captureSessionMessagesDurable journal durable sessionId messages))
        }
